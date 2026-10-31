// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums.GraphicObjects;
using DWSIM.MCPServer.Sessions;
using DWSIM.MCPServer.Tools.Common;
using DWSIM.UnitOperations.UnitOperations;
using DWSIM.UnitOperations.UnitOperations.Auxiliary.SepOps;
using FluentFlowsheet = DWSIM.Automation.FluentAPI.Flowsheet;

namespace DWSIM.MCPServer.Tools.Columns
{
    /// <summary>
    /// Column tools. The upstream server can add a column but not configure it: the shortcut column's
    /// keys are public fields its reflection misses, and a rigorous column wired with the generic
    /// connect tool never learns which stage a feed enters, so it fails validation. These tools call
    /// the column's own API (ConnectFeed with a stage, SetCondenserSpec, SetNumberOfStages ...).
    /// </summary>
    public class ColumnTools
    {
        private readonly SessionManager _sessions;

        public ColumnTools(SessionManager sessions) { _sessions = sessions; }

        // ------------------------------------------------------------------ shortcut column

        [McpTool("dwsim_column_shortcut_configure",
            "Configure a ShortcutColumn (Fenske-Underwood-Gilliland) added with dwsim_unitop_add: the light and heavy " +
            "key compounds, the mole fraction of the light key allowed in the bottoms and of the heavy key in the " +
            "distillate (the split), the reflux ratio (must exceed the minimum the column computes), the condenser " +
            "and reboiler pressures in Pa, and the condenser type (TotalCond or PartialCond). Connect the streams " +
            "with dwsim_unitop_connect: feed_port 0, distillate product_port 0, bottoms product_port 1, condenser " +
            "duty energy_product 0, reboiler duty energy_feed 0. Results after a solve: stages, minimum reflux, " +
            "duties, via dwsim_column_get.")]
        public JObject ShortcutConfigure(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Shortcut column tag")] string name,
            [McpParam("Light key compound", Required = false)] string light_key = null,
            [McpParam("Heavy key compound", Required = false)] string heavy_key = null,
            [McpParam("Mole fraction of the light key in the bottoms (default 0.01)", Required = false, JsonType = "number")] double? light_key_in_bottoms = null,
            [McpParam("Mole fraction of the heavy key in the distillate (default 0.01)", Required = false, JsonType = "number")] double? heavy_key_in_distillate = null,
            [McpParam("Reflux ratio L/D (default 1.5)", Required = false, JsonType = "number")] double? reflux_ratio = null,
            [McpParam("Condenser pressure, Pa", Required = false, JsonType = "number")] double? condenser_pressure_Pa = null,
            [McpParam("Reboiler pressure, Pa", Required = false, JsonType = "number")] double? reboiler_pressure_Pa = null,
            [McpParam("TotalCond (liquid distillate) or PartialCond (vapour distillate)", Required = false)] string condenser_type = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var col = Find.As<ShortcutColumn>(fs, name, "shortcut column");
            var changed = new JArray();

            if (light_key != null) { col.m_lightkey = Find.Compound(fs, light_key).Name; changed.Add("light_key"); }
            if (heavy_key != null) { col.m_heavykey = Find.Compound(fs, heavy_key).Name; changed.Add("heavy_key"); }
            if (!string.IsNullOrEmpty(col.m_lightkey) && col.m_lightkey == col.m_heavykey)
                throw new ArgumentException("The light and heavy keys must be different compounds.");
            if (light_key_in_bottoms.HasValue) { col.m_lightkeymolarfrac = Fraction(light_key_in_bottoms.Value, "light_key_in_bottoms"); changed.Add("light_key_in_bottoms"); }
            if (heavy_key_in_distillate.HasValue) { col.m_heavykeymolarfrac = Fraction(heavy_key_in_distillate.Value, "heavy_key_in_distillate"); changed.Add("heavy_key_in_distillate"); }
            if (reflux_ratio.HasValue)
            {
                if (reflux_ratio.Value <= 0) throw new ArgumentException("reflux_ratio must be positive.");
                col.m_refluxratio = reflux_ratio.Value; changed.Add("reflux_ratio");
            }
            if (condenser_pressure_Pa.HasValue) { col.m_condenserpressure = Pressure(condenser_pressure_Pa.Value, "condenser_pressure_Pa"); changed.Add("condenser_pressure_Pa"); }
            if (reboiler_pressure_Pa.HasValue) { col.m_boilerpressure = Pressure(reboiler_pressure_Pa.Value, "reboiler_pressure_Pa"); changed.Add("reboiler_pressure_Pa"); }
            if (condenser_type != null) { col.condtype = Json.Enum<ShortcutColumn.CondenserType>(condenser_type, "condenser type"); changed.Add("condenser_type"); }
            if (col.m_boilerpressure < col.m_condenserpressure)
                throw new ArgumentException("The reboiler pressure must be at or above the condenser pressure.");

            fs.Inner.ResetCalculationStatus();
            var o = DescribeShortcut(fs, col);
            o["changed"] = changed;
            return o;
        }

        // ------------------------------------------------------------------ rigorous columns

        [McpTool("dwsim_column_create",
            "Create a rigorous stage-by-stage column and set it up in one call: type Distillation (condenser + " +
            "reboiler), Absorption (no condenser, no reboiler; gas up, liquid down), Extractor (liquid-liquid, " +
            "needs a VLLE flash), ReboiledAbsorber (no condenser) or RefluxedAbsorber (no reboiler); the number of " +
            "stages (> 3, counting condenser and reboiler as stages 1 and N); the condenser type Total_Condenser, " +
            "Partial_Condenser or Full_Reflux; the top pressure and the pressure drop over the column, in Pa. Then " +
            "dwsim_column_connect wires feeds to stages and products, and dwsim_column_set_specs sets the two " +
            "specifications a distillation column needs. REACTIVE DISTILLATION IS NOT AVAILABLE: DWSIM's rigorous " +
            "column has no reactive-stage model, so a request with a reaction set is refused; model it as a reactor " +
            "followed by a column, with a Recycle if the loop matters, and say so in the report.")]
        public JObject Create(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Tag for the new column")] string name,
            [McpParam("Distillation, Absorption, Extractor, ReboiledAbsorber or RefluxedAbsorber (default Distillation)", Required = false)] string type = "Distillation",
            [McpParam("Number of stages including condenser and reboiler (default 10, minimum 4)", Required = false, JsonType = "integer")] int stages = 10,
            [McpParam("Total_Condenser (default), Partial_Condenser or Full_Reflux; distillation only", Required = false)] string condenser_type = "Total_Condenser",
            [McpParam("Pressure of the top stage, Pa (default 101325)", Required = false, JsonType = "number")] double top_pressure_Pa = 101325,
            [McpParam("Pressure drop over the column, Pa (default 0)", Required = false, JsonType = "number")] double pressure_drop_Pa = 0,
            [McpParam("Solver: 'Wang-Henke (Bubble Point)' (default), 'Burningham-Otto (Sum Rates)' (absorbers), 'Naphtali-Sandholm (Simultaneous Correction)'", Required = false)] string solving_method = null,
            [McpParam("Maximum solver iterations (default 100)", Required = false, JsonType = "integer")] int? max_iterations = null,
            [McpParam("NOT SUPPORTED: a reaction set for reactive distillation. Any value is refused.", Required = false)] string reaction_set = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the column a tag.");
            if (!string.IsNullOrWhiteSpace(reaction_set) || (type ?? "").IndexOf("reactive", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new ArgumentException(
                    "Reactive distillation is not available: DWSIM 10's rigorous column (RigorousColumn.vb) has no reaction " +
                    "hooks on its stages, and no property package supplies reactive equilibrium. Model the step as a reactor " +
                    "(EquilibriumReactor, CSTR or PFR with the reaction set) followed by a DistillationColumn, closing the " +
                    "loop with a Recycle where the reflux returns to the reactor, and report the surrogate as such.");
            if (fs.Inner.SimulationObjects.Values.Any(o => o.GraphicObject?.Tag == name))
                throw new ArgumentException($"An object tagged '{name}' already exists.");
            if (stages < 4) throw new ArgumentException("A rigorous column needs at least 4 stages (condenser, two trays, reboiler).");
            Pressure(top_pressure_Pa, "top_pressure_Pa");
            if (pressure_drop_Pa < 0) throw new ArgumentException("pressure_drop_Pa cannot be negative.");

            var kind = (type ?? "Distillation").Trim().Replace(" ", "").ToLowerInvariant();
            Column col;
            switch (kind)
            {
                case "distillation":
                case "distillationcolumn":
                case "reboiledabsorber":
                case "refluxedabsorber":
                    fs.AddDistillationColumn(name);
                    var dc = Find.As<DistillationColumn>(fs, name, "distillation column");
                    dc.ReboiledAbsorber = kind == "reboiledabsorber";
                    dc.RefluxedAbsorber = kind == "refluxedabsorber";
                    dc.CondenserType = Json.Enum<Column.condtype>(condenser_type ?? "Total_Condenser", "condenser type");
                    col = dc;
                    break;
                case "absorption":
                case "absorber":
                case "absorptioncolumn":
                case "stripper":
                case "stripping":
                    fs.AddAbsorptionColumn(name);
                    var ac = Find.As<AbsorptionColumn>(fs, name, "absorption column");
                    ac.OperationMode = AbsorptionColumn.OpMode.Absorber;
                    col = ac;
                    break;
                case "extractor":
                case "extraction":
                case "liquidliquidextractor":
                    fs.AddAbsorptionColumn(name);
                    var ex = Find.As<AbsorptionColumn>(fs, name, "absorption column");
                    ex.OperationMode = AbsorptionColumn.OpMode.Extractor;
                    col = ex;
                    break;
                default:
                    throw new ArgumentException($"'{type}' is not a column type. Use Distillation, Absorption, Extractor, ReboiledAbsorber or RefluxedAbsorber.");
            }

            if (col.Stages.Count != stages) col.SetNumberOfStages(stages);
            col.ColumnPressureDrop = pressure_drop_Pa;
            col.SetTopStagePressure(top_pressure_Pa);
            if (!string.IsNullOrWhiteSpace(solving_method)) col.SolvingMethodName = SolverName(solving_method);
            if (max_iterations.HasValue)
            {
                if (max_iterations.Value < 1) throw new ArgumentException("max_iterations must be positive.");
                col.MaxIterations = max_iterations.Value;
            }

            var o = DescribeRigorous(fs, col);
            o["next"] = "dwsim_column_connect to attach feeds (with their stage), products and duties; then dwsim_column_set_specs (distillation).";
            return o;
        }

        [McpTool("dwsim_column_connect",
            "Attach streams to a rigorous column through its own connectors, which record the stage of each feed " +
            "(the generic dwsim_unitop_connect only draws the line and the column then fails validation). feeds is " +
            "[{stream, stage}] with the stage as the editor numbers it, 1 = top (condenser) to N = bottom (reboiler), " +
            "or 'Stage7', 'top', 'bottom'. Distillation: distillate (liquid top product), bottoms, optional " +
            "vapor_product (partial condenser), condenser_duty and reboiler_duty energy streams (create them with " +
            "dwsim_stream_add_energy). Absorber/extractor: top_product and bottoms. side_draws is [{stream, stage, " +
            "vapor, molar_flow_mol_s}]. Streams must exist and be unattached.")]
        public JObject Connect(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Column tag")] string name,
            [McpParam("Feeds as [{stream, stage}]", Required = false, JsonType = "array")] JArray feeds = null,
            [McpParam("Distillate (liquid top product) stream tag", Required = false)] string distillate = null,
            [McpParam("Bottoms stream tag", Required = false)] string bottoms = null,
            [McpParam("Overhead vapour product stream tag (partial condenser)", Required = false)] string vapor_product = null,
            [McpParam("Top product stream tag (absorber / extractor)", Required = false)] string top_product = null,
            [McpParam("Condenser duty energy stream tag", Required = false)] string condenser_duty = null,
            [McpParam("Reboiler duty energy stream tag", Required = false)] string reboiler_duty = null,
            [McpParam("Side draws as [{stream, stage, vapor (default false), molar_flow_mol_s}]", Required = false, JsonType = "array")] JArray side_draws = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var col = Find.As<Column>(fs, name, "rigorous column");
            var dc = col as DistillationColumn;
            var ac = col as AbsorptionColumn;
            var done = new JArray();

            if (feeds != null)
            {
                foreach (var tok in feeds)
                {
                    var f = tok as JObject ?? throw new ArgumentException("Each feed is {stream, stage}.");
                    var stream = Material(fs, f.Value<string>("stream"), "feed");
                    Unattached(stream, intoColumn: true);
                    int idx = StageIndex(col, f["stage"]);
                    if (dc != null) dc.ConnectFeed(stream, idx); else ac.ConnectFeed(stream, idx);
                    done.Add($"feed {stream.GraphicObject.Tag} -> {col.Stages[idx].Name ?? $"Stage{idx + 1}"}");
                }
            }
            if (distillate != null)
            {
                if (dc == null) throw new ArgumentException("An absorber has no distillate; use top_product.");
                if (dc.CondenserType == Column.condtype.Full_Reflux) throw new ArgumentException("A Full_Reflux condenser has no liquid distillate; use vapor_product.");
                var s = Material(fs, distillate, "distillate"); Unattached(s, intoColumn: false);
                dc.ConnectDistillate(s); done.Add($"distillate <- {distillate}");
            }
            if (vapor_product != null)
            {
                if (dc == null) throw new ArgumentException("An absorber has no vapour product; use top_product.");
                if (dc.CondenserType == Column.condtype.Total_Condenser) throw new ArgumentException("A Total_Condenser has no vapour product; use Partial_Condenser or Full_Reflux.");
                var s = Material(fs, vapor_product, "vapor product"); Unattached(s, intoColumn: false);
                dc.ConnectVaporProduct(s); done.Add($"vapor_product <- {vapor_product}");
            }
            if (top_product != null)
            {
                if (ac == null) throw new ArgumentException("A distillation column's top product is distillate (or vapor_product).");
                var s = Material(fs, top_product, "top product"); Unattached(s, intoColumn: false);
                ac.ConnectTopProduct(s); done.Add($"top_product <- {top_product}");
            }
            if (bottoms != null)
            {
                var s = Material(fs, bottoms, "bottoms"); Unattached(s, intoColumn: false);
                if (dc != null) dc.ConnectBottoms(s); else ac.ConnectBottoms(s);
                done.Add($"bottoms <- {bottoms}");
            }
            if (condenser_duty != null)
            {
                if (dc == null || dc.ReboiledAbsorber) throw new ArgumentException("This column has no condenser.");
                var e = Energy(fs, condenser_duty); Unattached(e, intoColumn: false);
                dc.ConnectCondenserDuty(e); done.Add($"condenser_duty <- {condenser_duty}");
            }
            if (reboiler_duty != null)
            {
                if (dc == null || dc.RefluxedAbsorber) throw new ArgumentException("This column has no reboiler.");
                var e = Energy(fs, reboiler_duty); Unattached(e, intoColumn: true);
                dc.ConnectReboilerDuty(e); done.Add($"reboiler_duty -> {reboiler_duty}");
            }
            if (side_draws != null)
            {
                if (dc == null) throw new ArgumentException("Side draws are supported on distillation columns.");
                foreach (var tok in side_draws)
                {
                    var d = tok as JObject ?? throw new ArgumentException("Each side draw is {stream, stage, vapor, molar_flow_mol_s}.");
                    var s = Material(fs, d.Value<string>("stream"), "side draw"); Unattached(s, intoColumn: false);
                    int idx = StageIndex(col, d["stage"]);
                    bool vapor = d.Value<bool?>("vapor") ?? false;
                    double flow = d.Value<double?>("molar_flow_mol_s") ?? throw new ArgumentException($"Side draw {s.GraphicObject.Tag}: molar_flow_mol_s is required.");
                    if (flow <= 0) throw new ArgumentException($"Side draw {s.GraphicObject.Tag}: molar_flow_mol_s must be positive.");
                    dc.ConnectSideDraw(s, idx, vapor, flow);
                    done.Add($"side draw {s.GraphicObject.Tag} <- {col.Stages[idx].Name ?? $"Stage{idx + 1}"} ({(vapor ? "vapour" : "liquid")}, {flow} mol/s)");
                }
            }
            if (done.Count == 0) throw new ArgumentException("Nothing to connect: give feeds, products or duties.");

            fs.Inner.ResetCalculationStatus();
            var o = DescribeRigorous(fs, col);
            o["connected"] = done;
            return o;
        }

        [McpTool("dwsim_column_set_specs",
            "Set the two specifications of a distillation column, one at the condenser and one at the reboiler. " +
            "Each is {type, value, units, compound}. Types: Reflux_Ratio (condenser; value = L/D), Boilup_Ratio " +
            "(reboiler), Product_Molar_Flow_Rate (units mol/s, kmol/h ...), Product_Mass_Flow_Rate (kg/s, kg/h), " +
            "Component_Molar_Flow_Rate / Component_Mass_Flow_Rate (with compound), Component_Fraction (purity; " +
            "value 0-1, units 'Molar' or 'Mass', with compound), Component_Recovery (percent of the feed's compound " +
            "in that product, with compound), Heat_Duty (kW, W, kJ/h ...), Temperature (K, C), Feed_Recovery. " +
            "Reflux ratio at the condenser and a product flow or a purity at the reboiler is the robust pair; two " +
            "purities are the hardest. Specs are checked here because the engine silently ignores a misspelt type.")]
        public JObject SetSpecs(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Distillation column tag")] string name,
            [McpParam("Condenser spec as {type, value, units, compound}", Required = false, JsonType = "object")] JObject condenser = null,
            [McpParam("Reboiler spec as {type, value, units, compound}", Required = false, JsonType = "object")] JObject reboiler = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var dc = Find.As<DistillationColumn>(fs, name, "distillation column");
            if (condenser == null && reboiler == null) throw new ArgumentException("Give a condenser and/or a reboiler spec.");

            if (condenser != null)
            {
                var (t, v, u, c) = ParseSpec(fs, condenser, "condenser", allowReflux: true);
                dc.SetCondenserSpec(t, v, u, c);
            }
            if (reboiler != null)
            {
                var (t, v, u, c) = ParseSpec(fs, reboiler, "reboiler", allowReflux: false);
                dc.SetReboilerSpec(t, v, u, c);
            }
            fs.Inner.ResetCalculationStatus();
            var o = DescribeRigorous(fs, dc);
            return o;
        }

        [McpTool("dwsim_column_set_estimates",
            "Give a rigorous column initial estimates, one value per stage from the top: temperatures (K), liquid " +
            "and vapour molar flows leaving each stage (mol/s). Each list given is also switched on; use_* flags " +
            "switch the engine's use of the stored estimates without changing them. auto_update keeps the last " +
            "solution as the next estimate. Estimates are how a column that fails to converge is usually rescued.")]
        public JObject SetEstimates(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Column tag")] string name,
            [McpParam("Stage temperatures, K, top to bottom", Required = false, JsonType = "array")] double[] temperatures_K = null,
            [McpParam("Liquid molar flows leaving each stage, mol/s", Required = false, JsonType = "array")] double[] liquid_flows_mol_s = null,
            [McpParam("Vapour molar flows leaving each stage, mol/s", Required = false, JsonType = "array")] double[] vapor_flows_mol_s = null,
            [McpParam("Use the stored temperature estimates", Required = false, JsonType = "boolean")] bool? use_temperatures = null,
            [McpParam("Use the stored liquid flow estimates", Required = false, JsonType = "boolean")] bool? use_liquid_flows = null,
            [McpParam("Use the stored vapour flow estimates", Required = false, JsonType = "boolean")] bool? use_vapor_flows = null,
            [McpParam("Use the stored composition estimates", Required = false, JsonType = "boolean")] bool? use_compositions = null,
            [McpParam("Keep the last solution as the next estimate", Required = false, JsonType = "boolean")] bool? auto_update = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var col = Find.As<Column>(fs, name, "rigorous column");
            int n = col.Stages.Count;
            var changed = new JArray();

            void Check(double[] a, string what)
            {
                if (a.Length != n) throw new ArgumentException($"{what} needs one value per stage ({n}), got {a.Length}.");
                if (a.Any(x => double.IsNaN(x) || x < 0)) throw new ArgumentException($"{what}: every value must be a non-negative number.");
            }
            if (temperatures_K != null) { Check(temperatures_K, "temperatures_K"); col.SetInitialTemperatureEstimates(temperatures_K); col.UseTemperatureEstimates = true; changed.Add("temperatures"); }
            if (liquid_flows_mol_s != null) { Check(liquid_flows_mol_s, "liquid_flows_mol_s"); col.SetInitialLiquidMolarFlowEstimates(liquid_flows_mol_s); col.UseLiquidFlowEstimates = true; changed.Add("liquid_flows"); }
            if (vapor_flows_mol_s != null) { Check(vapor_flows_mol_s, "vapor_flows_mol_s"); col.SetInitialVaporMolarFlowEstimates(vapor_flows_mol_s); col.UseVaporFlowEstimates = true; changed.Add("vapor_flows"); }
            if (use_temperatures.HasValue) { col.UseTemperatureEstimates = use_temperatures.Value; changed.Add("use_temperatures"); }
            if (use_liquid_flows.HasValue) { col.UseLiquidFlowEstimates = use_liquid_flows.Value; changed.Add("use_liquid_flows"); }
            if (use_vapor_flows.HasValue) { col.UseVaporFlowEstimates = use_vapor_flows.Value; changed.Add("use_vapor_flows"); }
            if (use_compositions.HasValue) { col.UseCompositionEstimates = use_compositions.Value; changed.Add("use_compositions"); }
            if (auto_update.HasValue) { col.AutoUpdateInitialEstimates = auto_update.Value; changed.Add("auto_update"); }
            if (changed.Count == 0) throw new ArgumentException("Nothing to set.");

            fs.Inner.ResetCalculationStatus();
            return new JObject
            {
                ["column"] = name,
                ["stages"] = n,
                ["changed"] = changed,
                ["use"] = new JObject
                {
                    ["temperatures"] = col.UseTemperatureEstimates, ["liquid_flows"] = col.UseLiquidFlowEstimates,
                    ["vapor_flows"] = col.UseVaporFlowEstimates, ["compositions"] = col.UseCompositionEstimates,
                    ["auto_update"] = col.AutoUpdateInitialEstimates
                }
            };
        }

        [McpTool("dwsim_column_get",
            "Describe a column (shortcut or rigorous): configuration, the streams on each stage, the specs, and after " +
            "a solve the stage profile (T, P, liquid and vapour flows), duties and convergence. Call it after " +
            "dwsim_solve_run, or before to check the wiring.")]
        public JObject Get(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Column tag")] string name)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var obj = Find.ByTagOrId(fs, name, "column");
            switch (obj)
            {
                case ShortcutColumn sc: return DescribeShortcut(fs, sc);
                case Column col: return DescribeRigorous(fs, col);
                default: throw new ArgumentException($"'{name}' is a {obj.GraphicObject?.ObjectType}, not a column.");
            }
        }

        // ------------------------------------------------------------------ helpers

        private static double Fraction(double v, string what)
        {
            if (v <= 0 || v >= 1) throw new ArgumentException($"{what} is a mole fraction strictly between 0 and 1.");
            return v;
        }

        private static double Pressure(double p, string what)
        {
            if (p <= 0 || double.IsNaN(p)) throw new ArgumentException($"{what} must be a positive pressure in Pa.");
            return p;
        }

        private static readonly string[] Solvers =
        {
            "Wang-Henke (Bubble Point)", "Burningham-Otto (Sum Rates)", "Naphtali-Sandholm (Simultaneous Correction)"
        };

        private static string SolverName(string s)
        {
            var hit = Solvers.FirstOrDefault(x => string.Equals(x, s.Trim(), StringComparison.OrdinalIgnoreCase))
                   ?? Solvers.FirstOrDefault(x => x.IndexOf(s.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
            return hit ?? throw new ArgumentException($"'{s}' is not a column solver. Use one of: {string.Join("; ", Solvers)}.");
        }

        private static ISimulationObject Material(FluentFlowsheet fs, string tag, string what)
        {
            var s = Find.ByTagOrId(fs, tag, $"{what} stream");
            if (!(s is IMaterialStream)) throw new ArgumentException($"'{tag}' is a {s.GraphicObject?.ObjectType}, not a material stream.");
            return s;
        }

        private static ISimulationObject Energy(FluentFlowsheet fs, string tag)
        {
            var s = Find.ByTagOrId(fs, tag, "energy stream");
            if (s.GraphicObject?.ObjectType != ObjectType.EnergyStream) throw new ArgumentException($"'{tag}' is a {s.GraphicObject?.ObjectType}, not an energy stream. Create one with dwsim_stream_add_energy.");
            return s;
        }

        /// <summary>A stream entering the column must have a free outlet; one leaving it a free inlet.</summary>
        private static void Unattached(ISimulationObject stream, bool intoColumn)
        {
            var g = stream.GraphicObject;
            var c = intoColumn ? g.OutputConnectors[0] : g.InputConnectors[0];
            if (c.IsAttached)
            {
                var other = intoColumn ? c.AttachedConnector?.AttachedTo?.Tag : c.AttachedConnector?.AttachedFrom?.Tag;
                throw new ArgumentException($"Stream '{g.Tag}' is already connected to '{other}'. Use another stream or dwsim_unitop_disconnect first.");
            }
        }

        /// <summary>The 0-based stage index from the editor's 1-based number, 'StageN', 'top' or 'bottom'.</summary>
        private static int StageIndex(Column col, JToken stage)
        {
            int n = col.Stages.Count;
            if (stage == null || stage.Type == JTokenType.Null) throw new ArgumentException("Each feed needs a stage (1 = top, N = bottom).");
            int idx;
            if (stage.Type == JTokenType.Integer || stage.Type == JTokenType.Float)
                idx = stage.Value<int>() - 1;
            else
            {
                var s = stage.ToString().Trim();
                if (s.Equals("top", StringComparison.OrdinalIgnoreCase) || s.Equals("condenser", StringComparison.OrdinalIgnoreCase)) idx = 0;
                else if (s.Equals("bottom", StringComparison.OrdinalIgnoreCase) || s.Equals("reboiler", StringComparison.OrdinalIgnoreCase)) idx = n - 1;
                else if (int.TryParse(s, out var k)) idx = k - 1;
                else if (s.StartsWith("stage", StringComparison.OrdinalIgnoreCase) && int.TryParse(s.Substring(5).Trim(), out k)) idx = k - 1;
                else idx = col.StageIndexOf(s);
            }
            if (idx < 0 || idx >= n) throw new ArgumentException($"Stage {stage} is outside the column: stages are 1 (top) to {n} (bottom).");
            return idx;
        }

        /// <summary>Spec type aliases, to the engine's enum names; the engine's own TryParse would swallow a typo.</summary>
        private static readonly Dictionary<string, string> SpecAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Reflux_Ratio"] = "Reflux_Ratio", ["Reflux Ratio"] = "Reflux_Ratio", ["RefluxRatio"] = "Reflux_Ratio", ["Reflux"] = "Reflux_Ratio", ["RR"] = "Reflux_Ratio",
            ["Boilup_Ratio"] = "Boilup_Ratio", ["Boilup Ratio"] = "Boilup_Ratio", ["BoilupRatio"] = "Boilup_Ratio", ["Boilup"] = "Boilup_Ratio", ["Boil-up Ratio"] = "Boilup_Ratio",
            ["Stream_Ratio"] = "Stream_Ratio",
            ["Heat_Duty"] = "Heat_Duty", ["Heat Duty"] = "Heat_Duty", ["Duty"] = "Heat_Duty",
            ["Product_Molar_Flow_Rate"] = "Product_Molar_Flow_Rate", ["Product Molar Flow"] = "Product_Molar_Flow_Rate", ["Molar Flow"] = "Product_Molar_Flow_Rate", ["Distillate Rate"] = "Product_Molar_Flow_Rate", ["Bottoms Rate"] = "Product_Molar_Flow_Rate", ["Product Flow"] = "Product_Molar_Flow_Rate",
            ["Product_Mass_Flow_Rate"] = "Product_Mass_Flow_Rate", ["Product Mass Flow"] = "Product_Mass_Flow_Rate", ["Mass Flow"] = "Product_Mass_Flow_Rate",
            ["Component_Molar_Flow_Rate"] = "Component_Molar_Flow_Rate", ["Component Molar Flow"] = "Component_Molar_Flow_Rate",
            ["Component_Mass_Flow_Rate"] = "Component_Mass_Flow_Rate", ["Component Mass Flow"] = "Component_Mass_Flow_Rate",
            ["Component_Fraction"] = "Component_Fraction", ["Component Fraction"] = "Component_Fraction", ["Purity"] = "Component_Fraction", ["Mole Fraction"] = "Component_Fraction", ["Mass Fraction"] = "Component_Fraction", ["Composition"] = "Component_Fraction",
            ["Component_Recovery"] = "Component_Recovery", ["Component Recovery"] = "Component_Recovery", ["Recovery"] = "Component_Recovery",
            ["Temperature"] = "Temperature", ["Temp"] = "Temperature", ["T"] = "Temperature",
            ["Feed_Recovery"] = "Feed_Recovery", ["Feed Recovery"] = "Feed_Recovery",
        };

        private static readonly HashSet<string> ComponentSpecs = new HashSet<string> { "Component_Molar_Flow_Rate", "Component_Mass_Flow_Rate", "Component_Fraction", "Component_Recovery" };

        private static (string type, double value, string units, string compound) ParseSpec(FluentFlowsheet fs, JObject spec, string where, bool allowReflux)
        {
            var typeRaw = spec.Value<string>("type") ?? throw new ArgumentException($"{where} spec needs a type.");
            if (!SpecAliases.TryGetValue(typeRaw.Trim(), out var type))
                throw new ArgumentException($"'{typeRaw}' is not a {where} spec type. Use one of: {string.Join(", ", SpecAliases.Values.Distinct())}.");
            if (type == "Reflux_Ratio" && !allowReflux) type = "Boilup_Ratio";
            if (type == "Boilup_Ratio" && allowReflux) type = "Reflux_Ratio";
            if (type == "Stream_Ratio") type = allowReflux ? "Reflux_Ratio" : "Boilup_Ratio";

            var valueTok = spec["value"];
            if (valueTok == null || (valueTok.Type != JTokenType.Float && valueTok.Type != JTokenType.Integer))
                throw new ArgumentException($"{where} spec needs a numeric value.");
            var value = valueTok.Value<double>();
            var units = spec.Value<string>("units") ?? "";
            var compound = spec.Value<string>("compound");

            if (ComponentSpecs.Contains(type))
            {
                if (string.IsNullOrWhiteSpace(compound)) throw new ArgumentException($"{where} spec {type} needs a compound.");
                compound = Find.Compound(fs, compound).Name;
            }
            else compound = "";

            switch (type)
            {
                case "Component_Fraction":
                    if (value <= 0 || value >= 1) throw new ArgumentException($"{where} Component_Fraction is a fraction between 0 and 1.");
                    if (string.IsNullOrWhiteSpace(units)) units = "Molar";
                    break;
                case "Component_Recovery":
                    if (value <= 0 || value > 100) throw new ArgumentException($"{where} Component_Recovery is a percentage (0-100].");
                    units = "";
                    break;
                case "Reflux_Ratio":
                case "Boilup_Ratio":
                    if (value <= 0) throw new ArgumentException($"{where} {type} must be positive.");
                    units = "";
                    break;
                case "Product_Molar_Flow_Rate":
                case "Component_Molar_Flow_Rate":
                    if (value <= 0) throw new ArgumentException($"{where} {type} must be positive.");
                    if (string.IsNullOrWhiteSpace(units)) units = "mol/s";
                    break;
                case "Product_Mass_Flow_Rate":
                case "Component_Mass_Flow_Rate":
                    if (value <= 0) throw new ArgumentException($"{where} {type} must be positive.");
                    if (string.IsNullOrWhiteSpace(units)) units = "kg/s";
                    break;
                case "Heat_Duty":
                    if (string.IsNullOrWhiteSpace(units)) units = "kW";
                    break;
                case "Temperature":
                    if (string.IsNullOrWhiteSpace(units)) units = "K";
                    break;
                case "Feed_Recovery":
                    units = "";
                    break;
            }
            return (type, value, units, compound);
        }

        private static JObject DescribeShortcut(FluentFlowsheet fs, ShortcutColumn col)
        {
            var o = new JObject
            {
                ["column"] = col.GraphicObject.Tag,
                ["type"] = "ShortcutColumn",
                ["light_key"] = col.m_lightkey,
                ["heavy_key"] = col.m_heavykey,
                ["light_key_in_bottoms"] = col.m_lightkeymolarfrac,
                ["heavy_key_in_distillate"] = col.m_heavykeymolarfrac,
                ["reflux_ratio"] = col.m_refluxratio,
                ["condenser_pressure_Pa"] = col.m_condenserpressure,
                ["reboiler_pressure_Pa"] = col.m_boilerpressure,
                ["condenser_type"] = col.condtype.ToString(),
                ["calculated"] = col.Calculated,
                ["ports"] = Json.Strings(col.GetConnectionPortsList()),
            };
            if (!string.IsNullOrEmpty(col.ErrorMessage)) o["error"] = col.ErrorMessage;
            if (col.Calculated)
            {
                o["results"] = new JObject
                {
                    ["minimum_reflux_ratio"] = Json.Num(col.m_Rmin),
                    ["minimum_stages"] = Json.Num(col.m_Nmin),
                    ["actual_stages"] = Json.Num(col.m_N),
                    ["optimum_feed_stage"] = Json.Num(col.ofs),
                    ["condenser_duty_kW"] = Json.Num(col.m_Qc),
                    ["reboiler_duty_kW"] = Json.Num(col.m_Qb),
                    ["condenser_temperature_K"] = Json.Num(col.m_Tc),
                    ["reboiler_temperature_K"] = Json.Num(col.m_Tb),
                };
            }
            if (string.IsNullOrEmpty(col.m_lightkey) || string.IsNullOrEmpty(col.m_heavykey))
                o["warning"] = "Light and heavy keys are not set; the column cannot solve. Call dwsim_column_shortcut_configure.";
            return o;
        }

        private static JObject DescribeRigorous(FluentFlowsheet fs, Column col)
        {
            var dc = col as DistillationColumn;
            var ac = col as AbsorptionColumn;
            string TagOf(string id) => fs.Inner.SimulationObjects.TryGetValue(id, out var s) ? s.GraphicObject?.Tag ?? id : id;
            int StageOf(string stageId) => col.Stages.FindIndex(s => s.ID == stageId);

            var streams = new JArray(col.MaterialStreams.Values.Concat(col.EnergyStreams.Values).Select(si => new JObject
            {
                ["stream"] = TagOf(si.StreamID),
                ["role"] = si.StreamBehavior.ToString(),
                ["kind"] = si.StreamType.ToString(),
                ["stage"] = StageOf(si.AssociatedStage) + 1,
            }));

            var o = new JObject
            {
                ["column"] = col.GraphicObject.Tag,
                ["type"] = dc != null ? (dc.ReboiledAbsorber ? "ReboiledAbsorber" : dc.RefluxedAbsorber ? "RefluxedAbsorber" : "DistillationColumn")
                                      : (ac?.OperationMode == AbsorptionColumn.OpMode.Extractor ? "Extractor" : "AbsorptionColumn"),
                ["stages"] = col.Stages.Count,
                ["condenser_type"] = dc?.CondenserType.ToString(),
                ["top_pressure_Pa"] = col.Stages.Count > 0 ? Json.Num(col.Stages[0].P) : JValue.CreateNull(),
                ["bottom_pressure_Pa"] = col.Stages.Count > 0 ? Json.Num(col.Stages[col.Stages.Count - 1].P) : JValue.CreateNull(),
                ["pressure_drop_Pa"] = Json.Num(col.ColumnPressureDrop),
                ["solving_method"] = col.SolvingMethodName,
                ["max_iterations"] = col.MaxIterations,
                ["streams"] = streams,
                ["calculated"] = col.Calculated,
            };
            if (dc != null)
            {
                JObject Spec(string key)
                {
                    if (!dc.Specs.TryGetValue(key, out var sp)) return null;
                    var s = new JObject { ["type"] = sp.SType.ToString(), ["value"] = Json.Num(sp.SpecValue), ["units"] = sp.SpecUnit };
                    if (!string.IsNullOrEmpty(sp.ComponentID)) s["compound"] = sp.ComponentID;
                    if (dc.Calculated) s["calculated_value"] = Json.Num(sp.CalculatedValue);
                    return s;
                }
                o["specs"] = new JObject { ["condenser"] = Spec("C"), ["reboiler"] = Spec("R") };
            }
            if (!string.IsNullOrEmpty(col.ErrorMessage)) o["error"] = col.ErrorMessage;
            if (col.Calculated)
            {
                o["duties_kW"] = new JObject { ["condenser"] = Json.Num(col.CondenserDuty), ["reboiler"] = Json.Num(col.ReboilerDuty) };
                // the stage flows of the last solve live in LastSolution (the next run's estimates), not on the stages
                var last = col.GetLastSolution();
                double Flow(System.Collections.Generic.List<Parameter> list, int i) => list != null && i < list.Count ? list[i].Value : double.NaN;
                o["profile"] = new JArray(col.Stages.Select((s, i) => new JObject
                {
                    ["stage"] = i + 1,
                    ["T_K"] = Json.Num(s.T),
                    ["P_Pa"] = Json.Num(s.P),
                    ["L_mol_s"] = Json.Num(Flow(last?.LiqMolarFlows, i)),
                    ["V_mol_s"] = Json.Num(Flow(last?.VapMolarFlows, i)),
                }));
            }
            var missing = new List<string>();
            if (!col.MaterialStreams.Values.Any(s => s.StreamBehavior == StreamInformation.Behavior.Feed)) missing.Add("a feed");
            if (dc != null && !dc.ReboiledAbsorber && dc.CondenserType != Column.condtype.Full_Reflux
                && !col.MaterialStreams.Values.Any(s => s.StreamBehavior == StreamInformation.Behavior.Distillate)) missing.Add("distillate");
            if (dc != null && dc.CondenserType != Column.condtype.Total_Condenser && !dc.ReboiledAbsorber
                && !col.MaterialStreams.Values.Any(s => s.StreamBehavior == StreamInformation.Behavior.OverheadVapor)) missing.Add("vapor_product");
            if (!col.MaterialStreams.Values.Any(s => s.StreamBehavior == StreamInformation.Behavior.BottomsLiquid)) missing.Add("bottoms");
            if (dc != null && !dc.ReboiledAbsorber && !col.EnergyStreams.Values.Any(s => s.StreamBehavior == StreamInformation.Behavior.Distillate)) missing.Add("condenser_duty");
            if (dc != null && !dc.RefluxedAbsorber && !col.EnergyStreams.Values.Any(s => s.StreamBehavior == StreamInformation.Behavior.BottomsLiquid)) missing.Add("reboiler_duty");
            if (ac != null && !col.MaterialStreams.Values.Any(s => s.StreamBehavior == StreamInformation.Behavior.Distillate || s.StreamBehavior == StreamInformation.Behavior.OverheadVapor)) missing.Add("top_product");
            if (missing.Count > 0) o["missing_connections"] = Json.Strings(missing);
            return o;
        }
    }
}
