"""Live test: runs only with OPENRUN_LIVE=1 and a server answering at DWSIM_MCP_URL (default http://127.0.0.1:5901).
Each sample takes minutes (the Fischer columns inside their loops longest).
Runs the two samples through the CLI and checks the report against samples/expected/*.summary.json."""
import json
import os
import subprocess
import sys
import unittest
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
URL = os.environ.get("DWSIM_MCP_URL", "http://127.0.0.1:5901")


def server_up():
    try:
        with urllib.request.urlopen(URL + "/health", timeout=3) as r:
            return r.status == 200
    except Exception:
        return False


@unittest.skipUnless(os.environ.get("OPENRUN_LIVE") == "1" and server_up(), "set OPENRUN_LIVE=1 with a DWSIM-MCP server at " + URL)
class Live(unittest.TestCase):
    def run_sample(self, name, extra=()):
        out = os.path.join(ROOT, "out", "test_" + name)
        cmd = [sys.executable, "-m", "openrun_sim", "run", f"samples/{name}.plant.json", "--overrides", f"samples/overrides/{name}.overrides.json",
               "--out", out, "-q", *extra]
        env = dict(os.environ, DWSIM_SOLVE_TIMEOUT=os.environ.get("DWSIM_SOLVE_TIMEOUT", "600"))
        p = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True, env=env, timeout=3600)
        self.assertIn(p.returncode, (0,), p.stdout[-2000:] + p.stderr[-2000:])
        with open(os.path.join(out, "report.json"), encoding="utf-8") as f:
            return json.load(f)

    def check_expected(self, name, rep):
        with open(os.path.join(ROOT, "samples", "expected", f"{name}.summary.json"), encoding="utf-8") as f:
            exp = json.load(f)
        self.assertTrue(rep["plant_report"]["solved"])
        self.assertEqual(rep["check"]["errors"], 0, rep["check"]["findings"])
        prod = rep["plant_report"]["product"]
        self.assertAlmostEqual(prod["ratio_to_asked"], exp.get("product_ratio_expected", 1.0), delta=exp["product_ratio_tolerance"])
        self.assertGreaterEqual(prod["purity_mass"], exp["min_purity_mass"])
        self.assertEqual(len(rep["run"]["loops_closed"]), exp["loops_closed"])
        self.assertEqual(len(rep["run"]["loops_open"]), exp["loops_open"])
        if exp["loops_open"] == 0:
            self.assertTrue(rep["balance"]["closure"]["ok"], rep["balance"]["closure"])
        streams = {s["tag"]: s for s in rep["plant_report"]["streams"]}
        for tag, want in exp["streams"].items():
            s = streams[tag]
            if "T_C" in want:
                self.assertAlmostEqual(s["T_K"] - 273.15, want["T_C"][0], delta=want["T_C"][1], msg=tag)
            for comp, (val, tol) in want.get("x", {}).items():
                self.assertAlmostEqual(s["x"].get(comp, 0.0), val, delta=tol, msg=f"{tag} {comp}")

    def test_haber_bosch_closed_loop(self):
        rep = self.run_sample("ammonia_haber_bosch_llm_opus")
        self.check_expected("ammonia_haber_bosch_llm_opus", rep)

    def test_fischer_open_loops_quantified(self):
        rep = self.run_sample("etoac_fischer_llm_opus", ["--open-loops", "auto"])
        self.check_expected("etoac_fischer_llm_opus", rep)
        self.assertTrue(rep["balance"]["open_loop_mismatch"])

    def test_refusals_sample_refuses(self):
        out = os.path.join(ROOT, "out", "test_refusals")
        p = subprocess.run([sys.executable, "-m", "openrun_sim", "run", "samples/refusals.plant.json", "--out", out, "-q"], cwd=ROOT, capture_output=True, text=True, timeout=600)
        self.assertEqual(p.returncode, 2, p.stderr)
        self.assertIn("C2H6O", p.stderr)


if __name__ == "__main__":
    unittest.main()
