"""Offline tests of the builder's column and loop logic against a recording fake of the server (no DWSIM needed):
a failed solve before a column is refused naming the unit at fault, an unsolved feed never becomes a zero draw,
the column solver comes from the override / the decanter rule / the Wang-Henke fallback, the bottoms draw counts
unlisted heavy compounds, the missing end of a temperature profile comes from the feed's bubble point, and a
reopened loop cleans the column's connection table."""
import math
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)

from openrun_sim import contracts, recycles  # noqa: E402
from openrun_sim.builder import NAPHTALI_SANDHOLM, WANG_HENKE, Builder, stream_solved  # noqa: E402
from openrun_sim.compounds import resolve_all  # noqa: E402
from openrun_sim.errors import Refused  # noqa: E402
from openrun_sim.mcp import ToolError  # noqa: E402
from openrun_sim.run import Run  # noqa: E402
from openrun_sim.topology import Edge, Topology  # noqa: E402

NAMES = ["Ethyl acetate", "Water", "Ethanol", "Acetic acid"]
NBP = {"Ethyl acetate": 350.21, "Water": 373.15, "Ethanol": 351.44, "Acetic acid": 391.05}
SUBS = [{"formula": "C4H8O2", "name": "Ethyl acetate"}, {"formula": "H2O", "name": "Water"}, {"formula": "C2H6O", "name": "Ethanol"},
        {"formula": "C2H4O2", "name": "Acetic acid"}]


def state(mol_s, x, T=305.65, P=101325.0, mass=None):
    """What dwsim_stream_get_results returns (mole fractions as mass fractions too; the tests only read the molar side)."""
    return {"temperature_K": T, "pressure_Pa": P, "molar_flow_mol_s": mol_s, "mass_flow_kg_s": mol_s * 0.06 if mass is None else mass,
            "phases": [{"name": "Mixture", "compounds": {k: {"mole_fraction": v, "mass_fraction": v} for k, v in x.items()}}]}


ORGANIC = {"Ethyl acetate": 0.465, "Water": 0.285, "Ethanol": 0.218, "Acetic acid": 0.032}


class FakeClient:
    """Records every call; answers from the tables given, {} otherwise. solve: a list of dwsim_solve_run results, in order."""

    def __init__(self, streams=None, solve=None, fail=()):
        self.streams = dict(streams or {})
        self.solve_results = list(solve or [])
        self.fail = set(fail)
        self.calls = []
        self.log = []

    def call(self, tool, **a):
        self.calls.append((tool, a))
        if tool in self.fail:
            raise ToolError(f"{tool}: refused by the fake")
        if tool == "dwsim_flowsheet_create":
            return {"flowsheet_id": "scratch"}
        if tool == "dwsim_solve_run":
            return self.solve_results.pop(0) if self.solve_results else {"ok": True, "errors": [], "objects": []}
        if tool == "dwsim_stream_get_results":
            if a["name"] not in self.streams:
                raise ToolError("no such stream")
            return self.streams[a["name"]]
        if tool == "dwsim_compound_get":
            return {"normal_boiling_point_K": NBP.get(a["name"])}
        if tool == "dwsim_flowsheet_list_objects":
            return {"objects": []}
        if tool == "dwsim_flowsheet_summary":
            return {"compounds": NAMES}
        if tool == "dwsim_flowsheet_check":
            return {"findings": []}
        if tool == "dwsim_solve_diagnostics":
            return {"unsolved": []}
        return {}

    def try_call(self, tool, **a):
        try:
            return self.call(tool, **a)
        except ToolError:
            return None

    def named(self, tool):
        return [a for t, a in self.calls if t == tool]


def column_plant(bottoms=("C4H8O2",), distillate=("H2O", "C2H6O"), slots=None, overrides=None, feed_from_decanter=False):
    """One distillation step fed by the organic layer of a decanter (as a boundary feed unless feed_from_decanter): the ester is
    listed only in the bottoms, water and ethanol only in the distillate, and the acetic acid the feed carries on neither."""
    steps = []
    if feed_from_decanter:
        steps.append({"step_id": "S8", "order": 8, "unit_operation": "Decantation", "substances": SUBS,
                      "streams_in": [{"stream": "condensate", "components": ["C4H8O2", "H2O", "C2H6O", "C2H4O2"], "role": "intermediate", "step": ""}],
                      "streams_out": [{"stream": "organic phase", "components": ["C4H8O2", "H2O", "C2H6O"], "role": "intermediate", "step": "S9", "port": "organic"},
                                      {"stream": "aqueous phase", "components": ["H2O", "C2H6O"], "role": "waste", "step": "", "port": "aqueous"}]})
    steps.append({"step_id": "S9", "order": 9, "unit_operation": "Distillation", "substances": SUBS, "slots": slots or {},
                  "streams_in": [{"stream": "organic phase", "components": ["C4H8O2", "H2O", "C2H6O"], "role": "intermediate",
                                  "step": "S8" if feed_from_decanter else "", "port": "feed"}],
                  "streams_out": [{"stream": "ester tops", "components": list(distillate), "role": "intermediate", "step": "", "port": "distillate"},
                                  {"stream": "ester bottoms", "components": list(bottoms), "role": "product", "step": "", "port": "bottoms"}]})
    return contracts.Plant.of({"plant_id": "t", "target": {"product_formula": "C4H8O2", "product_name": "Ethyl acetate", "rate_kg_h": 95},
                               "process_steps": steps, "overrides": overrides or {}})


def builder_for(plant, client):
    run = Run(plant, client=client, log=lambda m: None)
    run.resolved = resolve_all(plant)
    run.fs = "fs"
    b = Builder(run, Topology(plant))
    return b


def build_column(b):
    b._node(b.topo.nodes["S9"])
    return b


class StreamState(unittest.TestCase):
    def test_uncalculated_stream_is_not_solved(self):
        self.assertTrue(stream_solved(state(0.636, ORGANIC)))
        self.assertFalse(stream_solved(state(0.0, ORGANIC, mass=0.0028)))            # DWSIM default mass flow, never calculated
        self.assertFalse(stream_solved(state(0.5, {"Water": float("nan"), "Ethanol": float("nan")})))
        self.assertFalse(stream_solved(None))


class ColumnBuild(unittest.TestCase):
    def test_failed_solve_before_column_is_refused_naming_the_unit(self):
        c = FakeClient(streams={"organic phase": state(0.0, ORGANIC, mass=0.0028)},
                       solve=[{"ok": False, "errors": ["S7-DISTILLATIO: Solver reached the maximum number of iterations without converging."],
                               "objects": [{"name": "S7-DISTILLATIO", "calculated": False, "error": "Solver reached the maximum number of iterations without converging."}]}])
        b = builder_for(column_plant(overrides={"column": {"S9": {"reboiler_spec": {"type": "feed_component_fraction", "compound": "H2O", "fraction": 0.95}}}}), c)
        b.run.units["S7"], b.run.unit_steps["S7-DISTILLATIO"] = "S7-DISTILLATIO", "S7"
        b.columns["S7-DISTILLATIO"] = {"step_id": "S7", "type": "Distillation", "solver": NAPHTALI_SANDHOLM, "source": "person"}
        with self.assertRaises(Refused) as cm:
            build_column(b)
        r = cm.exception
        self.assertEqual(r.step_id, "S7")
        self.assertEqual(r.what, "S7-DISTILLATIO")
        self.assertIn("maximum number of iterations", r.reason)
        self.assertIn("solving_method", r.resolve)
        self.assertEqual(c.named("dwsim_column_set_specs"), [], "no spec is sent from an unsolved feed")
        self.assertEqual(c.named("dwsim_column_create"), [], "the column is not even created")

    def test_wang_henke_column_is_retried_with_naphtali_sandholm(self):
        fail = {"ok": False, "errors": ["S7-DISTILLATIO: Solver reached the maximum number of iterations without converging."], "objects": []}
        c = FakeClient(streams={"organic phase": state(0.636, ORGANIC)}, solve=[fail, {"ok": True, "errors": []}])
        b = builder_for(column_plant(), c)
        b.run.units["S7"], b.run.unit_steps["S7-DISTILLATIO"] = "S7-DISTILLATIO", "S7"
        b.columns["S7-DISTILLATIO"] = {"step_id": "S7", "type": "Distillation", "solver": WANG_HENKE, "source": "server default"}
        build_column(b)
        sets = [a for a in c.named("dwsim_object_set") if a.get("path") == "SolvingMethodName"]
        self.assertEqual(len(sets), 1)
        self.assertEqual(sets[0]["target"], "S7-DISTILLATIO"); self.assertEqual(sets[0]["value"], NAPHTALI_SANDHOLM)
        self.assertEqual(b.columns["S7-DISTILLATIO"]["solver"], NAPHTALI_SANDHOLM)
        self.assertTrue(any("Naphtali" in a and "S7" in a for a in b.run.assumptions))
        self.assertEqual(len(c.named("dwsim_solve_run")), 2)
        self.assertTrue(b.run.stages[-1]["ok"]); self.assertIn("retried", b.run.stages[-1]["stage"])
        self.assertEqual(len(c.named("dwsim_column_create")), 1)

    def test_person_chosen_solver_is_never_replaced(self):
        fail = {"ok": False, "errors": ["S7-DISTILLATIO: did not converge"], "objects": []}
        c = FakeClient(streams={"organic phase": state(0.636, ORGANIC)}, solve=[fail, {"ok": True}])
        b = builder_for(column_plant(), c)
        b.run.units["S7"], b.run.unit_steps["S7-DISTILLATIO"] = "S7-DISTILLATIO", "S7"
        b.columns["S7-DISTILLATIO"] = {"step_id": "S7", "type": "Distillation", "solver": WANG_HENKE, "source": "person"}
        with self.assertRaises(Refused):
            build_column(b)
        self.assertEqual([a for a in c.named("dwsim_object_set") if a.get("path") == "SolvingMethodName"], [])

    def test_zero_feed_fraction_spec_is_refused_not_sent(self):
        c = FakeClient(streams={"organic phase": state(0.0, ORGANIC, mass=0.0028)})
        b = builder_for(column_plant(overrides={"column": {"S9": {"reboiler_spec": {"type": "feed_component_fraction", "compound": "H2O", "fraction": 0.95}}}}), c)
        with self.assertRaises(Refused) as cm:
            build_column(b)
        self.assertIn("not solved", cm.exception.reason); self.assertEqual(cm.exception.what, "organic phase")
        self.assertEqual(c.named("dwsim_column_set_specs"), [])

    def test_feed_fraction_of_an_absent_compound_is_refused(self):
        c = FakeClient(streams={"organic phase": state(0.636, {"Ethyl acetate": 1.0})})
        b = builder_for(column_plant(overrides={"column": {"S9": {"reboiler_spec": {"type": "feed_component_fraction", "compound": "H2O", "fraction": 0.95}}}}), c)
        with self.assertRaises(Refused) as cm:
            build_column(b)
        self.assertIn("carries no Water", cm.exception.reason)

    def test_solver_override_and_max_iterations_reach_column_create(self):
        c = FakeClient(streams={"organic phase": state(0.636, ORGANIC)})
        b = builder_for(column_plant(overrides={"column": {"S9": {"solving_method": "naphtali-sandholm", "max_iterations": 250}}}), c)
        build_column(b)
        cr = c.named("dwsim_column_create")[0]
        self.assertEqual(cr["solving_method"], NAPHTALI_SANDHOLM); self.assertEqual(cr["max_iterations"], 250)
        col = next(p for p in b.run.provenance if p["item"] == "column")
        self.assertEqual(col["value"]["solving_method"], NAPHTALI_SANDHOLM)
        self.assertEqual(next(p for p in b.run.provenance if p["item"] == "column solver")["source"], "person")
        with self.assertRaises(Refused):
            builder_for(column_plant(overrides={"column": {"S9": {"solving_method": "gauss"}}}), FakeClient(streams={"organic phase": state(0.636, ORGANIC)}))._node(
                Topology(column_plant()).nodes["S9"])

    def test_feed_from_a_decanter_defaults_to_naphtali_sandholm(self):
        c = FakeClient(streams={"organic phase": state(0.636, ORGANIC)})
        b = builder_for(column_plant(feed_from_decanter=True), c)
        build_column(b)
        self.assertEqual(c.named("dwsim_column_create")[0]["solving_method"], NAPHTALI_SANDHOLM)
        self.assertTrue(any("liquid-liquid split" in a and "S8" in a for a in b.run.assumptions))
        c2 = FakeClient(streams={"organic phase": state(0.636, ORGANIC)})
        b2 = builder_for(column_plant(), c2)
        build_column(b2)
        self.assertEqual(c2.named("dwsim_column_create")[0]["solving_method"], WANG_HENKE)

    def test_bottoms_draw_counts_unlisted_heavier_compounds(self):
        c = FakeClient(streams={"organic phase": state(0.636, ORGANIC)})
        b = builder_for(column_plant(), c)
        build_column(b)
        reb = c.named("dwsim_column_set_specs")[0]["reboiler"]
        self.assertEqual(reb["type"], "Product_Molar_Flow_Rate")
        self.assertAlmostEqual(reb["value"], 0.636 * (0.465 + 0.032), places=6)   # ethyl acetate (listed only in the bottoms) + acetic acid (unlisted, 118 C)
        a = next(x for x in b.run.assumptions if "bottoms draw" in x)
        self.assertIn("Acetic acid", a); self.assertIn("118 C", a); self.assertIn("check this specification", a)

    def test_bottoms_draw_without_boiling_points_reports_the_forced_overhead(self):
        c = FakeClient(streams={"organic phase": state(0.636, ORGANIC)})
        b = builder_for(column_plant(), c)
        b.nbp = {k: None for k in NAMES}  # no engine data and no offline value
        build_column(b)
        reb = c.named("dwsim_column_set_specs")[0]["reboiler"]
        self.assertAlmostEqual(reb["value"], 0.636 * 0.465, places=6)
        a = next(x for x in b.run.assumptions if "bottoms draw" in x)
        self.assertIn("Acetic acid", a); self.assertIn("forced overhead", a)

    def test_top_estimate_from_the_feed_bubble_point(self):
        c = FakeClient(streams={"organic phase": state(0.636, ORGANIC), "pt1": {"temperature_K": 343.4}})
        b = builder_for(column_plant(slots={"bottom_temperature": {"min_si": 350.15, "max_si": 351.15}}), c)
        build_column(b)
        est = c.named("dwsim_column_set_estimates")[0]["temperatures_K"]
        self.assertAlmostEqual(est[0], 343.4, places=3); self.assertAlmostEqual(est[-1], 350.65, places=3)
        self.assertTrue(all(est[i] < est[i + 1] for i in range(len(est) - 1)))
        flash = c.named("dwsim_stream_add_material")
        scratch = [a for a in flash if a.get("flowsheet_id") == "scratch"]
        self.assertEqual(len(scratch), 1); self.assertEqual(scratch[0]["vapor_fraction"], 0.0); self.assertEqual(scratch[0]["pressure_Pa"], 101325.0)
        self.assertEqual(c.named("dwsim_thermo_add_compounds")[0]["flowsheet_id"], "scratch")
        a = next(x for x in b.run.assumptions if "temperature estimates" in x)
        self.assertIn("bubble point", a); self.assertIn("70", a)

    def test_top_estimate_falls_back_to_the_lightest_listed_boiling_point(self):
        c = FakeClient(streams={"organic phase": state(0.636, ORGANIC)}, fail={"dwsim_flowsheet_create"})
        b = builder_for(column_plant(slots={"bottom_temperature": {"min_si": 350.15, "max_si": 351.15}}), c)
        build_column(b)
        est = c.named("dwsim_column_set_estimates")[0]["temperatures_K"]
        self.assertAlmostEqual(est[-1], 350.65, places=3)
        self.assertAlmostEqual(est[0], 350.65 - 5.0, places=3)   # ethanol (the lightest listed) boils at 351.4 K, above the stated bottom: held 5 K below it
        a = next(x for x in b.run.assumptions if "temperature estimates" in x)
        self.assertIn("Ethanol", a); self.assertIn("held 5 K below", a)

    def test_bottom_estimate_from_the_heaviest_listed_component(self):
        c = FakeClient(streams={"organic phase": state(0.636, ORGANIC)})
        b = builder_for(column_plant(bottoms=("C4H8O2", "C2H4O2"), slots={"top_temperature": {"min_si": 343.15, "max_si": 345.15}}), c)
        build_column(b)
        est = c.named("dwsim_column_set_estimates")[0]["temperatures_K"]
        self.assertAlmostEqual(est[0], 344.15, places=3); self.assertAlmostEqual(est[-1], 391.05, places=3)
        self.assertEqual([a for a in c.named("dwsim_stream_add_material") if a.get("flowsheet_id") == "scratch"], [])
        self.assertIn("Acetic acid", next(x for x in b.run.assumptions if "temperature estimates" in x))

    def test_both_ends_from_the_person_need_no_assumption(self):
        c = FakeClient(streams={"organic phase": state(0.636, ORGANIC)})
        b = builder_for(column_plant(overrides={"column": {"S9": {"top_temperature_K": 343.35, "bottom_temperature_K": 351.15}}}), c)
        build_column(b)
        est = c.named("dwsim_column_set_estimates")[0]["temperatures_K"]
        self.assertAlmostEqual(est[0], 343.35); self.assertAlmostEqual(est[-1], 351.15)
        self.assertFalse(any("temperature estimates" in x for x in b.run.assumptions))


class Loops(unittest.TestCase):
    def loop_builder(self, client, kind="column_feed"):
        b = builder_for(column_plant(), client)
        b.run.units["S9"], b.run.unit_steps["S9-COL"] = "S9-COL", "S9"
        e = Edge("acid return", "S9", "bottoms", "S3", "feed", ["C2H4O2"], "recycle", True)
        b.tear_targets["acid return"] = {"unit": "S9-COL", "kind": kind, "stage": 5, "port": 1, "step_id": "S9"}
        return b, e

    def test_reopen_syncs_the_column_connection_table(self):
        c = FakeClient()
        b, e = self.loop_builder(c)
        recycles._reopen(b, e, b.tear_targets["acid return"])
        deleted = [a["target"] for a in c.named("dwsim_object_delete")]
        self.assertEqual(deleted, ["RCY-acid return", "acid return-R"])
        inv = c.named("dwsim_object_invoke")
        self.assertEqual(len(inv), 1); self.assertEqual(inv[0]["target"], "S9-COL"); self.assertEqual(inv[0]["method"], "SyncConnectedStreams")
        self.assertEqual(c.calls.index(("dwsim_object_invoke", inv[0])) > c.calls.index(("dwsim_object_delete", {"flowsheet_id": "fs", "target": "acid return-R"})), True)
        self.assertEqual(len(c.named("dwsim_solve_run")), 1)
        c2 = FakeClient()
        b2, e2 = self.loop_builder(c2, kind="mixer")
        recycles._reopen(b2, e2, b2.tear_targets["acid return"])
        self.assertEqual(c2.named("dwsim_object_invoke"), [])

    def test_unseedable_tear_is_left_open_and_taken_apart(self):
        c = FakeClient(streams={"acid return": state(0.0, {"Acetic acid": 1.0}, mass=0.0028)}, fail={"dwsim_recycle_seed"})
        b, e = self.loop_builder(c)
        ok, why, wired = recycles._close_one(b, e, b.tear_targets["acid return"])
        self.assertFalse(ok); self.assertFalse(wired); self.assertIn("no solved state", why)
        self.assertEqual(c.named("dwsim_column_connect"), [], "the tear never reaches the column")
        self.assertEqual(c.named("dwsim_solve_run"), [], "no solve is attempted on a NaN loop")
        self.assertEqual([a["target"] for a in c.named("dwsim_object_delete")], ["RCY-acid return", "acid return-R"])
        self.assertEqual(c.named("dwsim_object_invoke")[0]["method"], "SyncConnectedStreams")

    def test_seed_refused_but_source_usable_is_copied_by_hand(self):
        src = state(0.4, {"Acetic acid": 0.9, "Water": 0.1}, T=390.0)
        c = FakeClient(streams={"acid return": src, "acid return-R": src}, fail={"dwsim_recycle_seed", "dwsim_plant_report"})
        b, e = self.loop_builder(c)
        ok, why, wired = recycles._close_one(b, e, b.tear_targets["acid return"])
        self.assertTrue(ok and wired, why)
        sc = c.named("dwsim_stream_set_conditions")[0]
        self.assertEqual(sc["name"], "acid return-R"); self.assertAlmostEqual(sc["molar_flow_mol_s"], 0.4); self.assertEqual(sc["temperature_K"], 390.0)
        self.assertEqual(c.named("dwsim_column_connect")[0]["feeds"], [{"stream": "acid return-R", "stage": 5}])

    def test_failed_closed_solve_names_the_unit(self):
        src = state(0.4, {"Acetic acid": 1.0})
        c = FakeClient(streams={"acid return": src, "acid return-R": src},
                       solve=[{"ok": False, "errors": ["S9-COL: Tried to calculate equilibrium with invalid mixture composition: { NaN, NaN, NaN, NaN }"], "objects": []}])
        b, e = self.loop_builder(c)
        b.columns["S9-COL"] = {"step_id": "S9", "type": "Distillation", "solver": NAPHTALI_SANDHOLM, "source": "person"}
        ok, why, wired = recycles._close_one(b, e, b.tear_targets["acid return"])
        self.assertFalse(ok); self.assertTrue(wired); self.assertIn("S9-COL", why); self.assertIn("NaN", why)

    def test_close_loops_stops_after_a_reopen_that_does_not_solve(self):
        src = state(0.4, {"Acetic acid": 1.0})
        bad = {"ok": False, "errors": ["S9-COL: One or more streams specified in the column connections window is missing."], "objects": []}
        c = FakeClient(streams={"acid return": src, "acid return-R": src, "ethanol return": src, "ethanol return-R": src}, solve=[bad, bad])
        b, e = self.loop_builder(c)
        b.columns["S9-COL"] = {"step_id": "S9", "type": "Distillation", "solver": NAPHTALI_SANDHOLM, "source": "person"}
        e2 = Edge("ethanol return", "S9", "distillate", "S3", "feed", ["C2H6O"], "recycle", True)
        b.tear_targets["ethanol return"] = {"unit": "S9-COL", "kind": "column_feed", "stage": 2, "step_id": "S9"}
        b.topo.edges += [e, e2]
        recycles.close_loops(b, mode="auto")
        self.assertEqual(b.run.loops_closed, [])
        self.assertEqual([l["stream"] for l in b.run.loops_open], ["acid return", "ethanol return"])
        self.assertIn("not attempted", b.run.loops_open[1]["reason"])
        self.assertEqual(len(c.named("dwsim_solve_run")), 2, "closed once, reopened once, then nothing")
        self.assertTrue(any("did not solve after loop acid return was reopened" in w for w in b.run.warnings))


if __name__ == "__main__":
    unittest.main()
