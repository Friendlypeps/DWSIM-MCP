"""Offline tests: no server needed. python3 -m unittest discover -s tests (from examples/openrun_sim)."""
import json
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)

from openrun_sim import concepts, contracts  # noqa: E402
from openrun_sim.compounds import elements, hill, package_for, resolve_all  # noqa: E402
from openrun_sim.errors import Ambiguous, OpenBound, Refused  # noqa: E402
from openrun_sim.feeds import feed_specs  # noqa: E402
from openrun_sim.reactions import balance, model_for, parse_equation  # noqa: E402
from openrun_sim.run import Run  # noqa: E402
from openrun_sim.setpoints import setpoint, windows_for  # noqa: E402
from openrun_sim.topology import Topology  # noqa: E402
from openrun_sim.__main__ import load_plant, offline_plan  # noqa: E402

S = os.path.join(ROOT, "samples")


def quiet(plant):
    r = Run(plant, log=lambda m: None)
    r.resolved = resolve_all(plant)
    return r


class Concepts(unittest.TestCase):
    def test_every_vocabulary_concept_is_classified(self):
        names = {c.concept for c in concepts.CONCEPTS}
        for must in ("Separation", "Reaction", "HeatTransfer", "Mixing", "Conveying", "SizeChange", "Distillation", "Rectification",
                     "VacuumDistillation", "FlashDistillation", "Evaporation", "Drying", "Crystallisation", "Filtration", "Centrifugation",
                     "Sedimentation", "PhaseSeparation", "Decantation", "Extraction", "LiquidLiquidExtraction", "Absorption", "GasScrubbing",
                     "Stripping", "Adsorption", "Esterification", "Acetylation", "Carboxylation", "Nitration", "Hydrolysis", "Dehydrogenation",
                     "TishchenkoReaction", "Oxidation", "Hydrogenation", "StirredTankReaction", "FixedBedReaction", "TubularReaction",
                     "GauzeReaction", "SlurryReaction", "HomogeneousGasReaction", "ReactiveDistillation", "Heating", "Vaporisation", "Cooling",
                     "Condensation", "WasteHeatRecovery", "Stirring", "GasMixing", "AcidMixing", "Dissolution", "Pumping", "Dosing",
                     "Compression", "Storage", "Recycling", "Milling"):
            self.assertIn(must, names)
        for c in concepts.CONCEPTS:
            self.assertTrue(c.recipe or c.reason, c.concept)
        self.assertEqual(concepts.lookup("http://x/vocab#Decantation").recipe, "vessel_lle")
        self.assertEqual(concepts.lookup("Crystallization").recipe, None)
        self.assertIsNone(concepts.lookup("Teleportation"))

    def test_generic_roots_and_solids_are_refused(self):
        for root in ("Separation", "HeatTransfer", "Conveying", "ThermalSeparation", "MechanicalSeparation", "MassTransferSeparation"):
            self.assertIsNone(concepts.lookup(root).recipe, root)
        for solid in ("Crystallisation", "Filtration", "Centrifugation", "Milling"):
            self.assertIsNone(concepts.lookup(solid).recipe, solid)
        self.assertEqual(concepts.lookup("Stripping").recipe, "stripper")
        self.assertEqual(concepts.lookup("WasteHeatRecovery").recipe, "hx")
        self.assertEqual(concepts.lookup("Sedimentation").recipe, "vessel_lle")


class Formulas(unittest.TestCase):
    def test_hill_and_elements(self):
        self.assertEqual(elements("CH3COOH"), {"C": 2, "H": 4, "O": 2})
        self.assertEqual(hill("CH3COOH"), "C2H4O2")
        self.assertEqual(hill("H3N"), "H3N")
        self.assertEqual(hill("NH3"), "H3N")
        self.assertEqual(elements("Ca(OH)2"), {"Ca": 1, "O": 2, "H": 2})

    def test_equation_parse_and_balance(self):
        r, p, rev = parse_equation("2 C2H6O -> C4H8O2 + 2 H2")
        self.assertEqual(r, [(2.0, "C2H6O")]); self.assertEqual(p, [(1.0, "C4H8O2"), (2.0, "H2")]); self.assertFalse(rev)
        self.assertEqual(balance(r, p), {})
        r, p, rev = parse_equation("N2 + 3 H2 <-> 2 H3N")
        self.assertTrue(rev); self.assertEqual(balance(r, p), {})
        r, p, _ = parse_equation("CH4 + H2O -> CO + 2 H2")
        self.assertEqual(balance(r, p), {"H": -2.0})

    def test_package_rule(self):
        self.assertEqual(package_for(["Hydrogen", "Methane"], False)[0], "PR")
        self.assertEqual(package_for(["Ethanol", "Water"], False)[0], "NRTL")
        self.assertEqual(package_for(["Ethanol", "Water"], True, override="UNIQUAC")[0], "UNIQUAC")


class Windows(unittest.TestCase):
    def test_parse_window(self):
        r = contracts.parse_window("70 to 80 degC", "temperature")
        self.assertAlmostEqual(r.min_si, 343.15); self.assertAlmostEqual(r.max_si, 353.15)
        self.assertAlmostEqual(contracts.parse_window("1 atm", "pressure").max_si, 101325)
        r = contracts.parse_window("25-40 bar", "pressure"); self.assertAlmostEqual(r.min_si, 25e5); self.assertAlmostEqual(r.max_si, 40e5)
        r = contracts.parse_window("-25 to -10 degC", "temperature"); self.assertAlmostEqual(r.min_si, 248.15)
        self.assertIsNone(contracts.parse_window("", "temperature"))

    def test_intersection_midpoint_open_bound_and_override(self):
        plant = load_plant(os.path.join(S, "etoac_fischer_llm_opus.plant.json"))
        u3 = plant.step("U3")
        rng, src = windows_for(plant, u3)["temperature"]
        self.assertAlmostEqual(rng.min_si, 343.15); self.assertAlmostEqual(rng.max_si, 353.15)   # document inside the module range
        v, s = setpoint(plant, u3, "temperature"); self.assertAlmostEqual(v, 348.15); self.assertIn("midpoint", s)
        u4 = plant.step("U4")
        rng, _ = windows_for(plant, u4)["temperature"]
        self.assertAlmostEqual(rng.max_si, 363.15)  # module 10-90 C cuts the document's 80-110 C
        ref = load_plant(os.path.join(S, "refusals.plant.json"))
        with self.assertRaises(OpenBound):
            setpoint(ref, ref.step("S1"), "pressure")
        ref.overrides.setpoints["S1"] = {"pressure_Pa": 250000}
        self.assertEqual(setpoint(ref, ref.step("S1"), "pressure"), (250000.0, "person"))
        with self.assertRaises(Refused):
            ref.overrides.setpoints["S1"] = {"pressure_Pa": 100000}; setpoint(ref, ref.step("S1"), "pressure")

    def test_zero_bound_is_refused(self):
        with self.assertRaises(Refused):
            contracts.Range.of({"min_si": 0, "max_si": 5})


class Reactions(unittest.TestCase):
    def test_model_order_and_placeholder_labelling(self):
        plant = load_plant(os.path.join(S, "etoac_fischer_llm_opus.plant.json"))
        run = quiet(plant); u4 = plant.step("U4")
        m = model_for(u4, "Liquid", None, run)
        self.assertEqual(m["kind"], "equilibrium_constant"); self.assertTrue(m["placeholder"]); self.assertEqual(len(run.placeholders), 1)
        m = model_for(u4, "Liquid", {"keq": 4.0, "source": "document"}, run)
        self.assertFalse(m["placeholder"]); self.assertEqual(m["args"]["keq"], 4.0)
        m = model_for(u4, "Vapor", None, run); self.assertEqual(m["kind"], "gibbs"); self.assertFalse(m["placeholder"])
        m = model_for(u4, "Liquid", {"conversion_pct": 60}, run); self.assertEqual(m["kind"], "conversion")
        m = model_for(u4, "Liquid", {"kinetics": {"a_forward": 1e3, "e_forward": 5e4}}, run); self.assertEqual(m["kind"], "kinetic")


class Compounds(unittest.TestCase):
    def test_ambiguous_formula_refused_and_name_resolves(self):
        ref = load_plant(os.path.join(S, "refusals.plant.json"))
        with self.assertRaises(Refused) as cm:
            resolve_all(ref)
        self.assertIn("C2H6O", str(cm.exception))
        ref.overrides.compounds["C2H6O"] = "Ethanol"
        res = resolve_all(ref)
        self.assertEqual(res.name("C2H6O"), "Ethanol"); self.assertEqual(res["C2H6O"]["source"], "person")
        self.assertEqual(res.name("C2H4O2"), "Acetic acid")  # the name in substances decides


class Feeds(unittest.TestCase):
    def test_feeds_from_target_and_ratio(self):
        plant = load_plant(os.path.join(S, "etoac_fischer_llm_opus.plant.json"), os.path.join(S, "overrides", "etoac_fischer_llm_opus.overrides.json"))
        run = quiet(plant)
        specs = {f["stream"]: f for f in feed_specs(plant, run.resolved, run)}
        prod = 300.0 / 3600 * 1000 / 88.106
        self.assertAlmostEqual(specs["ACID"]["mol_s"], prod, places=4)
        self.assertAlmostEqual(specs["ETOH"]["mol_s"], 1.1 * prod, places=4)
        self.assertEqual(specs["WATER-ADD"]["mol_s"], 0.5)
        hb = load_plant(os.path.join(S, "ammonia_haber_bosch_llm_opus.plant.json"), os.path.join(S, "overrides", "ammonia_haber_bosch_llm_opus.overrides.json"))
        run = quiet(hb)
        specs = {f["stream"]: f for f in feed_specs(hb, run.resolved, run)}
        self.assertAlmostEqual(specs["STEAM"]["mol_s"], 3.0 * specs["NATGAS"]["mol_s"])
        self.assertAlmostEqual(specs["AIR"]["x"]["Nitrogen"], 0.78)

    def test_utility_without_amount_is_refused(self):
        plant = load_plant(os.path.join(S, "etoac_fischer_llm_opus.plant.json"))
        run = quiet(plant)
        with self.assertRaises(Refused) as cm:
            feed_specs(plant, run.resolved, run)
        self.assertIn("WATER-ADD", str(cm.exception))


class TopologyAndSamples(unittest.TestCase):
    def test_tears_boundary_and_passthrough(self):
        plant = load_plant(os.path.join(S, "etoac_fischer_llm_opus.plant.json"))
        t = Topology(plant)
        self.assertEqual({e.stream for e in t.tears()}, {"RCY-ACID", "ORG-REFLUX", "RCY-AZEO", "RCY-ETOH"})
        self.assertTrue(t.nodes["U1a"].passthrough); self.assertTrue(t.nodes["U10"].passthrough)
        self.assertFalse(t.nodes["U7s"].passthrough)  # a Recycling step with two outlets is a splitter
        self.assertEqual({e.stream for e in t.boundary_products()}, {"PRODUCT", "WASTEWATER"})
        self.assertEqual(t.unmapped(), [])
        hb = load_plant(os.path.join(S, "ammonia_haber_bosch_llm_opus.plant.json"))
        self.assertEqual(len(Topology(hb).nodes["U1"].inlets), 2)  # two boundary feeds into one step

    def test_samples_validate_and_refusal_sample_refuses(self):
        for name in ("etoac_fischer_llm_opus", "ammonia_haber_bosch_llm_opus"):
            plant = load_plant(os.path.join(S, f"{name}.plant.json"), os.path.join(S, "overrides", f"{name}.overrides.json"))
            run = quiet(plant)
            topo, rows = offline_plan(plant, run)
            self.assertEqual([r.text() for r in run.refusals], [], name)
        ref = load_plant(os.path.join(S, "refusals.plant.json"))
        with self.assertRaises(Refused):
            quiet(ref)
        ref.overrides.compounds["C2H6O"] = "Ethanol"
        run = quiet(ref)
        topo, rows = offline_plan(plant=ref, run=run)
        texts = " | ".join(r.text() for r in run.refusals)
        self.assertIn("open on one side", texts)
        self.assertIn("Crystallizer", texts)

    def test_unknown_keys_are_refused(self):
        with self.assertRaises(Refused):
            contracts.Plant.of({"plant_id": "x", "target": {"product_formula": "H2O", "rate_kg_h": 1}, "process_steps": [], "bogus": 1})
        with self.assertRaises(Refused):
            contracts.ProcessStep.of({"step_id": "a", "order": 1, "unit_operation": "Heating", "slots": {"colour": "red"}})


if __name__ == "__main__":
    unittest.main()
