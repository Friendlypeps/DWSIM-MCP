"""Steps and connections -> the streams between units, the plant boundary and the tears.

Stream names come from the step records (streams_out.stream of the sending step, else streams_in.stream of the
receiving step, else '<from_step>.<port>'). A connection whose target comes earlier in the order, or whose stream
has role 'recycle', is a tear: the open-loop build leaves it out and recycles.py closes it. A Storage step at the
boundary is the feed or product stream itself; an internal Storage or a one-outlet Recycling step passes through.
"""
from dataclasses import dataclass, field
from typing import List, Optional

from . import concepts
from .errors import Refused


@dataclass
class Edge:
    stream: str
    from_step: str
    from_port: str
    to_step: str
    to_port: str
    components: List[str] = field(default_factory=list)
    role: str = "intermediate"
    tear: bool = False
    direction_assumed: bool = False


@dataclass
class Node:
    step: object
    entry: Optional[object]      # concepts.ConceptEntry
    inlets: List[Edge] = field(default_factory=list)      # edges and boundary feeds (from_step "")
    outlets: List[Edge] = field(default_factory=list)     # edges and boundary products (to_step "")
    passthrough: bool = False


class Topology:
    def __init__(self, plant):
        self.plant = plant
        self.nodes = {s.step_id: Node(s, concepts.lookup(s.unit_operation)) for s in plant.process_steps}
        self.edges = []
        self._build()

    def _build(self):
        p = self.plant
        order = {s.step_id: s.order for s in p.process_steps}
        seen = set()

        def add(stream, a, ap, b, bp, comps, role, assumed=False):
            key = (stream, a, ap, b, bp)
            if key in seen:
                return
            seen.add(key)
            e = Edge(stream, a, ap, b, bp, list(comps), role, False, assumed)
            if a and b:
                e.tear = role == "recycle" or order[b] <= order[a]
            self.edges.append(e)
            if a:
                self.nodes[a].outlets.append(e)
            if b:
                self.nodes[b].inlets.append(e)

        # explicit connections first (they carry the ports)
        for c in p.connections:
            st_out = _find(p.step(c.from_step).streams_out, port=c.from_port, step=c.to_step)
            st_in = _find(p.step(c.to_step).streams_in, port=c.to_port, step=c.from_step)
            name = (st_out.stream if st_out else None) or (st_in.stream if st_in else None) or f"{c.from_step}.{c.from_port or 'out'}"
            comps = (st_out.components if st_out else None) or (st_in.components if st_in else [])
            role = (st_out.role if st_out else None) or (st_in.role if st_in else "intermediate")
            add(name, c.from_step, c.from_port or (st_out.port if st_out else ""), c.to_step, c.to_port or (st_in.port if st_in else ""),
                comps, role, c.direction_assumed)
        # streams the records name but the connection list does not
        for s in p.process_steps:
            for st in s.streams_out:
                if st.step == "?":
                    raise Refused(f"stream {st.stream!r} goes to an unknown step ('?')", step_id=s.step_id, what=st.stream,
                                  resolve="connect it or mark it a plant product (step '')")
                if st.step and st.step in self.nodes:
                    partner = _find(p.step(st.step).streams_in, step=s.step_id, stream=st.stream)
                    if not any(e.from_step == s.step_id and e.to_step == st.step and e.stream == st.stream for e in self.edges):
                        add(st.stream, s.step_id, st.port, st.step, partner.port if partner else "", st.components, st.role)
                elif st.step == "":
                    add(st.stream, s.step_id, st.port, "", "", st.components, st.role)
            for st in s.streams_in:
                if st.step == "":
                    add(st.stream, "", "", s.step_id, st.port, st.components, st.role)
                elif st.step == "?":
                    raise Refused(f"stream {st.stream!r} comes from an unknown step ('?')", step_id=s.step_id, what=st.stream,
                                  resolve="connect it or mark it a plant feed (step '')")
        # pass-through steps
        for n in self.nodes.values():
            e = n.entry
            if e and e.recipe == "tank" and len(n.outlets) <= 1 and len(n.inlets) <= 1:
                n.passthrough = True
            if e and e.recipe == "recycle" and len(n.outlets) <= 1:
                n.passthrough = True
                for o in n.outlets:
                    o.tear = True

    def ordered(self):
        return [self.nodes[s.step_id] for s in self.plant.process_steps]

    def tears(self):
        return [e for e in self.edges if e.tear]

    def boundary_products(self):
        return [e for e in self.edges if e.to_step == ""]

    def boundary_feeds(self):
        return [e for e in self.edges if e.from_step == ""]

    def unmapped(self):
        """Steps whose concept has no recipe (refused by the vocabulary table) or is unknown."""
        out = []
        for n in self.nodes.values():
            if n.entry is None:
                out.append(Refused(f"unit operation {n.step.unit_operation!r} is not in the vocabulary table", step_id=n.step.step_id,
                                   concept=n.step.unit_operation, resolve="use one of the 58 concepts (python3 -m openrun_sim explain-concepts)"))
            elif n.entry.recipe is None:
                sur = self.plant.overrides.surrogates or {}
                if n.entry.concept == "ReactiveDistillation" and sur.get("reactive_distillation"):
                    continue
                out.append(Refused(n.entry.reason, step_id=n.step.step_id, concept=n.entry.concept,
                                   resolve="choose a narrower concept or a surrogate (overrides.surrogates)"))
        return out


def _find(streams, port=None, step=None, stream=None):
    for s in streams:
        if stream and s.stream == stream:
            return s
    for s in streams:
        if port and s.port == port and (not step or s.step == step):
            return s
    for s in streams:
        if step and s.step == step:
            return s
    return None
