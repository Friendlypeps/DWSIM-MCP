// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DWSIM.MCP.Server.Tests
{
    /// <summary>
    /// The thesis-shaped scenario, each step a tool call through the registry: a Fischer esterification
    /// (ethanol + acetic acid to ethyl acetate + water) with NRTL under a VLLE flash, an equilibrium
    /// reactor with a constant K, a shortcut column, a rigorous column, a component separator, an
    /// adjust, a sweep, a compound added late, and the XML export. Each test builds what it needs.
    /// </summary>
    [Collection("engine")]
    public class FischerPlantTests
    {
        private readonly EngineFixture _f;
        public FischerPlantTests(EngineFixture f) { _f = f; }

        private static readonly string[] Compounds = { "Ethanol", "Acetic acid", "Ethyl acetate", "Water" };

        private string Basis(string name)
        {
            var id = _f.NewFlowsheet(name);
            _f.Call("dwsim_thermo_add_compounds", new { flowsheet_id = id, names = Compounds });
            var pp = _f.Call("dwsim_thermo_set_property_package", new { flowsheet_id = id, name = "NRTL" });
            Assert.Equal(1, (int)pp["packages_in_flowsheet"]);
            return id;
        }

        private void Feed(string id, string tag = "FEED", double T = 350, double flow = 1.0)
        {
            _f.Call("dwsim_stream_add_material", new
            {
                flowsheet_id = id, name = tag, temperature_K = T, pressure_Pa = 101325.0, molar_flow_mol_s = flow,
                composition = new JObject { ["Ethanol"] = 0.5, ["Acetic acid"] = 0.5 }
            });
        }

        private JObject Solve(string id)
        {
            var r = _f.Call("dwsim_solve_run", new { flowsheet_id = id, timeout_s = 90 });
            Assert.True((bool)r["ok"], "solve failed: " + r.ToString());
            return r;
        }

        [Fact]
        public void PropertyPackageIsReplacedNotAppended()
        {
            var id = Basis("pp");
            var pr = _f.Call("dwsim_thermo_set_property_package", new { flowsheet_id = id, name = "PR" });
            Assert.Equal(1, (int)pr["packages_in_flowsheet"]);
            Assert.Equal("Peng-Robinson (PR)", pr["property_package"].ToString());
            Assert.Equal("NRTL", pr["replaced"][0].ToString());
            var appended = _f.Call("dwsim_thermo_set_property_package", new { flowsheet_id = id, name = "UNIQUAC", replace = false });
            Assert.Equal(2, (int)appended["packages_in_flowsheet"]);
        }

        [Fact]
        public void FlashSettingsAndBipsRoundTrip()
        {
            var id = Basis("thermo");
            var flash = _f.Call("dwsim_thermo_set_flash", new { flowsheet_id = id, equilibrium_type = "VLLE", approach = "NestedLoops" });
            Assert.Equal("VLLE", flash["flash"]["equilibrium_type"].ToString());
            Assert.Equal("NestedLoops", flash["flash_approach"].ToString());

            var bips = _f.Call("dwsim_thermo_get_bips", new { flowsheet_id = id });
            Assert.Equal("NRTL", bips["model"].ToString());
            Assert.Equal(6, bips["pairs"].Count()); // 4 compounds -> 6 pairs

            var est = _f.Call("dwsim_thermo_estimate_bips", new { flowsheet_id = id });
            Assert.Empty(est["missing"]);

            var set = _f.Call("dwsim_thermo_set_bips", new
            {
                flowsheet_id = id,
                pairs = new[] { new { c1 = "Ethyl acetate", c2 = "Water", A12 = 1000.0, A21 = 500.0, alpha12 = 0.35 } }
            });
            var pair = set["pairs"].First(p => p["c1"].ToString() == "Ethyl acetate" && p["c2"].ToString() == "Water");
            Assert.Equal(1000.0, (double)pair["values"]["A12"], 6);
            Assert.Equal(500.0, (double)pair["values"]["A21"], 6);
            Assert.Equal(0.35, (double)pair["values"]["alpha12"], 6);

            var describe = _f.Call("dwsim_thermo_get_property_package", new { flowsheet_id = id });
            Assert.Equal("NRTL", describe["bip_model"].ToString());
        }

        [Fact]
        public void ShortNamesAndBadNamesAreHandled()
        {
            var id = _f.NewFlowsheet("names");
            var ex = Assert.Throws<ArgumentException>(() => _f.Call("dwsim_thermo_add_compounds", new { flowsheet_id = id, names = new[] { "Etanol" } }));
            Assert.Contains("Ethanol", ex.Message);
            _f.Call("dwsim_thermo_add_compounds", new { flowsheet_id = id, names = new[] { "water", "64-17-5" } }); // case and CAS
            var again = _f.Call("dwsim_thermo_add_compounds", new { flowsheet_id = id, names = new[] { "Water" } });
            Assert.Single(again["already_present"]);
            var ex2 = Assert.Throws<ArgumentException>(() => _f.Call("dwsim_thermo_set_property_package", new { flowsheet_id = id, name = "no-such-package" }));
            Assert.Contains("not a property package", ex2.Message);
        }

        [Fact]
        public void EquilibriumReactorWithConstantKSolves()
        {
            var id = Basis("eq");
            Feed(id);
            var rx = _f.Call("dwsim_reaction_add_equilibrium", new
            {
                flowsheet_id = id, name = "Esterification",
                stoichiometry = new JObject { ["Ethanol"] = -1, ["Acetic acid"] = -1, ["Ethyl acetate"] = 1, ["Water"] = 1 },
                base_compound = "Acetic acid", phase = "Liquid", basis = "activity"
            });
            Assert.Equal("Gibbs", rx["keq_from"].ToString());
            var k = _f.Call("dwsim_reaction_set_equilibrium_constant", new { flowsheet_id = id, name = "Esterification", mode = "constant", keq = 4.0, tmin_K = 280.0, tmax_K = 450.0 });
            Assert.Equal("Constant", k["keq_mode"].ToString());
            Assert.Equal(4.0, (double)k["keq"], 9);
            Assert.True((double)k["heat_of_reaction_kJ_kmol"] != 0);

            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "EquilibriumReactor", name = "R-1" });
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "V-1" });
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "L-1" });
            _f.Call("dwsim_stream_add_energy", new { flowsheet_id = id, name = "Q-1" });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "R-1", feed_stream = "FEED", product_stream = "V-1", product_port = 0 });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "R-1", product_stream = "L-1", product_port = 1, energy_feed = "Q-1" });
            var mode = _f.Call("dwsim_unitop_set_mode", new { flowsheet_id = id, name = "R-1", mode = "Isothermic" });
            Assert.Equal("Isothermic", mode["mode"].ToString());

            Solve(id);
            var liquid = _f.Call("dwsim_stream_get_results", new { flowsheet_id = id, name = "L-1" });
            var mixture = liquid["phases"][0]["compounds"];
            Assert.True((double)mixture["Ethyl acetate"]["mole_fraction"] > 0.05, "no ester formed: " + liquid);
            Assert.True((double)mixture["Acetic acid"]["mole_fraction"] < 0.5);
        }

        [Fact]
        public void KineticAndHetCatReactionsAreCreatedAndEditable()
        {
            var id = Basis("kin");
            var kin = _f.Call("dwsim_reaction_add_kinetic", new
            {
                flowsheet_id = id, name = "K1",
                stoichiometry = new JObject { ["Ethanol"] = -1, ["Acetic acid"] = -1, ["Ethyl acetate"] = 1, ["Water"] = 1 },
                base_compound = "Acetic acid", a_forward = 1.2e-3, e_forward = 48000.0, a_reverse = 3e-4, e_reverse = 52000.0,
                e_forward_unit = "J/mol", reaction_set = "KIN"
            });
            Assert.Equal("Kinetic", kin["type"].ToString());
            Assert.Equal(1.0, (double)kin["direct_orders"]["Ethanol"], 9);
            Assert.Equal(0.0, (double)kin["direct_orders"]["Water"], 9);
            Assert.Equal(1.0, (double)kin["reverse_orders"]["Water"], 9);
            Assert.Equal("KIN", kin["reaction_set"].ToString());

            var edited = _f.Call("dwsim_reaction_set_kinetics", new { flowsheet_id = id, name = "K1", a_forward = 2e-3, direct_orders = new JObject { ["Ethanol"] = 0.5 }, tmax_K = 420.0 });
            Assert.Equal(2e-3, (double)edited["arrhenius"]["A_forward"], 12);
            Assert.Equal(0.5, (double)edited["direct_orders"]["Ethanol"], 9);
            Assert.Equal(420.0, (double)edited["t_range_K"][1], 9);

            var het = _f.Call("dwsim_reaction_add_hetcat", new
            {
                flowsheet_id = id, name = "H1",
                stoichiometry = new JObject { ["Ethanol"] = -1, ["Acetic acid"] = -1, ["Ethyl acetate"] = 1, ["Water"] = 1 },
                base_compound = "Ethanol", numerator_expression = "1e-4*exp(-3000/T)*R1*R2", denominator_expression = "(1+0.5*R1)^2",
                phase = "Liquid", basis = "molar concentration", amount_units = "mol/m3", rate_units = "mol/[kgcat.s]"
            });
            Assert.Equal("Heterogeneous_Catalytic", het["type"].ToString());
            Assert.Equal("Ethanol", het["rate_variables"]["R1"].ToString());

            var list = _f.Call("dwsim_reaction_list", new { flowsheet_id = id });
            Assert.Equal(2, list["reactions"].Count());
            var moved = _f.Call("dwsim_reactionset_add_reaction", new { flowsheet_id = id, reaction_set = "DefaultSet", reaction = "H1", rank = 1 });
            Assert.Contains(moved["reactions"], r => r["reaction"].ToString() == "H1");

            var ex = Assert.Throws<ArgumentException>(() => _f.Call("dwsim_reaction_add_kinetic", new
            {
                flowsheet_id = id, name = "Bad", stoichiometry = new JObject { ["Ethanol"] = -1, ["Water"] = 1 }, base_compound = "Ethanol", a_forward = 1.0, e_forward = 1.0
            }));
            Assert.Contains("does not balance", ex.Message);

            var del = _f.Call("dwsim_reaction_delete", new { flowsheet_id = id, name = "H1" });
            Assert.Single(del["remaining"]);
        }

        [Fact]
        public void ShortcutColumnConfiguresAndSolves()
        {
            var id = Basis("sc");
            _f.Call("dwsim_stream_add_material", new
            {
                flowsheet_id = id, name = "FEED", temperature_K = 345.0, pressure_Pa = 101325.0, molar_flow_mol_s = 1.0,
                composition = new JObject { ["Ethanol"] = 0.3, ["Acetic acid"] = 0.2, ["Ethyl acetate"] = 0.3, ["Water"] = 0.2 }
            });
            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "ShortcutColumn", name = "SC-1" });
            foreach (var s in new[] { "D", "B" }) _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = s });
            foreach (var s in new[] { "QC", "QR" }) _f.Call("dwsim_stream_add_energy", new { flowsheet_id = id, name = s });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "SC-1", feed_stream = "FEED", product_stream = "D", product_port = 0, energy_product = "QC" });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "SC-1", product_stream = "B", product_port = 1, energy_feed = "QR" });

            var before = _f.Call("dwsim_column_get", new { flowsheet_id = id, name = "SC-1" });
            Assert.NotNull(before["warning"]);
            var cfg = _f.Call("dwsim_column_shortcut_configure", new
            {
                flowsheet_id = id, name = "SC-1", light_key = "Ethyl acetate", heavy_key = "Acetic acid",
                light_key_in_bottoms = 0.02, heavy_key_in_distillate = 0.02, reflux_ratio = 3.0,
                condenser_pressure_Pa = 101325.0, reboiler_pressure_Pa = 111325.0, condenser_type = "TotalCond"
            });
            Assert.Equal("Ethyl acetate", cfg["light_key"].ToString());
            Assert.Equal(3.0, (double)cfg["reflux_ratio"], 9);

            Solve(id);
            var after = _f.Call("dwsim_column_get", new { flowsheet_id = id, name = "SC-1" });
            Assert.True((bool)after["calculated"]);
            Assert.True((double)after["results"]["actual_stages"] > 1, after.ToString());
            Assert.True((double)after["results"]["minimum_reflux_ratio"] > 0);

            // the generic reflection reaches the same field
            var got = _f.Call("dwsim_object_get", new { flowsheet_id = id, target = "SC-1", path = "m_lightkey" });
            Assert.Equal("Ethyl acetate", got["value"].ToString());
        }

        [Fact]
        public void RigorousColumnIsBuiltWiredSpecifiedAndSolves()
        {
            var id = _f.NewFlowsheet("dc");
            _f.Call("dwsim_thermo_add_compounds", new { flowsheet_id = id, names = new[] { "Benzene", "Toluene" } });
            _f.Call("dwsim_thermo_set_property_package", new { flowsheet_id = id, name = "PR" });
            _f.Call("dwsim_stream_add_material", new
            {
                flowsheet_id = id, name = "FEED", temperature_K = 365.0, pressure_Pa = 101325.0, molar_flow_mol_s = 1.0,
                composition = new JObject { ["Benzene"] = 0.5, ["Toluene"] = 0.5 }
            });
            var created = _f.Call("dwsim_column_create", new { flowsheet_id = id, name = "DC-1", type = "Distillation", stages = 12, condenser_type = "Total_Condenser", top_pressure_Pa = 101325.0, pressure_drop_Pa = 5000.0 });
            Assert.Equal(12, (int)created["stages"]);
            Assert.Equal("DistillationColumn", created["type"].ToString());
            Assert.Equal(106325.0, (double)created["bottom_pressure_Pa"], 3);
            Assert.NotNull(created["missing_connections"]);

            foreach (var s in new[] { "D", "B" }) _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = s });
            foreach (var s in new[] { "QC", "QR" }) _f.Call("dwsim_stream_add_energy", new { flowsheet_id = id, name = s });
            var wired = _f.Call("dwsim_column_connect", new
            {
                flowsheet_id = id, name = "DC-1",
                feeds = new[] { new { stream = "FEED", stage = 6 } },
                distillate = "D", bottoms = "B", condenser_duty = "QC", reboiler_duty = "QR"
            });
            Assert.Null(wired["missing_connections"]);
            Assert.True(wired["streams"].Any(s => s["stream"].ToString() == "FEED" && (int)s["stage"] == 6), "feed not on stage 6: " + wired["streams"]);

            var specs = _f.Call("dwsim_column_set_specs", new
            {
                flowsheet_id = id, name = "DC-1",
                condenser = new { type = "Reflux Ratio", value = 2.5 },
                reboiler = new { type = "Product_Molar_Flow_Rate", value = 0.5, units = "mol/s" }
            });
            Assert.Equal("Stream_Ratio", specs["specs"]["condenser"]["type"].ToString());
            Assert.Equal("Product_Molar_Flow_Rate", specs["specs"]["reboiler"]["type"].ToString());

            var bad = Assert.Throws<ArgumentException>(() => _f.Call("dwsim_column_set_specs", new { flowsheet_id = id, name = "DC-1", condenser = new { type = "Refluks", value = 2.0 } }));
            Assert.Contains("not a condenser spec type", bad.Message);

            Solve(id);
            var after = _f.Call("dwsim_column_get", new { flowsheet_id = id, name = "DC-1" });
            Assert.True((bool)after["calculated"], after.ToString());
            Assert.Equal(12, after["profile"].Count());
            var top = (double)after["profile"][0]["T_K"];
            var bottom = (double)after["profile"][11]["T_K"];
            Assert.True(bottom > top, $"T profile not increasing: {top} -> {bottom}");
            Assert.True(Math.Abs((double)after["duties_kW"]["reboiler"]) > 1.0, "no reboiler duty: " + after["duties_kW"]);
            Assert.True((double)after["profile"][5]["L_mol_s"] > 0.1, "stage liquid flow missing: " + after["profile"][5]);
            Assert.True((double)after["profile"][5]["V_mol_s"] > 0.1, "stage vapour flow missing: " + after["profile"][5]);
        }

        [Fact]
        public void ReactiveDistillationIsRefused()
        {
            var id = Basis("rd");
            var ex = Assert.Throws<ArgumentException>(() => _f.Call("dwsim_column_create", new { flowsheet_id = id, name = "RD-1", stages = 10, reaction_set = "DefaultSet" }));
            Assert.Contains("Reactive distillation is not available", ex.Message);
            Assert.DoesNotContain(_f.Call("dwsim_flowsheet_list_objects", new { flowsheet_id = id }).ToString(), "RD-1");
        }

        [Fact]
        public void ComponentSeparatorAndVesselModes()
        {
            var id = Basis("sep");
            Feed(id);
            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "ComponentSeparator", name = "CS-1" });
            foreach (var s in new[] { "OUT1", "OUT2" }) _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = s });
            _f.Call("dwsim_stream_add_energy", new { flowsheet_id = id, name = "QS" });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "CS-1", feed_stream = "FEED", product_stream = "OUT1", product_port = 0 });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "CS-1", product_stream = "OUT2", product_port = 1, energy_product = "QS" });
            var specs = _f.Call("dwsim_compsep_set_specs", new
            {
                flowsheet_id = id, name = "CS-1", specified_outlet = 0,
                specs = new JObject { ["Ethanol"] = 90, ["Acetic acid"] = new JObject { ["type"] = "PercentInletMolarFlow", ["value"] = 10 } }
            });
            Assert.Equal(2, specs["specs"].Count());
            Assert.Contains(specs["unspecified_compounds_go_to_other_outlet"], c => c.ToString() == "Water");
            Solve(id);
            var feed = _f.Call("dwsim_stream_get_results", new { flowsheet_id = id, name = "FEED" });
            var feedEthanol = (double)feed["molar_flow_mol_s"] * (double)feed["phases"][0]["compounds"]["Ethanol"]["mole_fraction"];
            var out1 = _f.Call("dwsim_stream_get_results", new { flowsheet_id = id, name = "OUT1" });
            var ethanolFlow = (double)out1["molar_flow_mol_s"] * (double)out1["phases"][0]["compounds"]["Ethanol"]["mole_fraction"];
            Assert.Equal(0.9 * feedEthanol, ethanolFlow, 3);

            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "Vessel", name = "V-1" });
            var listed = _f.Call("dwsim_unitop_set_mode", new { flowsheet_id = id, name = "V-1" });
            Assert.Contains(listed["modes"], m => m.ToString() == "Adiabatic");
            var set = _f.Call("dwsim_unitop_set_mode", new { flowsheet_id = id, name = "V-1", mode = "Adiabatic" });
            Assert.Equal("Adiabatic", set["mode"].ToString());
            var v = _f.Call("dwsim_vessel_configure", new { flowsheet_id = id, name = "V-1", override_t_K = 320.0 });
            Assert.True((bool)v["override_temperature"]);
            Assert.NotNull(v["warning"]); // no energy stream yet
        }

        [Fact]
        public void AdjustConvergesInsideTheSolve()
        {
            var id = Basis("adj");
            Feed(id, "FEED", 300.0);
            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "Heater", name = "H-1" });
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "HOT" });
            _f.Call("dwsim_stream_add_energy", new { flowsheet_id = id, name = "QH" });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "H-1", feed_stream = "FEED", product_stream = "HOT", energy_feed = "QH" });
            _f.Call("dwsim_unitop_set", new { flowsheet_id = id, name = "H-1", properties = new JObject { ["CalcMode"] = "OutletTemperature", ["OutletTemperature"] = 330.0 } });
            Solve(id);

            var props = _f.Call("dwsim_object_list_properties", new { flowsheet_id = id, target = "H-1", type = "WR" });
            var outletT = props["properties"].First(p => p["description"].ToString().IndexOf("Outlet Temperature", StringComparison.OrdinalIgnoreCase) >= 0)["id"].ToString();
            var dutyId = _f.Call("dwsim_object_list_properties", new { flowsheet_id = id, target = "H-1", type = "ALL" })["properties"]
                .First(p => p["description"].ToString().IndexOf("Heat Added", StringComparison.OrdinalIgnoreCase) >= 0)["id"].ToString();

            // a target inside the liquid range: 1.3 times the duty the 330 K outlet needs (the adjust solver's
            // Newton steps do not cross the bubble point gracefully, and neither would a person's)
            var dutyNow = (double)_f.Call("dwsim_object_list_properties", new { flowsheet_id = id, target = "H-1", type = "ALL" })["properties"]
                .First(p => p["id"].ToString() == dutyId)["value"];
            var target = Math.Round(1.3 * dutyNow, 3);
            var adj = _f.Call("dwsim_adjust_create", new
            {
                flowsheet_id = id, name = "ADJ-1",
                manipulated = new { @object = "H-1", property = outletT },
                controlled = new { @object = "H-1", property = dutyId },
                target_value = target, target_units = "kW", tolerance = 0.001, min = 300.0, max = 400.0
            });
            Assert.True((bool)adj["created"]);
            Assert.True((bool)adj["simultaneous"]);
            Assert.Equal(target, (double)adj["target_value_SI"], 6);

            Solve(id);
            var state = _f.Call("dwsim_adjust_get", new { flowsheet_id = id, name = "ADJ-1" });
            Assert.True(state["target_met"].Type == JTokenType.Boolean && (bool)state["target_met"], state.ToString());
            Assert.True((double)state["manipulated_value_SI"] > 330.0);
        }

        [Fact]
        public void SpecTiesTwoSpecifications()
        {
            var id = Basis("spec");
            Feed(id, "F1", 300.0);
            Feed(id, "F2", 300.0);
            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "Heater", name = "H-1" });
            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "Heater", name = "H-2" });
            foreach (var (h, f, o, q) in new[] { ("H-1", "F1", "O1", "Q1"), ("H-2", "F2", "O2", "Q2") })
            {
                _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = o });
                _f.Call("dwsim_stream_add_energy", new { flowsheet_id = id, name = q });
                _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = h, feed_stream = f, product_stream = o, energy_feed = q });
                _f.Call("dwsim_unitop_set", new { flowsheet_id = id, name = h, properties = new JObject { ["CalcMode"] = "OutletTemperature", ["OutletTemperature"] = 330.0 } });
            }
            var outletT = _f.Call("dwsim_object_list_properties", new { flowsheet_id = id, target = "H-1", type = "WR" })["properties"]
                .First(p => p["description"].ToString().IndexOf("Outlet Temperature", StringComparison.OrdinalIgnoreCase) >= 0)["id"].ToString();
            var spec = _f.Call("dwsim_spec_create", new
            {
                flowsheet_id = id, name = "SPEC-1",
                source = new { @object = "H-1", property = outletT },
                target = new { @object = "H-2", property = outletT },
                expression = "X + 20"
            });
            Assert.True((bool)spec["created"]);
            Solve(id);
            var o2 = _f.Call("dwsim_stream_get_results", new { flowsheet_id = id, name = "O2" });
            Assert.Equal(350.0, (double)o2["temperature_K"], 1);
        }

        [Fact]
        public void SweepRunsAGridAndRestores()
        {
            var id = Basis("sweep");
            Feed(id, "FEED", 300.0);
            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "Heater", name = "H-1" });
            _f.Call("dwsim_stream_add_material", new { flowsheet_id = id, name = "HOT" });
            _f.Call("dwsim_stream_add_energy", new { flowsheet_id = id, name = "QH" });
            _f.Call("dwsim_unitop_connect", new { flowsheet_id = id, unitop = "H-1", feed_stream = "FEED", product_stream = "HOT", energy_feed = "QH" });
            _f.Call("dwsim_unitop_set", new { flowsheet_id = id, name = "H-1", properties = new JObject { ["CalcMode"] = "OutletTemperature", ["OutletTemperature"] = 330.0 } });
            Solve(id);

            var vars = _f.Call("dwsim_study_list_variables", new { flowsheet_id = id, @object = "H-1" });
            var specs = vars["objects"][0]["specifications"];
            var outletT = specs.First(s => s["name"].ToString().IndexOf("Outlet Temperature", StringComparison.OrdinalIgnoreCase) >= 0)["property"].ToString();
            var results = vars["objects"][0]["results"];
            var duty = results.First(s => s["name"].ToString().IndexOf("Heat Added", StringComparison.OrdinalIgnoreCase) >= 0)["property"].ToString();

            var sweep = _f.Call("dwsim_study_sweep", new
            {
                flowsheet_id = id,
                variables = new[] { new { @object = "H-1", property = outletT, from = 310.0, to = 340.0, steps = 4 } },
                watches = new[] { new { @object = "H-1", property = duty } }
            });
            Assert.Equal(4, (int)sweep["points"]);
            Assert.Equal(4, (int)sweep["solved"]);
            var duties = sweep["rows"].Select(r => (double)r["outputs"][0]).ToList();
            Assert.True(duties.Zip(duties.Skip(1), (a, b) => b > a).All(x => x), "duty should rise with outlet T: " + string.Join(", ", duties));
            Assert.Equal("K", sweep["units"][0].ToString());
            var restored = _f.Call("dwsim_stream_get_results", new { flowsheet_id = id, name = "HOT" });
            Assert.Equal(330.0, (double)restored["temperature_K"], 1);
        }

        [Fact]
        public void CompoundAddedAfterStreamsReachesEveryPhase()
        {
            var id = Basis("late");
            Feed(id);
            var added = _f.Call("dwsim_thermo_add_compounds", new { flowsheet_id = id, names = new[] { "Methanol" } });
            Assert.Equal(1, (int)added["streams_updated"]);
            var phases = _f.Call("dwsim_object_get", new { flowsheet_id = id, target = "FEED", path = "Phases[0].Compounds", depth = 0 });
            Assert.NotNull(phases["value"]["Methanol"]);
            var mw = _f.Call("dwsim_compound_get", new { flowsheet_id = id, name = "Methanol" });
            Assert.Equal(32.04, (double)mw["molar_weight_g_mol"], 1);

            var tmp = Path.Combine(Path.GetTempPath(), $"mcp-test-{Guid.NewGuid():N}.json");
            try
            {
                _f.Call("dwsim_compound_export_json", new { flowsheet_id = id, name = "Methanol", path = tmp });
                var json = File.ReadAllText(tmp).Replace("\"Name\": \"Methanol\"", "\"Name\": \"MyMethanol\"").Replace("\"Name\":\"Methanol\"", "\"Name\":\"MyMethanol\"");
                var imported = _f.Call("dwsim_compound_import_json_inline", new { flowsheet_id = id, json });
                Assert.Equal("MyMethanol", imported["name"].ToString());
                Assert.Equal("added", imported["outcome"].ToString());
                Assert.Contains(imported["compounds"], c => c.ToString() == "MyMethanol");
            }
            finally { File.Delete(tmp); }

            var removed = _f.Call("dwsim_compound_remove", new { flowsheet_id = id, name = "MyMethanol" });
            Assert.DoesNotContain(removed["compounds"], c => c.ToString() == "MyMethanol");
        }

        [Fact]
        public void XmlExportHasNoSizeCut()
        {
            var id = Basis("xml");
            Feed(id);
            var tmp = Path.Combine(Path.GetTempPath(), $"mcp-test-{Guid.NewGuid():N}.xml");
            try
            {
                var r = _f.Call("dwsim_flowsheet_export_xml", new { flowsheet_id = id, path = tmp });
                Assert.True(File.Exists(tmp));
                Assert.True((long)r["bytes"] > 1000);
                Assert.NotNull(r["sections"]["SimulationObjects"]);
                var sections = _f.Call("dwsim_flowsheet_get_xml_section", new { flowsheet_id = id });
                Assert.Contains(sections["sections"], s => s["name"].ToString() == "Compounds");
                var one = _f.Call("dwsim_flowsheet_get_xml_section", new { flowsheet_id = id, section = "SimulationObjects", @object = "FEED" });
                Assert.Equal("SimulationObject", one["element"].ToString());
                Assert.Contains("MaterialStream", one["xml"].ToString());
                Assert.False((bool)one["truncated"]);
                var graphic = _f.Call("dwsim_flowsheet_get_xml_section", new { flowsheet_id = id, section = "GraphicObjects", @object = "FEED" });
                Assert.Contains("FEED", graphic["xml"].ToString());
            }
            finally { File.Delete(tmp); }
        }

        [Fact]
        public void ObjectToolsReadWriteInvokeAndDescribe()
        {
            var id = Basis("obj");
            Feed(id);
            var d = _f.Call("dwsim_object_describe", new { flowsheet_id = id, target = "FEED" });
            Assert.Equal("object", d["kind"].ToString());
            Assert.Contains(d["members"], m => m["name"].ToString() == "PropertyPackage");

            var pp = _f.Call("dwsim_object_get", new { flowsheet_id = id, target = "pp:", path = "FlashSettings" });
            Assert.NotNull(pp["value"]);
            var set = _f.Call("dwsim_object_set", new { flowsheet_id = id, target = "pp:", path = "FlashSettings[ForceEquilibriumCalculationType]", value = "VLLE" });
            Assert.Equal("VLLE", set["value"].ToString());
            var check = _f.Call("dwsim_thermo_get_property_package", new { flowsheet_id = id });
            Assert.Equal("VLLE", check["flash"]["equilibrium_type"].ToString());

            _f.Call("dwsim_unitop_add", new { flowsheet_id = id, type = "ComponentSeparator", name = "CS-1" });
            var created = _f.Call("dwsim_object_set", new
            {
                flowsheet_id = id, target = "CS-1", path = "ComponentSepSpecs[\"Water\"]",
                value = "{\"ComponentID\":\"Water\",\"SepSpec\":\"PercentInletMolarFlow\",\"SpecValue\":50}"
            });
            Assert.Equal(50.0, (double)created["value"]["SpecValue"], 9);

            var ports = _f.Call("dwsim_object_invoke", new { flowsheet_id = id, target = "CS-1", method = "GetConnectionPortsList" });
            Assert.True(ports["result"].Count() > 0);

            var deleted = _f.Call("dwsim_object_delete", new { flowsheet_id = id, target = "CS-1" });
            Assert.DoesNotContain(deleted["remaining"], t => t.ToString() == "CS-1");
        }

        [Fact]
        public void ScriptRunsHeadless()
        {
            var id = Basis("script");
            Feed(id);
            JObject r;
            try { r = _f.Call("dwsim_script_run", new { flowsheet_id = id, code = "print('hello from ' + str(Flowsheet.SimulationObjects.Count))\nFEED.Calculated = False" }); }
            catch (Exception ex) { throw new Xunit.Sdk.XunitException("script_run threw (headless scripting is unverified): " + (ex.InnerException ?? ex).Message); }
            Assert.True((bool)r["ran"], r.ToString());
            Assert.Contains(r["output"], l => l.ToString().Contains("hello from 1"));
        }
    }
}
