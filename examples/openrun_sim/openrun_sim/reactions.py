"""The reaction from the balanced equation (Hill formulas) and the model the data allow.

Order (decided with the user): override kinetics -> stated conversion / yield -> stated K -> gas phase Gibbs K
(engine data, source 'gibbs') -> a labelled placeholder from data/defaults.json (never silent). The server tool
dwsim_reaction_from_equation checks the element balance and builds the reaction; this module decides what to
hand it and records the provenance.
"""
import json
import os
import re

from . import concepts
from .compounds import PERMANENT_GASES, elements
from .errors import Refused

with open(os.path.join(os.path.dirname(__file__), "data", "defaults.json"), encoding="utf-8") as _f:
    DEFAULTS = json.load(_f)

_ARROWS = ["<->", "<=>", "⇌", "→", "->", "=>", "="]


def parse_equation(text):
    """'C2H4O2 + C2H6O -> C4H8O2 + H2O' -> ([(1,'C2H4O2'),(1,'C2H6O')], [(1,'C4H8O2'),(1,'H2O')], reversible)"""
    t = text.strip()
    arrow = next((a for a in _ARROWS if a in t), None)
    if not arrow:
        raise Refused(f"equation {text!r} has no arrow")
    lhs, rhs = t.split(arrow, 1)
    rev = arrow in ("<->", "<=>", "⇌", "=")

    def side(s):
        terms = []
        for term in re.split(r"\s\+\s|\+(?=\s)|(?<=\s)\+", " " + s.strip() + " "):
            term = term.strip()
            if not term:
                continue
            m = re.match(r"^(\d+(?:\.\d+)?)\s+(.+)$", term)
            if m:
                terms.append((float(m.group(1)), m.group(2).strip()))
            else:
                terms.append((1.0, term))
        return terms
    return side(lhs), side(rhs), rev


def balance(reactants, products):
    """Element deltas products - reactants; {} when balanced."""
    tot = {}
    for sign, side in ((-1, reactants), (1, products)):
        for coef, f in side:
            for e, n in elements(f).items():
                tot[e] = tot.get(e, 0.0) + sign * coef * n
    return {e: v for e, v in tot.items() if abs(v) > 1e-9}


def equations_of(step):
    return [e.strip() for e in re.split(r";|\n", step.equation or "") if e.strip()]


def phase_for(step, requirement, resolved, run):
    """Liquid or Vapor: slot, requirement, else inferred (permanent gas present or concept says gas) and assumed."""
    p = (step.slots.get("phase") or (requirement.phase if requirement else "") or "").lower()
    if p in ("liquid", "l"):
        return "Liquid", "document"
    if p in ("gas", "vapor", "vapour", "g", "v"):
        return "Vapor", "document"
    names = set()
    for eq in equations_of(step):
        r, pr, _ = parse_equation(eq)
        for _, f in r + pr:
            names.add(resolved.name(f) if f in resolved else f)
    if names & PERMANENT_GASES or (concepts.lookup(step.unit_operation) or concepts.lookup("Reaction")).concept in ("HomogeneousGasReaction", "GauzeReaction"):
        run.assume(f"step {step.step_id}: reaction phase Vapor (permanent gas in the equation or a gas-phase concept; not stated)")
        return "Vapor", "assumed"
    run.assume(f"step {step.step_id}: reaction phase Liquid (no permanent gas in the equation; not stated)")
    return "Liquid", "assumed"


def model_for(step, phase, override, run):
    """-> dict(kind=conversion|equilibrium_constant|equilibrium_expression|kinetic|gibbs, args, source, placeholder)"""
    o = override or {}
    src = o.get("source", "person")
    if o.get("kinetics"):
        k = o["kinetics"]
        return {"kind": "kinetic", "args": {kk: k[kk] for kk in ("a_forward", "e_forward", "e_forward_unit", "a_reverse", "e_reverse") if kk in k}, "source": src, "placeholder": False}
    if o.get("conversion_pct") is not None:
        return {"kind": "conversion", "args": {"conversion_pct": float(o["conversion_pct"])}, "source": src, "placeholder": False}
    if o.get("keq") is not None:
        a = {"keq": float(o["keq"])}
        if o.get("keq_basis"):
            a["keq_basis"] = o["keq_basis"]
        return {"kind": "equilibrium_constant", "args": a, "source": src, "placeholder": False}
    if o.get("lnkeq_expression"):
        return {"kind": "equilibrium_expression", "args": {"lnkeq_expression": o["lnkeq_expression"]}, "source": src, "placeholder": False}
    if phase == "Vapor":
        return {"kind": "gibbs", "args": {}, "source": "gibbs (engine: ideal-gas Gibbs energies of formation)", "placeholder": False}
    concept = (concepts.lookup(step.unit_operation) or concepts.lookup("Reaction")).concept
    if concept in DEFAULTS["irreversible_concepts"]:
        v = DEFAULTS["placeholder_conversion_pct"]
        run.placeholder(step.step_id, f"conversion {v} % of the base compound (irreversible chemistry, nothing stated)")
        return {"kind": "conversion", "args": {"conversion_pct": v}, "source": "PLACEHOLDER data/defaults.json", "placeholder": True}
    v = DEFAULTS["placeholder_keq_molar_fraction"]
    run.placeholder(step.step_id, f"liquid-phase equilibrium constant K = {v} (molar fraction basis), nothing stated")
    return {"kind": "equilibrium_constant", "args": {"keq": v, "keq_basis": "molar fraction"}, "source": "PLACEHOLDER data/defaults.json", "placeholder": True}


def reactor_type_for(model, step, run):
    if model["kind"] == "conversion":
        return "ConversionReactor"
    if model["kind"] == "kinetic":
        if step.slots.get("residence_time") or (step.unit_operation or "").lower() in ("fixedbedreaction", "tubularreaction"):
            return "PFR"
        return "CSTR"
    return "EquilibriumReactor"


def create_reactions(client, fs, step, resolved, phase, model, run, temperature_window=None):
    """Create one reaction per equation of the step in reaction set RS-<step>. Returns the set id and reaction names."""
    rs, names = f"RS-{step.step_id}", []
    for i, eq in enumerate(equations_of(step)):
        r, p, _ = parse_equation(eq)
        delta = balance(r, p)
        if delta:
            raise Refused(f"equation {eq!r} is not balanced: {delta}", step_id=step.step_id, resolve="correct the equation")
        name_eq = " + ".join(f"{int(c) if float(c).is_integer() else c} {resolved.name(f)}" for c, f in r) + " -> " + \
                  " + ".join(f"{int(c) if float(c).is_integer() else c} {resolved.name(f)}" for c, f in p)
        rname = f"R-{step.step_id}" + (f"-{i + 1}" if i else "")
        args = {"flowsheet_id": fs, "name": rname, "equation": name_eq, "phase": phase, "reaction_set": rs,
                "description": f"{step.label or step.unit_operation}; model {model['kind']} ({model['source']})"}
        args.update(model["args"])
        if model["kind"] == "gibbs":
            pass  # nothing given + Vapor -> the server chooses Gibbs
        if temperature_window and temperature_window.is_closed() and model["kind"].startswith("equilibrium"):
            args["tmin_K"] = max(temperature_window.min_si - 50, 1.0)
            args["tmax_K"] = temperature_window.max_si + 50
        res = client.call("dwsim_reaction_from_equation", **args)
        names.append(rname)
        run.provenance.append({"step_id": step.step_id, "item": f"reaction {rname}", "value": name_eq, "model": res.get("model_chosen", model["kind"]),
                               "source": model["source"], "placeholder": model["placeholder"]})
    return rs, names
