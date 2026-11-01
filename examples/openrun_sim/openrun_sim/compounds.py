"""Any chemical: a Hill formula (plus name, CAS, InChIKey when the entry carries them) -> the databank name
the engine takes, the molar mass, and the property package the basis needs.

Rules (brief section 3): the formula alone is often ambiguous (C2H6O is ethanol or dimethyl ether) so the
name and CAS decide; an InChIKey is echoed and mapped to a CAS through data/inchikey_cas.json because the
10.2.12 databanks carry no InChI; an override (formula -> databank name) wins over everything and is reported
with source 'person'. Any ambiguous / conflicting / unknown formula refuses the run before a flowsheet exists.
"""
import json
import os
import re

from .errors import Ambiguous, Refused

_DATA = os.path.join(os.path.dirname(__file__), "data")


def _load(name):
    with open(os.path.join(_DATA, name), encoding="utf-8") as f:
        d = json.load(f)
    if isinstance(d, dict):
        d.pop("_note", None)
    return d


PERMANENT_GASES = set(_load("permanent_gases.json"))
INCHIKEY_CAS = _load("inchikey_cas.json")
KNOWN = _load("known_compounds.json")
LLE_PAIRS = _load("lle_bips.json")["pairs"]

_ELEM = re.compile(r"([A-Z][a-z]?)(\d*)")


def elements(formula):
    """Atom counts of a Hill or simple structural formula ('CH3COOH' -> {C:2,H:4,O:2}); parentheses with a multiplier."""
    f = formula.strip().replace("·", "").replace(" ", "")

    def parse(s, i=0):
        counts, n = {}, len(s)
        while i < n:
            ch = s[i]
            if ch == "(":
                sub, i = parse(s, i + 1)
                m = re.match(r"\d+", s[i:])
                k = int(m.group(0)) if m else 1
                i += len(m.group(0)) if m else 0
                for e, c in sub.items():
                    counts[e] = counts.get(e, 0) + c * k
            elif ch == ")":
                return counts, i + 1
            else:
                m = _ELEM.match(s, i)
                if not m or not m.group(1):
                    raise Refused(f"cannot read formula {formula!r} at {s[i:]!r}")
                e, k = m.group(1), int(m.group(2) or 1)
                counts[e] = counts.get(e, 0) + k
                i = m.end()
        return counts, i

    c, _ = parse(f)
    if not c:
        raise Refused(f"empty formula {formula!r}")
    return c


def hill(formula_or_elements):
    e = formula_or_elements if isinstance(formula_or_elements, dict) else elements(formula_or_elements)
    keys = list(e)
    if "C" in e:
        order = ["C"] + (["H"] if "H" in e else []) + sorted(k for k in keys if k not in ("C", "H"))
    else:
        order = sorted(keys)
    return "".join(k + (str(e[k]) if e[k] != 1 else "") for k in order)


class Resolved(dict):
    """formula (as written) -> {name, mw, cas, hill, source, status}"""

    def name(self, formula):
        return self[formula]["name"]

    def mw(self, formula):
        return self[formula]["mw"]

    def names(self):
        out = []
        for v in self.values():
            if v["name"] not in out:
                out.append(v["name"])
        return out


def formulas_in(plant):
    """Every formula the plant mentions, with the hints its substances carry."""
    hints = {}
    for s in plant.process_steps:
        for sub in s.substances:
            h = hints.setdefault(sub.formula, {})
            for k in ("name", "cas", "inchikey"):
                v = getattr(sub, k)
                if v and not h.get(k):
                    h[k] = v
        for st in s.streams_in + s.streams_out:
            for f in st.components:
                hints.setdefault(f, {})
        for f in s.educts + s.produces:
            hints.setdefault(f, {})
    hints.setdefault(plant.target["product_formula"], {})
    if plant.target.get("product_name"):
        hints[plant.target["product_formula"]].setdefault("name", plant.target["product_name"])
    for fd in plant.overrides.feeds.values():
        for f in (fd.get("composition") or {}):
            hints.setdefault(f, {})
    return hints


def resolve_all(plant, client=None, log=None):
    """Resolve every formula. With a client: dwsim_thermo_resolve_compound; without: the offline table."""
    out, problems = Resolved(), []
    overrides = plant.overrides.compounds or {}
    for formula, hint in formulas_in(plant).items():
        try:
            out[formula] = _resolve_one(formula, hint, overrides.get(formula), client)
        except Refused as r:
            problems.append(r)
    if problems:
        raise Refused("compounds not resolved: " + "; ".join(p.reason for p in problems), what="compounds",
                      resolve="overrides.compounds maps a formula to a databank name")
    if log is not None:
        for f, v in out.items():
            log(f"{f} -> {v['name']} ({v['source']})")
    return out


def _resolve_one(formula, hint, override, client):
    cas = hint.get("cas") or INCHIKEY_CAS.get(hint.get("inchikey") or "", "")
    if override:
        name = override if isinstance(override, str) else override.get("name")
        rec = {"name": name, "source": "person", "status": "resolved", "hill": hill(formula), "cas": cas, "mw": None}
        if client:
            r = client.call("dwsim_thermo_resolve_compound", name=name)
            if r.get("status") != "resolved":
                raise Refused(f"override {formula} -> {name!r}: {r.get('status')} ({', '.join(r.get('candidates_names', []) or [c.get('name', '') for c in r.get('candidates', [])])})")
            rec["mw"] = r.get("molar_weight_g_mol"); rec["cas"] = r.get("cas")
            if r.get("hill_formula") and r["hill_formula"] != hill(formula):
                raise Refused(f"override {formula} -> {name!r}: the databank compound is {r['hill_formula']}")
        else:
            rec["mw"] = _mw_offline(formula, name)
        return rec
    if client:
        args = {"formula": formula}
        if hint.get("name"):
            args["name"] = hint["name"]
        if cas:
            args["cas"] = cas
        if hint.get("inchikey"):
            args["inchikey"] = hint["inchikey"]
        r = client.call("dwsim_thermo_resolve_compound", **args)
        st = r.get("status")
        if st == "resolved":
            return {"name": r["name"], "mw": r.get("molar_weight_g_mol"), "cas": r.get("cas"), "hill": r.get("hill_formula"),
                    "source": "formula+" + "+".join(k for k in ("name", "cas", "inchikey") if args.get(k)) if len(args) > 1 else "formula",
                    "status": st}
        if st == "ambiguous":
            raise Ambiguous(formula, [c.get("name") for c in r.get("candidates", [])])
        if st == "conflict":
            raise Refused(f"{formula}: name {hint.get('name')!r} is not a {formula} in the databank ({r.get('limitation') or r.get('message', '')})".strip(),
                          what=formula, resolve="correct the name or give the CAS")
        raise Refused(f"{formula}: {st} (closest: {', '.join(c.get('name', '') for c in r.get('candidates', [])[:3])})",
                      what=formula, resolve="overrides.compounds or a user compound JSON")
    # offline
    cands = KNOWN.get(hill(formula)) or KNOWN.get(formula) or []
    if cas:
        cands = [c for c in cands if c["cas"] == cas] or cands
    if hint.get("name"):
        named = [c for c in cands if c["name"].lower() == hint["name"].lower()]
        if named:
            cands = named
        elif cands:
            raise Refused(f"{formula}: name {hint['name']!r} is not among {[c['name'] for c in cands]} (offline table)", what=formula)
    if len(cands) == 1:
        return {"name": cands[0]["name"], "mw": cands[0]["mw"], "cas": cands[0]["cas"], "hill": hill(formula), "source": "offline table", "status": "resolved"}
    if len(cands) > 1:
        raise Ambiguous(formula, [c["name"] for c in cands])
    raise Refused(f"{formula}: not in the offline table (run against a server to search the databanks)", what=formula)


def _mw_offline(formula, name):
    for c in KNOWN.get(hill(formula), []):
        if c["name"].lower() == name.lower():
            return c["mw"]
    return None


# ---- the thermodynamic basis -------------------------------------------------------------------

def package_for(names, needs_lle, override=""):
    """PR when a permanent gas is in the basis, NRTL otherwise (UNIQUAC only by override). Returns (package, reason)."""
    if override:
        return override, "person"
    gases = sorted(set(names) & PERMANENT_GASES)
    if gases and not needs_lle:
        return "PR", f"permanent gases in the basis ({', '.join(gases)})"
    if gases and needs_lle:
        return "NRTL", f"liquid-liquid split needed; {', '.join(gases)} present as traces (brief: NRTL with the gas as Henry-like supercritical, check the flash)"
    return "NRTL", "polar liquids, no permanent gas"


def lle_pairs_for(names):
    """The regressed LLE pairs whose two compounds are both in the basis."""
    s = set(names)
    return [p for p in LLE_PAIRS if p["c1"] in s and p["c2"] in s]


def apply_basis(client, fs, run, names, needs_lle):
    pkg, why = package_for(names, needs_lle, run.plant.overrides.package)
    client.call("dwsim_thermo_add_compounds", flowsheet_id=fs, names=names)
    client.call("dwsim_thermo_set_property_package", flowsheet_id=fs, name=pkg)
    run.note("package", f"{pkg}: {why}", source="person" if run.plant.overrides.package else "rule")
    if needs_lle:
        client.call("dwsim_thermo_set_flash", flowsheet_id=fs, equilibrium_type="VLLE")
        est = client.try_call("dwsim_thermo_estimate_bips", flowsheet_id=fs)
        if est and est.get("estimated"):
            run.assume(f"NRTL pairs missing in the databank estimated by UNIFAC: {est.get('estimated')}")
        pairs = lle_pairs_for(names)
        if pairs:
            client.call("dwsim_thermo_set_bips", flowsheet_id=fs, pairs=[{k: p[k] for k in ("c1", "c2", "A12", "A21", "alpha12")} for p in pairs])
            for p in pairs:
                run.assume(f"NRTL {p['c1']}/{p['c2']} pair replaced by one {p['source']} (A12 {p['A12']}, A21 {p['A21']}, alpha {p['alpha12']}): the databank pair gives no liquid split")
        else:
            run.warn("liquid-liquid recipe present but no regressed LLE pair in data/lle_bips.json for this basis; the databank VLE pairs may give one liquid in the decanter")
    return pkg
