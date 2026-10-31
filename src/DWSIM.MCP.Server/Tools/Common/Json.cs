// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DWSIM.MCPServer.Tools.Common
{
    /// <summary>JSON argument helpers shared by the extension tools.</summary>
    public static class Json
    {
        /// <summary>A {compound: number} object as a dictionary; a non-numeric value is an error naming the key.</summary>
        public static Dictionary<string, double> NumberMap(JObject o, string what)
        {
            var d = new Dictionary<string, double>();
            if (o == null) return d;
            foreach (var p in o.Properties())
            {
                if (p.Value.Type != JTokenType.Float && p.Value.Type != JTokenType.Integer)
                    throw new ArgumentException($"{what}: the value of '{p.Name}' must be a number.");
                d[p.Name] = p.Value.Value<double>();
            }
            return d;
        }

        /// <summary>A finite double for the response, or null for NaN/infinity (JSON has no NaN).</summary>
        public static JToken Num(double v) => double.IsNaN(v) || double.IsInfinity(v) ? JValue.CreateNull() : new JValue(v);

        public static JToken Num(double? v) => v.HasValue ? Num(v.Value) : JValue.CreateNull();

        /// <summary>An enum value parsed by name (case-insensitive) or number; the error lists the names.</summary>
        public static TEnum Enum<TEnum>(string value, string what) where TEnum : struct, System.Enum
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                if (System.Enum.TryParse<TEnum>(value.Trim().Replace(" ", "_"), true, out var parsed)) return parsed;
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && System.Enum.IsDefined(typeof(TEnum), n))
                    return (TEnum)System.Enum.ToObject(typeof(TEnum), n);
            }
            throw new ArgumentException($"'{value}' is not a {what}. Use one of: {string.Join(", ", System.Enum.GetNames(typeof(TEnum)))}.");
        }

        public static JArray Strings(IEnumerable<string> items) => new JArray(items.Select(s => (JToken)s));
    }
}
