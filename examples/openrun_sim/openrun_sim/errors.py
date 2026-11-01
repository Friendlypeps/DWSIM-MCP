"""The refusals: each names the step, what is missing and what would resolve it. A run collects them and
stops before any flowsheet is built when it can; a refusal mid-build is reported with the partial state."""


class Refused(Exception):
    def __init__(self, reason, step_id=None, concept=None, what=None, resolve=None):
        self.reason, self.step_id, self.concept, self.what, self.resolve = reason, step_id, concept, what, resolve
        super().__init__(self.text())

    def text(self):
        parts = []
        if self.step_id:
            parts.append(f"step {self.step_id}")
        if self.concept:
            parts.append(f"({self.concept})")
        parts.append(self.reason)
        if self.resolve:
            parts.append(f"-> {self.resolve}")
        return " ".join(parts)

    def to_json(self):
        return {"step_id": self.step_id, "concept": self.concept, "what": self.what, "reason": self.reason, "resolve": self.resolve}


class Ambiguous(Refused):
    def __init__(self, formula, candidates, step_id=None):
        self.candidates = candidates
        super().__init__(f"formula {formula} matches {len(candidates)} databank compounds: {', '.join(candidates)}",
                         step_id=step_id, what=formula, resolve="give the name or CAS (overrides.compounds)")


class OpenBound(Refused):
    def __init__(self, step_id, quantity, window):
        self.quantity, self.window = quantity, window
        super().__init__(f"{quantity} window {window} is open on one side", step_id=step_id, what=quantity,
                         resolve="give the value in overrides.setpoints")
