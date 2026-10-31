// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using DWSIM.Automation.FluentAPI;
using DWSIM.Interfaces;
using DWSIM.MCPServer.Sessions;
using DWSIM.MCPServer.Tools.Common;
using DWSIM.UnitOperations.UnitOperations;
using DWSIM.UnitOperations.UnitOperations.Auxiliary;
using FluentFlowsheet = DWSIM.Automation.FluentAPI.Flowsheet;

namespace DWSIM.MCPServer.Tools.Separators
{
    /// <summary>
    /// Separators the upstream tools cannot specify: the component separator's per-compound split
    /// table (its entries must be created, not just edited), the Vessel's flash override, and the
    /// calculation mode of units whose mode is a plain enum property (Vessel, Filter), where the
    /// upstream setter throws.
    /// </summary>
    public class SeparatorTools
    {
        private readonly SessionManager _sessions;

        public SeparatorTools(SessionManager sessions) { _sessions = sessions; }

        [McpTool("dwsim_compsep_set_specs",
            "Specify a ComponentSeparator: for each compound, how much of the inlet goes to the specified outlet " +
            "(port 0 or 1); the rest goes to the other outlet, and a compound with no entry goes wholly to the " +
            "other outlet. specs is {compound: value} with value a percent of the inlet molar flow, or " +
            "{compound: {type, value, unit}} with type PercentInletMolarFlow, PercentInletMassFlow, MolarFlow " +
            "(unit mol/s, kmol/h ...) or MassFlow (kg/s, kg/h ...). This is the stand-in for separations DWSIM has " +
            "no model for (pervaporation, adsorption, extraction, drying): the split is typed in, not simulated, " +
            "and any report must say so.")]
        public JObject SetCompSepSpecs(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Component separator tag")] string name,
            [McpParam("Per-compound specs as {compound: percent} or {compound: {type, value, unit}}", JsonType = "object")] JObject specs,
            [McpParam("Outlet the specs refer to: 0 (first outlet, default) or 1", Required = false, JsonType = "integer")] int? specified_outlet = null,
            [McpParam("Drop the existing entries first (default false)", Required = false, JsonType = "boolean")] bool clear = false)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var sep = Find.As<ComponentSeparator>(fs, name, "component separator");
            if (specs == null || specs.Count == 0) throw new ArgumentException("Give at least one compound in specs.");
            if (specified_outlet.HasValue)
            {
                if (specified_outlet != 0 && specified_outlet != 1) throw new ArgumentException("specified_outlet is 0 or 1.");
                sep.SpecifiedStreamIndex = (byte)specified_outlet.Value;
            }
            if (sep.ComponentSepSpecs == null) sep.ComponentSepSpecs = new Dictionary<string, ComponentSeparationSpec>();
            if (clear) sep.ComponentSepSpecs.Clear();

            foreach (var p in specs.Properties())
            {
                var comp = Find.Compound(fs, p.Name).Name;
                SeparationSpec type;
                double value;
                string unit = "";
                if (p.Value.Type == JTokenType.Float || p.Value.Type == JTokenType.Integer)
                {
                    type = SeparationSpec.PercentInletMolarFlow;
                    value = p.Value.Value<double>();
                }
                else if (p.Value is JObject o)
                {
                    type = Json.Enum<SeparationSpec>(o.Value<string>("type") ?? "PercentInletMolarFlow", "separation spec type");
                    var v = o["value"];
                    if (v == null || (v.Type != JTokenType.Float && v.Type != JTokenType.Integer)) throw new ArgumentException($"{comp}: value must be a number.");
                    value = v.Value<double>();
                    unit = o.Value<string>("unit") ?? "";
                }
                else throw new ArgumentException($"{comp}: give a number (percent of inlet molar flow) or {{type, value, unit}}.");

                switch (type)
                {
                    case SeparationSpec.PercentInletMolarFlow:
                    case SeparationSpec.PercentInletMassFlow:
                        if (value < 0 || value > 100) throw new ArgumentException($"{comp}: a percent spec is between 0 and 100.");
                        unit = "";
                        break;
                    case SeparationSpec.MolarFlow:
                        if (value < 0) throw new ArgumentException($"{comp}: a flow cannot be negative.");
                        if (string.IsNullOrWhiteSpace(unit)) unit = "mol/s";
                        break;
                    case SeparationSpec.MassFlow:
                        if (value < 0) throw new ArgumentException($"{comp}: a flow cannot be negative.");
                        if (string.IsNullOrWhiteSpace(unit)) unit = "kg/s";
                        break;
                }
                sep.ComponentSepSpecs[comp] = new ComponentSeparationSpec(comp, type, value, unit);
            }
            fs.Inner.ResetCalculationStatus();
            return DescribeCompSep(fs, sep);
        }

        [McpTool("dwsim_unitop_set_mode",
            "Set the calculation mode of any unit operation by name. Works for the units with a mode list (Heater, " +
            "Cooler, Pump, Compressor, Expander, Valve, Splitter, HeatExchanger) and for the units whose mode is a " +
            "plain property, where dwsim_unitop_set {\"CalcMode\": ...} fails: Vessel (Adiabatic, Legacy, " +
            "HeatingCoolingIsothermic, HeatingCoolingIsobaric), Filter (Simulation, Design), reactors " +
            "(Isothermic, Adiabatic, OutletTemperature, NonIsothermalNonAdiabatic, HeatExchange). With no mode " +
            "given it only lists the modes. After the mode, set its specification with dwsim_unitop_set " +
            "(e.g. OutletVaporFraction, DeltaP, OutletTemperature).")]
        public JObject SetMode(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Unit operation tag")] string name,
            [McpParam("Mode name (or its number); omit to list the modes", Required = false)] string mode = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var obj = Find.ByTagOrId(fs, name, "unit operation");
            var listed = PropertySetter.CalculationModes(obj);
            var prop = ModeProperty(obj);

            if (listed.Count == 0 && prop == null)
                throw new ArgumentException($"'{name}' ({obj.GraphicObject?.ObjectType}) has no calculation mode.");

            string current = null;
            var options = new List<string>();
            if (listed.Count > 0) options.AddRange(listed.Keys);
            else options.AddRange(Enum.GetNames(prop.PropertyType));

            if (!string.IsNullOrWhiteSpace(mode))
            {
                if (listed.Count > 0)
                {
                    int id;
                    var hit = listed.Keys.FirstOrDefault(k => string.Equals(k, mode.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (hit != null) id = listed[hit];
                    else if (int.TryParse(mode, out id) && listed.Values.Contains(id)) { }
                    else throw new ArgumentException($"'{mode}' is not a mode of '{name}'. Use one of: {string.Join(", ", options)}.");
                    var m = obj.GetType().GetMethod("SetCalculationMode", BindingFlags.Public | BindingFlags.Instance)
                            ?? throw new ArgumentException($"'{name}' lists modes but has no SetCalculationMode.");
                    m.Invoke(obj, new object[] { id });
                }
                else
                {
                    object value;
                    var names = Enum.GetNames(prop.PropertyType);
                    var hit = names.FirstOrDefault(n => string.Equals(n, mode.Trim().Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
                    if (hit != null) value = Enum.Parse(prop.PropertyType, hit);
                    else if (int.TryParse(mode, out var n) && Enum.IsDefined(prop.PropertyType, n)) value = Enum.ToObject(prop.PropertyType, n);
                    else throw new ArgumentException($"'{mode}' is not a mode of '{name}'. Use one of: {string.Join(", ", names)}.");
                    prop.SetValue(obj, value);
                }
                fs.Inner.ResetCalculationStatus();
            }

            if (listed.Count > 0)
            {
                var get = obj.GetType().GetMethod("GetCalculationMode", BindingFlags.Public | BindingFlags.Instance);
                try { current = get?.Invoke(obj, null)?.ToString(); } catch { }
                if (current == null && prop != null) current = prop.GetValue(obj)?.ToString();
            }
            else current = prop.GetValue(obj)?.ToString();

            return new JObject
            {
                ["unitop"] = obj.GraphicObject.Tag,
                ["type"] = obj.GraphicObject.ObjectType.ToString(),
                ["mode"] = current,
                ["modes"] = Json.Strings(options),
                ["mode_property"] = prop?.Name,
            };
        }

        [McpTool("dwsim_vessel_configure",
            "Set a Vessel (flash drum / separator / decanter) to flash at a given temperature and/or pressure " +
            "instead of the feed's: override_t_K and override_p_Pa switch the overrides on with those values; " +
            "clear_overrides switches them off. An override needs an energy stream on the vessel (inlet energy port) " +
            "to carry the duty; the response says if it is missing. pressure_calculation sets how the feed " +
            "pressures combine (Minimum, Average, Maximum). Two liquid outlets only fill under a VLLE flash: " +
            "dwsim_thermo_set_flash equilibrium_type=VLLE, and attach a stream to outlet port 2.")]
        public JObject VesselConfigure(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Vessel tag")] string name,
            [McpParam("Flash temperature, K (switches the temperature override on)", Required = false, JsonType = "number")] double? override_t_K = null,
            [McpParam("Flash pressure, Pa (switches the pressure override on)", Required = false, JsonType = "number")] double? override_p_Pa = null,
            [McpParam("Switch both overrides off", Required = false, JsonType = "boolean")] bool clear_overrides = false,
            [McpParam("Minimum (default), Average or Maximum of the feed pressures", Required = false)] string pressure_calculation = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var v = Find.As<Vessel>(fs, name, "vessel");
            if (clear_overrides) { v.OverrideT = false; v.OverrideP = false; }
            if (override_t_K.HasValue)
            {
                if (override_t_K <= 0) throw new ArgumentException("override_t_K must be a positive temperature in K.");
                v.OverrideT = true; v.FlashTemperature = override_t_K.Value;
            }
            if (override_p_Pa.HasValue)
            {
                if (override_p_Pa <= 0) throw new ArgumentException("override_p_Pa must be a positive pressure in Pa.");
                v.OverrideP = true; v.FlashPressure = override_p_Pa.Value;
            }
            if (pressure_calculation != null) v.PressureCalculation = Json.Enum<Vessel.PressureBehavior>(pressure_calculation, "pressure calculation rule");
            fs.Inner.ResetCalculationStatus();

            var g = v.GraphicObject;
            bool energy = g.InputConnectors.Count > 6 && g.InputConnectors[6].IsAttached;
            var o = new JObject
            {
                ["vessel"] = g.Tag,
                ["override_temperature"] = v.OverrideT,
                ["flash_temperature_K"] = v.FlashTemperature,
                ["override_pressure"] = v.OverrideP,
                ["flash_pressure_Pa"] = v.FlashPressure,
                ["pressure_calculation"] = v.PressureCalculation.ToString(),
                ["calculation_mode"] = v.CalculationMode.ToString(),
                ["energy_stream_attached"] = energy,
                ["second_liquid_outlet_attached"] = g.OutputConnectors.Count > 2 && g.OutputConnectors[2].IsAttached,
                ["ports"] = Json.Strings(v.GetConnectionPortsList()),
            };
            if ((v.OverrideT || v.OverrideP) && !energy)
                o["warning"] = "An override needs an energy stream on the vessel's energy inlet (dwsim_unitop_connect energy_feed); the solve will fail with EnergyStreamRequired otherwise.";
            return o;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>The CalcMode / CalculationMode enum property of a unit that has one.</summary>
        private static PropertyInfo ModeProperty(ISimulationObject obj)
        {
            foreach (var n in new[] { "CalcMode", "CalculationMode", "ReactorOperationMode", "OperationMode" })
            {
                var p = obj.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.PropertyType.IsEnum && p.CanWrite) return p;
            }
            return null;
        }

        private static JObject DescribeCompSep(FluentFlowsheet fs, ComponentSeparator sep)
        {
            var specified = sep.ComponentSepSpecs.Keys.ToHashSet();
            return new JObject
            {
                ["separator"] = sep.GraphicObject.Tag,
                ["specified_outlet"] = sep.SpecifiedStreamIndex,
                ["specs"] = new JArray(sep.ComponentSepSpecs.Values.Select(s => new JObject
                {
                    ["compound"] = s.ComponentID, ["type"] = s.SepSpec.ToString(), ["value"] = s.SpecValue, ["unit"] = s.SpecUnit
                })),
                ["unspecified_compounds_go_to_other_outlet"] = Json.Strings(Find.CompoundNames(fs).Where(c => !specified.Contains(c))),
                ["ports"] = Json.Strings(sep.GetConnectionPortsList()),
                ["note"] = "Split fractions are specified, not simulated.",
            };
        }
    }
}
