"""The input contract, mirroring OPEN_RUN_SIMULATION_BRIEF.md section 1:
ProcessStepRecord (p3), RequirementSpec (p3), Flowsheet (p5), plus the target and the overrides a person adds.

Loading is strict: unknown keys are refused, so a field the pipeline renames does not vanish silently.
"""
import json
import re
from dataclasses import dataclass, field, asdict
from typing import Dict, List, Optional

from .errors import Refused

ROLES = {"feed", "intermediate", "recycle", "utility", "product", "by_product", "waste"}
KINDS = {"UNIT_PROCESS", "UNIT_OPERATION"}
STATUSES = {"CHOSEN", "CHOSEN_WITHOUT_DATA", "AWAITING_CHOICE", "LEFT_EMPTY", "NO_CANDIDATE"}
SLOT_KEYS = {"stages", "feed_stage", "reflux_ratio", "top_temperature", "bottom_temperature", "top_pressure",
             "bottom_pressure", "inlet_temperature", "outlet_temperature", "inlet_pressure", "outlet_pressure",
             "duty", "medium", "phase", "phases", "residence_time", "material", "catalyst", "temperature", "pressure"}


@dataclass
class Range:
    min_si: Optional[float] = None
    max_si: Optional[float] = None

    @staticmethod
    def of(d):
        if d is None:
            return None
        if isinstance(d, (int, float)):
            return Range(float(d), float(d))
        if isinstance(d, dict):
            return Range(_num(d.get("min_si")), _num(d.get("max_si")))
        if isinstance(d, (list, tuple)) and len(d) == 2:
            return Range(_num(d[0]), _num(d[1]))
        raise Refused(f"a range is {{min_si, max_si}}, got {d!r}")

    def is_closed(self):
        return self.min_si is not None and self.max_si is not None

    def text(self):
        a = "open" if self.min_si is None else f"{self.min_si:g}"
        b = "open" if self.max_si is None else f"{self.max_si:g}"
        return f"[{a}, {b}]"


def _num(v):
    if v is None:
        return None
    if isinstance(v, (int, float)):
        if v == 0:
            raise Refused("a bound of zero is not a value the brief allows (None = open, never zero)")
        return float(v)
    raise Refused(f"a bound must be a number or null, got {v!r}")


@dataclass
class Stream:
    stream: str
    components: List[str]          # Hill formulas
    role: str
    step: str                      # other end's step_id; "" plant boundary; "?" not stated
    port: str = ""

    @staticmethod
    def of(d):
        _only(d, {"stream", "components", "role", "step", "port"}, "stream")
        s = Stream(d["stream"], list(d.get("components", [])), d.get("role", "intermediate"), d.get("step", ""), d.get("port", ""))
        if s.role not in ROLES:
            raise Refused(f"stream {s.stream}: role {s.role!r} is not one of {sorted(ROLES)}")
        return s


@dataclass
class Substance:
    formula: str
    name: str = ""
    role: str = ""
    inchikey: str = ""
    cas: str = ""

    @staticmethod
    def of(d):
        _only(d, {"formula", "name", "role", "inchikey", "cas"}, "substance")
        return Substance(d["formula"], d.get("name", ""), d.get("role", ""), d.get("inchikey", ""), d.get("cas", ""))


@dataclass
class ProcessStep:
    step_id: str
    order: int
    kind: str
    label: str
    unit_operation: str
    equation: str = ""
    produces: List[str] = field(default_factory=list)
    educts: List[str] = field(default_factory=list)
    substances: List[Substance] = field(default_factory=list)
    t_window: str = ""
    p_window: str = ""
    catalyst: str = ""
    capability_uri: str = ""
    streams_in: List[Stream] = field(default_factory=list)
    streams_out: List[Stream] = field(default_factory=list)
    provenance: Dict[str, str] = field(default_factory=dict)
    slots: Dict[str, object] = field(default_factory=dict)

    @staticmethod
    def of(d):
        _only(d, {"step_id", "order", "kind", "label", "unit_operation", "equation", "produces", "educts", "substances",
                  "t_window", "p_window", "catalyst", "capability_uri", "streams_in", "streams_out", "provenance", "slots"}, "process step")
        kind = d.get("kind", "UNIT_OPERATION")
        if kind not in KINDS:
            raise Refused(f"step {d.get('step_id')}: kind {kind!r} is not UNIT_PROCESS or UNIT_OPERATION")
        slots = dict(d.get("slots", {}))
        bad = set(slots) - SLOT_KEYS
        if bad:
            raise Refused(f"step {d.get('step_id')}: unknown slots {sorted(bad)}")
        return ProcessStep(
            step_id=str(d["step_id"]), order=int(d["order"]), kind=kind, label=d.get("label", ""), unit_operation=d["unit_operation"],
            equation=d.get("equation", ""), produces=list(d.get("produces", [])), educts=list(d.get("educts", [])),
            substances=[Substance.of(x) for x in d.get("substances", [])], t_window=d.get("t_window", ""), p_window=d.get("p_window", ""),
            catalyst=d.get("catalyst", ""), capability_uri=d.get("capability_uri", ""),
            streams_in=[Stream.of(x) for x in d.get("streams_in", [])], streams_out=[Stream.of(x) for x in d.get("streams_out", [])],
            provenance=dict(d.get("provenance", {})), slots=slots)


@dataclass
class Requirement:
    step_id: str
    temperature: Optional[Range] = None
    pressure: Optional[Range] = None
    phase: str = ""
    materials_required: List[str] = field(default_factory=list)
    substances: List[str] = field(default_factory=list)
    volume: Optional[float] = None
    throughput: Optional[float] = None
    continuous: Optional[bool] = None
    safety_classes: List[str] = field(default_factory=list)

    @staticmethod
    def of(d):
        _only(d, {"step_id", "temperature", "pressure", "phase", "materials_required", "substances", "volume", "throughput",
                  "continuous", "safety_classes"}, "requirement")
        return Requirement(str(d["step_id"]), Range.of(d.get("temperature")), Range.of(d.get("pressure")), d.get("phase", ""),
                           list(d.get("materials_required", [])), list(d.get("substances", [])), d.get("volume"), d.get("throughput"),
                           d.get("continuous"), list(d.get("safety_classes", [])))


@dataclass
class FlowsheetStep:
    step_id: str
    capability: str = ""
    status: str = "CHOSEN"
    pea: Dict[str, str] = field(default_factory=dict)
    offered_ranges: Dict[str, Range] = field(default_factory=dict)
    inlets: List[str] = field(default_factory=list)
    outlets: List[str] = field(default_factory=list)

    @staticmethod
    def of(d):
        _only(d, {"step_id", "capability", "status", "pea", "offered_ranges", "inlets", "outlets"}, "flowsheet step")
        st = d.get("status", "CHOSEN")
        if st not in STATUSES:
            raise Refused(f"flowsheet step {d.get('step_id')}: status {st!r} is not one of {sorted(STATUSES)}")
        return FlowsheetStep(str(d["step_id"]), d.get("capability", ""), st, dict(d.get("pea", {})),
                             {k: Range.of(v) for k, v in d.get("offered_ranges", {}).items()},
                             list(d.get("inlets", [])), list(d.get("outlets", [])))


@dataclass
class Connection:
    from_step: str
    from_port: str
    to_step: str
    to_port: str
    source: str = ""
    direction_assumed: bool = False

    @staticmethod
    def of(d):
        _only(d, {"from_step", "from_port", "to_step", "to_port", "source", "direction_assumed"}, "connection")
        return Connection(str(d["from_step"]), d.get("from_port", ""), str(d["to_step"]), d.get("to_port", ""),
                          d.get("source", ""), bool(d.get("direction_assumed", False)))


@dataclass
class Overrides:
    """What a person adds. Every value used from here is reported with source 'person'."""
    setpoints: Dict[str, Dict[str, float]] = field(default_factory=dict)      # step_id -> {temperature_K, pressure_Pa}
    splits: Dict[str, Dict[str, float]] = field(default_factory=dict)         # step_id -> {compound: percent to outlet 0}
    split_ratios: Dict[str, List[float]] = field(default_factory=dict)        # step_id -> splitter ratios
    column: Dict[str, Dict[str, object]] = field(default_factory=dict)        # step_id -> {stages, feed_stage, reflux_ratio, reboiler_spec, condenser_type}
    reaction: Dict[str, Dict[str, object]] = field(default_factory=dict)      # step_id -> {conversion_pct, keq, keq_basis, kinetics, feed_conversion, phase}
    feeds: Dict[str, Dict[str, object]] = field(default_factory=dict)         # stream name -> {molar_flow_mol_s, composition (mole fractions by formula or name), T_K, P_Pa, excess}
    surrogates: Dict[str, object] = field(default_factory=dict)               # {reactive_distillation: true}
    loops: Dict[str, List[str]] = field(default_factory=dict)                 # {close: [stream names], leave_open: [...]}
    compounds: Dict[str, object] = field(default_factory=dict)                # formula -> databank name, or {json_path}
    units: Dict[str, Dict[str, object]] = field(default_factory=dict)         # step_id -> free unit properties (dwsim_unitop_set), e.g. {"OutletVaporFraction": 0.3}
    package: str = ""                                                         # force a property package
    windows: Dict[str, Dict[str, List[Optional[float]]]] = field(default_factory=dict)  # step_id -> {quantity: [min, max]} extra checks

    @staticmethod
    def of(d):
        d = {k: v for k, v in (d or {}).items() if not k.startswith("_")}
        _only(d, {"setpoints", "splits", "split_ratios", "column", "reaction", "feeds", "surrogates", "loops", "compounds", "units", "package", "windows"}, "overrides")
        return Overrides(**{k: v for k, v in d.items()})


@dataclass
class Plant:
    plant_id: str
    target: Dict[str, object]
    process_steps: List[ProcessStep]
    requirements: List[Requirement]
    flowsheet_steps: List[FlowsheetStep]
    connections: List[Connection]
    overrides: Overrides
    options: Dict[str, object] = field(default_factory=dict)

    def step(self, step_id):
        for s in self.process_steps:
            if s.step_id == step_id:
                return s
        raise Refused(f"no process step {step_id!r}")

    def requirement(self, step_id):
        return next((r for r in self.requirements if r.step_id == step_id), None)

    def flowsheet_step(self, step_id):
        return next((f for f in self.flowsheet_steps if f.step_id == step_id), None)

    @staticmethod
    def of(d):
        _only(d, {"plant_id", "target", "process_steps", "requirements", "flowsheet", "overrides", "options", "_comment"}, "plant")
        fsd = d.get("flowsheet", {}) or {}
        _only(fsd, {"steps", "connections"}, "flowsheet")
        p = Plant(
            plant_id=d.get("plant_id", "plant"), target=dict(d.get("target", {})),
            process_steps=sorted([ProcessStep.of(x) for x in d.get("process_steps", [])], key=lambda s: s.order),
            requirements=[Requirement.of(x) for x in d.get("requirements", [])],
            flowsheet_steps=[FlowsheetStep.of(x) for x in fsd.get("steps", [])],
            connections=[Connection.of(x) for x in fsd.get("connections", [])],
            overrides=Overrides.of(d.get("overrides")), options=dict(d.get("options", {})))
        ids = [s.step_id for s in p.process_steps]
        if len(ids) != len(set(ids)):
            raise Refused("duplicate step_id in process_steps")
        for c in p.connections:
            for sid in (c.from_step, c.to_step):
                if sid not in ids:
                    raise Refused(f"connection {c.from_step}/{c.from_port} -> {c.to_step}/{c.to_port} names unknown step {sid!r}")
        for key in ("product_formula", "rate_kg_h"):
            if key not in p.target:
                raise Refused(f"target.{key} is required (what the plant makes and how much)")
        return p


def _only(d, allowed, what):
    if not isinstance(d, dict):
        raise Refused(f"{what} must be an object")
    bad = set(d) - set(allowed)
    if bad:
        raise Refused(f"{what}: unknown keys {sorted(bad)} (allowed: {sorted(allowed)})")


def load(path):
    with open(path, encoding="utf-8") as f:
        return Plant.of(json.load(f))


def to_json(obj):
    return json.loads(json.dumps(asdict(obj), default=str))


# ---- windows as the source wrote them ("70 to 80 degC", "1 atm", "25-40 bar") -> SI Range ----------------
_UNIT_T = {"degc": ("C", 1.0), "°c": ("C", 1.0), "c": ("C", 1.0), "k": ("K", 1.0), "degf": ("F", 1.0)}
_UNIT_P = {"atm": 101325.0, "bar": 1e5, "bara": 1e5, "mbar": 100.0, "kpa": 1e3, "mpa": 1e6, "pa": 1.0, "psi": 6894.757, "psia": 6894.757}


def parse_window(text, kind):
    """'70 to 80 degC' | '80-110 °C' | '1 atm' | '2 to 2.1 atm' | '-10 degC' -> Range in SI, or None when not stated."""
    if not text or not str(text).strip():
        return None
    t = str(text).strip().lower().replace("−", "-").replace("–", "-")
    m = re.match(r"^\s*(-?\d+(?:\.\d+)?)\s*(?:(?:to|-|\.\.)\s*(-?\d+(?:\.\d+)?))?\s*([a-z°]*)\s*$", t)
    if not m:
        return None
    a = float(m.group(1)); b = float(m.group(2)) if m.group(2) else a
    unit = m.group(3)
    if kind == "temperature":
        if unit in ("k",):
            return Range(min(a, b), max(a, b))
        if unit in ("degf", "f"):
            a, b = (a - 32) * 5 / 9 + 273.15, (b - 32) * 5 / 9 + 273.15
        else:  # degC default
            a, b = a + 273.15, b + 273.15
        return Range(min(a, b), max(a, b))
    f = _UNIT_P.get(unit, None)
    if f is None:
        f = 1e5 if unit == "" else None
    if f is None:
        return None
    return Range(min(a, b) * f, max(a, b) * f)
