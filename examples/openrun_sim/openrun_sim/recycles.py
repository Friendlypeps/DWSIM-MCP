"""Closing the loops: open-loop first, then one tear at a time (override order, else gas loops before loops
around columns), each seeded from the open-loop solution; a loop that does not converge is opened again and
reported with its mismatch instead of being hidden (gold findings 2, 4, 5).
"""
from .compounds import PERMANENT_GASES
from .reactions import DEFAULTS


def close_loops(builder, mode="auto"):
    run, c, fs, plant = builder.run, builder.c, builder.run.fs, builder.run.plant
    tears = [e for e in builder.topo.tears() if e.tear]
    lo = plant.overrides.loops or {}
    leave = set(lo.get("leave_open", []))
    order = list(lo.get("close", []))
    gas_first = sorted(tears, key=lambda e: (0 if e.components and all(f in _gas_formulas(run) for f in e.components) else 1))
    seq = [e for n in order for e in tears if e.stream == n] + [e for e in gas_first if e.stream not in order]
    for e in seq:
        tgt = builder.tear_targets.get(e.stream)
        if e.stream in leave or mode == "all":
            run.loops_open.append({"stream": e.stream, "to_step": e.to_step, "reason": "left open by request" if e.stream in leave else "all loops left open (--open-loops all)"})
            continue
        if not tgt:
            run.loops_open.append({"stream": e.stream, "to_step": e.to_step, "reason": "no target unit built for this tear"})
            continue
        ok, why = _close_one(builder, e, tgt)
        if ok:
            run.loops_closed.append({"stream": e.stream, "recycle": f"RCY-{e.stream}", "to_step": e.to_step})
        elif mode == "none":
            run.loops_closed.append({"stream": e.stream, "recycle": f"RCY-{e.stream}", "to_step": e.to_step, "converged": False, "reason": why})
            run.warn(f"loop {e.stream} closed but not converged: {why}")
        else:
            _reopen(builder, e)
            run.loops_open.append({"stream": e.stream, "to_step": e.to_step, "reason": why})
            run.log(f"  loop {e.stream} reopened: {why}")


def _gas_formulas(run):
    return {f for f, v in run.resolved.items() if v["name"] in PERMANENT_GASES}


def _close_one(builder, e, tgt):
    run, c, fs = builder.run, builder.c, builder.run.fs
    src = run.actual(e.stream)
    rcy, out = f"RCY-{e.stream}", f"{e.stream}-R"
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="Recycle", name=rcy)
    c.call("dwsim_stream_add_material", flowsheet_id=fs, name=out)
    c.call("dwsim_unitop_connect", flowsheet_id=fs, unitop=rcy, feed_stream=src, feed_port=0, product_stream=out, product_port=0)
    seeded = c.try_call("dwsim_recycle_seed", flowsheet_id=fs, name=rcy)
    if tgt["kind"] == "mixer":
        c.call("dwsim_unitop_connect", flowsheet_id=fs, unitop=tgt["unit"], feed_stream=out, feed_port=tgt["port"])
    elif tgt["kind"] == "column_feed":
        c.call("dwsim_column_connect", flowsheet_id=fs, name=tgt["unit"], feeds=[{"stream": out, "stage": tgt["stage"]}])
    else:
        c.call("dwsim_unitop_connect", flowsheet_id=fs, unitop=tgt["unit"], feed_stream=out, feed_port=tgt.get("port", 0))
    c.call("dwsim_recycle_configure", flowsheet_id=fs, name=rcy, max_iterations=DEFAULTS["recycle_max_iterations"])
    builder.touch()
    r = builder.solve(f"loop {e.stream} closed")
    if not r.get("ok") and _is_column_balance_error(r):
        for tag in run.units.values():
            for path in ("InternalLoopTolerance", "ExternalLoopTolerance"):
                c.try_call("dwsim_object_set", flowsheet_id=fs, target=tag, path=path, value=1e-3)
        run.assume("column loop tolerances loosened to 1e-3 after a trace mass-balance failure inside the recycle (gold finding 4)")
        builder.touch()
        r = builder.solve(f"loop {e.stream} closed, loosened column tolerances")
    if not r.get("ok"):
        return False, f"solve failed: {str(r.get('errors') or r)[:300]}"
    st = c.try_call("dwsim_plant_report", flowsheet_id=fs, streams=[src]) or {}
    rec = next((x for x in st.get("recycles", []) if x.get("tag") == rcy), None)
    if rec and rec.get("converged") is False:
        return False, f"recycle not converged after {rec.get('iterations')} iterations"
    return True, ""


def _reopen(builder, e):
    c, fs = builder.c, builder.run.fs
    c.try_call("dwsim_object_delete", flowsheet_id=fs, target=f"RCY-{e.stream}")
    c.try_call("dwsim_object_delete", flowsheet_id=fs, target=f"{e.stream}-R")
    builder.touch()
    builder.solve(f"loop {e.stream} reopened")


def _is_column_balance_error(r):
    t = str(r.get("errors") or "").lower()
    return "balance" in t or "mass" in t
