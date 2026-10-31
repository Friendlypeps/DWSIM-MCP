// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums;
using DWSIM.Interfaces.Enums.GraphicObjects;
using DWSIM.MCPServer.Sessions;
using DWSIM.MCPServer.Tools.Common;
using DWSIM.UnitOperations.SpecialOps;
using FluentFlowsheet = DWSIM.Automation.FluentAPI.Flowsheet;

namespace DWSIM.MCPServer.Tools.Logical
{
    /// <summary>
    /// Logical blocks. The upstream server can add an Adjust or a Spec but cannot link them to the
    /// variables they read and write; these tools create (or reconfigure) them fully wired, and
    /// configure a Recycle's convergence.
    /// </summary>
    public class LogicalTools
    {
        private readonly SessionManager _sessions;

        public LogicalTools(SessionManager sessions) { _sessions = sessions; }

        [McpTool("dwsim_adjust_create",
            "Create an Adjust block (or reconfigure one that exists with this tag): the flowsheet varies the " +
            "manipulated specification until the controlled variable meets the target, so a design question " +
            "('what reactor temperature gives 67 % conversion', 'what reflux gives 99.5 % purity', 'what feed " +
            "makes 95 kg/h of product') becomes one solve instead of a sweep. manipulated and controlled are " +
            "{object, property} with a PROP_* id from dwsim_object_list_properties (manipulated must be a " +
            "specification, controlled may be any result). target_value is in target_units (empty = SI). " +
            "Headless, only simultaneous adjusts run (the default here), inside the flowsheet solve, with a " +
            "fixed cap of 25 Newton iterations: give min/max bounds and a reachable target, or the solve " +
            "reports the adjust as not converged.")]
        public JObject AdjustCreate(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Tag of the Adjust block")] string name,
            [McpParam("Manipulated variable as {object, property}", JsonType = "object")] JObject manipulated,
            [McpParam("Controlled variable as {object, property}", JsonType = "object")] JObject controlled,
            [McpParam("Target value of the controlled variable", JsonType = "number")] double target_value,
            [McpParam("Units of target_value (default: SI)", Required = false)] string target_units = null,
            [McpParam("Tolerance on the controlled variable, in its own units (default 1e-4)", Required = false, JsonType = "number")] double? tolerance = null,
            [McpParam("Reference variable as {object, property}: the target is then an offset from it", Required = false, JsonType = "object")] JObject reference = null,
            [McpParam("Solve together with the flowsheet (default true; false needs the GUI)", Required = false, JsonType = "boolean")] bool simultaneous = true,
            [McpParam("Lower bound of the manipulated variable, SI", Required = false, JsonType = "number")] double? min = null,
            [McpParam("Upper bound of the manipulated variable, SI", Required = false, JsonType = "number")] double? max = null,
            [McpParam("Initial estimate of the manipulated variable, SI", Required = false, JsonType = "number")] double? initial_estimate = null,
            [McpParam("Step size for the search", Required = false, JsonType = "number")] double? step = null,
            [McpParam("Maximum iterations (default 10; the simultaneous solver uses its own cap of 25)", Required = false, JsonType = "integer")] int? max_iterations = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the Adjust a tag.");
            if (manipulated == null || controlled == null) throw new ArgumentException("manipulated and controlled are required.");

            var existing = fs.Inner.SimulationObjects.Values.FirstOrDefault(o => o.GraphicObject?.Tag == name);
            Adjust adj;
            if (existing == null)
            {
                fs.AddAdjust(name);
                adj = Find.As<Adjust>(fs, name, "Adjust block");
            }
            else adj = existing as Adjust ?? throw new ArgumentException($"'{name}' exists and is a {existing.GraphicObject.ObjectType}, not an Adjust.");

            var mvInfo = Links.Describe(fs, manipulated.Value<string>("object"), manipulated.Value<string>("property"), mustBeWritable: true, out var mvObj);
            var cvInfo = Links.Describe(fs, controlled.Value<string>("object"), controlled.Value<string>("property"), mustBeWritable: false, out var cvObj);
            if (ReferenceEquals(mvObj, cvObj) && mvInfo.PropertyName == cvInfo.PropertyName)
                throw new ArgumentException("The manipulated and controlled variables are the same.");
            Links.AttachAdjust(adj, mvObj, AdjustVarType.Manipulated, mvInfo);
            Links.AttachAdjust(adj, cvObj, AdjustVarType.Controlled, cvInfo);
            if (reference != null)
            {
                var rvInfo = Links.Describe(fs, reference.Value<string>("object"), reference.Value<string>("property"), mustBeWritable: false, out var rvObj);
                Links.AttachAdjust(adj, rvObj, AdjustVarType.Reference, rvInfo);
            }
            else adj.Referenced = false;

            adj.AdjustValue = Units.ToSI(target_units, target_value);
            if (tolerance.HasValue)
            {
                if (tolerance.Value <= 0) throw new ArgumentException("tolerance must be positive.");
                adj.Tolerance = tolerance.Value;
            }
            adj.SimultaneousAdjust = simultaneous;
            if (min.HasValue && max.HasValue && min > max) throw new ArgumentException("min must not exceed max.");
            adj.MinVal = min;
            adj.MaxVal = max;
            adj.InitialEstimate = initial_estimate;
            if (step.HasValue) adj.StepSize = step.Value;
            if (max_iterations.HasValue) adj.MaximumIterations = Math.Max(1, max_iterations.Value);
            fs.Inner.ResetCalculationStatus();

            var o = DescribeAdjust(fs, adj);
            o["created"] = existing == null;
            return o;
        }

        [McpTool("dwsim_adjust_get",
            "The state of an Adjust block: its links, target, tolerance, the current values of the controlled and " +
            "manipulated variables, and whether the last solve met the target.")]
        public JObject AdjustGet(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Adjust tag")] string name)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            return DescribeAdjust(fs, Find.As<Adjust>(fs, name, "Adjust block"));
        }

        [McpTool("dwsim_spec_create",
            "Create a Spec block (or reconfigure one with this tag): after the source object solves, the target " +
            "specification is set to an expression of the source variable, e.g. a cooler outlet temperature that " +
            "follows a reactor temperature ('X - 10'), or a second feed that tracks the first ('2*X'). source and " +
            "target are {object, property} (target must be a specification). The expression uses X for the " +
            "source value in its flowsheet units. calc_mode: GlobalSetting (default), AfterSourceObject, " +
            "BeforeTargetObject, BeforeFlowsheet, AfterFlowsheet.")]
        public JObject SpecCreate(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Tag of the Spec block")] string name,
            [McpParam("Source variable as {object, property}", JsonType = "object")] JObject source,
            [McpParam("Target specification as {object, property}", JsonType = "object")] JObject target,
            [McpParam("Expression in X (the source value), e.g. 'X - 10' or '2*X'")] string expression,
            [McpParam("Lower limit of the target value", Required = false, JsonType = "number")] double? min = null,
            [McpParam("Upper limit of the target value", Required = false, JsonType = "number")] double? max = null,
            [McpParam("GlobalSetting, AfterSourceObject, BeforeTargetObject, BeforeFlowsheet or AfterFlowsheet", Required = false)] string calc_mode = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the Spec a tag.");
            if (source == null || target == null) throw new ArgumentException("source and target are required.");
            if (string.IsNullOrWhiteSpace(expression)) throw new ArgumentException("expression is required (use X for the source value).");

            var existing = fs.Inner.SimulationObjects.Values.FirstOrDefault(o => o.GraphicObject?.Tag == name);
            Spec spec;
            if (existing == null)
            {
                fs.AddSpec(name);
                spec = Find.As<Spec>(fs, name, "Spec block");
            }
            else spec = existing as Spec ?? throw new ArgumentException($"'{name}' exists and is a {existing.GraphicObject.ObjectType}, not a Spec.");

            var svInfo = Links.Describe(fs, source.Value<string>("object"), source.Value<string>("property"), mustBeWritable: false, out var svObj);
            var tvInfo = Links.Describe(fs, target.Value<string>("object"), target.Value<string>("property"), mustBeWritable: true, out var tvObj);
            Links.AttachSpec(spec, svObj, SpecVarType.Source, svInfo);
            Links.AttachSpec(spec, tvObj, SpecVarType.Target, tvInfo);
            spec.Expression = expression;
            if (min.HasValue && max.HasValue && min > max) throw new ArgumentException("min must not exceed max.");
            spec.MinVal = min;
            spec.MaxVal = max;
            if (!string.IsNullOrWhiteSpace(calc_mode))
            {
                var mode = Json.Enum<SpecCalcMode2>(calc_mode, "spec calculation mode");
                spec.SpecCalculationMode = mode;
            }
            fs.Inner.ResetCalculationStatus();

            return new JObject
            {
                ["spec"] = name,
                ["created"] = existing == null,
                ["source"] = Info(spec.SourceObjectData),
                ["target"] = Info(spec.TargetObjectData),
                ["expression"] = spec.Expression,
                ["min"] = Json.Num(spec.MinVal), ["max"] = Json.Num(spec.MaxVal),
                ["calc_mode"] = spec.SpecCalculationMode.ToString(),
                ["source_value"] = Json.Num(Links.ValueOf(fs, spec.SourceObjectData)),
                ["target_value"] = Json.Num(Links.ValueOf(fs, spec.TargetObjectData)),
            };
        }

        [McpTool("dwsim_recycle_configure",
            "Configure a Recycle block (dwsim_unitop_add Recycle, on the torn stream of a loop): maximum iterations, " +
            "the acceleration method, and the convergence tolerances on temperature (K), pressure (Pa), mass flow " +
            "(kg/s), enthalpy, entropy, vapour fraction and composition. Reports the last convergence state. " +
            "A column inside a loop converges better with a loose mass-flow tolerance first, tightened after.")]
        public JObject RecycleConfigure(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Recycle tag")] string name,
            [McpParam("Maximum iterations", Required = false, JsonType = "integer")] int? max_iterations = null,
            [McpParam("Acceleration method by name (see the response's 'acceleration_methods')", Required = false)] string acceleration = null,
            [McpParam("Tolerances as {temperature, pressure, mass_flow, enthalpy, entropy, vapor_fraction, composition}", Required = false, JsonType = "object")] JObject tolerances = null,
            [McpParam("Smoothing factor (0-1; 1 = none)", Required = false, JsonType = "number")] double? smoothing_factor = null,
            [McpParam("Legacy convergence mode", Required = false, JsonType = "boolean")] bool? legacy_mode = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var rec = Find.As<Recycle>(fs, name, "Recycle block");
            var changed = new JArray();
            if (max_iterations.HasValue) { if (max_iterations < 1) throw new ArgumentException("max_iterations must be positive."); rec.MaximumIterations = max_iterations.Value; changed.Add("max_iterations"); }
            if (!string.IsNullOrWhiteSpace(acceleration)) { rec.AccelerationMethod = Json.Enum<AccelMethod>(acceleration, "acceleration method"); changed.Add("acceleration"); }
            if (smoothing_factor.HasValue) { if (smoothing_factor <= 0 || smoothing_factor > 1) throw new ArgumentException("smoothing_factor is in (0, 1]."); rec.SmoothingFactor = smoothing_factor.Value; changed.Add("smoothing_factor"); }
            if (legacy_mode.HasValue) { rec.LegacyMode = legacy_mode.Value; changed.Add("legacy_mode"); }
            if (tolerances != null)
            {
                var cp = rec.ConvergenceParameters;
                foreach (var p in tolerances.Properties())
                {
                    var v = p.Value.Value<double>();
                    if (v <= 0) throw new ArgumentException($"tolerance '{p.Name}' must be positive.");
                    switch (p.Name.ToLowerInvariant())
                    {
                        case "temperature": cp.Temperatura = v; break;
                        case "pressure": cp.Pressao = v; break;
                        case "mass_flow": case "massflow": cp.VazaoMassica = v; break;
                        case "enthalpy": cp.Entalpia = v; break;
                        case "entropy": cp.Entropia = v; break;
                        case "vapor_fraction": case "vapour_fraction": cp.FracaoVapor = v; break;
                        case "composition": cp.Composicao = v; break;
                        default: throw new ArgumentException($"'{p.Name}' is not a tolerance. Use temperature, pressure, mass_flow, enthalpy, entropy, vapor_fraction, composition.");
                    }
                    changed.Add("tolerance." + p.Name);
                }
            }
            if (changed.Count > 0) fs.Inner.ResetCalculationStatus();

            var c = rec.ConvergenceParameters;
            return new JObject
            {
                ["recycle"] = name,
                ["changed"] = changed,
                ["max_iterations"] = rec.MaximumIterations,
                ["acceleration"] = rec.AccelerationMethod.ToString(),
                ["acceleration_methods"] = Json.Strings(Enum.GetNames(typeof(AccelMethod))),
                ["smoothing_factor"] = rec.SmoothingFactor,
                ["legacy_mode"] = rec.LegacyMode,
                ["tolerances"] = new JObject
                {
                    ["temperature"] = c.Temperatura, ["pressure"] = c.Pressao, ["mass_flow"] = c.VazaoMassica,
                    ["enthalpy"] = c.Entalpia, ["entropy"] = c.Entropia, ["vapor_fraction"] = c.FracaoVapor, ["composition"] = c.Composicao
                },
                ["converged"] = rec.Converged,
                ["iterations"] = rec.IterationCount,
                ["ports"] = Json.Strings(rec.GetConnectionPortsList()),
            };
        }

        // ------------------------------------------------------------------ helpers

        private static JObject Info(ISpecialOpObjectInfo i) => i == null || string.IsNullOrEmpty(i.ID) ? null : new JObject
        {
            ["object"] = i.Name, ["property"] = i.PropertyName, ["units"] = i.Units, ["type"] = i.Type
        };

        private static JObject DescribeAdjust(FluentFlowsheet fs, Adjust adj)
        {
            double cv = Links.ValueOf(fs, adj.ControlledObjectData);
            double target = adj.AdjustValue;
            if (adj.Referenced) target += Links.ValueOf(fs, adj.ReferencedObjectData);
            bool? met = double.IsNaN(cv) ? (bool?)null : Math.Abs(cv - target) <= Math.Max(adj.Tolerance, 1e-12);
            return new JObject
            {
                ["adjust"] = adj.GraphicObject.Tag,
                ["manipulated"] = Info(adj.ManipulatedObjectData),
                ["controlled"] = Info(adj.ControlledObjectData),
                ["reference"] = Info(adj.ReferencedObjectData),
                ["target_value_SI"] = Json.Num(adj.AdjustValue),
                ["tolerance"] = adj.Tolerance,
                ["simultaneous"] = adj.SimultaneousAdjust,
                ["min"] = Json.Num(adj.MinVal), ["max"] = Json.Num(adj.MaxVal), ["initial_estimate"] = Json.Num(adj.InitialEstimate),
                ["step_size"] = adj.StepSize, ["max_iterations"] = adj.MaximumIterations,
                ["manipulated_value_SI"] = Json.Num(Links.ValueOf(fs, adj.ManipulatedObjectData)),
                ["controlled_value_SI"] = Json.Num(cv),
                ["target_met"] = met,
                ["status"] = adj.Status,
                ["calculated"] = adj.Calculated,
            };
        }
    }
}
