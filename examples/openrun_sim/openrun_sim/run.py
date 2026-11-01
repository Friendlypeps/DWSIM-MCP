"""The run context: everything the builder learns or assumes, so the report can state the provenance of
every number and the reader sees what was stated, what was derived, what was assumed and what is a placeholder."""
import sys
import time


class Run:
    def __init__(self, plant, client=None, log=None):
        self.plant = plant
        self.client = client
        self.fs = None
        self.resolved = None
        self.started = time.time()
        self.assumptions = []      # ASSUMED: needed to close the simulation, not stated
        self.placeholders = []     # PLACEHOLDER: a reaction model number nobody stated
        self.warnings = []
        self.notes = []            # {kind, text, source}
        self.provenance = []       # {step_id, item, value, source}
        self.refusals = []
        self.surrogates = []       # what is a stand-in and the sentence the report must carry
        self.units = {}            # step_id -> main unit tag
        self.unit_steps = {}       # unit tag -> step_id
        self.aliases = {}          # stream name -> actual stream name (storage pass-through, mixers)
        self.tears = []            # topology.Tear list
        self.loops_closed = []
        self.loops_open = []       # {stream, reason}
        self.windows = []          # dwsim_plant_check windows
        self.feeds = []            # feed specs
        self.stages = []           # {stage, ok, errors}
        self.product_stream = None
        self.product_name = None
        self._log = log or (lambda m: print(m, file=sys.stderr))

    def log(self, msg):
        self._log(msg)

    def assume(self, text):
        if text not in self.assumptions:
            self.assumptions.append(text)
            self.log("  ASSUMED: " + text)

    def placeholder(self, step_id, text):
        self.placeholders.append({"step_id": step_id, "text": text})
        self.log(f"  PLACEHOLDER step {step_id}: {text}")

    def warn(self, text):
        self.warnings.append(text)
        self.log("  WARNING: " + text)

    def note(self, kind, text, source=""):
        self.notes.append({"kind": kind, "text": text, "source": source})
        self.log(f"  {kind}: {text}")

    def value(self, step_id, item, value, source):
        self.provenance.append({"step_id": step_id, "item": item, "value": value, "source": source})

    def surrogate(self, step_id, text):
        self.surrogates.append({"step_id": step_id, "text": text})
        self.log(f"  SURROGATE step {step_id}: {text}")

    def actual(self, stream):
        seen = set()
        while stream in self.aliases and stream not in seen:
            seen.add(stream)
            stream = self.aliases[stream]
        return stream

    def summary(self):
        return {"plant_id": self.plant.plant_id, "flowsheet_id": self.fs, "elapsed_s": round(time.time() - self.started, 1),
                "assumptions": self.assumptions, "placeholders": self.placeholders, "warnings": self.warnings, "notes": self.notes,
                "provenance": self.provenance, "refusals": [r.to_json() if hasattr(r, "to_json") else str(r) for r in self.refusals],
                "surrogates": self.surrogates, "units": self.units, "loops_closed": self.loops_closed, "loops_open": self.loops_open,
                "stages": self.stages, "feeds": self.feeds, "product_stream": self.product_stream}
