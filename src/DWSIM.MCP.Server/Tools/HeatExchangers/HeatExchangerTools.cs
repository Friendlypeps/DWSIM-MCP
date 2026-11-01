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
using DWSIM.UnitOperations.UnitOperations;
using FluentFlowsheet = DWSIM.Automation.FluentAPI.Flowsheet;

namespace DWSIM.MCPServer.Tools.HeatExchangers
{
    /// <summary>
    /// The two-sided HeatExchanger (feed/effluent exchange, waste heat recovery) the generic setters reach only
    /// by CLR property names, and the surrogate of two one-sided exchangers tied by duty for loops the rigorous
    /// unit does not solve.
    /// </summary>
    public class HeatExchangerTools
    {
        private readonly SessionManager _sessions;
        public HeatExchangerTools(SessionManager sessions) { _sessions = sessions; }

        private static readonly Dictionary<string, (HeatExchangerCalcMode mode, SpecifiedTemperature side)> Aliases =
            new Dictionary<string, (HeatExchangerCalcMode, SpecifiedTemperature)>(StringComparer.OrdinalIgnoreCase)
            {
                // by what the caller specifies; the engine's name says what it calculates
                ["specify_hot_outlet"] = (HeatExchangerCalcMode.CalcTempColdOut, SpecifiedTemperature.Hot_Fluid),
                ["specify_cold_outlet"] = (HeatExchangerCalcMode.CalcTempHotOut, SpecifiedTemperature.Cold_Fluid),
                ["specify_duty"] = (HeatExchangerCalcMode.CalcBothTemp, SpecifiedTemperature.Hot_Fluid),
                ["specify_ua"] = (HeatExchangerCalcMode.CalcBothTemp_UA, SpecifiedTemperature.Hot_Fluid),
                ["pinch"] = (HeatExchangerCalcMode.PinchPoint, SpecifiedTemperature.Hot_Fluid),
                ["thermal_efficiency"] = (HeatExchangerCalcMode.ThermalEfficiency, SpecifiedTemperature.Hot_Fluid),
                ["calctemphotout"] = (HeatExchangerCalcMode.CalcTempHotOut, SpecifiedTemperature.Cold_Fluid),
                ["calctempcoldout"] = (HeatExchangerCalcMode.CalcTempColdOut, SpecifiedTemperature.Hot_Fluid),
                ["calcbothtemp"] = (HeatExchangerCalcMode.CalcBothTemp, SpecifiedTemperature.Hot_Fluid),
                ["calcbothtemp_ua"] = (HeatExchangerCalcMode.CalcBothTemp_UA, SpecifiedTemperature.Hot_Fluid),
                ["calcarea"] = (HeatExchangerCalcMode.CalcArea, SpecifiedTemperature.Hot_Fluid),
                ["pinchpoint"] = (HeatExchangerCalcMode.PinchPoint, SpecifiedTemperature.Hot_Fluid),
                ["thermalefficiency"] = (HeatExchangerCalcMode.ThermalEfficiency, SpecifiedTemperature.Hot_Fluid),
                ["outletvaporfraction1"] = (HeatExchangerCalcMode.OutletVaporFraction1, SpecifiedTemperature.Hot_Fluid),
                ["outletvaporfraction2"] = (HeatExchangerCalcMode.OutletVaporFraction2, SpecifiedTemperature.Cold_Fluid),
            };

        [McpTool("dwsim_hx_configure",
            "Configure a two-sided HeatExchanger (feed/effluent exchange, waste heat recovery). Inlet port i feeds outlet " +
            "port i; the engine decides at solve time which inlet is the hot side by temperature. mode says what is " +
            "specified: specify_hot_outlet (hot_outlet_T_K), specify_cold_outlet (cold_outlet_T_K), specify_duty " +
            "(duty_kW), specify_ua (ua_W_K), pinch (mita_K), thermal_efficiency (0-1); the engine's own names " +
            "(CalcTempHotOut, CalcTempColdOut, CalcBothTemp, CalcBothTemp_UA, CalcArea, PinchPoint, ThermalEfficiency, " +
            "OutletVaporFraction1/2) are accepted too. Shell-and-tube rating modes are not covered. SI values.")]
        public JObject Configure(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("HeatExchanger tag")] string name,
            [McpParam("Mode (see description)", Required = false)] string mode = null,
            [McpParam("Hot side outlet temperature, K", Required = false, JsonType = "number")] double? hot_outlet_T_K = null,
            [McpParam("Cold side outlet temperature, K", Required = false, JsonType = "number")] double? cold_outlet_T_K = null,
            [McpParam("Duty, kW", Required = false, JsonType = "number")] double? duty_kW = null,
            [McpParam("UA, W/K", Required = false, JsonType = "number")] double? ua_W_K = null,
            [McpParam("Area, m2 (with overall_U_W_m2K)", Required = false, JsonType = "number")] double? area_m2 = null,
            [McpParam("Overall heat transfer coefficient, W/m2K", Required = false, JsonType = "number")] double? overall_U_W_m2K = null,
            [McpParam("Minimum internal temperature approach, K (pinch mode)", Required = false, JsonType = "number")] double? mita_K = null,
            [McpParam("Thermal efficiency 0-1 (thermal_efficiency mode)", Required = false, JsonType = "number")] double? thermal_efficiency = null,
            [McpParam("Hot side pressure drop, Pa", Required = false, JsonType = "number")] double? hot_side_dp_Pa = null,
            [McpParam("Cold side pressure drop, Pa", Required = false, JsonType = "number")] double? cold_side_dp_Pa = null,
            [McpParam("CounterCurrent (default) or CoCurrent", Required = false)] string flow_direction = null,
            [McpParam("Ignore the LMTD error on a temperature cross", Required = false, JsonType = "boolean")] bool? ignore_lmtd_error = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var hx = Find.As<HeatExchanger>(fs, name, "heat exchanger");
            bool changed = !string.IsNullOrWhiteSpace(mode) || hot_outlet_T_K.HasValue || cold_outlet_T_K.HasValue || duty_kW.HasValue || ua_W_K.HasValue
                        || area_m2.HasValue || overall_U_W_m2K.HasValue || mita_K.HasValue || thermal_efficiency.HasValue || hot_side_dp_Pa.HasValue
                        || cold_side_dp_Pa.HasValue || !string.IsNullOrWhiteSpace(flow_direction) || ignore_lmtd_error.HasValue;

            if (!string.IsNullOrWhiteSpace(mode))
            {
                var key = mode.Trim().Replace(" ", "_");
                if (!Aliases.TryGetValue(key, out var m))
                    throw new ArgumentException($"'{mode}' is not a mode. Use one of: {string.Join(", ", Aliases.Keys)}.");
                hx.CalculationMode = m.mode;
                hx.DefinedTemperature = m.side;
            }
            if (hot_outlet_T_K.HasValue) { hx.HotSideOutletTemperature = hot_outlet_T_K.Value; hx.DefinedTemperature = SpecifiedTemperature.Hot_Fluid; }
            if (cold_outlet_T_K.HasValue) { hx.ColdSideOutletTemperature = cold_outlet_T_K.Value; if (!hot_outlet_T_K.HasValue) hx.DefinedTemperature = SpecifiedTemperature.Cold_Fluid; }
            if (duty_kW.HasValue) hx.Q = duty_kW.Value;
            if (ua_W_K.HasValue) { hx.OverallCoefficient = ua_W_K.Value; hx.Area = 1.0; }
            if (area_m2.HasValue) hx.Area = area_m2.Value;
            if (overall_U_W_m2K.HasValue) hx.OverallCoefficient = overall_U_W_m2K.Value;
            if (mita_K.HasValue) hx.MITA = mita_K.Value;
            if (thermal_efficiency.HasValue)
            {
                if (thermal_efficiency.Value <= 0 || thermal_efficiency.Value > 1) throw new ArgumentException("thermal_efficiency is between 0 and 1.");
                hx.ThermalEfficiency = thermal_efficiency.Value * 100.0;
            }
            if (hot_side_dp_Pa.HasValue) hx.HotSidePressureDrop = hot_side_dp_Pa.Value;
            if (cold_side_dp_Pa.HasValue) hx.ColdSidePressureDrop = cold_side_dp_Pa.Value;
            if (!string.IsNullOrWhiteSpace(flow_direction)) hx.FlowDir = Json.Enum<FlowDirection>(flow_direction, "flow direction");
            if (ignore_lmtd_error.HasValue) hx.IgnoreLMTDError = ignore_lmtd_error.Value;

            // the spec the mode reads must be there
            switch (hx.CalculationMode)
            {
                case HeatExchangerCalcMode.CalcTempColdOut: if (!(hx.HotSideOutletTemperature > 0)) throw new ArgumentException("specify_hot_outlet needs hot_outlet_T_K."); break;
                case HeatExchangerCalcMode.CalcTempHotOut: if (!(hx.ColdSideOutletTemperature > 0)) throw new ArgumentException("specify_cold_outlet needs cold_outlet_T_K."); break;
                case HeatExchangerCalcMode.CalcBothTemp: if (!(hx.Q.GetValueOrDefault() > 0)) throw new ArgumentException("specify_duty needs duty_kW > 0."); break;
                case HeatExchangerCalcMode.CalcBothTemp_UA: if (!(hx.OverallCoefficient.GetValueOrDefault() * hx.Area.GetValueOrDefault() > 0)) throw new ArgumentException("specify_ua needs ua_W_K (or area_m2 with overall_U_W_m2K)."); break;
                case HeatExchangerCalcMode.PinchPoint: if (!(hx.MITA > 0)) throw new ArgumentException("pinch needs mita_K."); break;
                case HeatExchangerCalcMode.ThermalEfficiency: if (!(hx.ThermalEfficiency > 0)) throw new ArgumentException("thermal_efficiency mode needs thermal_efficiency."); break;
            }

            var f = fs.Inner;
            var inlets = Plant.PlantTools.Attached(f, hx, true).ToList();
            var outlets = Plant.PlantTools.Attached(f, hx, false).ToList();
            double T(IMaterialStream s) { try { return s.GetTemperature(); } catch { return double.NaN; } }
            var tIn = inlets.Select(T).ToList();
            string hot = "decided at solve time by the inlet temperatures";
            if (tIn.Count == 2 && tIn.All(t => t > 0))
            {
                hot = tIn[0] >= tIn[1] ? "inlet 0" : "inlet 1";
                double hotIn = Math.Max(tIn[0], tIn[1]), coldIn = Math.Min(tIn[0], tIn[1]);
                if (hx.CalculationMode == HeatExchangerCalcMode.CalcTempColdOut && hx.HotSideOutletTemperature < coldIn)
                    throw new ArgumentException($"hot_outlet_T_K {hx.HotSideOutletTemperature:0.##} K is below the cold inlet {coldIn:0.##} K: a temperature cross.");
                if (hx.CalculationMode == HeatExchangerCalcMode.CalcTempHotOut && hx.ColdSideOutletTemperature > hotIn)
                    throw new ArgumentException($"cold_outlet_T_K {hx.ColdSideOutletTemperature:0.##} K is above the hot inlet {hotIn:0.##} K: a temperature cross.");
            }
            if (changed) fs.Inner.ResetCalculationStatus();

            var o = new JObject
            {
                ["unitop"] = name,
                ["mode"] = hx.CalculationMode.ToString(),
                ["specified_side"] = hx.DefinedTemperature.ToString(),
                ["hot_outlet_T_K"] = Json.Num(hx.HotSideOutletTemperature),
                ["cold_outlet_T_K"] = Json.Num(hx.ColdSideOutletTemperature),
                ["duty_kW"] = Json.Num(hx.Q),
                ["ua_W_K"] = Json.Num(hx.OverallCoefficient.GetValueOrDefault() * hx.Area.GetValueOrDefault()),
                ["mita_K"] = Json.Num(hx.MITA),
                ["thermal_efficiency_pct"] = Json.Num(hx.ThermalEfficiency),
                ["hot_side_dp_Pa"] = Json.Num(hx.HotSidePressureDrop),
                ["cold_side_dp_Pa"] = Json.Num(hx.ColdSidePressureDrop),
                ["flow_direction"] = hx.FlowDir.ToString(),
                ["inlets"] = new JArray(inlets.Select((s, i) => new JObject { ["port"] = i, ["stream"] = (s as ISimulationObject).GraphicObject.Tag, ["T_K"] = Json.Num(T(s)) })),
                ["outlets"] = new JArray(outlets.Select((s, i) => new JObject { ["port"] = i, ["stream"] = (s as ISimulationObject).GraphicObject.Tag, ["T_K"] = Json.Num(T(s)) })),
                ["hot_side"] = hot,
                ["note"] = "inlet port i feeds outlet port i; the hotter inlet is the hot side",
            };
            if (hx.Calculated || hx.Q.GetValueOrDefault() != 0)
                o["results"] = new JObject { ["duty_kW"] = Json.Num(hx.Q), ["lmtd_K"] = Json.Num(hx.LMTD), ["max_heat_exchange_kW"] = Json.Num(hx.MaxHeatExchange) };
            return o;
        }

        [McpTool("dwsim_hx_tie_halves",
            "The feed/effluent exchanger as two one-sided units tied by duty: a Heater on the cold stream (or a Cooler " +
            "on the hot one) with the specified outlet temperature, and on the other side a Cooler (Heater) whose duty " +
            "a Spec block copies from the first, so both halves exchange the same heat. Solves inside recycle loops " +
            "where the two-sided HeatExchanger does not. Creates <name>-COLD, <name>-HOT, their energy streams and " +
            "<name>-SPEC; the result is a surrogate (no UA, no temperature-cross check) and says so.")]
        public JObject TieHalves(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Base tag for the created units")] string name,
            [McpParam("Hot inlet stream tag")] string hot_in,
            [McpParam("Hot outlet stream tag (created if missing)")] string hot_out,
            [McpParam("Cold inlet stream tag")] string cold_in,
            [McpParam("Cold outlet stream tag (created if missing)")] string cold_out,
            [McpParam("Outlet temperature specified, K")] double outlet_T_K,
            [McpParam("Which side outlet_T_K belongs to: cold (default) or hot", Required = false)] string specified_side = "cold",
            [McpParam("Hot side pressure drop, Pa", Required = false, JsonType = "number")] double? hot_side_dp_Pa = null,
            [McpParam("Cold side pressure drop, Pa", Required = false, JsonType = "number")] double? cold_side_dp_Pa = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the exchanger a base tag.");
            bool coldSpecified = string.IsNullOrWhiteSpace(specified_side) || specified_side.Trim().Equals("cold", StringComparison.OrdinalIgnoreCase);
            if (!coldSpecified && !specified_side.Trim().Equals("hot", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("specified_side is 'cold' or 'hot'.");

            var hotIn = Find.ByTagOrId(fs, hot_in, "hot inlet stream");
            var coldIn = Find.ByTagOrId(fs, cold_in, "cold inlet stream");
            var hotOut = Ensure(fs, hot_out);
            var coldOut = Ensure(fs, cold_out);
            var su = fs.Inner.FlowsheetOptions.SelectedUnitSystem;

            string coldTag = name + "-COLD", hotTag = name + "-HOT";
            fs.AddHeater(coldTag);
            fs.AddCooler(hotTag);
            fs.AddEnergyStream(name + "-Q-COLD");
            fs.AddEnergyStream(name + "-Q-HOT");
            var heater = Find.ByTagOrId(fs, coldTag, "heater");
            var cooler = Find.ByTagOrId(fs, hotTag, "cooler");
            heater.ConnectFeedMaterialStream(coldIn, 0); heater.ConnectProductMaterialStream(coldOut, 0);
            heater.ConnectFeedEnergyStream(Find.ByTagOrId(fs, name + "-Q-COLD", "energy stream"), 1);
            cooler.ConnectFeedMaterialStream(hotIn, 0); cooler.ConnectProductMaterialStream(hotOut, 0);
            cooler.ConnectProductEnergyStream(Find.ByTagOrId(fs, name + "-Q-HOT", "energy stream"), 0);

            // the specified half fixes its outlet temperature, the other half takes the duty
            var specified = coldSpecified ? heater : cooler;
            var follower = coldSpecified ? cooler : heater;
            PropertySetter.Apply(specified, new[] { new KeyValuePair<string, object>("CalcMode", "OutletTemperature"), new KeyValuePair<string, object>("OutletTemperature", outlet_T_K) }, su);
            PropertySetter.Apply(follower, new[] { new KeyValuePair<string, object>("CalcMode", coldSpecified ? "HeatRemoved" : "HeatAdded"), new KeyValuePair<string, object>("DeltaQ", 1.0) }, su);
            if (hot_side_dp_Pa.HasValue) PropertySetter.Apply(cooler, new[] { new KeyValuePair<string, object>("DeltaP", hot_side_dp_Pa.Value) }, su);
            if (cold_side_dp_Pa.HasValue) PropertySetter.Apply(heater, new[] { new KeyValuePair<string, object>("DeltaP", cold_side_dp_Pa.Value) }, su);

            string sourceProp = PropertyId(specified, su, coldSpecified ? "Heat Added" : "Heat Removed", PropertyType.ALL);
            string targetProp = PropertyId(follower, su, coldSpecified ? "Heat Removed" : "Heat Added", PropertyType.WR);
            var spec = Logical.LogicalTools.CreateSpec(fs, name + "-SPEC", specified.GraphicObject.Tag, sourceProp, follower.GraphicObject.Tag, targetProp, "X");

            return new JObject
            {
                ["cold_half"] = coldTag, ["hot_half"] = hotTag, ["spec"] = name + "-SPEC",
                ["specified_side"] = coldSpecified ? "cold" : "hot", ["outlet_T_K"] = outlet_T_K,
                ["source_property"] = sourceProp, ["target_property"] = targetProp,
                ["surrogate"] = true,
                ["note"] = "two one-sided exchangers tied by duty; no UA, no temperature-cross check: read the hot outlet temperature after the solve and compare it with the cold inlet",
                ["spec_result"] = spec,
            };
        }

        private static ISimulationObject Ensure(FluentFlowsheet fs, string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) throw new ArgumentException("Name every stream of the exchanger.");
            var existing = fs.Inner.SimulationObjects.Values.FirstOrDefault(o => o.GraphicObject?.Tag == tag);
            if (existing != null) return existing;
            fs.AddMaterialStream(tag);
            return Find.ByTagOrId(fs, tag, "stream");
        }

        private static string PropertyId(ISimulationObject obj, IUnitsOfMeasure su, string description, PropertyType kind)
        {
            var entries = PropertyCatalog.For(obj, su, kind);
            var hit = entries.FirstOrDefault(e => (e.Description ?? "").IndexOf(description, StringComparison.OrdinalIgnoreCase) >= 0);
            if (hit == null) throw new ArgumentException($"'{obj.GraphicObject.Tag}' has no property described '{description}'. Properties: {string.Join(", ", entries.Select(e => e.Id + " " + e.Description).Take(20))}.");
            return hit.Id;
        }
    }
}
