// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using DWSIM.Automation.DynamicRunner.LiveSliders;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums;
using DWSIM.MCPServer.Sessions;
using DWSIM.MCPServer.Tools.Common;
using FluentFlowsheet = DWSIM.Automation.FluentAPI.Flowsheet;

namespace DWSIM.MCPServer.Tools.Study
{
    /// <summary>
    /// Parameter studies: which variables can be swept and watched (the engine's LiveSliderStudy
    /// catalogue), and a sweep that solves the flowsheet once per point with the session's own solver.
    /// </summary>
    public class StudyTools
    {
        private readonly SessionManager _sessions;

        public StudyTools(SessionManager sessions) { _sessions = sessions; }

        [McpTool("dwsim_study_list_variables",
            "List, for every object or for one, the specifications that can be swept (with their property ids, " +
            "current values and units, in SI) and the results that can be watched. The ids are what dwsim_study_sweep, " +
            "dwsim_adjust_create and dwsim_spec_create take.")]
        public JObject ListVariables(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Tag of one object; omit for all", Required = false)] string @object = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            IEnumerable<ISimulationObject> objects = string.IsNullOrWhiteSpace(@object)
                ? LiveSliderStudy.ObjectsWithSpecifications(fs.Inner).Union(LiveSliderStudy.ObjectsWithResults(fs.Inner))
                : new[] { Find.ByTagOrId(fs, @object, "object") };

            var arr = new JArray();
            foreach (var obj in objects.Where(o => o.GraphicObject != null).OrderBy(o => o.GraphicObject.Tag))
            {
                arr.Add(new JObject
                {
                    ["object"] = obj.GraphicObject.Tag,
                    ["type"] = obj.GraphicObject.ObjectType.ToString(),
                    ["specifications"] = new JArray(LiveSliderStudy.Specifications(fs.Inner, obj).Select(c => Choice(obj, c))),
                    ["results"] = new JArray(LiveSliderStudy.Results(fs.Inner, obj).Select(c => Choice(obj, c))),
                });
            }
            return new JObject { ["units"] = "SI", ["objects"] = arr };
        }

        [McpTool("dwsim_study_sweep",
            "Sweep one or two specifications over a grid and record watched results at every point, solving the " +
            "flowsheet once per point. variables is [{object, property, values: [..]}] or [{object, property, " +
            "from, to, steps}] (two variables make a full grid); watches is [{object, property}]. Values are in SI " +
            "(K, Pa, kg/s, mol/s, kW ...); the response names the unit of each column. The " +
            "swept inputs are restored and the flowsheet re-solved afterwards unless restore is false. The grid " +
            "is capped at max_points; each failed point carries its error and the sweep goes on unless " +
            "stop_on_error. Solve the flowsheet once before sweeping.")]
        public JObject Sweep(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Variables as [{object, property, values} | {object, property, from, to, steps}]", JsonType = "array")] JArray variables,
            [McpParam("Watched results as [{object, property}]", JsonType = "array")] JArray watches,
            [McpParam("Maximum grid points (default 400)", Required = false, JsonType = "integer")] int max_points = 400,
            [McpParam("Restore the inputs and re-solve afterwards (default true)", Required = false, JsonType = "boolean")] bool restore = true,
            [McpParam("Stop at the first point that fails (default false)", Required = false, JsonType = "boolean")] bool stop_on_error = false,
            [McpParam("Solver timeout per point in seconds (default 120)", Required = false, JsonType = "integer")] int timeout_s = 120)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            if (variables == null || variables.Count == 0) throw new ArgumentException("Give at least one variable.");
            if (variables.Count > 3) throw new ArgumentException("At most three variables; the grid grows as their product.");
            if (watches == null || watches.Count == 0) throw new ArgumentException("Give at least one watch: the result to record at each point.");
            if (max_points < 1) max_points = 400;

            var su = SI;
            var vars = new List<(ISimulationObject obj, string prop, double[] values, string unit)>();
            foreach (var tok in variables)
            {
                var v = tok as JObject ?? throw new ArgumentException("Each variable is an object.");
                var obj = Find.ByTagOrId(fs, v.Value<string>("object"), "object");
                var prop = ResolveSpec(fs, obj, v.Value<string>("property"));
                double[] values;
                if (v["values"] is JArray list)
                {
                    values = list.Select(x => x.Value<double>()).ToArray();
                }
                else
                {
                    double from = v.Value<double?>("from") ?? throw new ArgumentException($"{obj.GraphicObject.Tag}.{prop}: give values or from/to/steps.");
                    double to = v.Value<double?>("to") ?? throw new ArgumentException($"{obj.GraphicObject.Tag}.{prop}: 'to' is required with 'from'.");
                    int steps = v.Value<int?>("steps") ?? 5;
                    if (steps < 2) throw new ArgumentException("steps must be at least 2.");
                    values = Enumerable.Range(0, steps).Select(i => from + (to - from) * i / (steps - 1)).ToArray();
                }
                if (values.Length == 0) throw new ArgumentException($"{obj.GraphicObject.Tag}.{prop}: no values.");
                vars.Add((obj, prop, values, Unit(obj, prop)));
            }

            var watchList = new List<(ISimulationObject obj, string prop, string unit)>();
            foreach (var tok in watches)
            {
                var w = tok as JObject ?? throw new ArgumentException("Each watch is {object, property}.");
                var obj = Find.ByTagOrId(fs, w.Value<string>("object"), "object");
                var prop = ResolveAny(obj, w.Value<string>("property"));
                watchList.Add((obj, prop, Unit(obj, prop)));
            }

            long total = vars.Aggregate(1L, (a, v) => a * v.values.Length);
            bool truncated = total > max_points;

            var columns = new JArray(vars.Select(v => (JToken)$"{v.obj.GraphicObject.Tag}.{v.prop}").Concat(watchList.Select(w => (JToken)$"{w.obj.GraphicObject.Tag}.{w.prop}")));
            var units = new JArray(vars.Select(v => (JToken)v.unit).Concat(watchList.Select(w => (JToken)w.unit)));
            var originals = vars.Select(v => Value(v.obj, v.prop)).ToArray();

            var rows = new JArray();
            int solved = 0, failed = 0;
            var sw = Stopwatch.StartNew();

            _sessions.WithLock(flowsheet_id, _ =>
            {
                var idx = new int[vars.Count];
                long point = 0;
                while (point < Math.Min(total, max_points))
                {
                    var inputs = new double[vars.Count];
                    for (int i = 0; i < vars.Count; i++) inputs[i] = vars[i].values[idx[i]];

                    var row = new JObject { ["inputs"] = new JArray(inputs.Select(Json.Num)) };
                    string error = null;
                    var pointWatch = Stopwatch.StartNew();
                    try
                    {
                        for (int i = 0; i < vars.Count; i++)
                            vars[i].obj.SetPropertyValue(vars[i].prop, inputs[i], su);
                        var errors = Solve(fs, flowsheet_id, timeout_s);
                        if (errors.Count > 0) error = errors[0].Message;
                    }
                    catch (Exception ex) { error = (ex.InnerException ?? ex).Message; }
                    row["solved"] = error == null;
                    row["seconds"] = Math.Round(pointWatch.Elapsed.TotalSeconds, 3);
                    if (error != null) { row["error"] = error; failed++; }
                    else solved++;
                    row["outputs"] = new JArray(watchList.Select(w => Json.Num(Value(w.obj, w.prop))));
                    rows.Add(row);

                    if (error != null && stop_on_error) break;

                    // next grid point (last variable fastest)
                    point++;
                    for (int i = vars.Count - 1; i >= 0; i--)
                    {
                        idx[i]++;
                        if (idx[i] < vars[i].values.Length) break;
                        idx[i] = 0;
                    }
                }

                if (restore)
                {
                    for (int i = 0; i < vars.Count; i++)
                        if (!double.IsNaN(originals[i])) vars[i].obj.SetPropertyValue(vars[i].prop, originals[i], su);
                    try { Solve(fs, flowsheet_id, timeout_s); } catch { }
                }
                return null;
            });

            return new JObject
            {
                ["columns"] = columns,
                ["units"] = units,
                ["variables"] = vars.Count,
                ["watches"] = watchList.Count,
                ["points"] = rows.Count,
                ["grid_size"] = total,
                ["truncated"] = truncated,
                ["solved"] = solved,
                ["failed"] = failed,
                ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2),
                ["restored"] = restore,
                ["rows"] = rows,
            };
        }

        // ------------------------------------------------------------------ helpers

        private static readonly IUnitsOfMeasure SI = new DWSIM.SharedClasses.SystemsOfUnits.SI();

        private static JObject Choice(ISimulationObject obj, DWSIM.Automation.DynamicRunner.Scenarios.PropertyChoice c) => new JObject
        {
            ["property"] = c.Property, ["name"] = c.Name, ["unit"] = Unit(obj, c.Property), ["value"] = Json.Num(Value(obj, c.Property))
        };

        /// <summary>A property value in SI, or NaN when unreadable.</summary>
        private static double Value(ISimulationObject obj, string prop)
        {
            try { return Convert.ToDouble(obj.GetPropertyValue(prop, SI), System.Globalization.CultureInfo.InvariantCulture); }
            catch { return double.NaN; }
        }

        private static string Unit(ISimulationObject obj, string prop)
        {
            try { return obj.GetPropertyUnit(prop, SI) ?? ""; } catch { return ""; }
        }

        /// <summary>A writable property id of the object (case-insensitive), or an error listing them.</summary>
        private static string ResolveSpec(FluentFlowsheet fs, ISimulationObject obj, string property)
        {
            if (string.IsNullOrWhiteSpace(property)) throw new ArgumentException($"{obj.GraphicObject.Tag}: name the property.");
            var specs = LiveSliderStudy.Specifications(fs.Inner, obj);
            var hit = specs.FirstOrDefault(s => string.Equals(s.Property, property, StringComparison.OrdinalIgnoreCase))
                   ?? specs.FirstOrDefault(s => string.Equals(s.Name, property, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit.Property;
            var wr = (obj.GetProperties(PropertyType.WR) ?? Array.Empty<string>()).Concat(obj.GetProperties(PropertyType.RW) ?? Array.Empty<string>()).ToList();
            var any = wr.FirstOrDefault(p => string.Equals(p, property, StringComparison.OrdinalIgnoreCase));
            if (any != null) return any;
            throw new ArgumentException($"'{property}' is not a specification of '{obj.GraphicObject.Tag}'. Its specifications: {string.Join(", ", specs.Select(s => $"{s.Property} ({s.Name})"))}.");
        }

        private static string ResolveAny(ISimulationObject obj, string property)
        {
            if (string.IsNullOrWhiteSpace(property)) throw new ArgumentException($"{obj.GraphicObject.Tag}: name the property.");
            var all = obj.GetProperties(PropertyType.ALL) ?? Array.Empty<string>();
            var hit = all.FirstOrDefault(p => string.Equals(p, property, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
            throw new ArgumentException($"'{property}' is not a property of '{obj.GraphicObject.Tag}'. See dwsim_study_list_variables or dwsim_object_list_properties.");
        }

        /// <summary>The same solve path dwsim_solve_run uses, so results match.</summary>
        private List<Exception> Solve(FluentFlowsheet fs, string flowsheetId, int timeoutSeconds)
        {
            var mcpFs = _sessions.GetMcpFlowsheet(flowsheetId);
            if (mcpFs != null)
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))))
                    return mcpFs.SolveFlowsheet(cts.Token, timeoutSeconds);
            }
            return fs.TrySolve().ToList();
        }
    }
}
