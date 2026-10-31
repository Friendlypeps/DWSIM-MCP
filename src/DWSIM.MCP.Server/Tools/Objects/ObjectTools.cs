// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using DWSIM.Automation.FluentAPI;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums;
using DWSIM.MCPServer.Sessions;
using DWSIM.MCPServer.Tools.Common;
using FluentFlowsheet = DWSIM.Automation.FluentAPI.Flowsheet;

namespace DWSIM.MCPServer.Tools.Objects
{
    /// <summary>
    /// The generic escape hatch: describe, read, write and invoke on any live engine object by path,
    /// list the property-system ids, delete an object, run a flowsheet script. Everything a typed tool
    /// does not cover is reachable here, at the price of knowing the engine's member names, which
    /// dwsim_object_describe reveals.
    /// </summary>
    public class ObjectTools
    {
        private readonly SessionManager _sessions;

        public ObjectTools(SessionManager sessions) { _sessions = sessions; }

        private const string TargetHelp =
            "target is an object tag or id, or one of: 'flowsheet' (the IFlowsheet), 'options' (FlowsheetOptions), " +
            "'pp:<tag or UniqueID>' (a property package; 'pp:' alone when there is one), 'reaction:<name>', " +
            "'reactionset:<id>', 'compound:<name>'.";

        [McpTool("dwsim_object_describe",
            "List the public properties, fields (with current scalar values and enum options) and methods of a live " +
            "engine object, or of the value at a path inside it (a dictionary shows its keys, a list its length). " +
            "This is how to find the member name for dwsim_object_get/_set/_invoke. " + TargetHelp)]
        public JObject Describe(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Object tag/id, or flowsheet, options, pp:<x>, reaction:<x>, reactionset:<x>, compound:<x>")] string target,
            [McpParam("Path inside the object, e.g. Specs[\"C\"] or m_pr.InteractionParameters", Required = false)] string path = null,
            [McpParam("At most this many members/keys (default 200)", Required = false, JsonType = "integer")] int max_items = 200)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var root = Resolve(fs, target);
            var value = string.IsNullOrWhiteSpace(path) ? root : Reflect.Get(root, path);
            var o = Reflect.Describe(value, max_items);
            o["target"] = target;
            if (!string.IsNullOrWhiteSpace(path)) o["path"] = path;
            return o;
        }

        [McpTool("dwsim_object_get",
            "Read a value from a live engine object by path: a scalar as is, an enum by name, a dictionary or list " +
            "as JSON, an object as its scalar members (depth levels deep). Examples: target 'COL-1' path " +
            "'Specs[\"C\"].SpecValue'; target 'pp:' path 'FlashSettings'; target 'reaction:R1' path 'ReactionHeat'. " + TargetHelp)]
        public JObject Get(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Object tag/id, or flowsheet, options, pp:<x>, reaction:<x>, reactionset:<x>, compound:<x>")] string target,
            [McpParam("Path to the value; empty for the object itself", Required = false)] string path = null,
            [McpParam("How many levels of nested objects to expand (default 1)", Required = false, JsonType = "integer")] int depth = 1,
            [McpParam("At most this many items per collection (default 200)", Required = false, JsonType = "integer")] int max_items = 200)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var root = Resolve(fs, target);
            var value = string.IsNullOrWhiteSpace(path) ? root : Reflect.Get(root, path);
            return new JObject
            {
                ["target"] = target,
                ["path"] = path ?? "",
                ["type"] = value?.GetType().Name,
                ["value"] = Reflect.ToJson(value, Math.Max(0, depth), Math.Max(1, max_items)),
            };
        }

        [McpTool("dwsim_object_set",
            "Write a value into a live engine object by path. value is JSON text: a number (300), a string " +
            "(\"NRTL\" or NRTL), true/false, an enum name, an array, or an object whose members are applied to the " +
            "target (a missing dictionary entry is created). Examples: target 'SC-1' path 'm_lightkey' value " +
            "\"Ethyl acetate\"; target 'pp:' path 'FlashSettings[ForceEquilibriumCalculationType]' value VLLE; " +
            "target 'R-1' path 'Volume' value 2.5 (SI units, always). Prefer the typed tools; this bypasses their " +
            "checks. Run dwsim_flowsheet_check afterwards. " + TargetHelp)]
        public JObject Set(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Object tag/id, or flowsheet, options, pp:<x>, reaction:<x>, reactionset:<x>, compound:<x>")] string target,
            [McpParam("Path to the member to write")] string path,
            [McpParam("The value as JSON text (a bare word is taken as a string)")] JToken value)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var root = Resolve(fs, target);
            var token = AsJson(value);
            var before = SafeGet(root, path);
            var written = Reflect.Set(root, path, token);
            fs.Inner.ResetCalculationStatus();
            return new JObject
            {
                ["target"] = target,
                ["path"] = path,
                ["previous"] = Reflect.ToJson(before, 0),
                ["value"] = Reflect.ToJson(written, 1),
                ["note"] = "Values are in SI. Run dwsim_flowsheet_check before the next solve.",
            };
        }

        [McpTool("dwsim_object_invoke",
            "Call a public method of a live engine object (or of the value at a path): e.g. target 'COL-1' method " +
            "'SetNumberOfStages' args [12]; target 'COL-1' method 'GetConnectionPortsList'; target 'pp:' method " +
            "'EstimateMissingInteractionParameters' args [false]. Arguments are JSON, converted to the parameter " +
            "types of the first matching overload; the result is returned as JSON. " + TargetHelp)]
        public JObject Invoke(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Object tag/id, or flowsheet, options, pp:<x>, reaction:<x>, reactionset:<x>, compound:<x>")] string target,
            [McpParam("Method name")] string method,
            [McpParam("Arguments as a JSON array", Required = false, JsonType = "array")] JArray args = null,
            [McpParam("Path to the object to call the method on; empty for the target itself", Required = false)] string path = null,
            [McpParam("Levels of the result to expand (default 1)", Required = false, JsonType = "integer")] int depth = 1)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var root = Resolve(fs, target);
            var obj = string.IsNullOrWhiteSpace(path) ? root : Reflect.Get(root, path);
            if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("Name the method.");
            var result = Reflect.Invoke(obj, method, args);
            fs.Inner.ResetCalculationStatus();
            return new JObject
            {
                ["target"] = target, ["method"] = method,
                ["result"] = Reflect.ToJson(result, Math.Max(0, depth)),
            };
        }

        [McpTool("dwsim_object_list_properties",
            "List the property-system ids of an object (the PROP_* names dwsim_unitop_set, dwsim_adjust_create, " +
            "dwsim_spec_create and dwsim_study_sweep take), with description, current value, units and whether each " +
            "is writable (a specification) or read-only (a result). type filters: ALL (default), WR (writable " +
            "specifications), RO (results), RW.")]
        public JObject ListProperties(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Object tag or id")] string target,
            [McpParam("ALL, WR, RO or RW (default ALL)", Required = false)] string type = "ALL")
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var obj = Find.ByTagOrId(fs, target, "object");
            var pt = Json.Enum<PropertyType>(string.IsNullOrWhiteSpace(type) ? "ALL" : type, "property type");
            var su = fs.Inner.FlowsheetOptions.SelectedUnitSystem;
            var entries = PropertyCatalog.For(obj, su, pt);
            return new JObject
            {
                ["object"] = obj.GraphicObject.Tag,
                ["type"] = obj.GraphicObject.ObjectType.ToString(),
                ["unit_system"] = su.Name,
                ["properties"] = new JArray(entries.Select(e => new JObject
                {
                    ["id"] = e.Id, ["description"] = e.Description, ["units"] = e.Units,
                    ["value"] = Reflect.ToJson(e.Value, 0), ["writable"] = e.Writable, ["dynamic"] = e.IsDynamic
                })),
                ["calculation_modes"] = Json.Strings(PropertySetter.CalculationModes(obj).Keys),
            };
        }

        [McpTool("dwsim_object_delete",
            "Delete a stream, unit operation or logical block from the flowsheet, disconnecting it first.")]
        public JObject Delete(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Object tag or id")] string target)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var obj = Find.ByTagOrId(fs, target, "object");
            var tag = obj.GraphicObject.Tag;
            fs.Inner.DeleteSelectedObject(null, EventArgs.Empty, obj.GraphicObject, false, false);
            if (fs.Inner.SimulationObjects.ContainsKey(obj.Name)) fs.Inner.SimulationObjects.Remove(obj.Name);
            fs.Inner.ResetCalculationStatus();
            return new JObject { ["deleted"] = tag, ["remaining"] = Json.Strings(fs.Inner.SimulationObjects.Values.Where(o => o.GraphicObject != null).Select(o => o.GraphicObject.Tag)) };
        }

        [McpTool("dwsim_script_run",
            "Run a Python script against the live flowsheet with the engine's own script host (IronPython by default; " +
            "'PythonNET' needs --python-path). In scope: Flowsheet (IFlowsheet), Solver, and every object by its tag " +
            "with '-' replaced by '_' (e.g. R_1.Volume = 2.0). print output and errors are returned. With keep the " +
            "script stays in the flowsheet under title and is saved with it. EXPERIMENTAL on a headless server: " +
            "if it fails to start, use dwsim_object_set / dwsim_object_invoke instead.")]
        public JObject RunScript(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Python source code")] string code,
            [McpParam("IronPython (default) or PythonNET", Required = false)] string interpreter = "IronPython",
            [McpParam("Keep the script in the flowsheet (default false)", Required = false, JsonType = "boolean")] bool keep = false,
            [McpParam("Title of the kept script", Required = false)] string title = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("code is empty.");
            var interp = Json.Enum<DWSIM.Interfaces.Enums.Scripts.Interpreter>(string.IsNullOrWhiteSpace(interpreter) ? "IronPython" : interpreter, "interpreter");
            var inner = fs.Inner as FlowsheetBase.FlowsheetBase ?? throw new ArgumentException("This flowsheet host cannot run scripts.");

            var id = Guid.NewGuid().ToString();
            var script = new DWSIM.FlowsheetSolver.Script
            {
                ID = id,
                Title = string.IsNullOrWhiteSpace(title) ? "mcp-" + id.Substring(0, 8) : title,
                ScriptText = code,
                PythonInterpreter = interp,
                Linked = false
            };
            inner.Scripts[id] = script;

            var output = new List<string>();
            var mcpFs = _sessions.GetMcpFlowsheet(flowsheet_id);
            Action<string, IFlowsheet.MessageType> previous = mcpFs?.MessageSink;
            if (mcpFs != null) mcpFs.MessageSink = (text, type) => { lock (output) output.Add($"[{type}] {text}"); };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string failure = null;
            try { inner.RunScript(id); }
            catch (Exception ex) { failure = (ex.InnerException ?? ex).Message; }
            finally
            {
                if (mcpFs != null) mcpFs.MessageSink = previous;
                if (!keep) inner.Scripts.Remove(id);
            }
            fs.Inner.ResetCalculationStatus();
            var o = new JObject
            {
                ["ran"] = failure == null,
                ["interpreter"] = interp.ToString(),
                ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 3),
                ["output"] = Json.Strings(output),
                ["output_captured"] = mcpFs != null,
                ["kept"] = keep,
            };
            if (keep) o["script_title"] = script.Title;
            if (failure != null) o["error"] = failure;
            if (output.Any(l => l.Contains("Error running script"))) o["error"] = o["error"] ?? "The script raised an error; see output.";
            return o;
        }

        // ------------------------------------------------------------------ helpers

        internal static object Resolve(FluentFlowsheet fs, string target)
        {
            if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Name the target. " + TargetHelp);
            var t = target.Trim();
            if (t.Equals("flowsheet", StringComparison.OrdinalIgnoreCase)) return fs.Inner;
            if (t.Equals("options", StringComparison.OrdinalIgnoreCase)) return fs.Inner.FlowsheetOptions;
            int colon = t.IndexOf(':');
            if (colon > 0)
            {
                var kind = t.Substring(0, colon).Trim().ToLowerInvariant();
                var name = t.Substring(colon + 1).Trim();
                switch (kind)
                {
                    case "pp": case "package": return Find.Package(fs, string.IsNullOrEmpty(name) ? null : name);
                    case "reaction":
                        if (fs.Inner.Reactions.TryGetValue(name, out var r)) return r;
                        return fs.Inner.Reactions.Values.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                            ?? throw new ArgumentException($"No reaction '{name}'. Reactions: {string.Join(", ", fs.Inner.Reactions.Values.Select(x => x.Name))}.");
                    case "reactionset":
                        if (fs.Inner.ReactionSets.TryGetValue(name, out var set)) return set;
                        throw new ArgumentException($"No reaction set '{name}'. Sets: {string.Join(", ", fs.Inner.ReactionSets.Keys)}.");
                    case "compound": return Find.Compound(fs, name);
                }
            }
            return Find.ByTagOrId(fs, t, "object");
        }

        /// <summary>A tool argument that may arrive as JSON text (the schema says string) or as a JSON value.</summary>
        private static JToken AsJson(JToken value)
        {
            if (value == null) return JValue.CreateNull();
            if (value.Type != JTokenType.String) return value;
            var s = value.Value<string>() ?? "";
            var trimmed = s.Trim();
            if (trimmed.Length == 0) return new JValue("");
            try
            {
                var parsed = JToken.Parse(trimmed);
                // "NRTL" stays a string, but a bare word that is not JSON is the string itself.
                return parsed;
            }
            catch { return new JValue(s); }
        }

        private static object SafeGet(object root, string path)
        {
            try { return Reflect.Get(root, path); } catch { return null; }
        }
    }
}
