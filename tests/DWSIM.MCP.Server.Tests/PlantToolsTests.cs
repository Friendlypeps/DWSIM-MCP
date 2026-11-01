// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DWSIM.MCP.Server.Tests
{
    /// <summary>
    /// The plant-level tools an open-run simulation needs: compound resolution from a formula, a reaction from its
    /// equation, the two-sided exchanger and its duty-tied surrogate, the recycle seed, and the report / balance /
    /// check views of a solved flowsheet.
    /// </summary>
    [Collection("engine")]
    public class PlantToolsTests
    {
        private readonly EngineFixture _f;
        public PlantToolsTests(EngineFixture f) { _f = f; }

        private string FischerBasis(string name)
        {
            var id = _f.NewFlowsheet(name);
            _f.Call("dwsim_thermo_add_compounds", new { flowsheet_id = id, names = new[] { "Ethanol", "Acetic acid", "Ethyl acetate", "Water" } });
            _f.Call("dwsim_thermo_set_property_package", new { flowsheet_id = id, name = "NRTL" });
            return id;
        }

        private JObject Solve(string id)
        {
            var r = _f.Call("dwsim_solve_run", new { flowsheet_id = id, timeout_s = 120 });
            Assert.True((bool)r["ok"], "solve failed: " + r.ToString());
            return r;
        }

        [Fact]
        public void ResolveCompoundByFormulaNameAndCas()
        {
            var amb = _f.Call("dwsim_thermo_resolve_compound", new { formula = "C2H6O" });
            Assert.Equal("ambiguous", amb["status"].ToString());
            var names = amb["candidates"].Select(c => c["name"].ToString()).ToList();
            Assert.Contains("Ethanol", names);
            Assert.Contains(names, n => n.IndexOf("ether", StringComparison.OrdinalIgnoreCase) >= 0);

            var ok = _f.Call("dwsim_thermo_resolve_compound", new { formula = "C2H6O", name = "ethanol" });
            Assert.True(ok["status"].ToString() == "resolved", "expected resolved: " + ok.ToString());
            Assert.Equal("Ethanol", ok["name"].ToString());
            Assert.Equal("C2H6O", ok["hill_formula"].ToString());
            Assert.Equal(46.07, (double)ok["molar_weight_g_mol"], 1);
            Assert.False((bool)ok["inchikey_used"]);

            // a structural formula is read by its element counts: CH3COOH is C2H4O2, which is also methyl formate
            var structural = _f.Call("dwsim_thermo_resolve_compound", new { formula = "CH3COOH" });
            Assert.Equal("ambiguous", structural["status"].ToString());
            Assert.Equal("C2H4O2", structural["hill_formula"].ToString());
            Assert.Contains(structural["candidates"], c => c["name"].ToString() == "Acetic acid");
            var settled = _f.Call("dwsim_thermo_resolve_compound", new { formula = "CH3COOH", cas = "64-19-7" });
            Assert.Equal("resolved", settled["status"].ToString());
            Assert.Equal("Acetic acid", settled["name"].ToString());

            var cas = _f.Call("dwsim_thermo_resolve_compound", new { cas = "64-17-5" });
            Assert.Equal("resolved", cas["status"].ToString());
            Assert.Equal("Ethanol", cas["name"].ToString());

            var conflict = _f.Call("dwsim_thermo_resolve_compound", new { formula = "C2H4O2", name = "Ethanol" });
            Assert.Equal("conflict", conflict["status"].ToString());

            var key = _f.Call("dwsim_thermo_resolve_compound", new { inchikey = "LFQSCWFLJHTTHZ-UHFFFAOYSA-N" });
            Assert.Equal("unsupported_key", key["status"].ToString());

            var typo = _f.Call("dwsim_thermo_resolve_compound", new { name = "Etanol" });
            Assert.Equal("not_found", typo["status"].ToString());
            Assert.Contains(typo["closest"], c => c.ToString() == "Ethanol");
        }

        [Fact]
        public void ReactionFromEquationChoosesTheModelFromTheData()
        {
            var id = FischerBasis("eqn");
            var k = _f.Call("dwsim_reaction_from_equation", new { flowsheet_id = id, name = "Ester", equation = "Acetic acid + Ethanol <-> Ethyl acetate + Water", phase = "Liquid", keq = 4.0 });
            Assert.Equal("equilibrium_constant", k["model_chosen"].ToString());
            Assert.Equal(4.0, (double)k["keq"], 9);
            Assert.Equal("Acetic acid", k["base_compound"].ToString());
            Assert.Equal(1.0, (double)k["equation_parsed"]["products"]["Water"], 9);

            var bad = Assert.Throws<ArgumentException>(() => _f.Call("dwsim_reaction_from_equation", new { flowsheet_id = id, name = "Bad", equation = "Ethanol -> Water", phase = "Liquid", keq = 1.0 }));
            Assert.Contains("does not balance", bad.Message);

            var none = Assert.Throws<ArgumentException>(() => _f.Call("dwsim_reaction_from_equation", new { flowsheet_id = id, name = "None", equation = "Acetic acid + Ethanol -> Ethyl acetate + Water", phase = "Liquid" }));
            Assert.Contains("placeholder", none.Message);

            var gas = _f.NewFlowsheet("gas");
            _f.Call("dwsim_thermo_add_compounds", new { flowsheet_id = gas, names = new[] { "Nitrogen", "Hydrogen", "Ammonia", "Oxygen", "Water" } });
            _f.Call("dwsim_thermo_set_property_package", new { flowsheet_id = gas, name = "PR" });
            var gibbs = _f.Call("dwsim_reaction_from_equation", new { flowsheet_id = gas, name = "NH3", equation = "Nitrogen + 3 Hydrogen <-> 2 Ammonia", phase = "Vapor" });
            Assert.Equal("equilibrium_gibbs", gibbs["model_chosen"].ToString());
            Assert.Equal(-3.0, (double)gibbs["stoichiometry"]["Hydrogen"], 9);
            var conv = _f.Call("dwsim_reaction_from_equation", new { flowsheet_id = gas, name = "Burn", equation = "2 Hydrogen + Oxygen -> 2 Water", phase = "Vapor", conversion_pct = 100.0, base_compound = "Oxygen", reaction_set = "BURN" });
            Assert.Equal("conversion", conv["model_chosen"].ToString());
            Assert.Equal("BURN", conv["reaction_set"].ToString());
        }

        [Fact]
        public void ReportBalanceAndWindowsOnAReactorPlant()
        {
            var id = FischerBasis("plant");
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "FEED", temperature_K = 300.0, pressure_Pa = 101325.0, molar_flow_mol_s = 1.0, composition = new JObject { ["Ethanol"] = 0.5, ["Acetic acid"] = 0.5 } });
            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "Heater", name = "H-1" });
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "HOT" });
            _f.Call("dwsim_stream_add_energy", new { flowsheet_id = id, name = "QH" });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "H-1", feed_stream = "FEED", product_stream = "HOT", energy_feed = "QH" });
            _f.Call("dwsim_unitop_set", new { flowsheet_id = id, name = "H-1", properties = new JObject { ["CalcMode"] = "OutletTemperature", ["OutletTemperature"] = 330.0 } });
            _f.Call("dwsim_reaction_from_equation", new { flowsheet_id = id, name = "Ester", equation = "Acetic acid + Ethanol <-> Ethyl acetate + Water", phase = "Liquid", keq = 4.0 });
            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "EquilibriumReactor", name = "R-1" });
            foreach (var s in new[] { "V-1", "L-1" }) _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = s });
            _f.Call("dwsim_stream_add_energy", new { flowsheet_id = id, name = "Q-1" });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "R-1", feed_stream = "HOT", product_stream = "V-1", product_port = 0 });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "R-1", product_stream = "L-1", product_port = 1, energy_feed = "Q-1" });
            _f.Call("dwsim_unitop_set_mode", new { flowsheet_id = id, name = "R-1", mode = "Isothermic" });
            Solve(id);

            var rep = _f.Call("dwsim_plant_report", new { flowsheet_id = id, product_stream = "L-1", product_compound = "Ethyl acetate", asked_product_rate_kg_s = 0.02 });
            Assert.True((bool)rep["solved"], rep.ToString());
            Assert.Equal(4, rep["streams"].Count());
            var feed = rep["streams"].First(s => s["tag"].ToString() == "FEED");
            Assert.Equal("feed", feed["boundary"].ToString());
            Assert.Equal("H-1", feed["to"].ToString());
            Assert.NotNull(feed["w"]["Ethanol"]);
            var qh = rep["duties"].First(d => d["tag"].ToString() == "QH");
            Assert.True((double)qh["signed_kW"] > 0, "heater duty should be added to the process: " + qh);
            Assert.True((double)rep["product"]["ratio_to_asked"] > 0);
            Assert.True((double)rep["product"]["purity_mass"] > 0.05);

            var bal = _f.Call("dwsim_plant_balance", new { flowsheet_id = id });
            Assert.Contains(bal["feeds"], s => s.ToString() == "FEED");
            Assert.Equal(2, bal["products"].Count());
            Assert.True((double)bal["closure"]["mass_rel"] < 1e-6, bal["closure"].ToString());
            Assert.True((double)bal["closure"]["worst_element_rel"] < 1e-6, bal["elements"].ToString());
            Assert.True((bool)bal["closure"]["ok"], bal["closure"].ToString());
            var ethanol = bal["compounds"].First(c => c["compound"].ToString() == "Ethanol");
            Assert.True((bool)ethanol["reacting"]);
            Assert.True((double)ethanol["rel"] > 0.1, "ethanol is consumed: " + ethanol);

            var chk = _f.Call("dwsim_plant_check", new
            {
                flowsheet_id = id,
                windows = new JArray(
                    new JObject { ["object"] = "H-1", ["quantity"] = "outlet_temperature", ["min_si"] = 300.0, ["max_si"] = 320.0 },
                    new JObject { ["object"] = "FEED", ["quantity"] = "pressure", ["min_si"] = 100000.0, ["max_si"] = null })
            });
            Assert.False((bool)chk["ok"], chk.ToString());
            Assert.Contains(chk["findings"], x => x["code"].ToString() == "WINDOW_VIOLATION" && x["object"].ToString() == "H-1");
            Assert.Contains(chk["findings"], x => x["code"].ToString() == "WINDOW_OK" && x["object"].ToString() == "FEED");
            Assert.Equal(2, (int)chk["windows_checked"]);
            Assert.DoesNotContain(chk["findings"], x => x["code"].ToString() == "REACTOR_PHASE_MISMATCH");
        }

        [Fact]
        public void CheckFindsPumpVapourCompressorLiquidAndReactorPhase()
        {
            var id = FischerBasis("checks");
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "STEAM", temperature_K = 420.0, pressure_Pa = 101325.0, molar_flow_mol_s = 1.0, composition = new JObject { ["Water"] = 1.0 } });
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "COLDWATER", temperature_K = 300.0, pressure_Pa = 101325.0, molar_flow_mol_s = 1.0, composition = new JObject { ["Water"] = 1.0 } });
            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "Compressor", name = "K-1" });
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "K-OUT" });
            _f.Call("dwsim_stream_add_energy", new { flowsheet_id = id, name = "W-K" });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "K-1", feed_stream = "COLDWATER", product_stream = "K-OUT", energy_feed = "W-K" });
            _f.Call("dwsim_unitop_set", new { flowsheet_id = id, name = "K-1", properties = new JObject { ["CalcMode"] = "OutletPressure", ["POut"] = 300000.0 } });
            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "Pump", name = "P-1" });
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "P-OUT" });
            _f.Call("dwsim_stream_add_energy", new { flowsheet_id = id, name = "W-P" });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "P-1", feed_stream = "STEAM", product_stream = "P-OUT", energy_feed = "W-P" });
            _f.Call("dwsim_unitop_set", new { flowsheet_id = id, name = "P-1", properties = new JObject { ["CalcMode"] = "OutletPressure", ["Pout"] = 300000.0 } });
            _f.Call("dwsim_solve_run", new { flowsheet_id = id, timeout_s = 120 }); // the pump may refuse the vapour; the checks read the inlets anyway
            var chk = _f.Call("dwsim_plant_check", new { flowsheet_id = id });
            var codes = chk["findings"].Select(x => x["code"].ToString()).ToList();
            Assert.Contains("PUMP_VAPOR_INLET", codes);
            Assert.Contains("COMPRESSOR_LIQUID_INLET", codes);
            Assert.NotEmpty(chk["explanations"]["COMPRESSOR_LIQUID_INLET"].ToString());

            // a liquid-phase reaction run where everything is vapour, in its own flowsheet so the pump error cannot stop it
            id = FischerBasis("checks2");
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "RXFEED", temperature_K = 450.0, pressure_Pa = 101325.0, molar_flow_mol_s = 1.0, composition = new JObject { ["Ethanol"] = 0.5, ["Acetic acid"] = 0.5 } });
            _f.Call("dwsim_reaction_from_equation", new { flowsheet_id = id, name = "Ester", equation = "Acetic acid + Ethanol <-> Ethyl acetate + Water", phase = "Liquid", keq = 4.0 });
            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "EquilibriumReactor", name = "R-1" });
            foreach (var s in new[] { "RV", "RL" }) _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = s });
            _f.Call("dwsim_stream_add_energy", new { flowsheet_id = id, name = "QR" });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "R-1", feed_stream = "RXFEED", product_stream = "RV", product_port = 0 });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "R-1", product_stream = "RL", product_port = 1, energy_feed = "QR" });
            _f.Call("dwsim_unitop_set_mode", new { flowsheet_id = id, name = "R-1", mode = "Isothermic" });
            Solve(id);
            var chk2 = _f.Call("dwsim_plant_check", new { flowsheet_id = id });
            Assert.Contains(chk2["findings"], x => x["code"].ToString() == "REACTOR_PHASE_MISMATCH");
        }

        [Fact]
        public void HeatExchangerConfiguresAndSurrogateTiesTheDuties()
        {
            var id = _f.NewFlowsheet("hx");
            _f.Call("dwsim_thermo_add_compounds", new { flowsheet_id = id, names = new[] { "Water" } });
            _f.Call("dwsim_thermo_set_property_package", new { flowsheet_id = id, name = "SteamTables" });
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "HOT-IN", temperature_K = 400.0, pressure_Pa = 500000.0, molar_flow_mol_s = 1.0, composition = new JObject { ["Water"] = 1.0 } });
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "COLD-IN", temperature_K = 300.0, pressure_Pa = 500000.0, molar_flow_mol_s = 1.0, composition = new JObject { ["Water"] = 1.0 } });
            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "HeatExchanger", name = "E-1" });
            foreach (var s in new[] { "HOT-OUT", "COLD-OUT" }) _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = s });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "E-1", feed_stream = "HOT-IN", feed_port = 0, product_stream = "HOT-OUT", product_port = 0 });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "E-1", feed_stream = "COLD-IN", feed_port = 1, product_stream = "COLD-OUT", product_port = 1 });

            var cross = Assert.Throws<ArgumentException>(() => _f.Call("dwsim_hx_configure", new { flowsheet_id = id, name = "E-1", mode = "specify_cold_outlet", cold_outlet_T_K = 450.0 }));
            Assert.Contains("cross", cross.Message);
            var cfg = _f.Call("dwsim_hx_configure", new { flowsheet_id = id, name = "E-1", mode = "specify_cold_outlet", cold_outlet_T_K = 350.0, hot_side_dp_Pa = 10000.0 });
            Assert.Equal("CalcTempHotOut", cfg["mode"].ToString());
            Assert.Equal("inlet 0", cfg["hot_side"].ToString());
            Solve(id);
            var cold = _f.Call("dwsim_stream_get_results", new { flowsheet_id = id, name = "COLD-OUT" });
            var hot = _f.Call("dwsim_stream_get_results", new { flowsheet_id = id, name = "HOT-OUT" });
            Assert.Equal(350.0, (double)cold["temperature_K"], 1);
            Assert.True((double)hot["temperature_K"] < 399.0, "hot side should cool: " + hot["temperature_K"]);
            Assert.Equal(490000.0, (double)hot["pressure_Pa"], 0);
            var after = _f.Call("dwsim_hx_configure", new { flowsheet_id = id, name = "E-1" });
            Assert.True((double)after["results"]["duty_kW"] > 1.0, after.ToString());

            // the surrogate on fresh streams
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "H2-IN", temperature_K = 400.0, pressure_Pa = 500000.0, molar_flow_mol_s = 1.0, composition = new JObject { ["Water"] = 1.0 } });
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "C2-IN", temperature_K = 300.0, pressure_Pa = 500000.0, molar_flow_mol_s = 1.0, composition = new JObject { ["Water"] = 1.0 } });
            var tie = _f.Call("dwsim_hx_tie_halves", new { flowsheet_id = id, name = "E-2", hot_in = "H2-IN", hot_out = "H2-OUT", cold_in = "C2-IN", cold_out = "C2-OUT", outlet_T_K = 350.0 });
            Assert.True((bool)tie["surrogate"]);
            Solve(id);
            double qAdded = (double)_f.Call("dwsim_object_get", new { flowsheet_id = id, target = "E-2-COLD", path = "DeltaQ" })["value"];
            double qRemoved = (double)_f.Call("dwsim_object_get", new { flowsheet_id = id, target = "E-2-HOT", path = "DeltaQ" })["value"];
            Assert.True(qAdded > 1.0, "heater duty " + qAdded);
            Assert.Equal(qAdded, qRemoved, 2);
            var c2 = _f.Call("dwsim_stream_get_results", new { flowsheet_id = id, name = "C2-OUT" });
            Assert.Equal(350.0, (double)c2["temperature_K"], 1);
        }

        [Fact]
        public void RecycleSeedCopiesTheOpenLoopState()
        {
            var id = FischerBasis("seed");
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "FEED", temperature_K = 300.0, pressure_Pa = 200000.0, molar_flow_mol_s = 1.0, composition = new JObject { ["Ethanol"] = 1.0 } });
            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "Mixer", name = "M-1" });
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "MIXED" });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "M-1", feed_stream = "FEED", feed_port = 0, product_stream = "MIXED" });
            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "Splitter", name = "S-1" });
            foreach (var s in new[] { "OUT", "BACK" }) _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = s });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "S-1", feed_stream = "MIXED", product_stream = "OUT", product_port = 0 });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "S-1", product_stream = "BACK", product_port = 1 });
            _f.Call("dwsim_object_set", new { flowsheet_id = id, target = "S-1", path = "Ratios[0]", value = 0.8 });
            _f.Call("dwsim_object_set", new { flowsheet_id = id, target = "S-1", path = "Ratios[1]", value = 0.2 });
            Solve(id);
            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "Recycle", name = "RCY" });
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "BACK-R" });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "RCY", feed_stream = "BACK", product_stream = "BACK-R" });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "M-1", feed_stream = "BACK-R", feed_port = 1 });
            var seed = _f.Call("dwsim_recycle_seed", new { flowsheet_id = id, name = "RCY" });
            Assert.Equal("BACK", seed["from"].ToString());
            Assert.Equal(0.2, (double)seed["molar_flow_mol_s"], 6);
            var st = _f.Call("dwsim_stream_get_results", new { flowsheet_id = id, name = "BACK-R" });
            Assert.Equal(0.2, (double)st["molar_flow_mol_s"], 6);
            Assert.Equal(300.0, (double)st["temperature_K"], 3);
            Solve(id);
            var rep = _f.Call("dwsim_plant_report", new { flowsheet_id = id });
            Assert.True((bool)rep["recycles"][0]["converged"], rep["recycles"].ToString());
            var bal = _f.Call("dwsim_plant_balance", new { flowsheet_id = id });
            Assert.True((bool)bal["closure"]["ok"], bal["closure"].ToString());
        }
    }
}
