"""Minimal JSON-RPC client for the DWSIM-MCP server's HTTP transport (POST /mcp).

Shared by the gold-plant scripts in this folder. Standard library only.

    c = Client("http://127.0.0.1:5901")
    fs = c.call("dwsim_flowsheet_create", name="demo")["flowsheet_id"]
"""
import json
import sys
import time
import urllib.request


class ToolError(RuntimeError):
    pass


import os


class Client:
    def __init__(self, base=None, token=None, verbose=True):
        base = base or os.environ.get("DWSIM_MCP_URL", "http://127.0.0.1:5901")
        self.base = base.rstrip("/")
        self.token = token
        self.verbose = verbose
        self._id = 0
        self.log = []  # (tool, arguments, result or error)

    def rpc(self, method, params=None, timeout=600):
        self._id += 1
        body = {"jsonrpc": "2.0", "id": self._id, "method": method}
        if params is not None:
            body["params"] = params
        req = urllib.request.Request(self.base + "/mcp", data=json.dumps(body).encode(),
                                     headers={"Content-Type": "application/json"})
        if self.token:
            req.add_header("X-MCP-Token", self.token)
        with urllib.request.urlopen(req, timeout=max(timeout, 2 * int(os.environ.get("DWSIM_SOLVE_TIMEOUT", "300")))) as r:
            return json.loads(r.read().decode())

    def call(self, tool, **arguments):
        """Call a tool; return its JSON payload. Raises ToolError on a JSON-RPC error or isError result."""
        t0 = time.time()
        resp = self.rpc("tools/call", {"name": tool, "arguments": arguments})
        dt = time.time() - t0
        if "error" in resp:
            msg = resp["error"].get("message", str(resp["error"]))
            self.log.append((tool, arguments, {"error": msg}))
            if self.verbose:
                print(f"  !! {tool} -> ERROR {msg}", file=sys.stderr)
            raise ToolError(f"{tool}: {msg}")
        result = resp.get("result", {})
        content = result.get("content", [])
        text = "".join(c.get("text", "") for c in content if c.get("type") == "text")
        try:
            payload = json.loads(text) if text else {}
        except json.JSONDecodeError:
            payload = {"text": text}
        if result.get("isError"):
            self.log.append((tool, arguments, {"error": payload}))
            if self.verbose:
                print(f"  !! {tool} -> isError {text[:400]}", file=sys.stderr)
            raise ToolError(f"{tool}: {text[:2000]}")
        self.log.append((tool, arguments, payload))
        if self.verbose:
            print(f"  ok {tool} ({dt:.1f}s)", file=sys.stderr)
        return payload

    def try_call(self, tool, **arguments):
        """Like call, but returns None instead of raising."""
        try:
            return self.call(tool, **arguments)
        except ToolError:
            return None


# ---- helpers used by every plant script -------------------------------------------------------

def C(celsius):
    return celsius + 273.15


def bar(b):
    return b * 1e5


ATM = 101325.0

# g/mol, DWSIM databank values rounded; used only to turn mole fractions into the mass fractions
# that dwsim_stream_add_material / dwsim_stream_set_conditions take.
MW = {
    "Water": 18.015, "Ethanol": 46.069, "Acetic acid": 60.052, "Ethyl acetate": 88.106,
    "Acetaldehyde": 44.053, "Diethyl ether": 74.123, "Methyl ethyl ketone": 72.107,
    "Hydrogen": 2.016, "Nitrogen": 28.013, "Oxygen": 31.999, "Argon": 39.948,
    "Methane": 16.043, "Carbon monoxide": 28.010, "Carbon dioxide": 44.010, "Ammonia": 17.031,
    "Ethylene": 28.054, "Benzene": 78.114, "Toluene": 92.141, "Methanol": 32.042,
}


def mass_fractions(mole_fractions):
    """{compound: mole fraction} -> {compound: mass fraction} (normalised)."""
    m = {k: v * MW[k] for k, v in mole_fractions.items() if v > 0}
    tot = sum(m.values())
    return {k: v / tot for k, v in m.items()}


def add_feed(c, fs, name, T_K, P_Pa, mol_s, x):
    """Material stream from mole fractions and a molar flow."""
    return c.call("dwsim_stream_add_material", flowsheet_id=fs, name=name, temperature_K=T_K, pressure_Pa=P_Pa,
                  molar_flow_mol_s=mol_s, composition=mass_fractions(x))


def set_feed(c, fs, name, T_K=None, P_Pa=None, mol_s=None, x=None):
    args = {}
    if T_K is not None:
        args["temperature_K"] = T_K
    if P_Pa is not None:
        args["pressure_Pa"] = P_Pa
    if mol_s is not None:
        args["molar_flow_mol_s"] = mol_s
    if x is not None:
        args["composition"] = mass_fractions(x)
    return c.call("dwsim_stream_set_conditions", flowsheet_id=fs, name=name, **args)


def unit(c, fs, type_, name, feed=None, product=None, energy_in=None, energy_out=None,
         feed_port=0, product_port=0, props=None, mode=None, seed_from=None):
    """Add a unit and wire one feed / one product / one energy stream in a single call.

    Streams named here that do not exist yet are created empty first.
    """
    c.call("dwsim_unitop_add", flowsheet_id=fs, type=type_, name=name)
    existing = {o.get("name") or o.get("tag") for o in c.call("dwsim_flowsheet_list_objects", flowsheet_id=fs).get("objects", [])}
    for s in (product,):
        if s and s not in existing:
            c.call("dwsim_stream_add_material", flowsheet_id=fs, name=s)
    for s in (energy_in, energy_out):
        if s and s not in existing:
            c.call("dwsim_stream_add_energy", flowsheet_id=fs, name=s)
    args = {"flowsheet_id": fs, "unitop": name}
    if feed:
        args.update(feed_stream=feed, feed_port=feed_port)
    if product:
        args.update(product_stream=product, product_port=product_port)
    if energy_in:
        args["energy_feed"] = energy_in
    if energy_out:
        args["energy_product"] = energy_out
    c.call("dwsim_unitop_connect", **args)
    if mode:
        c.call("dwsim_unitop_set_mode", flowsheet_id=fs, name=name, mode=mode)
    if props:
        c.call("dwsim_unitop_set", flowsheet_id=fs, name=name, properties=props)
    if type_ == "Recycle" and feed and product:
        # a new stream starts as 1 kg/s equimolar at 25 C; seed the tear stream from the open-loop solution
        # of the stream it recycles (or of seed_from, a solved stream upstream of an unsolved tear), so the
        # first pass of the loop sees a sensible estimate
        try:
            r = c.call("dwsim_stream_get_results", flowsheet_id=fs, name=seed_from or feed)
            mix = r["phases"][0]["compounds"]
            comp = {k: v["mass_fraction"] for k, v in mix.items() if v.get("mass_fraction", 0) > 0}
            if r.get("molar_flow_mol_s", 0) > 0 and comp:
                c.call("dwsim_stream_set_conditions", flowsheet_id=fs, name=product, temperature_K=r["temperature_K"],
                       pressure_Pa=r["pressure_Pa"], molar_flow_mol_s=r["molar_flow_mol_s"], composition=comp)
        except ToolError:
            pass


def connect(c, fs, name, feed=None, product=None, energy_in=None, energy_out=None, feed_port=0, product_port=0,
            energy_in_port=0, energy_out_port=0):
    existing = {o.get("name") or o.get("tag") for o in c.call("dwsim_flowsheet_list_objects", flowsheet_id=fs).get("objects", [])}
    if product and product not in existing:
        c.call("dwsim_stream_add_material", flowsheet_id=fs, name=product)
    for s in (energy_in, energy_out):
        if s and s not in existing:
            c.call("dwsim_stream_add_energy", flowsheet_id=fs, name=s)
    args = {"flowsheet_id": fs, "unitop": name}
    if feed:
        args.update(feed_stream=feed, feed_port=feed_port)
    if product:
        args.update(product_stream=product, product_port=product_port)
    if energy_in:
        args.update(energy_feed=energy_in, energy_feed_port=energy_in_port)
    if energy_out:
        args.update(energy_product=energy_out, energy_product_port=energy_out_port)
    return c.call("dwsim_unitop_connect", **args)


def stream_summary(c, fs, name):
    """Compact view of a solved material stream: T, P, flows and mixture mole fractions."""
    r = c.call("dwsim_stream_get_results", flowsheet_id=fs, name=name)
    mix = r.get("phases", [{}])[0].get("compounds", {})
    out = {
        "stream": name,
        "T_C": round(r.get("temperature_K", float("nan")) - 273.15, 2),
        "P_bar": round(r.get("pressure_Pa", float("nan")) / 1e5, 3),
        "mol_s": round(r.get("molar_flow_mol_s", float("nan")), 5),
        "kg_s": round(r.get("mass_flow_kg_s", float("nan")), 5),
        "x": {k: round(v.get("mole_fraction", 0.0), 4) for k, v in mix.items() if v.get("mole_fraction", 0) > 1e-6},
        "w": {k: round(v.get("mass_fraction", 0.0), 4) for k, v in mix.items() if v.get("mass_fraction", 0) > 1e-6},
    }
    if "vapor_fraction" in r:
        out["vf"] = round(r["vapor_fraction"], 4)
    phases = r.get("phases", [])
    out["phases"] = [p.get("name") for p in phases[1:] if (p.get("molar_fraction") or p.get("mole_fraction") or 0) > 1e-6]
    return out


def print_streams(c, fs, names, title=None):
    if title:
        print(f"\n== {title}")
    rows = []
    for n in names:
        try:
            rows.append(stream_summary(c, fs, n))
        except ToolError as e:
            rows.append({"stream": n, "error": str(e)[:200]})
    for s in rows:
        if "error" in s:
            print(f"  {s['stream']:<14} ERROR {s['error']}")
            continue
        comp = " ".join(f"{k}={v:.3f}" for k, v in sorted(s["x"].items(), key=lambda kv: -kv[1]))
        print(f"  {s['stream']:<14} {s['T_C']:8.2f} C {s['P_bar']:7.3f} bar {s['mol_s']:10.4f} mol/s {s['kg_s']:9.4f} kg/s  x: {comp}")
    return rows


def solve(c, fs, timeout_s=None):
    timeout_s = timeout_s or int(os.environ.get("DWSIM_SOLVE_TIMEOUT", "300"))
    chk = c.call("dwsim_flowsheet_check", flowsheet_id=fs)
    bad = [f for f in chk.get("findings", []) if f.get("severity") in ("error", "blocking")]
    if bad:
        print("  check findings:", json.dumps(bad)[:1500], file=sys.stderr)
    r = c.call("dwsim_solve_run", flowsheet_id=fs, timeout_s=timeout_s)
    ok = bool(r.get("ok"))
    print(f"  solve ok={ok} in {r.get('elapsed_s', r.get('seconds', '?'))}s", file=sys.stderr)
    if not ok:
        print("  solve result:", json.dumps(r)[:3000], file=sys.stderr)
        d = c.try_call("dwsim_solve_diagnostics", flowsheet_id=fs)
        if d:
            print("  diagnostics:", json.dumps(d)[:3000], file=sys.stderr)
    return r


def save(c, fs, path):
    return c.call("dwsim_flowsheet_save", flowsheet_id=fs, filepath=path, compressed=True)


def compsep(c, fs, name, specified_outlet, specs):
    """dwsim_compsep_set_specs with an explicit 0 % entry for every compound not named.

    DWSIM 10.2.12's ComponentSeparator sends a compound with no spec entry to neither outlet (it
    vanishes from the balance), so every compound gets an entry here.
    """
    compounds = c.call("dwsim_flowsheet_summary", flowsheet_id=fs).get("compounds", [])
    full = {k: 0.0 for k in compounds}
    full.update(specs)
    return c.call("dwsim_compsep_set_specs", flowsheet_id=fs, name=name, specified_outlet=specified_outlet, specs=full, clear=True)


# ---- ethyl acetate / water: an LLE-consistent NRTL pair -----------------------------------------
# DWSIM's databank NRTL pair for ethyl acetate / water (A12 1286, A21 1606 cal/mol, alpha 0.4393) is
# VLE-fitted: it gives the heteroazeotrope temperature (70.6 C) but no liquid split at 25-40 C, so a
# decanter under it is one liquid. The pair below is regressed (alpha 0.2) to the handbook mutual
# solubilities at 25 C, 8.0 wt% ethyl acetate in water and 3.3 wt% water in ethyl acetate; with it the
# engine's VLLE flash splits the decanter feed and the heteroazeotrope still boils at 70.6 C.
ETOAC_WATER_LLE_BIP = {"c1": "Ethyl acetate", "c2": "Water", "A12": 349.75, "A21": 2176.38, "alpha12": 0.2}


def apply_lle_bips(c, fs):
    return c.call("dwsim_thermo_set_bips", flowsheet_id=fs, pairs=[ETOAC_WATER_LLE_BIP])


# ---- NRTL liquid-liquid split, computed outside the engine --------------------------------------
# DWSIM 10.2.12's three-phase flash does not find the ethyl acetate / water liquid split under NRTL
# (its stability test reports the feed unstable and returns the water-rich trial phase, but the
# three-phase flash collapses to one liquid). The decanters below therefore use the NRTL parameters
# the server reports (dwsim_thermo_get_bips, cal/mol) in a plain isoactivity LLE flash here, and hand
# the split to a ComponentSeparator. R in cal/(mol K) as the engine uses it.
import math

R_CAL = 1.98588


def nrtl_gamma(x, names, bips, T):
    """Activity coefficients. bips: {(i, j): (A_ij, A_ji, alpha)} with A in cal/mol, tau_ij = A_ij/(R T)."""
    n = len(names)
    tau = [[0.0] * n for _ in range(n)]
    G = [[1.0] * n for _ in range(n)]
    for i in range(n):
        for j in range(n):
            if i == j:
                continue
            key = (names[i], names[j])
            if key in bips:
                a_ij, a_ji, alpha = bips[key]
            elif (names[j], names[i]) in bips:
                a_ji, a_ij, alpha = bips[(names[j], names[i])]
            else:
                continue
            tau[i][j] = a_ij / (R_CAL * T)
            G[i][j] = math.exp(-alpha * tau[i][j])
    lng = []
    for i in range(n):
        s1 = sum(tau[j][i] * G[j][i] * x[j] for j in range(n))
        s2 = sum(G[k][i] * x[k] for k in range(n))
        term = s1 / s2
        for j in range(n):
            s3 = sum(G[k][j] * x[k] for k in range(n))
            s4 = sum(x[m] * tau[m][j] * G[m][j] for m in range(n))
            term += x[j] * G[i][j] / s3 * (tau[i][j] - s4 / s3)
        lng.append(term)
    return [math.exp(v) for v in lng]


def bips_from_server(c, fs):
    """{(c1, c2): (A12, A21, alpha12)} in the stored direction, from dwsim_thermo_get_bips."""
    out = {}
    for p in c.call("dwsim_thermo_get_bips", flowsheet_id=fs)["pairs"]:
        if not p.get("present"):
            continue
        v = p["values"]
        a, b = p["stored_as"].split("->")
        out[(a, b)] = (v["A12"], v["A21"], v.get("alpha12", 0.3))
    return out


def lle_split(z, names, bips, T, heavy="Water", iters=500):
    """Two-liquid isoactivity flash. Returns (beta_II, x_I, x_II) with phase II the heavy (water-rich) one,
    or None when the mixture is one liquid. z, x as dicts over names."""
    n = len(names)
    zz = [max(z.get(k, 0.0), 0.0) for k in names]
    tot = sum(zz)
    zz = [v / tot for v in zz]
    # initial estimate: phase II nearly pure heavy, phase I the rest
    ih = names.index(heavy)
    xII = [0.02 / (n - 1)] * n
    xII[ih] = 0.98
    xI = [v for v in zz]
    xI[ih] = max(0.02, zz[ih] * 0.2)
    s = sum(xI)
    xI = [v / s for v in xI]
    beta = 0.5
    for it in range(iters):
        gI = nrtl_gamma(xI, names, bips, T)
        gII = nrtl_gamma(xII, names, bips, T)
        K = [gI[i] / gII[i] for i in range(n)]  # x_II / x_I
        # Rachford-Rice for beta (fraction of phase II)
        lo, hi = 0.0, 1.0
        for _ in range(100):
            b = 0.5 * (lo + hi)
            f = sum(zz[i] * (K[i] - 1.0) / (1.0 + b * (K[i] - 1.0)) for i in range(n))
            if f > 0:
                lo = b
            else:
                hi = b
        beta_new = 0.5 * (lo + hi)
        xI_new = [zz[i] / (1.0 + beta_new * (K[i] - 1.0)) for i in range(n)]
        xII_new = [K[i] * xI_new[i] for i in range(n)]
        sI, sII = sum(xI_new), sum(xII_new)
        xI_new = [v / sI for v in xI_new]
        xII_new = [v / sII for v in xII_new]
        err = max(abs(xI_new[i] - xI[i]) for i in range(n)) + max(abs(xII_new[i] - xII[i]) for i in range(n))
        xI, xII, beta = xI_new, xII_new, beta_new
        if err < 1e-9:
            break
    if beta < 1e-4 or beta > 1 - 1e-4 or max(abs(xI[i] - xII[i]) for i in range(n)) < 1e-3:
        return None
    return beta, dict(zip(names, xI)), dict(zip(names, xII))


def decanter_specs(c, fs, feed_stream, bips, T=None):
    """Percent of each compound that goes to the light (organic) outlet, from the LLE of the feed stream."""
    s = c.call("dwsim_stream_get_results", flowsheet_id=fs, name=feed_stream)
    mix = s["phases"][0]["compounds"]
    names = list(mix.keys())
    z = {k: v["mole_fraction"] for k, v in mix.items()}
    Tk = T if T is not None else s["temperature_K"]
    r = lle_split(z, names, bips, Tk)
    if r is None:
        return None, None
    beta, xI, xII = r
    frac_org = {k: (1 - beta) * xI[k] / z[k] if z[k] > 1e-12 else 100.0 for k in names}
    return {k: 100.0 * min(max(v, 0.0), 1.0) for k, v in frac_org.items()}, (beta, xI, xII)
