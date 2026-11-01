"""The builder: walks the steps in order and turns each concept into its recipe on the DWSIM-MCP server,
open-loop first (tears left out), with staged solves where a unit needs the state of its feed (columns).

Recipes (concepts.py): heater, cooler, reactor, column, absorber, stripper, vessel, vessel_lle, evaporator,
compsep, hx, mixer, pump, compressor, tank, recycle. Every number that is not stated is assumed and reported.
"""
import json

from . import mcp
from .compounds import PERMANENT_GASES, apply_basis
from .errors import Refused
from .feeds import apply_ratios, feed_specs
from .reactions import DEFAULTS, create_reactions, model_for, phase_for, reactor_type_for
from .setpoints import check_windows, setpoint, windows_for

VESSEL_PORTS = {"vapor": 0, "vapour": 0, "gas": 0, "top": 0, "vent": 0, "overhead": 0,
                "liquid": 1, "organic": 1, "light": 1, "bottom": 1, "condensate": 1, "liquid1": 1, "oil": 1,
                "liquid2": 2, "aqueous": 2, "heavy": 2, "water": 2}
COLUMN_OUT = {"distillate": "distillate", "top": "distillate", "overhead": "distillate", "light": "distillate", "vapor": "distillate",
              "bottoms": "bottoms", "bottom": "bottoms", "heavy": "bottoms", "product": None, "out": None}
ABSORBER_IN = {"gas": "gas", "bottom_feed": "gas", "vapor": "gas", "feed": "gas", "liquid": "liquid", "top_feed": "liquid",
               "solvent": "liquid", "wash": "liquid", "stripping_gas": "gas"}
ABSORBER_OUT = {"top": "top", "gas_out": "top", "vapor": "top", "clean_gas": "top", "overhead": "top", "offgas": "top",
                "bottom": "bottom", "liquid_out": "bottom", "rich": "bottom", "bottoms": "bottom", "liquid": "bottom"}
HX_PORTS = {"hot_in": "hot", "hot": "hot", "cold_in": "cold", "cold": "cold", "hot_out": "hot", "cold_out": "cold"}


class Builder:
    def __init__(self, run, topo):
        self.run, self.topo, self.c = run, topo, run.client
        self.reaction_sets = {}
        self.tear_targets = {}       # tear stream -> {unit, kind, port|stage}
        self.deferred_hx = {}        # tear stream (hot inlet of a WasteHeatRecovery step) -> hx info
        self.solved_once = False
        self.dirty = True

    # ---- orchestration ----------------------------------------------------------------------
    def build(self):
        run, c, plant = self.run, self.c, self.run.plant
        run.fs = c.call("dwsim_flowsheet_create", name=plant.plant_id)["flowsheet_id"]
        fs = run.fs
        needs_lle = any(n.entry and n.entry.recipe == "vessel_lle" for n in self.topo.nodes.values())
        apply_basis(c, fs, run, run.resolved.names(), needs_lle)
        for n in self.topo.ordered():
            if n.entry and n.entry.recipe == "reactor" or (n.entry and n.entry.concept == "ReactiveDistillation"):
                self._reactions(n)
        self._feeds()
        for n in self.topo.ordered():
            self._node(n)
        self.solve("open loop, all units")
        self._product()
        self._scale_to_target()
        return run

    def solve(self, stage):
        if not self.dirty:
            return self.run.stages[-1]["result"]
        r = mcp.solve(self.c, self.run.fs)
        self.run.stages.append({"stage": stage, "ok": bool(r.get("ok")), "errors": r.get("errors"), "result": {k: v for k, v in r.items() if k != "objects"}})
        self.solved_once, self.dirty = True, False
        return r

    def touch(self):
        self.dirty = True

    # ---- basis and reactions ------------------------------------------------------------------
    def _reactions(self, node):
        step, plant, run = node.step, self.run.plant, self.run
        if not step.equation:
            raise Refused("reaction step without an equation", step_id=step.step_id, concept=node.entry.concept, resolve="give the balanced equation")
        phase, psrc = phase_for(step, plant.requirement(step.step_id), run.resolved, run)
        model = model_for(step, phase, plant.overrides.reaction.get(step.step_id), run)
        tw, _ = windows_for(plant, step)["temperature"]
        rs, names = create_reactions(self.c, run.fs, step, run.resolved, phase, model, run, tw)
        self.reaction_sets[step.step_id] = (rs, reactor_type_for(model, step, run), phase, model)
        run.value(step.step_id, "reaction phase", phase, psrc)

    def _feeds(self):
        run, c, plant = self.run, self.c, self.run.plant
        first_p = None
        for s in plant.process_steps:
            try:
                v, _ = setpoint(plant, s, "pressure", required=False)
            except Refused:
                v = None
            if v:
                first_p = v
                break
        for spec in feed_specs(plant, run.resolved, run, first_p):
            mcp.add_feed(c, run.fs, spec["stream"], spec["T_K"], spec["P_Pa"], spec["mol_s"], spec["x"])
            run.feeds.append(spec)
            run.value(spec["step_id"], f"feed {spec['stream']} molar flow mol/s", round(spec["mol_s"], 6), spec["source"])
        self.touch()

    # ---- nodes ------------------------------------------------------------------------------
    def _node(self, node):
        step, entry, run = node.step, node.entry, self.run
        if entry is None or (entry.recipe is None and not (entry.concept == "ReactiveDistillation" and (run.plant.overrides.surrogates or {}).get("reactive_distillation"))):
            return  # refused earlier by topology.unmapped()
        fss = run.plant.flowsheet_step(step.step_id)
        if fss and fss.status not in ("CHOSEN", "CHOSEN_WITHOUT_DATA"):
            run.warn(f"step {step.step_id}: module status {fss.status}; built from the document values alone")
        if node.passthrough:
            return self._passthrough(node)
        recipe = entry.recipe or "reactive_distillation"
        if recipe in ("tank", "recycle"):
            recipe = "splitter"
        fn = getattr(self, "r_" + recipe)
        tag = f"{step.step_id}-{entry.concept.upper()[:12]}"
        run.units[step.step_id] = tag
        run.unit_steps[tag] = step.step_id
        fn(node, tag)
        for w in check_windows(run.plant, step, tag):
            run.windows.append(w)
        self._resolve_deferred_hx(node)
        self.touch()
        self._close_early(node)

    def _close_early(self, node):
        """Loops named in overrides.loops.close_early are closed as soon as both ends exist: a column whose
        feed only makes sense with its recycle (the Fischer acid loop) cannot be solved open first."""
        early = set((self.run.plant.overrides.loops or {}).get("close_early", []))
        if not early:
            return
        from .recycles import close_now
        for e in node.outlets:
            if e.tear and e.stream in early and e.stream in self.tear_targets and not getattr(e, "closed", False):
                close_now(self, e)

    # inlets / outlets helpers
    def _inlets(self, node):
        live, tears = [], []
        for e in node.inlets:
            (tears if e.tear else live).append(e)
        return live, tears

    def _single_feed(self, node, tag):
        """One feed stream for a one-inlet unit: the inlet itself, or a mixer of all inlets (tears join later)."""
        live, tears = self._inlets(node)
        if not live and not tears:
            raise Refused("no inlet stream", step_id=node.step.step_id)
        if len(live) + len(tears) == 1 and live:
            return self.run.actual(live[0].stream)
        mix = f"MIX-{node.step.step_id}"
        out = f"{node.step.step_id}.mixed"
        self.c.call("dwsim_unitop_add", flowsheet_id=self.run.fs, type="Mixer", name=mix)
        for i, e in enumerate(live):
            mcp.connect(self.c, self.run.fs, mix, feed=self.run.actual(e.stream), feed_port=i, product=out if i == len(live) - 1 else None)
        if not live:
            self.c.call("dwsim_stream_add_material", flowsheet_id=self.run.fs, name=out)
            mcp.connect(self.c, self.run.fs, mix, product=out)
            self.run.warn(f"step {node.step.step_id}: every inlet is a recycle; the unit has no feed until the loop closes")
        for k, e in enumerate(tears):
            self.tear_targets[e.stream] = {"unit": mix, "kind": "mixer", "port": len(live) + k, "step_id": node.step.step_id}
        return out

    def _outlet(self, node, names, default_name, note=True):
        """The outlet edge whose port (or stream name) matches one of names; else the n-th unnamed one."""
        for e in node.outlets:
            if e.from_port.lower() in names or e.stream.lower() in names:
                return e.stream
        return None

    def _outlets_in_order(self, node):
        return [e.stream for e in node.outlets]

    def _condition(self, node, tag, feed):
        """Valve and cooler in front of a unit that has no own T/p specification (vessels, separators)."""
        step, plant, run = node.step, self.run.plant, self.run
        p, psrc = setpoint(plant, step, "pressure", required=False)
        t, tsrc = setpoint(plant, step, "temperature", required=False)
        if p is not None:
            mcp.unit(self.c, run.fs, "Valve", tag + "-V", feed=feed, product=f"{step.step_id}.pv", mode="OutletPressure", props={"OutletPressure": p})
            run.value(step.step_id, "pressure (valve in front)", p, psrc)
            feed = f"{step.step_id}.pv"
        if t is not None:
            mcp.unit(self.c, run.fs, "Cooler", tag + "-C", feed=feed, product=f"{step.step_id}.tc", energy_out=f"Q-{tag}-C",
                     mode="OutletTemperature", props={"OutletTemperature": t})
            run.value(step.step_id, "temperature (cooler in front)", t, tsrc)
            feed = f"{step.step_id}.tc"
        return feed

    def _props_override(self, step):
        o = dict(self.run.plant.overrides.units.get(step.step_id) or {})
        for k in ("OutletVaporFraction", "rigorous_extractor", "hot_outlet_T_K", "cold_outlet_T_K", "Volume"):
            o.pop(k, None)
        return o

    # ---- recipes ------------------------------------------------------------------------------
    def r_heater(self, node, tag, kind="Heater"):
        step, run, plant = node.step, self.run, self.run.plant
        feed = self._single_feed(node, tag)
        out = node.outlets[0].stream if node.outlets else f"{step.step_id}.out"
        t, src = setpoint(plant, step, "temperature", required=False)
        uo = plant.overrides.units.get(step.step_id) or {}
        props = self._props_override(step)
        if t is None and "OutletVaporFraction" in uo:
            mode, props["OutletVaporFraction"] = "OutletVaporFraction", float(uo["OutletVaporFraction"])
            run.value(step.step_id, "outlet vapour fraction", uo["OutletVaporFraction"], "person")
        elif t is None:
            raise Refused("no outlet temperature (window, slot or override)", step_id=step.step_id, concept=node.entry.concept,
                          resolve="state t_window or overrides.setpoints.<step>.temperature_K or overrides.units.<step>.OutletVaporFraction")
        else:
            mode, props["OutletTemperature"] = "OutletTemperature", t
            run.value(step.step_id, "outlet temperature K", round(t, 2), src)
        if "pressure" in step.slots or "outlet_pressure" in step.slots:
            pass
        energy = {"energy_in": f"Q-{tag}"} if kind == "Heater" else {"energy_out": f"Q-{tag}"}
        mcp.unit(self.c, run.fs, kind, tag, feed=feed, product=out, mode=mode, props=props, **energy)

    def r_cooler(self, node, tag):
        self.r_heater(node, tag, kind="Cooler")

    def r_reactor(self, node, tag):
        step, run, plant = node.step, self.run, self.run.plant
        rs, rtype, phase, model = self.reaction_sets[step.step_id]
        feed = self._single_feed(node, tag)
        t, src = setpoint(plant, step, "temperature", required=False)
        props = {"ReactionSetID": rs}
        props.update(self._props_override(step))
        if t is not None:
            mode = "OutletTemperature"; props["OutletTemperature"] = t
            run.value(step.step_id, "reactor outlet temperature K", round(t, 2), src)
        else:
            mode = "Adiabatic"
            run.assume(f"step {step.step_id}: no reactor temperature stated; adiabatic")
        if rtype in ("PFR", "CSTR"):
            uo = plant.overrides.units.get(step.step_id) or {}
            req = plant.requirement(step.step_id)
            vol = uo.get("Volume") or (req.volume if req else None)
            if not vol:
                raise Refused(f"{rtype} needs a volume (kinetic model)", step_id=step.step_id, resolve="requirements.volume or overrides.units.<step>.Volume")
            props["Volume"] = float(vol)
        vap = self._outlet(node, {"vapor", "vapour", "gas"}, None)
        liq = self._outlet(node, {"liquid"}, None)
        merged = None
        if vap is None and liq is None:
            out = node.outlets[0].stream if node.outlets else f"{step.step_id}.out"
            if phase == "Vapor":
                # the vapour outlet is the product; a mixer merging both outlets would double the solver's upstream path
                # count at every reactor (DWSIM's calculation-order walk is capped at 10000 object visits)
                vap, liq = out, f"{step.step_id}.liq"
                run.note("reactor outlets", f"step {step.step_id}: gas-phase reactor; {out} is the vapour outlet, any liquid leaves as {liq}")
            else:
                merged = out
                vap, liq = f"{step.step_id}.vap", f"{step.step_id}.liq"
        vap = vap or f"{step.step_id}.vap"
        liq = liq or f"{step.step_id}.liq"
        mcp.unit(self.c, run.fs, rtype, tag, feed=feed, product=vap, product_port=0, energy_in=f"Q-{tag}", mode=mode, props=props)
        mcp.connect(self.c, run.fs, tag, product=liq, product_port=1)
        if merged:
            mixer = f"MIX-{tag}-OUT"
            self.c.call("dwsim_unitop_add", flowsheet_id=run.fs, type="Mixer", name=mixer)
            mcp.connect(self.c, run.fs, mixer, feed=vap, feed_port=0)
            mcp.connect(self.c, run.fs, mixer, feed=liq, feed_port=1, product=merged)
        run.value(step.step_id, "reactor type", rtype, f"model {model['kind']}: {model['source']}")

    def r_column(self, node, tag, feed_override=None):
        step, run, plant, c, fs = node.step, self.run, self.run.plant, self.c, self.run.fs
        o = plant.overrides.column.get(step.step_id) or {}
        live, tears = self._inlets(node)
        if not live and not feed_override:
            raise Refused("column without a feed that exists before the loop closes", step_id=step.step_id)
        p, psrc = setpoint(plant, step, "pressure", required=False)
        if p is None:
            p, psrc = DEFAULTS["feed_pressure_Pa"], "ASSUMED default (atmospheric)"
            run.assume(f"step {step.step_id}: column top pressure 1 atm (not stated)")
        n = int(o.get("stages") or step.slots.get("stages") or 0)
        if not n:
            n = DEFAULTS["column_stages"]; run.assume(f"step {step.step_id}: {n} stages (not stated)")
        fstage = int(o.get("feed_stage") or step.slots.get("feed_stage") or 0)
        if not fstage:
            fstage = max(2, int(round(n * DEFAULTS["column_feed_stage_fraction"])))
            run.assume(f"step {step.step_id}: feed on stage {fstage} of {n} (not stated)")
        rr = o.get("reflux_ratio") or step.slots.get("reflux_ratio")
        if isinstance(rr, dict):
            rr = 0.5 * (rr["min_si"] + rr["max_si"])
        if rr is None:
            rr = DEFAULTS["column_reflux_ratio"]; run.assume(f"step {step.step_id}: reflux ratio {rr} (not stated)")
        dp = o.get("pressure_drop_Pa", DEFAULTS["column_pressure_drop_Pa"])
        cond = o.get("condenser_type", "Total_Condenser")
        self.solve(f"before column {tag}")
        feeds = []
        for e in live:
            s = feed_override or run.actual(e.stream)
            stage = 1 if e.to_port.lower() in ("reflux", "top") else fstage
            feeds.append({"stream": s, "stage": stage})
        for e in tears:
            self.tear_targets[e.stream] = {"unit": tag, "kind": "column_feed", "stage": 1 if e.to_port.lower() in ("reflux", "top") else fstage, "step_id": step.step_id}
        # permanent gas in a total-condenser feed: knock it out first (gold finding 3)
        if cond == "Total_Condenser" and feeds:
            r = c.try_call("dwsim_stream_get_results", flowsheet_id=fs, name=feeds[0]["stream"])
            if r:
                gases = {k: v.get("mole_fraction", 0) for k, v in r["phases"][0]["compounds"].items() if k in PERMANENT_GASES and v.get("mole_fraction", 0) > 1e-6}
                if gases:
                    ko, vent, liq = f"KO-{tag}", f"{step.step_id}.vent", f"{step.step_id}.kofeed"
                    c.call("dwsim_unitop_add", flowsheet_id=fs, type="ComponentSeparator", name=ko)
                    mcp.connect(c, fs, ko, feed=feeds[0]["stream"], feed_port=0, product=vent, product_port=0, energy_out=f"Q-{ko}")
                    mcp.connect(c, fs, ko, product=liq, product_port=1)
                    mcp.compsep(c, fs, ko, 0, {k: 100.0 for k in gases})
                    feeds[0]["stream"] = liq
                    run.surrogate(step.step_id, f"permanent gas ({', '.join(gases)}) removed before the total condenser by a specified split; it leaves as {vent}")
        dist = self._outlet(node, {"distillate", "top", "overhead", "light", "vapor"}, None)
        btm = self._outlet(node, {"bottoms", "bottom", "heavy"}, None)
        outs = self._outlets_in_order(node)
        if dist is None and btm is None and outs:
            dist = outs[0]; btm = outs[1] if len(outs) > 1 else None
            run.assume(f"step {step.step_id}: outlet ports not named; {dist} taken as the distillate" + (f", {btm} as the bottoms" if btm else ""))
        dist = dist or f"{step.step_id}.distillate"
        btm = btm or f"{step.step_id}.bottoms"
        c.call("dwsim_column_create", flowsheet_id=fs, name=tag, type="Distillation", stages=n, condenser_type=cond, top_pressure_Pa=p, pressure_drop_Pa=dp)
        for s in (dist, btm):
            c.try_call("dwsim_stream_add_material", flowsheet_id=fs, name=s)
        for s in (f"QC-{tag}", f"QR-{tag}"):
            c.call("dwsim_stream_add_energy", flowsheet_id=fs, name=s)
        args = {"flowsheet_id": fs, "name": tag, "feeds": feeds, "bottoms": btm, "condenser_duty": f"QC-{tag}", "reboiler_duty": f"QR-{tag}"}
        if cond == "Partial_Condenser":
            args["vapor_product"] = dist
            args["distillate"] = f"{step.step_id}.reflux_liquid"
            c.try_call("dwsim_stream_add_material", flowsheet_id=fs, name=args["distillate"])
        else:
            args["distillate"] = dist
        c.call("dwsim_column_connect", **args)
        reb = o.get("reboiler_spec")
        if reb and reb.get("type") == "feed_component_fraction":
            # bottoms draw = fraction x flow of one feed component (e.g. 95 % of the water), from the solved feed
            comp = run.resolved.name(reb["compound"]) if reb["compound"] in run.resolved else reb["compound"]
            flow = 0.0
            for f in feeds:
                r = c.call("dwsim_stream_get_results", flowsheet_id=fs, name=f["stream"])
                flow += r["molar_flow_mol_s"] * r["phases"][0]["compounds"].get(comp, {}).get("mole_fraction", 0)
            reb = {"type": "Product_Molar_Flow_Rate", "value": float(reb["fraction"]) * flow, "units": "mol/s"}
            run.value(step.step_id, "bottoms draw mol/s", round(reb["value"], 5), f"person: {o['reboiler_spec']['fraction']} of the feed {comp}")
        if not reb:
            reb = self._bottoms_spec(node, feeds, dist, btm)
        c.call("dwsim_column_set_specs", flowsheet_id=fs, name=tag, condenser={"type": "Reflux Ratio", "value": float(rr)}, reboiler=reb)
        tt, bt = step.slots.get("top_temperature"), step.slots.get("bottom_temperature")
        if tt or bt:
            # a linear temperature profile seeds the bubble-point solver (gold Fischer: 71 -> 118 C); with one end stated the
            # other is taken 40 K away and reported as assumed
            t0 = _mid(tt) if tt else _mid(bt) - 40.0
            t1 = _mid(bt) if bt else _mid(tt) + 40.0
            if not (tt and bt):
                run.assume(f"step {step.step_id}: column temperature estimates {t0 - 273.15:.0f} -> {t1 - 273.15:.0f} C from the one stated end (the other taken 40 K away)")
            c.call("dwsim_column_set_estimates", flowsheet_id=fs, name=tag, temperatures_K=[t0 + (t1 - t0) * i / (n - 1) for i in range(n)], use_temperatures=True)
        run.value(step.step_id, "column", {"stages": n, "feed_stage": fstage, "reflux_ratio": rr, "top_pressure_Pa": p, "condenser": cond, "reboiler_spec": reb}, psrc)

    def _bottoms_spec(self, node, feeds, dist, btm):
        """Bottoms draw from the component lists: the components listed only in the bottoms outlet leave there."""
        step, run = node.step, self.run
        top_e = next((e for e in node.outlets if e.stream == dist), None)
        bot_e = next((e for e in node.outlets if e.stream == btm), None)
        if bot_e and bot_e.components:
            heavy = [f for f in bot_e.components if not top_e or f not in top_e.components]
            if heavy:
                flow = 0.0
                for f in feeds:
                    r = self.c.try_call("dwsim_stream_get_results", flowsheet_id=run.fs, name=f["stream"])
                    if not r:
                        break
                    comps = r["phases"][0]["compounds"]
                    flow += sum(r["molar_flow_mol_s"] * comps.get(run.resolved.name(h), {}).get("mole_fraction", 0) for h in heavy)
                else:
                    if flow > 0:
                        run.assume(f"step {step.step_id}: bottoms draw {flow:.4g} mol/s = feed flow of the components listed only in the bottoms ({', '.join(heavy)})")
                        return {"type": "Product_Molar_Flow_Rate", "value": flow, "units": "mol/s"}
        raise Refused("no reboiler specification can be derived (component lists overlap or the feed is unsolved)", step_id=step.step_id,
                      resolve="overrides.column.<step>.reboiler_spec = {type, value, units, compound}")

    def r_absorber(self, node, tag, column_type="Absorption"):
        step, run, plant, c, fs = node.step, self.run, self.run.plant, self.c, self.run.fs
        o = plant.overrides.column.get(step.step_id) or {}
        live, tears = self._inlets(node)
        if column_type in ("Absorption", "Extractor") and len(live) + len(tears) < 2:
            if plant.overrides.splits.get(step.step_id):
                run.surrogate(step.step_id, f"{node.entry.concept} with one feed stream (no solvent stream in the flowsheet): a specified split "
                                            "(overrides.splits), not a rigorous column")
                return self.r_compsep(node, tag, allow_rigorous=False)
            raise Refused("an absorber needs a gas feed and a liquid (solvent) feed", step_id=step.step_id, concept=node.entry.concept,
                          resolve=f"add the solvent stream, or overrides.splits.{step.step_id} for a specified split")
        p, psrc = setpoint(plant, step, "pressure", required=False)
        if p is None:
            p = DEFAULTS["feed_pressure_Pa"]; run.assume(f"step {step.step_id}: absorber pressure 1 atm (not stated)")
        n = int(o.get("stages") or step.slots.get("stages") or 0) or DEFAULTS["column_stages"]
        gas = liq = None
        for e in live:
            role = ABSORBER_IN.get(e.to_port.lower())
            if role == "gas" and gas is None:
                gas = run.actual(e.stream)
            elif role == "liquid" and liq is None:
                liq = run.actual(e.stream)
        if gas is None and liq is None and live:
            gas = run.actual(live[0].stream); liq = run.actual(live[1].stream) if len(live) > 1 else None
            run.assume(f"step {step.step_id}: inlet ports not named; {gas} taken as the gas (bottom) feed" + (f", {liq} as the liquid (top) feed" if liq else ""))
        for e in tears:
            self.tear_targets[e.stream] = {"unit": tag, "kind": "column_feed", "stage": 1 if ABSORBER_IN.get(e.to_port.lower()) == "liquid" else n, "step_id": step.step_id}
        top = self._outlet(node, set(k for k, v in ABSORBER_OUT.items() if v == "top"), None)
        bot = self._outlet(node, set(k for k, v in ABSORBER_OUT.items() if v == "bottom"), None)
        outs = self._outlets_in_order(node)
        if (top is None or bot is None or top == bot) and len(outs) >= 2:
            top, bot = outs[0], outs[1]
            run.assume(f"step {step.step_id}: outlet ports not named as top/bottom; {top} taken as the top product, {bot} as the bottoms")
        top = top or f"{step.step_id}.top"
        bot = bot or f"{step.step_id}.bottom"
        if column_type == "ReboiledAbsorber" and liq is None and gas is not None:
            liq, gas = gas, None  # a stripper with one liquid feed on top
        c.call("dwsim_column_create", flowsheet_id=fs, name=tag, type=column_type, stages=n, top_pressure_Pa=p,
               pressure_drop_Pa=o.get("pressure_drop_Pa", 0.0), solving_method="Burningham-Otto (Sum Rates)" if column_type == "Absorption" else "Wang-Henke (Bubble Point)")
        for s in (top, bot):
            c.try_call("dwsim_stream_add_material", flowsheet_id=fs, name=s)
        feeds = []
        if liq:
            feeds.append({"stream": liq, "stage": 1})
        if gas:
            feeds.append({"stream": gas, "stage": n})
        args = {"flowsheet_id": fs, "name": tag, "feeds": feeds, "top_product": top, "bottoms": bot}
        if column_type == "ReboiledAbsorber":
            c.call("dwsim_stream_add_energy", flowsheet_id=fs, name=f"QR-{tag}")
            args["reboiler_duty"] = f"QR-{tag}"
        c.call("dwsim_column_connect", **args)
        if column_type == "ReboiledAbsorber":
            reb = o.get("reboiler_spec")
            if not reb:
                raise Refused("a stripper with one feed needs a reboiler specification", step_id=step.step_id, resolve="overrides.column.<step>.reboiler_spec")
            c.call("dwsim_column_set_specs", flowsheet_id=fs, name=tag, reboiler=reb)
        run.value(step.step_id, column_type, {"stages": n, "top_pressure_Pa": p}, psrc or "assumed")

    def r_stripper(self, node, tag):
        live, _ = self._inlets(node)
        self.r_absorber(node, tag, column_type="Absorption" if len(live) >= 2 else "ReboiledAbsorber")

    def r_vessel(self, node, tag, lle=False):
        step, run, c, fs = node.step, self.run, self.c, self.run.fs
        feed = self._condition(node, tag, self._single_feed(node, tag))
        phases = str(step.slots.get("phases", "")).lower()
        if "solid" in phases:
            raise Refused("a solid phase is not in the basis", step_id=step.step_id, concept=node.entry.concept)
        c.call("dwsim_unitop_add", flowsheet_id=fs, type="Vessel", name=tag)
        used = {}
        unnamed = []
        for e in node.outlets:
            port = VESSEL_PORTS.get(e.from_port.lower(), VESSEL_PORTS.get(e.stream.lower()))
            if port is None or port in used:
                unnamed.append(e)
            else:
                used[port] = e.stream
        free = [p for p in (0, 1, 2) if p not in used]
        for e in unnamed:
            if not free:
                raise Refused(f"more outlets than a vessel has ({len(node.outlets)})", step_id=step.step_id)
            p = free.pop(0 if not lle or len(unnamed) > 1 else (1 if 1 in free else 0))
            used[p] = e.stream
            run.assume(f"step {step.step_id}: outlet {e.stream} not named by port; taken as {['vapour', 'liquid', 'second liquid'][p]}")
        if lle and 0 not in used:
            used[0] = f"{step.step_id}.vapor"
            run.note("vent", f"step {step.step_id}: no vapour outlet named; {used[0]} created (reported if it carries flow)")
        if lle and 2 not in used:
            run.warn(f"step {step.step_id}: a decanter with one liquid outlet named; the second liquid goes to {step.step_id}.liquid2")
            used[2] = f"{step.step_id}.liquid2"
        mcp.connect(c, fs, tag, feed=feed, feed_port=0, product=used.get(0, f"{step.step_id}.vapor"), product_port=0)
        for p in (1, 2):
            if p in used:
                mcp.connect(c, fs, tag, product=used[p], product_port=p)
        if lle:
            run.note("decanter", f"step {step.step_id}: Vessel with the VLLE flash; organic = port 1, aqueous = port 2")

    def r_vessel_lle(self, node, tag):
        self.r_vessel(node, tag, lle=True)

    def r_evaporator(self, node, tag):
        step, run, plant, c, fs = node.step, self.run, self.run.plant, self.c, self.run.fs
        feed = self._single_feed(node, tag)
        uo = plant.overrides.units.get(step.step_id) or {}
        t, src = setpoint(plant, step, "temperature", required=False)
        mid = f"{step.step_id}.evap"
        if "OutletVaporFraction" in uo:
            mcp.unit(c, fs, "Heater", tag + "-H", feed=feed, product=mid, energy_in=f"Q-{tag}", mode="OutletVaporFraction", props={"OutletVaporFraction": float(uo["OutletVaporFraction"])})
            run.value(step.step_id, "vapour fraction", uo["OutletVaporFraction"], "person")
        elif t is not None:
            mcp.unit(c, fs, "Heater", tag + "-H", feed=feed, product=mid, energy_in=f"Q-{tag}", mode="OutletTemperature", props={"OutletTemperature": t})
            run.value(step.step_id, "evaporator temperature K", round(t, 2), src)
        else:
            vf = DEFAULTS["evaporator_vapor_fraction"]
            run.assume(f"step {step.step_id}: evaporator vapour fraction {vf} (neither temperature nor fraction stated)")
            mcp.unit(c, fs, "Heater", tag + "-H", feed=feed, product=mid, energy_in=f"Q-{tag}", mode="OutletVaporFraction", props={"OutletVaporFraction": vf})
        c.call("dwsim_unitop_add", flowsheet_id=fs, type="Vessel", name=tag)
        vap = self._outlet(node, {"vapor", "vapour", "gas", "top", "overhead"}, None) or (node.outlets[0].stream if node.outlets else f"{step.step_id}.vapor")
        liq = self._outlet(node, {"liquid", "bottom", "concentrate"}, None) or (node.outlets[1].stream if len(node.outlets) > 1 else f"{step.step_id}.liquid")
        mcp.connect(c, fs, tag, feed=mid, feed_port=0, product=vap, product_port=0)
        mcp.connect(c, fs, tag, product=liq, product_port=1)

    def r_compsep(self, node, tag, allow_rigorous=True):
        step, run, plant, c, fs = node.step, self.run, self.run.plant, self.c, self.run.fs
        uo = plant.overrides.units.get(step.step_id) or {}
        if allow_rigorous and uo.get("rigorous_extractor"):
            return self.r_absorber(node, tag, column_type="Extractor")
        feed = self._condition(node, tag, self._single_feed(node, tag))
        outs = self._outlets_in_order(node)
        if len(outs) < 2:
            outs = outs + [f"{step.step_id}.rest"]
            run.warn(f"step {step.step_id}: one outlet named; the rest leaves as {outs[1]}")
        splits = plant.overrides.splits.get(step.step_id)
        if splits:
            specs = {run.resolved.name(k) if k in run.resolved else k: float(v) for k, v in splits.items()}
            src = "person"
        else:
            e0 = node.outlets[0]; e1 = node.outlets[1] if len(node.outlets) > 1 else None
            if e0.components and e1 and e1.components and not (set(e0.components) & set(e1.components)):
                specs = {run.resolved.name(f): 100.0 for f in e0.components}
                src = "sharp split from the component lists"
                run.assume(f"step {step.step_id}: sharp split, 100 % of {', '.join(e0.components)} to {e0.stream} and nothing else (fractions not stated)")
            else:
                raise Refused("no split fractions stated and the outlet component lists overlap", step_id=step.step_id, concept=node.entry.concept,
                              resolve=f"overrides.splits.{step.step_id} = {{formula: percent to {outs[0]}}}")
        c.call("dwsim_unitop_add", flowsheet_id=fs, type="ComponentSeparator", name=tag)
        mcp.connect(c, fs, tag, feed=feed, feed_port=0, product=outs[0], product_port=0, energy_out=f"Q-{tag}")
        mcp.connect(c, fs, tag, product=outs[1], product_port=1)
        mcp.compsep(c, fs, tag, 0, specs)
        run.surrogate(step.step_id, f"{node.entry.concept}: specified split ({src}), not simulated. " + (node.entry.note or ""))
        run.value(step.step_id, "split % to " + outs[0], specs, src)

    def r_hx(self, node, tag):
        step, run, plant, c, fs = node.step, self.run, self.run.plant, self.c, self.run.fs
        uo = plant.overrides.units.get(step.step_id) or {}
        sides = {"hot": None, "cold": None}
        tear_side = {}
        for e in node.inlets:
            side = HX_PORTS.get(e.to_port.lower())
            if side is None:
                side = "hot" if sides["hot"] is None and e.tear else ("cold" if sides["cold"] is None else "hot")
                run.assume(f"step {step.step_id}: inlet {e.stream} port not named; taken as the {side} side")
            sides[side] = e
            if e.tear:
                tear_side[side] = e
        outs = {"hot": None, "cold": None}
        for e in node.outlets:
            side = HX_PORTS.get(e.from_port.lower())
            if side is None:
                side = "cold" if outs["cold"] is None else "hot"
                run.assume(f"step {step.step_id}: outlet {e.stream} port not named; taken as the {side} side")
            outs[side] = e.stream
        hot_out = outs["hot"] or f"{step.step_id}.hot_out"
        cold_out = outs["cold"] or f"{step.step_id}.cold_out"
        if "hot_outlet_T_K" in uo:
            t, spec_side, src = float(uo["hot_outlet_T_K"]), "hot", "person"
        elif "cold_outlet_T_K" in uo:
            t, spec_side, src = float(uo["cold_outlet_T_K"]), "cold", "person"
        else:
            t, src = setpoint(plant, step, "temperature")
            spec_side = "cold"
            run.assume(f"step {step.step_id}: the temperature window applies to the cold outlet (the stream being heated)")
        run.value(step.step_id, f"{spec_side} outlet temperature K", round(t, 2), src)
        if not sides["hot"] or not sides["cold"]:
            raise Refused("a heat exchanger needs a hot and a cold inlet", step_id=step.step_id)
        if tear_side:
            # the other stream exists later (or in a loop): two halves tied by duty, built when the hot stream exists
            info = {"tag": tag, "hot_in": sides["hot"].stream, "hot_out": hot_out, "cold_in": sides["cold"].stream, "cold_out": cold_out,
                    "outlet_T_K": t, "specified_side": spec_side, "step_id": step.step_id, "tear": list(tear_side.values())}
            for e in tear_side.values():
                e.tear = False  # the halves remove the loop
                self.deferred_hx[e.stream] = info
            for s in (cold_out, hot_out):
                c.try_call("dwsim_stream_add_material", flowsheet_id=fs, name=s)
            run.surrogate(step.step_id, "heat exchanger across a loop: the two sides are a heater and a cooler tied by duty (dwsim_hx_tie_halves); no area or LMTD")
            return
        c.call("dwsim_unitop_add", flowsheet_id=fs, type="HeatExchanger", name=tag)
        mcp.connect(c, fs, tag, feed=run.actual(sides["hot"].stream), feed_port=0, product=hot_out, product_port=0)
        mcp.connect(c, fs, tag, feed=run.actual(sides["cold"].stream), feed_port=1, product=cold_out, product_port=1)
        args = {"flowsheet_id": fs, "name": tag, "mode": "specify_cold_outlet" if spec_side == "cold" else "specify_hot_outlet"}
        args["cold_outlet_T_K" if spec_side == "cold" else "hot_outlet_T_K"] = t
        c.call("dwsim_hx_configure", **args)

    def _resolve_deferred_hx(self, node):
        """After a node is built: if one of its outlets is the hot inlet of a deferred exchanger, tie the halves now."""
        run, c, fs = self.run, self.c, self.run.fs
        for e in node.outlets:
            info = self.deferred_hx.pop(e.stream, None)
            if not info:
                continue
            c.call("dwsim_hx_tie_halves", flowsheet_id=fs, name=info["tag"], hot_in=run.actual(info["hot_in"]), hot_out=info["hot_out"],
                   cold_in=run.actual(info["cold_in"]), cold_out=info["cold_out"], outlet_T_K=info["outlet_T_K"], specified_side=info["specified_side"])
            self.touch()

    def r_mixer(self, node, tag):
        step, run, c, fs = node.step, self.run, self.c, self.run.fs
        live, tears = self._inlets(node)
        out = node.outlets[0].stream if node.outlets else f"{step.step_id}.out"
        c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name=tag)
        for i, e in enumerate(live):
            mcp.connect(c, fs, tag, feed=run.actual(e.stream), feed_port=i, product=out if i == len(live) - 1 else None)
        if not live:
            c.try_call("dwsim_stream_add_material", flowsheet_id=fs, name=out)
            mcp.connect(c, fs, tag, product=out)
        for k, e in enumerate(tears):
            self.tear_targets[e.stream] = {"unit": tag, "kind": "mixer", "port": len(live) + k, "step_id": step.step_id}

    def r_pump(self, node, tag, kind="Pump"):
        step, run, plant = node.step, self.run, self.run.plant
        feed = self._single_feed(node, tag)
        out = node.outlets[0].stream if node.outlets else f"{step.step_id}.out"
        p, src = setpoint(plant, step, "pressure")
        run.value(step.step_id, "discharge pressure Pa", round(p), src)
        props = {"Pout" if kind == "Pump" else "POut": p}
        props.update(self._props_override(step))
        mcp.unit(self.c, run.fs, kind, tag, feed=feed, product=out, energy_in=f"W-{tag}", mode="OutletPressure", props=props)

    def r_compressor(self, node, tag):
        self.r_pump(node, tag, kind="Compressor")

    def r_splitter(self, node, tag):
        step, run, plant, c, fs = node.step, self.run, self.run.plant, self.c, self.run.fs
        feed = self._single_feed(node, tag)
        outs = self._outlets_in_order(node)
        ratios = plant.overrides.split_ratios.get(step.step_id)
        if ratios and len(ratios) != len(outs):
            raise Refused(f"split_ratios has {len(ratios)} values for {len(outs)} outlets", step_id=step.step_id)
        if not ratios:
            ratios = [1.0 / len(outs)] * len(outs)
            run.assume(f"step {step.step_id}: split {', '.join(outs)} equally (ratios not stated)")
        c.call("dwsim_unitop_add", flowsheet_id=fs, type="Splitter", name=tag)
        for i, s in enumerate(outs):
            mcp.connect(c, fs, tag, feed=feed if i == 0 else None, feed_port=0, product=s, product_port=i)
            c.call("dwsim_object_set", flowsheet_id=fs, target=tag, path=f"Ratios[{i}]", value=float(ratios[i]))
        run.value(step.step_id, "split ratios " + "/".join(outs), ratios, "person" if step.step_id in plant.overrides.split_ratios else "assumed equal")

    def r_reactive_distillation(self, node, tag):
        """Surrogate: an equilibrium reactor at the column's conditions feeding a column; flagged."""
        step, run = node.step, self.run
        run.surrogate(step.step_id, "reactive distillation as a reactor followed by a column (DWSIM 10.2 has no reactive column); "
                                    "the bottoms leave instead of returning to the reaction zone")
        rs, rtype, phase, model = self.reaction_sets[step.step_id]
        feed = self._single_feed(node, tag)
        t, src = setpoint(run.plant, step, "temperature", required=False)
        props = {"ReactionSetID": rs}
        mode = "OutletTemperature" if t is not None else "Adiabatic"
        if t is not None:
            props["OutletTemperature"] = t
        rx_out = f"{step.step_id}.rx"
        mcp.unit(self.c, run.fs, rtype, tag + "-RX", feed=feed, product=f"{step.step_id}.rxv", product_port=0, energy_in=f"Q-{tag}-RX", mode=mode, props=props)
        mcp.connect(self.c, run.fs, tag + "-RX", product=f"{step.step_id}.rxl", product_port=1)
        self.c.call("dwsim_unitop_add", flowsheet_id=run.fs, type="Mixer", name=f"MIX-{tag}")
        mcp.connect(self.c, run.fs, f"MIX-{tag}", feed=f"{step.step_id}.rxv", feed_port=0)
        mcp.connect(self.c, run.fs, f"MIX-{tag}", feed=f"{step.step_id}.rxl", feed_port=1, product=rx_out)
        self.touch()
        self.r_column(node, tag, feed_override=rx_out)

    def _passthrough(self, node):
        step, run = node.step, self.run
        if node.inlets and node.outlets:
            run.aliases[node.outlets[0].stream] = run.actual(node.inlets[0].stream)
            run.note("pass-through", f"step {step.step_id} ({node.entry.concept}): {node.outlets[0].stream} is {run.actual(node.inlets[0].stream)}")
        elif node.outlets and not node.inlets:
            raise Refused("storage with an outlet but no inlet: give it a boundary feed stream (step '')", step_id=step.step_id)

    # ---- product and target ---------------------------------------------------------------------
    def _product(self):
        run, plant = self.run, self.run.plant
        target_f = plant.target["product_formula"]
        run.product_name = run.resolved.name(target_f)
        ps = plant.target.get("product_stream")
        if not ps:
            cands = [e for e in self.topo.boundary_products() if e.role == "product" and target_f in e.components]
            cands = cands or [e for e in self.topo.boundary_products() if target_f in e.components]
            if cands:
                ps = cands[0].stream
        if ps:
            run.product_stream = run.actual(ps)

    def _scale_to_target(self, label="open loop"):
        run, c, plant = self.run, self.c, self.run.plant
        if not run.product_stream or plant.options.get("scale_to_target", True) is False:
            return
        derived = [f for f in run.feeds if f["derived"]]
        if not derived:
            return
        asked = float(plant.target["rate_kg_h"]) / 3600.0
        for it in range(int(plant.options.get("scale_iterations", 2))):
            rep = c.try_call("dwsim_plant_report", flowsheet_id=run.fs, product_stream=run.product_stream, product_compound=run.product_name,
                             asked_product_rate_kg_s=asked, streams=[run.product_stream])
            got = (rep or {}).get("product", {}).get("mass_flow_kg_s") or 0
            if got <= 0:
                run.warn("product rate zero or unknown after the open-loop solve; feeds not scaled to the target")
                return
            ratio = asked / got
            if abs(ratio - 1) < 0.005:
                break
            lo, hi = plant.options.get("scale_range", [0.5, 2.0])
            if not lo <= ratio <= hi:
                run.warn(f"product {got * 3600:.4g} kg/h vs asked {asked * 3600:.4g} kg/h ({label}): the ratio {ratio:.3g} is outside "
                         f"[{lo}, {hi}], so the feeds are not scaled (a plant with loops left open loses product through them; absolute "
                         f"column draws would not follow a large scale change). The report states achieved / asked.")
                return
            before = {f["stream"]: f["mol_s"] for f in run.feeds}
            for f in derived:
                f["mol_s"] *= ratio
                f["source"] += f"; scaled x{ratio:.4g} to meet the target (pass {it + 1})"
            apply_ratios(run.feeds)
            for f in run.feeds:
                if f["derived"] or f.get("ratio_to"):
                    c.call("dwsim_stream_set_conditions", flowsheet_id=run.fs, name=f["stream"], molar_flow_mol_s=f["mol_s"])
            # the closed loops' tear streams hold the old solution: scale them too, or the recycles start 1/ratio off
            for lp in run.loops_closed:
                rs = c.try_call("dwsim_stream_get_results", flowsheet_id=run.fs, name=lp["stream"] + "-R")
                if rs and rs.get("molar_flow_mol_s", 0) > 0:
                    c.try_call("dwsim_stream_set_conditions", flowsheet_id=run.fs, name=lp["stream"] + "-R", molar_flow_mol_s=rs["molar_flow_mol_s"] * ratio)
            self.touch()
            r = self.solve(f"feeds scaled x{ratio:.4g} to the target ({label})")
            if not r.get("ok"):
                for f in run.feeds:
                    if f["mol_s"] != before[f["stream"]]:
                        f["mol_s"] = before[f["stream"]]
                        f["source"] = f["source"].split("; scaled x")[0]
                        c.try_call("dwsim_stream_set_conditions", flowsheet_id=run.fs, name=f["stream"], molar_flow_mol_s=f["mol_s"])
                for lp in run.loops_closed:
                    rs = c.try_call("dwsim_stream_get_results", flowsheet_id=run.fs, name=lp["stream"] + "-R")
                    if rs and rs.get("molar_flow_mol_s", 0) > 0:
                        c.try_call("dwsim_stream_set_conditions", flowsheet_id=run.fs, name=lp["stream"] + "-R", molar_flow_mol_s=rs["molar_flow_mol_s"] / ratio)
                self.touch()
                self.solve(f"feeds restored after the scaled solve failed ({label})")
                run.warn(f"solve failed after scaling the feeds x{ratio:.4g} ({label}); feeds restored to the previous values")
                return
        run.note("target", f"derived feeds scaled so that the product rate meets {asked * 3600:.4g} kg/h ({label})")


def _mid(v):
    if isinstance(v, dict):
        return 0.5 * (v["min_si"] + v["max_si"])
    if isinstance(v, (list, tuple)):
        return 0.5 * (v[0] + v[1])
    return float(v)
