"""python3 -m openrun_sim run|validate|map|explain-concepts ..."""
import argparse
import json
import os
import sys

from . import concepts, contracts
from .builder import Builder
from .compounds import resolve_all
from .errors import Refused
from .feeds import feed_specs
from .mcp import Client, ToolError
from .recycles import close_loops
from .reactions import model_for, phase_for
from .run import Run
from .setpoints import setpoint
from .topology import Topology


def load_plant(path, overrides_path=None):
    with open(path, encoding="utf-8") as f:
        d = json.load(f)
    if overrides_path:
        with open(overrides_path, encoding="utf-8") as f:
            o = json.load(f)
        base = d.get("overrides") or {}
        for k, v in o.items():
            if isinstance(v, dict) and isinstance(base.get(k), dict):
                base[k] = {**base[k], **v}
            else:
                base[k] = v
        d["overrides"] = base
    return contracts.Plant.of(d)


def offline_plan(plant, run):
    """Everything that can be decided without an engine: concept mapping, setpoints, reaction models, feeds. Collects refusals."""
    topo = Topology(plant)
    run.refusals.extend(topo.unmapped())
    rows = []
    for n in topo.ordered():
        row = {"step_id": n.step.step_id, "order": n.step.order, "concept": n.step.unit_operation, "recipe": n.entry.recipe if n.entry else None,
               "passthrough": n.passthrough, "inlets": [e.stream + (" (tear)" if e.tear else "") for e in n.inlets], "outlets": [e.stream for e in n.outlets]}
        uo = plant.overrides.units.get(n.step.step_id) or {}
        for q in ("temperature", "pressure"):
            must = n.entry is not None and n.entry.knob == q and (n.entry.recipe in ("pump", "compressor", "hx") or
                                                                    (n.entry.recipe in ("heater", "cooler") and "OutletVaporFraction" not in uo))
            try:
                v, src = setpoint(plant, n.step, q, required=must)
                row[q] = None if v is None else {"value_si": round(v, 3), "source": src}
            except Refused as r:
                run.refusals.append(r)
                row[q] = {"refused": r.reason}
        if n.entry and n.entry.recipe == "reactor":
            try:
                phase, _ = phase_for(n.step, plant.requirement(n.step.step_id), run.resolved, run)
                m = model_for(n.step, phase, plant.overrides.reaction.get(n.step.step_id), run)
                row["reaction"] = {"phase": phase, "model": m["kind"], "source": m["source"], "placeholder": m["placeholder"]}
            except Refused as r:
                run.refusals.append(r)
        rows.append(row)
    try:
        run.feeds = feed_specs(plant, run.resolved, run)
    except Refused as r:
        run.refusals.append(r)
    return topo, rows


def cmd_validate(args):
    code = 0
    for path in args.plant:
        print(f"== {path}")
        try:
            plant = load_plant(path, args.overrides)
            run = Run(plant, log=lambda m: None)
            run.resolved = resolve_all(plant)
            topo, rows = offline_plan(plant, run)
        except Refused as r:
            print("  REFUSED:", r.text()); code = 2; continue
        for r in rows:
            print(f"  {r['order']:>2} {r['step_id']:<8} {r['concept']:<24} -> {r['recipe'] or 'REFUSED':<12} T={_short(r.get('temperature'))} p={_short(r.get('pressure'))}"
                  + (f" reaction={r['reaction']['model']}({'PLACEHOLDER' if r['reaction']['placeholder'] else r['reaction']['source'][:20]})" if r.get("reaction") else ""))
        for t in topo.tears():
            print(f"  tear: {t.stream} ({t.from_step} -> {t.to_step})")
        for f in run.feeds:
            print(f"  feed {f['stream']}: {f['mol_s']:.4g} mol/s {f['x']} ({f['source']})")
        for a in run.assumptions:
            print("  ASSUMED:", a)
        for p in run.placeholders:
            print(f"  PLACEHOLDER step {p['step_id']}: {p['text']}")
        if run.refusals:
            code = 2
            print(f"  REFUSED ({len(run.refusals)}):")
            for r in run.refusals:
                print("   -", r.text())
        else:
            print("  ok: every step maps, every setpoint is closed, every feed is determined")
    return code


def _short(v):
    if v is None:
        return "-"
    if "refused" in v:
        return "REFUSED"
    return f"{v['value_si']:.6g}({v['source'][:10]})"


def cmd_map(args):
    for path in args.plant:
        plant = load_plant(path, args.overrides)
        run = Run(plant, log=lambda m: None)
        run.resolved = resolve_all(plant)
        topo, rows = offline_plan(plant, run)
        print(json.dumps({"plant_id": plant.plant_id, "compounds": run.resolved, "steps": rows, "tears": [t.stream for t in topo.tears()],
                          "feeds": run.feeds, "assumptions": run.assumptions, "placeholders": run.placeholders,
                          "refusals": [r.to_json() for r in run.refusals]}, indent=1, default=str))


def cmd_explain(args):
    rows = concepts.table()
    if args.json:
        print(json.dumps(rows, indent=1)); return
    print(f"{'concept':<26} {'parent':<24} {'recipe':<12} knob        note / reason")
    for r in rows:
        print(f"{r['concept']:<26} {r['parent'] or '-':<24} {r['recipe'] or 'REFUSED':<12} {r['knob'] or '-':<11} {r['reason'] or r['note']}")


def cmd_run(args):
    plant = load_plant(args.plant, args.overrides)
    out_dir = args.out or os.path.join("out", plant.plant_id)
    os.makedirs(out_dir, exist_ok=True)
    c = Client(args.url, verbose=args.verbose)
    run = Run(plant, client=c)
    b = None
    try:
        run.resolved = resolve_all(plant, client=c, log=lambda m: run.log("  compound " + m))
        pre = Run(plant, log=lambda m: None); pre.resolved = run.resolved
        topo, rows = offline_plan(plant, pre)
        if pre.refusals:
            run.refusals = pre.refusals
            print(f"REFUSED before building ({len(pre.refusals)}):", file=sys.stderr)
            for r in pre.refusals:
                print("  -", r.text(), file=sys.stderr)
            _write_refusal(out_dir, run)
            return 2
        topo = Topology(plant)
        b = Builder(run, topo)
        b.build()
        close_loops(b, mode=args.open_loops)
        b._scale_to_target(label="with the loops closed")
        from . import report
        data = report.collect(b)
        if args.sweep:
            from .sweep import operating_range
            data["operating_range"] = operating_range(b)
            with open(os.path.join(out_dir, "operating_range.json"), "w", encoding="utf-8") as f:
                json.dump(data["operating_range"], f, indent=1, default=str)
        md = report.write(b, data, out_dir)
        prod = data["plant_report"].get("product") or {}
        print(f"\nreport: {md}\nsolved={data['plant_report'].get('solved')} product {prod.get('mass_flow_kg_s', 0) * 3600:.4g} kg/h "
              f"(asked {prod.get('asked_kg_s', 0) * 3600:.4g}) purity {prod.get('purity_mass')} | loops closed {len(run.loops_closed)} open {len(run.loops_open)} | "
              f"check errors {data['check'].get('errors')} warnings {data['check'].get('warnings')} | balance ok {data['balance'].get('closure', {}).get('ok')}")
        if run.placeholders:
            print("PLACEHOLDERS:", "; ".join(p["text"] for p in run.placeholders))
        return 0 if data["plant_report"].get("solved") else 1
    except Refused as r:
        run.refusals.append(r)
        print("REFUSED:", r.text(), file=sys.stderr)
        _write_refusal(out_dir, run)
        return 2
    except ToolError as e:
        run.warnings.append(str(e))
        print("TOOL ERROR:", e, file=sys.stderr)
        _write_refusal(out_dir, run)
        return 3
    finally:
        if b is not None:
            b._close_scratch()  # the flash flowsheet is not the plant; a refusal mid-build must not leave it on the server


def cmd_sweep(args):
    """Operating-range sweep on a flowsheet an earlier run left on the server (its report.json gives the handle and tags)."""
    from .sweep import operating_range
    plant = load_plant(args.plant, args.overrides)
    with open(args.report, encoding="utf-8") as f:
        rep = json.load(f)
    c = Client(args.url, verbose=args.verbose)
    run = Run(plant, client=c)
    run.fs = rep["run"]["flowsheet_id"]
    run.units = rep["run"]["units"]
    run.product_stream = rep["run"]["product_stream"]
    c.call("dwsim_flowsheet_summary", flowsheet_id=run.fs)  # the handle must still be alive

    class _B:  # what operating_range reads from a builder
        pass
    b = _B(); b.run, b.c = run, c
    data = operating_range(b)
    out = os.path.join(os.path.dirname(args.report), "operating_range.json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1, default=str)
    for r in data:
        print(f"{r['step_id']} {r['quantity']}: window {r.get('window')} solved {r.get('solved_points')}/{r.get('total_points')} -> solved range {r.get('solved_range')}")
    print("operating range:", out)
    return 0


def _write_refusal(out_dir, run):
    with open(os.path.join(out_dir, "report.json"), "w", encoding="utf-8") as f:
        json.dump({"run": run.summary()}, f, indent=1, default=str)


def main(argv=None):
    ap = argparse.ArgumentParser(prog="openrun_sim", description=__doc__)
    sub = ap.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("run", help="build, solve, close loops, report")
    r.add_argument("plant"); r.add_argument("--overrides"); r.add_argument("--out"); r.add_argument("--url")
    r.add_argument("--open-loops", choices=["auto", "all", "none"], default="auto"); r.add_argument("--sweep", action="store_true")
    r.add_argument("-q", "--quiet", dest="verbose", action="store_false")
    v = sub.add_parser("validate", help="offline: contract, mapping, setpoints, feeds, refusals")
    v.add_argument("plant", nargs="+"); v.add_argument("--overrides")
    m = sub.add_parser("map", help="offline mapping as JSON")
    m.add_argument("plant", nargs="+"); m.add_argument("--overrides")
    sw = sub.add_parser("sweep", help="operating-range sweep on the flowsheet of an earlier run (still on the server)")
    sw.add_argument("plant"); sw.add_argument("--overrides"); sw.add_argument("--report", required=True); sw.add_argument("--url")
    sw.add_argument("-q", "--quiet", dest="verbose", action="store_false")
    e = sub.add_parser("explain-concepts", help="the 58 concepts and what the builder makes of each")
    e.add_argument("--json", action="store_true")
    a = ap.parse_args(argv)
    return {"run": cmd_run, "validate": cmd_validate, "map": cmd_map, "sweep": cmd_sweep, "explain-concepts": cmd_explain}[a.cmd](a) or 0


if __name__ == "__main__":
    sys.exit(main())
