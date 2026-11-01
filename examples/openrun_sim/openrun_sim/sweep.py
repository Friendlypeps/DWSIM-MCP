"""Operating range: for each knob with a closed window, sweep the unit's specification across the module window
with dwsim_study_sweep (one variable at a time) and watch the product; the sub-range where the plant solves is the
operating range the report states.
"""
from .setpoints import windows_for


def operating_range(builder, points=5, timeout_s=120):
    run, c, fs, plant = builder.run, builder.c, builder.run.fs, builder.run.plant
    out = []
    prod = run.product_stream
    watches = [{"object": prod, "property": "PROP_MS_2"}, {"object": prod, "property": "PROP_MS_3"}] if prod else []
    for step_id, tag in run.units.items():
        step = plant.step(step_id)
        wins = windows_for(plant, step)
        vars_ = c.try_call("dwsim_study_list_variables", flowsheet_id=fs, object=tag)
        specs = (vars_ or {}).get("objects", [{}])[0].get("specifications", []) if vars_ else []
        for q in ("temperature", "pressure"):
            rng, sources = wins[q]
            if not rng or not rng.is_closed() or rng.min_si == rng.max_si:
                continue
            spec = next((s for s in specs if q in s.get("name", "").lower()), None)
            if not spec:
                continue
            r = c.try_call("dwsim_study_sweep", flowsheet_id=fs, variables=[{"object": tag, "property": spec["property"], "from": rng.min_si, "to": rng.max_si, "steps": points}],
                           watches=watches, timeout_s=timeout_s, restore=True)
            if not r:
                out.append({"step_id": step_id, "unit": tag, "quantity": q, "window": [rng.min_si, rng.max_si], "error": "sweep failed"})
                continue
            pts = r.get("points", r.get("results", []))
            ok_vals = [p.get("inputs", p.get("values", {})) for p in pts if p.get("ok", p.get("solved", True))]
            out.append({"step_id": step_id, "unit": tag, "quantity": q, "property": spec["property"], "window": [rng.min_si, rng.max_si], "sources": sources,
                        "points": pts, "solved_points": len(ok_vals), "total_points": len(pts)})
    return out
