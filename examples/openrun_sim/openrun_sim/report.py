"""The report: every stream, every duty with its sign, the plant balance (loops left open quantified), the
checks (pump fed vapour, compressor fed liquid, reactor outlet phase, windows), the product against the target,
and the provenance of every value. Written as report.json and report.md beside the saved flowsheet.
"""
import json
import os

from . import mcp


def collect(builder):
    run, c, fs, plant = builder.run, builder.c, builder.run.fs, builder.run.plant
    asked = float(plant.target["rate_kg_h"]) / 3600.0
    rep = c.call("dwsim_plant_report", flowsheet_id=fs, product_stream=run.product_stream, product_compound=run.product_name, asked_product_rate_kg_s=asked)
    open_loops = [{"out": run.actual(l["stream"])} for l in run.loops_open if l.get("stream")]
    bal = c.call("dwsim_plant_balance", flowsheet_id=fs, open_loops=open_loops or None, tolerance_rel=plant.options.get("balance_tolerance_rel", 1e-3))
    windows = [{k: w[k] for k in ("object", "quantity", "min_si", "max_si")} for w in run.windows if w["object"] in _units(rep)]
    chk = c.call("dwsim_plant_check", flowsheet_id=fs, windows=windows or None)
    return {"plant_report": rep, "balance": bal, "check": chk, "windows_requested": run.windows}


def _units(rep):
    return {u.get("tag") for u in rep.get("units", [])} | {d.get("unit") for d in rep.get("duties", [])}


def write(builder, data, out_dir):
    run = builder.run
    os.makedirs(out_dir, exist_ok=True)
    dw = os.path.join(out_dir, f"{run.plant.plant_id}.dwxmz")
    try:
        mcp.save(builder.c, run.fs, os.path.abspath(dw))
    except mcp.ToolError as e:
        run.warn(f"flowsheet not saved: {e}")
        dw = None
    full = {"run": run.summary(), "flowsheet_file": dw, **data}
    with open(os.path.join(out_dir, "report.json"), "w", encoding="utf-8") as f:
        json.dump(full, f, indent=1, default=str)
    with open(os.path.join(out_dir, "calls.json"), "w", encoding="utf-8") as f:
        json.dump([{"tool": t, "args": a, "error": r.get("error") if isinstance(r, dict) and "error" in r else None} for t, a, r in builder.c.log], f, indent=0, default=str)
    md = markdown(full)
    with open(os.path.join(out_dir, "report.md"), "w", encoding="utf-8") as f:
        f.write(md)
    return os.path.join(out_dir, "report.md")


def markdown(full):
    run, rep, bal, chk = full["run"], full["plant_report"], full["balance"], full["check"]
    L = [f"# {run['plant_id']}: simulation report", ""]
    prod = rep.get("product") or {}
    solved = rep.get("solved")
    L.append(f"Solved: **{solved}**. Loops closed: {len(run['loops_closed'])}, left open: {len(run['loops_open'])}. "
             f"Check: {chk.get('errors', 0)} errors, {chk.get('warnings', 0)} warnings. Balance ok: {bal.get('closure', {}).get('ok')}.")
    if prod:
        L += ["", "## Product", "", "| item | value |", "|---|---|",
              f"| stream | {prod.get('stream')} |", f"| compound | {prod.get('compound')} |",
              f"| mass flow kg/h | {_f(prod.get('mass_flow_kg_s'), 3600)} |", f"| asked kg/h | {_f(prod.get('asked_kg_s'), 3600)} |",
              f"| achieved / asked | {_f(prod.get('ratio_to_asked'))} |", f"| purity (mass) | {_f(prod.get('purity_mass'))} |",
              f"| purity (mole) | {_f(prod.get('purity_mole'))} |"]
    if run["placeholders"]:
        L += ["", "## Placeholders (numbers nobody stated; replace before trusting the result)", ""]
        L += [f"- step {p['step_id']}: {p['text']}" for p in run["placeholders"]]
    if run["surrogates"]:
        L += ["", "## Stand-ins", ""] + [f"- step {s['step_id']}: {s['text']}" for s in run["surrogates"]]
    if run["loops_open"]:
        L += ["", "## Loops left open", ""]
        mism = {l.get("out"): l for l in bal.get("open_loop_mismatch", [])}
        for l in run["loops_open"]:
            m = mism.get(l["stream"]) or {}
            L.append(f"- {l['stream']} -> step {l['to_step']}: {l['reason']}. Leaves {_f(m.get('out_mol_s'))} mol/s ({_f(m.get('out_kg_s'), 3600)} kg/h) that the fresh feeds cover.")
    L += ["", "## Streams", "", "| stream | from | to | T °C | p bar | kg/h | mol/s | vap. frac. | composition (mole) |", "|---|---|---|---|---|---|---|---|---|"]
    for s in rep.get("streams", []):
        x = s.get("x") or {}
        comp = ", ".join(f"{k} {v:.3f}" for k, v in sorted(x.items(), key=lambda kv: -kv[1]) if v > 1e-4)
        L.append(f"| {s['tag']} | {s.get('from') or '—'} | {s.get('to') or '—'} | {_f(s.get('T_K'), 1, -273.15)} | {_f(s.get('P_Pa'), 1e-5)} | "
                 f"{_f(s.get('mass_flow_kg_s'), 3600)} | {_f(s.get('molar_flow_mol_s'))} | {_f(s.get('vapor_fraction'))} | {comp} |")
    L += ["", "## Duties", "", "| unit | side | kW (signed: + added, − removed) |", "|---|---|---|"]
    for d in rep.get("duties", []):
        L.append(f"| {d.get('unit')} | {d.get('side')} | {_f(d.get('signed_kW'))} |")
    e = rep.get("energy_kW") or {}
    L.append(f"| **plant** | added {_f(e.get('added'))} / removed {_f(e.get('removed'))} | net {_f(e.get('net'))} |")
    cl = bal.get("closure") or {}
    L += ["", "## Balance", "", f"Mass in {_f((bal.get('mass_kg_s') or {}).get('in'), 3600)} kg/h, out {_f((bal.get('mass_kg_s') or {}).get('out'), 3600)} kg/h, "
          f"relative gap {_f(cl.get('mass_rel'))}; worst element gap {_f(cl.get('worst_element_rel'))}; worst non-reacting compound gap {_f(cl.get('worst_nonreacting_compound_rel'))}.",
          "", "| element | in mol/s | out mol/s | rel |", "|---|---|---|---|"]
    for r in bal.get("elements", []):
        L.append(f"| {r['element']} | {_f(r['in_mol_s'])} | {_f(r['out_mol_s'])} | {_f(r['rel'])} |")
    L += ["", "## Checks", ""]
    if not chk.get("findings"):
        L.append("No findings.")
    for f in chk.get("findings", []):
        L.append(f"- **{f.get('severity')}** {f.get('code')} {f.get('object') or ''}: {f.get('message')}" + (f" Fix: {f['fix']}" if f.get("fix") else ""))
    L += ["", "## Feeds", "", "| stream | mol/s | composition | T °C | p bar | source |", "|---|---|---|---|---|---|"]
    for fd in run["feeds"]:
        L.append(f"| {fd['stream']} | {_f(fd['mol_s'])} | {', '.join(f'{k} {v:.3f}' for k, v in fd['x'].items())} | {_f(fd['T_K'], 1, -273.15)} | {_f(fd['P_Pa'], 1e-5)} | {fd['source']} |")
    L += ["", "## Assumptions (needed to close the simulation, not stated)", ""] + [f"- {a}" for a in run["assumptions"]]
    if run["warnings"]:
        L += ["", "## Warnings", ""] + [f"- {w}" for w in run["warnings"]]
    L += ["", "## Provenance of every value", "", "| step | item | value | source |", "|---|---|---|---|"]
    for p in run["provenance"]:
        L.append(f"| {p['step_id']} | {p['item']} | {json.dumps(p['value'], default=str) if not isinstance(p['value'], (int, float, str)) else p['value']} | {p.get('source', '')} |")
    L += ["", "## Solve stages", ""] + [f"- {s['stage']}: ok={s['ok']}" + (f" errors={str(s['errors'])[:200]}" if s.get('errors') else "") for s in run["stages"]]
    return "\n".join(L) + "\n"


def _f(v, scale=1.0, offset=0.0):
    if v is None:
        return "—"
    try:
        x = float(v) * scale + offset
    except (TypeError, ValueError):
        return str(v)
    if abs(x) >= 1000:
        return f"{x:.0f}"
    if abs(x) >= 1:
        return f"{x:.3g}" if abs(x) < 10 else f"{x:.4g}"
    return f"{x:.4g}"
