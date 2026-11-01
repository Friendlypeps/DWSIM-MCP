"""Closing the loops: open-loop first, then one tear at a time (override order, else gas loops before loops
around columns), each seeded from the open-loop solution; a loop that does not converge is opened again and
reported with its mismatch instead of being hidden (gold findings 2, 4, 5).

A reopened loop must leave a solvable flowsheet: the tear stream of a column feed is also removed from the
column's own connection table (deleting the stream only detaches the drawing; the engine then refuses the column
with 'one or more streams specified in the column connections window is missing'), and a tear whose open-loop
source holds no solved state is never closed (the Recycle would hand the loop a NaN composition).
"""
from .compounds import PERMANENT_GASES
from .mcp import ToolError
from .reactions import DEFAULTS


def close_loops(builder, mode="auto"):
    run, c, fs, plant = builder.run, builder.c, builder.run.fs, builder.run.plant
    tears = [e for e in builder.topo.tears() if e.tear and not getattr(e, "closed", False)]
    lo = plant.overrides.loops or {}
    leave = set(lo.get("leave_open", []))
    order = list(lo.get("close", []))
    gas_first = sorted(tears, key=lambda e: (0 if e.components and all(f in _gas_formulas(run) for f in e.components) else 1))
    seq = [e for n in order for e in tears if e.stream == n] + [e for e in gas_first if e.stream not in order]
    for i, e in enumerate(seq):
        tgt = builder.tear_targets.get(e.stream)
        if e.stream in leave or mode == "all":
            run.loops_open.append({"stream": e.stream, "to_step": e.to_step, "reason": "left open by request" if e.stream in leave else "all loops left open (--open-loops all)"})
            continue
        if not tgt:
            run.loops_open.append({"stream": e.stream, "to_step": e.to_step, "reason": "no target unit built for this tear"})
            continue
        ok, why, wired = _close_one(builder, e, tgt)
        if ok:
            run.loops_closed.append({"stream": e.stream, "recycle": f"RCY-{e.stream}", "to_step": e.to_step})
        elif not wired:
            run.loops_open.append({"stream": e.stream, "to_step": e.to_step, "reason": why})
            run.warn(f"loop {e.stream} not closed: {why}")
        elif mode == "none":
            run.loops_closed.append({"stream": e.stream, "recycle": f"RCY-{e.stream}", "to_step": e.to_step, "converged": False, "reason": why})
            run.warn(f"loop {e.stream} closed but not converged: {why}")
        else:
            reopened = _reopen(builder, e, tgt)
            run.loops_open.append({"stream": e.stream, "to_step": e.to_step, "reason": why})
            run.log(f"  loop {e.stream} reopened: {why}")
            if not reopened:
                failed = builder.run.stages[-1].get("failed") or []
                what = f"{failed[0][0]} failed with '{failed[0][1][:160]}'" if failed else "the solve failed"
                run.warn(f"the flowsheet did not solve after loop {e.stream} was reopened ({what}); the remaining loops are left open")
                for rest in seq[i + 1:]:
                    if rest.stream in leave or mode == "all" or rest.stream not in builder.tear_targets:
                        continue
                    run.loops_open.append({"stream": rest.stream, "to_step": rest.to_step,
                                           "reason": f"not attempted: the flowsheet did not solve after loop {e.stream} was reopened"})
                break


def _gas_formulas(run):
    return {f for f, v in run.resolved.items() if v["name"] in PERMANENT_GASES}


def _close_one(builder, e, tgt):
    """-> (converged, reason, wired). wired=False: the loop was taken apart again before any solve (it could not be seeded)."""
    run, c, fs = builder.run, builder.c, builder.run.fs
    src = run.actual(e.stream)
    rcy, out = f"RCY-{e.stream}", f"{e.stream}-R"
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="Recycle", name=rcy)
    c.call("dwsim_stream_add_material", flowsheet_id=fs, name=out)
    c.call("dwsim_unitop_connect", flowsheet_id=fs, unitop=rcy, feed_stream=src, feed_port=0, product_stream=out, product_port=0)
    seeded = c.try_call("dwsim_recycle_seed", flowsheet_id=fs, name=rcy)
    if not seeded and not _seed_from(builder, src, out):
        _unwire(builder, e, None)
        return False, (f"tear stream {src} has no solved state in the open loop (zero flow or an invalid composition), so the loop cannot be "
                       f"seeded; make the units upstream of {src} solve first"), False
    if not _usable(c.try_call("dwsim_stream_get_results", flowsheet_id=fs, name=out)):
        _unwire(builder, e, None)
        return False, f"the seeded tear stream {out} reads back with no flow or a NaN composition; the loop is not closed", False
    try:
        if tgt["kind"] == "mixer":
            c.call("dwsim_unitop_connect", flowsheet_id=fs, unitop=tgt["unit"], feed_stream=out, feed_port=tgt["port"])
        elif tgt["kind"] == "column_feed":
            c.call("dwsim_column_connect", flowsheet_id=fs, name=tgt["unit"], feeds=[{"stream": out, "stage": tgt["stage"]}])
        else:
            c.call("dwsim_unitop_connect", flowsheet_id=fs, unitop=tgt["unit"], feed_stream=out, feed_port=tgt.get("port", 0))
    except ToolError as ex:
        # a port already taken or no free feed on the column: the loop stays open with the engine's reason, the run goes on
        _unwire(builder, e, tgt)
        return False, f"the tear stream could not be attached to {tgt['unit']} ({tgt['kind']}): {str(ex)[:240]}", False
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
        failed = builder._failed(r)
        detail = f"{failed[0][0]}: {failed[0][1][:240]}" if failed and failed[0][0] else str(r.get("errors") or r)[:300]
        return False, f"solve failed: {detail}", True
    st = c.try_call("dwsim_plant_report", flowsheet_id=fs, streams=[src]) or {}
    rec = next((x for x in st.get("recycles", []) if x.get("tag") == rcy), None)
    if rec and rec.get("converged") is False:
        return False, f"recycle not converged after {rec.get('iterations')} iterations", True
    return True, "", True


def _usable(r):
    """A specified or solved stream: a positive molar flow and finite, non-empty fractions (NaN reads back as nan or null).
    A stream the engine never calculated reads as zero molar flow, often with the default mass flow still on it."""
    if not isinstance(r, dict):
        return False
    flow = r.get("molar_flow_mol_s")
    if not isinstance(flow, (int, float)) or flow != flow or flow <= 0:
        return False
    try:
        fr = [v.get("mole_fraction") for v in r["phases"][0]["compounds"].values()]
    except (KeyError, IndexError, TypeError, AttributeError):
        return False
    return bool(fr) and all(isinstance(x, (int, float)) and x == x and x >= 0 for x in fr) and sum(fr) > 0.5


def _seed_from(builder, src, out):
    """dwsim_recycle_seed refused (the source has no flow): copy the source state by hand when it is usable at all."""
    c, fs = builder.c, builder.run.fs
    r = c.try_call("dwsim_stream_get_results", flowsheet_id=fs, name=src)
    if not _usable(r):
        return False
    comp = {k: v["mass_fraction"] for k, v in r["phases"][0]["compounds"].items() if isinstance(v.get("mass_fraction"), (int, float)) and v["mass_fraction"] > 0}
    if not comp:
        return False
    args = {"molar_flow_mol_s": r["molar_flow_mol_s"], "composition": comp}
    for key in ("temperature_K", "pressure_Pa"):
        if isinstance(r.get(key), (int, float)) and r[key] > 0:
            args[key] = r[key]
    return c.try_call("dwsim_stream_set_conditions", flowsheet_id=fs, name=out, **args) is not None


def _unwire(builder, e, tgt):
    """Take a closed tear apart: the Recycle, its outlet stream, and the entry a column keeps for that stream in its own
    connection table (SyncConnectedStreams drops the entries whose stream is gone; without it the next solve refuses the column)."""
    c, fs = builder.c, builder.run.fs
    c.try_call("dwsim_object_delete", flowsheet_id=fs, target=f"RCY-{e.stream}")
    c.try_call("dwsim_object_delete", flowsheet_id=fs, target=f"{e.stream}-R")
    tgt = tgt or builder.tear_targets.get(e.stream) or {}
    if tgt.get("kind") == "column_feed":
        c.try_call("dwsim_object_invoke", flowsheet_id=fs, target=tgt["unit"], method="SyncConnectedStreams")
    builder.touch()


def _reopen(builder, e, tgt=None):
    """-> True when the flowsheet solves again with the loop open."""
    _unwire(builder, e, tgt)
    r = builder.solve(f"loop {e.stream} reopened")
    return bool(r.get("ok"))


def _is_column_balance_error(r):
    t = str(r.get("errors") or "").lower()
    return "balance" in t or "mass" in t


def close_now(builder, e):
    """Close one tear during the build (overrides.loops.close_early); kept closed even if this staged solve fails."""
    run = builder.run
    ok, why, wired = _close_one(builder, e, builder.tear_targets[e.stream])
    e.closed = True
    if not wired:
        run.loops_open.append({"stream": e.stream, "to_step": e.to_step, "reason": why})
        run.warn(f"loop {e.stream} (close_early) not closed: {why}")
        return
    entry = {"stream": e.stream, "recycle": f"RCY-{e.stream}", "to_step": e.to_step, "closed_early": True}
    if not ok:
        entry["converged"] = False
        entry["reason"] = why
        run.warn(f"loop {e.stream} closed early but not converged yet: {why}")
    run.loops_closed.append(entry)
