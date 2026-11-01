"""Setpoints from windows (brief section 4 / 7): per step and quantity, window = document window
(t_window / p_window text, requirement Range) intersected with the chosen module's offered range; the value
is the midpoint; an open side after intersection is refused unless overrides.setpoints gives a value; an empty
intersection is refused with both windows quoted. Every value carries its provenance.
"""
from .contracts import Range, parse_window
from .errors import OpenBound, Refused

QUANTITIES = ("temperature", "pressure")
_SI_KEY = {"temperature": "temperature_K", "pressure": "pressure_Pa"}


def windows_for(plant, step):
    """{quantity: (Range or None, sources[])} after intersection; None = nothing stated anywhere."""
    req = plant.requirement(step.step_id)
    fss = plant.flowsheet_step(step.step_id)
    out = {}
    for q in QUANTITIES:
        parts = []
        doc = parse_window(step.t_window if q == "temperature" else step.p_window, q)
        if doc:
            parts.append((doc, "document"))
        slot = step.slots.get(q) or step.slots.get("outlet_" + q)
        if slot is not None:
            parts.append((Range.of(slot), "document slot"))
        if req and getattr(req, q):
            parts.append((getattr(req, q), "requirement"))
        if fss and fss.offered_ranges.get(q):
            parts.append((fss.offered_ranges[q], "module"))
        if not parts:
            out[q] = (None, [])
            continue
        lo = max((r.min_si for r, _ in parts if r.min_si is not None), default=None)
        hi = min((r.max_si for r, _ in parts if r.max_si is not None), default=None)
        if lo is not None and hi is not None and lo > hi:
            quoted = "; ".join(f"{s} {r.text()}" for r, s in parts)
            raise Refused(f"{q} windows do not overlap: {quoted}", step_id=step.step_id, what=q,
                          resolve="change the module or correct the document window")
        out[q] = (Range(lo, hi), [s for _, s in parts])
    return out


def setpoint(plant, step, quantity, required=True):
    """-> (value_SI, provenance) or (None, None) when nothing is stated and the value is not required."""
    o = (plant.overrides.setpoints.get(step.step_id) or {}).get(_SI_KEY[quantity])
    rng, sources = windows_for(plant, step)[quantity]
    if o is not None:
        if rng and ((rng.min_si is not None and o < rng.min_si - 1e-9) or (rng.max_si is not None and o > rng.max_si + 1e-9)):
            raise Refused(f"override {quantity} {o:g} lies outside the window {rng.text()} ({', '.join(sources)})", step_id=step.step_id, what=quantity)
        return float(o), "person"
    if rng is None:
        if required:
            raise Refused(f"no {quantity} stated (document, requirement or module)", step_id=step.step_id, what=quantity,
                          resolve=f"state it in the step or give overrides.setpoints.{step.step_id}.{_SI_KEY[quantity]}")
        return None, None
    if not rng.is_closed():
        raise OpenBound(step.step_id, quantity, rng.text())
    src = "module" if "module" in sources and len(sources) > 1 else sources[0]
    return 0.5 * (rng.min_si + rng.max_si), src + " midpoint" if rng.min_si != rng.max_si else src


def check_windows(plant, step, object_tag, extra=None):
    """dwsim_plant_check windows for a step: outlet T/p against the stated windows, plus column top/bottom slots."""
    out = []
    for q in QUANTITIES:
        rng, sources = windows_for(plant, step)[q]
        if rng and (rng.min_si is not None or rng.max_si is not None):
            out.append({"object": object_tag, "quantity": "outlet_" + q, "min_si": rng.min_si, "max_si": rng.max_si, "sources": sources})
    for key, quantity in (("top_temperature", "top_temperature"), ("bottom_temperature", "bottom_temperature"),
                          ("top_pressure", "top_pressure"), ("bottom_pressure", "bottom_pressure")):
        if key in step.slots:
            r = Range.of(step.slots[key])
            out.append({"object": object_tag, "quantity": quantity, "min_si": r.min_si, "max_si": r.max_si, "sources": ["document slot"]})
    for q, lim in (plant.overrides.windows.get(step.step_id) or {}).items():
        out.append({"object": object_tag, "quantity": q, "min_si": lim[0], "max_si": lim[1], "sources": ["person"]})
    return out
