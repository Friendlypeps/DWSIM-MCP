"""Operating range: for each knob with a closed window, sweep the unit's specification across the module window
with dwsim_study_sweep (one variable at a time) and watch the product; the sub-range where the plant solves is the
operating range the report states.
"""
from .setpoints import windows_for


def operating_range(builder, points=None, timeout_s=None):
    """Sweeps the steps named in options.sweep_steps (default: the reaction steps), options.sweep_points per window
    (default 3), options.sweep_timeout_s per point (default 90): each point is a full plant solve with the loops closed."""
    run, c, fs, plant = builder.run, builder.c, builder.run.fs, builder.run.plant
    points = points or int(plant.options.get("sweep_points", 3))
    timeout_s = timeout_s or int(plant.options.get("sweep_timeout_s", 300))
    wanted = plant.options.get("sweep_steps") or [s.step_id for s in plant.process_steps if s.equation]
    out = []
    prod = run.product_stream
    watches = [{"object": prod, "property": "PROP_MS_2"}, {"object": prod, "property": "PROP_MS_3"}] if prod else []
    for step_id, tag in run.units.items():
        if step_id not in wanted:
            continue
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
            # the grid runs from the middle outwards: the base case is the current converged state, and each extreme then
            # starts from a converged neighbour instead of from a far-off loop state (a closed loop needs many passes otherwise)
            grid = [rng.min_si + (rng.max_si - rng.min_si) * i / (points - 1) for i in range(points)] if points > 1 else [0.5 * (rng.min_si + rng.max_si)]
            grid = sorted(grid, key=lambda v: abs(v - 0.5 * (rng.min_si + rng.max_si)))
            r = c.try_call("dwsim_study_sweep", flowsheet_id=fs, variables=[{"object": tag, "property": spec["property"], "values": grid}],
                           watches=watches, timeout_s=timeout_s, restore=True)
            if not r:
                out.append({"step_id": step_id, "unit": tag, "quantity": q, "window": [rng.min_si, rng.max_si], "error": "sweep failed"})
                continue
            rows = r.get("rows", [])
            pts = [{"value_si": row["inputs"][0], "solved": bool(row.get("solved")), "error": row.get("error"), "seconds": row.get("seconds"),
                    "product_kg_s": (row.get("outputs") or [None])[0], "product_mol_s": (row.get("outputs") or [None, None])[1]} for row in rows]
            pts.sort(key=lambda p: p["value_si"])
            ok = [p["value_si"] for p in pts if p["solved"]]
            out.append({"step_id": step_id, "unit": tag, "quantity": q, "property": spec["property"], "window": [rng.min_si, rng.max_si], "sources": sources,
                        "points": pts, "solved_points": len(ok), "total_points": len(pts),
                        "solved_range": [min(ok), max(ok)] if ok else None})
    return out
