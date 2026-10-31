// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace DWSIM.MCPServer.Tools.Common
{
    /// <summary>
    /// Path-based reflection over live engine objects: public properties AND fields (the engine's VB
    /// classes expose many settings as fields), dictionary keys and list indices, Nullable and enum
    /// conversion. Path grammar: <c>A.B.C</c>, <c>Dict["key"]</c>, <c>Dict[key]</c>, <c>List[3]</c>;
    /// member names are matched case-insensitively.
    /// </summary>
    public static class Reflect
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance;

        private static readonly Regex Segment = new Regex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)?(?<idx>(\[[^\]]*\])*)$", RegexOptions.Compiled);

        /// <summary>One step of a path: a member name and/or indexers.</summary>
        public sealed class Step
        {
            public string Member;
            public List<string> Keys = new List<string>();
            public override string ToString() => Member + string.Concat(Keys.Select(k => $"[{k}]"));
        }

        public static List<Step> Parse(string path)
        {
            var steps = new List<Step>();
            if (string.IsNullOrWhiteSpace(path)) return steps;
            foreach (var raw in SplitDots(path))
            {
                var m = Segment.Match(raw.Trim());
                if (!m.Success) throw new ArgumentException($"'{raw}' is not a path segment. Use Name, Name[key], Name[\"key\"] or List[3].");
                var step = new Step { Member = m.Groups["name"].Success ? m.Groups["name"].Value : null };
                foreach (Capture c in m.Groups["idx"].Captures)
                    foreach (Match k in Regex.Matches(c.Value, @"\[([^\]]*)\]"))
                        step.Keys.Add(k.Groups[1].Value.Trim().Trim('"', '\''));
                if (step.Member == null && step.Keys.Count == 0) throw new ArgumentException($"Empty path segment in '{path}'.");
                steps.Add(step);
            }
            return steps;
        }

        /// <summary>Splits on dots that are not inside brackets or quotes.</summary>
        private static IEnumerable<string> SplitDots(string path)
        {
            var sb = new System.Text.StringBuilder();
            int depth = 0; bool quote = false;
            foreach (var ch in path)
            {
                if (ch == '"' || ch == '\'') quote = !quote;
                else if (!quote && ch == '[') depth++;
                else if (!quote && ch == ']') depth--;
                if (ch == '.' && depth == 0 && !quote) { yield return sb.ToString(); sb.Clear(); }
                else sb.Append(ch);
            }
            yield return sb.ToString();
        }

        /// <summary>Walks the path and returns the value at its end.</summary>
        public static object Get(object root, string path)
        {
            object current = root;
            foreach (var step in Parse(path))
            {
                if (step.Member != null) current = GetMember(current, step.Member, path);
                foreach (var key in step.Keys) current = GetIndexed(current, key, path);
            }
            return current;
        }

        /// <summary>Sets the value at the end of the path, converting the JSON to the member's type.</summary>
        public static object Set(object root, string path, JToken value)
        {
            var steps = Parse(path);
            if (steps.Count == 0) throw new ArgumentException("A path is required to set a value.");
            object current = root;
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                bool last = i == steps.Count - 1;
                if (step.Member != null)
                {
                    if (last && step.Keys.Count == 0)
                        return SetMember(current, step.Member, value, path);
                    current = GetMember(current, step.Member, path);
                }
                for (int k = 0; k < step.Keys.Count; k++)
                {
                    bool lastKey = last && k == step.Keys.Count - 1;
                    if (lastKey) return SetIndexed(current, step.Keys[k], value, path);
                    current = GetIndexed(current, step.Keys[k], path);
                }
            }
            throw new ArgumentException($"Nothing to set at '{path}'.");
        }

        public static MemberInfo FindMember(Type t, string name)
        {
            var p = t.GetProperty(name, Flags) ?? t.GetProperties(Flags).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && x.GetIndexParameters().Length == 0);
            if (p != null) return p;
            var f = t.GetField(name, Flags) ?? t.GetFields(Flags).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return f;
        }

        private static object GetMember(object target, string name, string path)
        {
            if (target == null) throw new ArgumentException($"'{path}': the value before '{name}' is null.");
            var m = FindMember(target.GetType(), name) ?? throw new ArgumentException($"'{name}' is not a public property or field of {target.GetType().Name}. {MemberHint(target)}");
            return m is PropertyInfo p ? p.GetValue(target) : ((FieldInfo)m).GetValue(target);
        }

        private static object SetMember(object target, string name, JToken value, string path)
        {
            if (target == null) throw new ArgumentException($"'{path}': the value before '{name}' is null.");
            var m = FindMember(target.GetType(), name) ?? throw new ArgumentException($"'{name}' is not a public property or field of {target.GetType().Name}. {MemberHint(target)}");
            Type type; Action<object> write;
            if (m is PropertyInfo p)
            {
                if (!p.CanWrite || p.GetSetMethod() == null) throw new ArgumentException($"{target.GetType().Name}.{p.Name} is read-only.");
                type = p.PropertyType; write = v => p.SetValue(target, v);
            }
            else
            {
                var f = (FieldInfo)m;
                if (f.IsInitOnly) throw new ArgumentException($"{target.GetType().Name}.{f.Name} is read-only.");
                type = f.FieldType; write = v => f.SetValue(target, v);
            }
            var converted = Convert(value, type, m is PropertyInfo pi ? pi.GetValue(target) : ((FieldInfo)m).GetValue(target));
            write(converted);
            return converted;
        }

        private static object GetIndexed(object target, string key, string path)
        {
            if (target == null) throw new ArgumentException($"'{path}': cannot index a null value.");
            if (target is IDictionary dict)
            {
                var k = DictionaryKey(dict, key);
                if (k == null || !dict.Contains(k)) throw new ArgumentException($"Key '{key}' not found. Keys: {string.Join(", ", dict.Keys.Cast<object>().Take(40))}.");
                return dict[k];
            }
            if (target is IList list)
            {
                if (!int.TryParse(key, out var i) || i < 0 || i >= list.Count) throw new ArgumentException($"Index '{key}' is outside 0..{list.Count - 1}.");
                return list[i];
            }
            // an indexer property (VB default property)
            var indexer = target.GetType().GetProperties(Flags).FirstOrDefault(p => p.GetIndexParameters().Length == 1);
            if (indexer != null)
            {
                var pt = indexer.GetIndexParameters()[0].ParameterType;
                return indexer.GetValue(target, new[] { Convert(new JValue(key), pt, null) });
            }
            throw new ArgumentException($"{target.GetType().Name} cannot be indexed with [{key}].");
        }

        private static object SetIndexed(object target, string key, JToken value, string path)
        {
            if (target == null) throw new ArgumentException($"'{path}': cannot index a null value.");
            if (target is IDictionary dict)
            {
                var valueType = dict.GetType().IsGenericType ? dict.GetType().GetGenericArguments()[1] : typeof(object);
                var k = DictionaryKey(dict, key) ?? ConvertKey(dict, key);
                var existing = dict.Contains(k) ? dict[k] : null;
                var converted = Convert(value, valueType, existing);
                dict[k] = converted;
                return converted;
            }
            if (target is IList list)
            {
                if (!int.TryParse(key, out var i) || i < 0 || i >= list.Count) throw new ArgumentException($"Index '{key}' is outside 0..{list.Count - 1}.");
                var elementType = list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : list.GetType().GetElementType() ?? typeof(object);
                var converted = Convert(value, elementType, list[i]);
                list[i] = converted;
                return converted;
            }
            throw new ArgumentException($"{target.GetType().Name} cannot be indexed with [{key}] for writing.");
        }

        private static object DictionaryKey(IDictionary dict, string key)
        {
            foreach (var k in dict.Keys)
            {
                if (k is string s && string.Equals(s, key, StringComparison.Ordinal)) return k;
            }
            foreach (var k in dict.Keys)
            {
                if (k is string s && string.Equals(s, key, StringComparison.OrdinalIgnoreCase)) return k;
                if (!(k is string) && string.Equals(k.ToString(), key, StringComparison.OrdinalIgnoreCase)) return k;
            }
            return null;
        }

        private static object ConvertKey(IDictionary dict, string key)
        {
            var keyType = dict.GetType().IsGenericType ? dict.GetType().GetGenericArguments()[0] : typeof(string);
            return Convert(new JValue(key), keyType, null);
        }

        /// <summary>JSON to a CLR value of the given type; a JSON object onto an existing instance or a new one.</summary>
        public static object Convert(JToken value, Type type, object existing)
        {
            if (value == null || value.Type == JTokenType.Null)
            {
                if (!type.IsValueType || Nullable.GetUnderlyingType(type) != null) return null;
                throw new ArgumentException($"null is not a {type.Name}.");
            }
            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            if (underlying.IsEnum)
            {
                var s = value.ToString().Trim();
                var name = Enum.GetNames(underlying).FirstOrDefault(n => string.Equals(n, s.Replace(" ", "_"), StringComparison.OrdinalIgnoreCase));
                if (name != null) return Enum.Parse(underlying, name);
                if (long.TryParse(s, out var num) && Enum.IsDefined(underlying, System.Convert.ChangeType(num, Enum.GetUnderlyingType(underlying)))) return Enum.ToObject(underlying, num);
                throw new ArgumentException($"'{s}' is not a {underlying.Name}. Use one of: {string.Join(", ", Enum.GetNames(underlying))}.");
            }
            if (underlying == typeof(string)) return value.Type == JTokenType.String ? value.Value<string>() : value.ToString(Newtonsoft.Json.Formatting.None);
            if (underlying == typeof(bool))
            {
                if (value.Type == JTokenType.Boolean) return value.Value<bool>();
                var s = value.ToString().Trim().ToLowerInvariant();
                if (s == "true" || s == "1" || s == "yes") return true;
                if (s == "false" || s == "0" || s == "no") return false;
                throw new ArgumentException($"'{value}' is not a boolean.");
            }
            if (underlying.IsPrimitive || underlying == typeof(decimal))
            {
                try { return System.Convert.ChangeType(value.Type == JTokenType.String ? (object)value.Value<string>() : value.ToObject<double>(), underlying, CultureInfo.InvariantCulture); }
                catch (Exception ex) { throw new ArgumentException($"'{value}' is not a {underlying.Name}: {ex.Message}"); }
            }
            if (value is JObject jo && existing != null && !(existing is IDictionary) && !(existing is IEnumerable))
            {
                foreach (var p in jo.Properties()) SetMember(existing, p.Name, p.Value, p.Name);
                return existing;
            }
            if (value is JObject jo2 && existing == null && !typeof(IDictionary).IsAssignableFrom(underlying) && underlying.GetConstructor(Type.EmptyTypes) != null && underlying != typeof(object))
            {
                var inst = Activator.CreateInstance(underlying);
                foreach (var p in jo2.Properties()) SetMember(inst, p.Name, p.Value, p.Name);
                return inst;
            }
            try { return value.ToObject(underlying); }
            catch (Exception ex) { throw new ArgumentException($"Cannot convert {value.Type} to {underlying.Name}: {ex.Message}"); }
        }

        /// <summary>A value as JSON: scalars as they are, enums by name, collections and objects to the given depth.</summary>
        public static JToken ToJson(object value, int depth = 1, int maxItems = 200)
        {
            if (value == null) return JValue.CreateNull();
            var t = value.GetType();
            if (t.IsEnum) return value.ToString();
            if (value is string s) return s;
            if (value is bool b) return b;
            if (value is double d) return Json.Num(d);
            if (value is float f) return Json.Num(f);
            if (value is decimal m) return (double)m;
            if (t.IsPrimitive) return new JValue(value);
            if (value is DateTime dt) return dt.ToString("o");
            if (value is JToken jt) return jt;
            if (value is IDictionary dict)
            {
                var o = new JObject();
                int n = 0;
                foreach (DictionaryEntry e in dict)
                {
                    if (n++ >= maxItems) { o["..."] = $"{dict.Count - maxItems} more"; break; }
                    o[e.Key?.ToString() ?? "null"] = depth > 0 ? ToJson(e.Value, depth - 1, maxItems) : Summary(e.Value);
                }
                return o;
            }
            if (value is IEnumerable list)
            {
                var a = new JArray();
                int n = 0;
                foreach (var item in list)
                {
                    if (n++ >= maxItems) { a.Add($"... more"); break; }
                    a.Add(depth > 0 ? ToJson(item, depth - 1, maxItems) : Summary(item));
                }
                return a;
            }
            if (depth <= 0) return Summary(value);
            var obj = new JObject { ["$type"] = t.Name };
            foreach (var mem in Members(t))
            {
                object v;
                try { v = mem is PropertyInfo p ? p.GetValue(value) : ((FieldInfo)mem).GetValue(value); }
                catch (Exception ex) { obj[mem.Name] = "<error: " + (ex.InnerException ?? ex).Message + ">"; continue; }
                var vt = v?.GetType();
                if (v == null || vt.IsPrimitive || vt.IsEnum || v is string || v is decimal || v is DateTime)
                    obj[mem.Name] = ToJson(v, 0, maxItems);
                else if (depth > 1)
                    obj[mem.Name] = ToJson(v, depth - 1, maxItems);
                else
                    obj[mem.Name] = Summary(v);
            }
            return obj;
        }

        private static JToken Summary(object v)
        {
            if (v == null) return JValue.CreateNull();
            var t = v.GetType();
            if (t.IsPrimitive || t.IsEnum || v is string || v is decimal) return ToJson(v, 0);
            if (v is IDictionary d) return $"<{t.Name}: {d.Count} entries>";
            if (v is ICollection c) return $"<{t.Name}: {c.Count} items>";
            return $"<{t.Name}>";
        }

        /// <summary>Public readable properties (no indexers) and fields, declared most-derived first, deduplicated by name.</summary>
        public static List<MemberInfo> Members(Type t)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<MemberInfo>();
            foreach (var p in t.GetProperties(Flags).Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
                if (seen.Add(p.Name)) list.Add(p);
            foreach (var f in t.GetFields(Flags))
                if (seen.Add(f.Name)) list.Add(f);
            return list.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static JObject Describe(object value, int maxItems = 200)
        {
            if (value == null) return new JObject { ["value"] = null };
            var t = value.GetType();
            var o = new JObject { ["type"] = t.FullName };
            if (value is IDictionary dict)
            {
                o["kind"] = "dictionary"; o["count"] = dict.Count;
                o["keys"] = new JArray(dict.Keys.Cast<object>().Take(maxItems).Select(k => (JToken)k.ToString()));
                if (dict.GetType().IsGenericType) o["value_type"] = dict.GetType().GetGenericArguments()[1].Name;
                return o;
            }
            if (value is ICollection col && !(value is string))
            {
                o["kind"] = "list"; o["count"] = col.Count;
                var et = value.GetType().IsGenericType ? value.GetType().GetGenericArguments().FirstOrDefault() : value.GetType().GetElementType();
                if (et != null) o["element_type"] = et.Name;
                return o;
            }
            if (t.IsPrimitive || t.IsEnum || value is string || value is decimal)
            {
                o["kind"] = "scalar"; o["value"] = ToJson(value, 0);
                if (t.IsEnum) o["options"] = Json.Strings(Enum.GetNames(t));
                return o;
            }
            o["kind"] = "object";
            var members = new JArray();
            foreach (var mem in Members(t))
            {
                var mt = mem is PropertyInfo p ? p.PropertyType : ((FieldInfo)mem).FieldType;
                var entry = new JObject
                {
                    ["name"] = mem.Name,
                    ["type"] = TypeName(mt),
                    ["kind"] = mem is PropertyInfo ? "property" : "field",
                    ["writable"] = mem is PropertyInfo pw ? pw.CanWrite && pw.GetSetMethod() != null : !((FieldInfo)mem).IsInitOnly,
                };
                try
                {
                    var v = mem is PropertyInfo pg ? pg.GetValue(value) : ((FieldInfo)mem).GetValue(value);
                    entry["value"] = Summary(v);
                }
                catch (Exception ex) { entry["value"] = "<error: " + (ex.InnerException ?? ex).Message + ">"; }
                var ut = Nullable.GetUnderlyingType(mt) ?? mt;
                if (ut.IsEnum) entry["options"] = Json.Strings(Enum.GetNames(ut));
                members.Add(entry);
            }
            o["members"] = members;
            var methods = t.GetMethods(Flags)
                .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object) && !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_"))
                .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => TypeName(p.ParameterType) + " " + p.Name))})")
                .Distinct().OrderBy(x => x).Take(maxItems);
            o["methods"] = Json.Strings(methods);
            return o;
        }

        public static string TypeName(Type t)
        {
            var u = Nullable.GetUnderlyingType(t);
            if (u != null) return TypeName(u) + "?";
            if (t.IsGenericType) return t.Name.Split('`')[0] + "<" + string.Join(", ", t.GetGenericArguments().Select(TypeName)) + ">";
            return t.Name;
        }

        private static string MemberHint(object target)
        {
            var names = Members(target.GetType()).Select(m => m.Name).Take(60).ToList();
            return "Members: " + string.Join(", ", names) + (names.Count >= 60 ? ", ..." : "") + ". Use dwsim_object_describe.";
        }

        /// <summary>Invokes a public method by name, choosing the overload whose parameters the JSON arguments convert to.</summary>
        public static object Invoke(object target, string method, JArray args)
        {
            if (target == null) throw new ArgumentException("Cannot invoke a method on a null value.");
            args = args ?? new JArray();
            var candidates = target.GetType().GetMethods(Flags)
                .Where(m => string.Equals(m.Name, method, StringComparison.OrdinalIgnoreCase))
                .Where(m => m.GetParameters().Length >= args.Count && m.GetParameters().Skip(args.Count).All(p => p.IsOptional))
                .OrderBy(m => m.GetParameters().Length)
                .ToList();
            if (candidates.Count == 0)
            {
                var all = target.GetType().GetMethods(Flags).Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object)).Select(m => m.Name).Distinct().OrderBy(x => x);
                throw new ArgumentException($"{target.GetType().Name} has no public method '{method}' taking {args.Count} argument(s). Methods: {string.Join(", ", all)}.");
            }
            var errors = new List<string>();
            foreach (var m in candidates)
            {
                var ps = m.GetParameters();
                var values = new object[ps.Length];
                try
                {
                    for (int i = 0; i < ps.Length; i++)
                        values[i] = i < args.Count ? Convert(args[i], ps[i].ParameterType, null) : (ps[i].HasDefaultValue ? ps[i].DefaultValue : Type.Missing);
                }
                catch (Exception ex) { errors.Add($"{m}: {ex.Message}"); continue; }
                try { return m.Invoke(target, values); }
                catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
            }
            throw new ArgumentException("No overload accepted the arguments. " + string.Join(" | ", errors));
        }
    }
}
