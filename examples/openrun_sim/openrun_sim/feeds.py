"""Fresh feeds from the target rate: product kg/h -> mol/s with the engine's molar mass; each educt of the
reaction that makes the product = product rate x stoichiometric ratio / feed conversion, chained back through
the reactors that make an intermediate until a plant feed is reached; overrides.feeds gives absolute amounts,
excess factors, compositions and conditions. Utility streams need an override amount, else they are refused
by name. After the first solve the builder scales every derived feed by asked/achieved once (options).
"""
from .compounds import PERMANENT_GASES
from .errors import Refused
from .reactions import DEFAULTS, equations_of, parse_equation


def boundary_feeds(plant):
    """[(step, Stream)] for every streams_in whose other end is the plant boundary."""
    out = []
    for s in plant.process_steps:
        for st in s.streams_in:
            if st.step == "":
                out.append((s, st))
            elif st.step == "?":
                raise Refused(f"stream {st.stream!r} comes from an unknown step ('?')", step_id=s.step_id, what=st.stream,
                              resolve="connect it or mark it a plant feed (step '')")
    return out


def product_mol_s(plant, resolved):
    f = plant.target["product_formula"]
    mw = resolved.mw(f)
    if not mw:
        raise Refused(f"no molar mass for {f}")
    return float(plant.target["rate_kg_h"]) / 3600.0 * 1000.0 / mw


def _producers(plant):
    """formula -> [(step, coef_product, reactants[(coef, formula)])] over every equation."""
    out = {}
    for s in plant.process_steps:
        for eq in equations_of(s):
            r, p, _ = parse_equation(eq)
            for c, f in p:
                out.setdefault(f, []).append((s, c, r))
    return out


def required_feeds(plant, resolved, run):
    """{formula: mol/s} of fresh educts needed for the target, chained through the producers; derived values."""
    producers = _producers(plant)
    feeds_formulas = {f for _, st in boundary_feeds(plant) if st.role != "utility" for f in st.components}  # a utility trickle covers no demand
    need = {}
    visited = set()

    def demand(formula, mol_s, depth=0):
        if depth > 8:
            return
        if formula in feeds_formulas:
            need[formula] = need.get(formula, 0.0) + mol_s  # a plant feed is covered fresh, whatever else makes it
            return
        if formula not in producers:
            need[formula] = need.get(formula, 0.0) + mol_s  # not fed, not produced: the report will show it missing
            return
        step, cp, reactants = producers[formula][0]
        if len(producers[formula]) > 1 and formula not in visited:
            run.assume(f"{formula} is made in {len(producers[formula])} steps; the feed demand is chained through step {step.step_id} only")
        visited.add(formula)
        fc = float((plant.overrides.reaction.get(step.step_id) or {}).get("feed_conversion", 1.0))
        for c, f in reactants:
            if f in feeds_formulas and f in producers and f == formula:
                continue
            demand(f, mol_s * c / cp / fc, depth + 1)

    demand(plant.target["product_formula"], product_mol_s(plant, resolved))
    return need


def feed_specs(plant, resolved, run, first_pressure_Pa=None):
    """[{stream, step_id, mol_s, x (mole fractions by databank name), T_K, P_Pa, source}] for every boundary feed."""
    need = required_feeds(plant, resolved, run)
    specs = []
    for step, st in boundary_feeds(plant):
        o = plant.overrides.feeds.get(st.stream) or {}
        comp = o.get("composition")
        if comp:
            x = {resolved.name(f) if f in resolved else f: float(v) for f, v in comp.items()}
            comp_src = "person"
        elif len(st.components) == 1:
            x = {resolved.name(st.components[0]): 1.0}
            comp_src = "pure (one component listed)"
        else:
            raise Refused(f"feed {st.stream!r} lists {len(st.components)} components and no composition", step_id=step.step_id,
                          what=st.stream, resolve=f"overrides.feeds.{st.stream}.composition")
        tot = sum(x.values())
        x = {k: v / tot for k, v in x.items()}
        if o.get("ratio_to"):
            mol_s, src = None, f"person: {o['ratio_to']['mol_per_mol']} mol per mol of {o['ratio_to']['stream']}"
        elif o.get("molar_flow_mol_s") is not None:
            mol_s, src = float(o["molar_flow_mol_s"]), "person"
        elif st.role == "utility":
            raise Refused(f"utility stream {st.stream!r} has no amount", step_id=step.step_id, what=st.stream,
                          resolve=f"overrides.feeds.{st.stream}.molar_flow_mol_s")
        else:
            main = [f for f in st.components if f in need]
            if not main:
                raise Refused(f"feed {st.stream!r} carries no educt of the product chain and no amount", step_id=step.step_id,
                              what=st.stream, resolve=f"overrides.feeds.{st.stream}.molar_flow_mol_s")
            f0 = main[0]
            frac = x.get(resolved.name(f0), 1.0)
            mol_s = need[f0] / max(frac, 1e-12) * float(o.get("excess", 1.0))
            src = f"derived from target ({f0} demand {need[f0]:.4g} mol/s" + (f", excess {o['excess']}" if o.get("excess") else "") + ")"
        T = float(o.get("T_K", DEFAULTS["feed_temperature_K"]))
        P = float(o.get("P_Pa", first_pressure_Pa or DEFAULTS["feed_pressure_Pa"]))
        if "T_K" not in o or "P_Pa" not in o:
            run.assume(f"feed {st.stream}: {'' if 'T_K' in o else f'{T - 273.15:.0f} C'}{' and ' if 'T_K' not in o and 'P_Pa' not in o else ''}{'' if 'P_Pa' in o else f'{P / 1e5:.3g} bar'} (not stated)")
        specs.append({"stream": st.stream, "step_id": step.step_id, "mol_s": mol_s, "x": x, "T_K": T, "P_Pa": P, "source": src,
                      "derived": src.startswith("derived"), "ratio_to": o.get("ratio_to"), "role": st.role, "composition_source": comp_src})
    apply_ratios(specs)
    return specs


def apply_ratios(specs):
    """Feeds given as a ratio to another feed (steam-to-carbon, air per mol gas) follow that feed's amount."""
    by = {s["stream"]: s for s in specs}
    for _ in range(3):
        for s in specs:
            r = s.get("ratio_to")
            if r:
                ref = by.get(r["stream"])
                if ref is None or ref["mol_s"] is None:
                    raise Refused(f"feed {s['stream']}: ratio_to names unknown or undetermined feed {r['stream']!r}", what=s["stream"])
                s["mol_s"] = float(r["mol_per_mol"]) * ref["mol_s"]
