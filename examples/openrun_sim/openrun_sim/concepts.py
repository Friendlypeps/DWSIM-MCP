"""The 58 unit-operation concepts of the vocabulary (product_to_pea/vocab/unit_operations.ttl) and what the
builder makes of each: a recipe the builder knows, or a refusal with the reason. Follows the brief's table
(section 2) and adds the three mappings it left open (Stripping, WasteHeatRecovery, Sedimentation) and the
stand-ins (Extraction, LiquidLiquidExtraction, Adsorption, Drying, Pervaporation as a specified split).
"""
from dataclasses import dataclass, field
from typing import List, Optional


@dataclass
class ConceptEntry:
    concept: str
    parent: Optional[str]
    recipe: Optional[str]            # None = refused
    knob: str = ""                   # the setpoint the recipe takes: temperature | pressure | none
    needs: List[str] = field(default_factory=list)   # overrides/slots that must exist
    reason: str = ""                 # for refusals
    note: str = ""                   # what the report must say for stand-ins


E = ConceptEntry
GENERIC = "generic root: it says not which unit separates (or conveys, or transfers heat) how; a reading must not leave it on a step"

CONCEPTS: List[ConceptEntry] = [
    # roots
    E("Separation", None, None, reason=GENERIC),
    E("Reaction", None, "reactor", "temperature"),
    E("HeatTransfer", None, None, reason=GENERIC + "; Heating, Cooling, Condensation, Vaporisation or WasteHeatRecovery say it"),
    E("Mixing", None, "mixer", "none"),
    E("Conveying", None, None, reason=GENERIC + "; Pumping, Compression or Storage say it"),
    E("SizeChange", None, None, reason="no size-change unit in DWSIM"),
    # thermal separation
    E("ThermalSeparation", "Separation", None, reason=GENERIC),
    E("Distillation", "ThermalSeparation", "column", "pressure"),
    E("Rectification", "Distillation", "column", "pressure"),
    E("VacuumDistillation", "Distillation", "column", "pressure"),
    E("FlashDistillation", "Distillation", "vessel", "none"),
    E("Evaporation", "ThermalSeparation", "evaporator", "none", needs=["slots.outlet_temperature or units.OutletVaporFraction (ASSUMED default otherwise)"]),
    E("Drying", "ThermalSeparation", "compsep", "none", needs=["overrides.splits"], note="specified split, not simulated: DWSIM has no dryer"),
    E("Crystallisation", "ThermalSeparation", None, reason="the Crystallizer needs a solid phase in the package; no route studied has a solid in its basis"),
    # mechanical separation
    E("MechanicalSeparation", "Separation", None, reason=GENERIC),
    E("Filtration", "MechanicalSeparation", None, reason="the Filter needs a solid phase in the package"),
    E("Centrifugation", "MechanicalSeparation", None, reason="the Centrifuge needs a solid phase in the package"),
    E("Sedimentation", "MechanicalSeparation", "vessel_lle", "none", note="two liquids settling: a Vessel with a VLLE flash and liquid-liquid parameters"),
    E("PhaseSeparation", "MechanicalSeparation", "vessel", "none"),
    E("Decantation", "PhaseSeparation", "vessel_lle", "none"),
    # mass transfer separation
    E("MassTransferSeparation", "Separation", None, reason=GENERIC),
    E("Extraction", "MassTransferSeparation", "compsep", "none", needs=["overrides.splits"], note="specified split, not simulated (overrides.units.rigorous_extractor builds the rigorous Extractor column instead)"),
    E("LiquidLiquidExtraction", "Extraction", "compsep", "none", needs=["overrides.splits"], note="specified split, not simulated (overrides.units.rigorous_extractor builds the rigorous Extractor column instead)"),
    E("Absorption", "MassTransferSeparation", "absorber", "pressure"),
    E("GasScrubbing", "Absorption", "absorber", "pressure"),
    E("Stripping", "MassTransferSeparation", "stripper", "pressure", note="a reboiled absorber (one liquid feed) or an absorber with the stripping gas as second feed"),
    E("Adsorption", "MassTransferSeparation", "compsep", "none", needs=["overrides.splits"], note="specified split, not simulated: DWSIM has no adsorber"),
    E("Pervaporation", "MassTransferSeparation", "compsep", "none", needs=["overrides.splits"], note="specified split, not simulated: DWSIM has no membrane unit"),
    # reactions
    E("Esterification", "Reaction", "reactor", "temperature"),
    E("Acetylation", "Reaction", "reactor", "temperature"),
    E("Carboxylation", "Reaction", "reactor", "temperature"),
    E("Nitration", "Reaction", "reactor", "temperature"),
    E("Hydrolysis", "Reaction", "reactor", "temperature"),
    E("Dehydrogenation", "Reaction", "reactor", "temperature"),
    E("TishchenkoReaction", "Reaction", "reactor", "temperature"),
    E("Oxidation", "Reaction", "reactor", "temperature"),
    E("Hydrogenation", "Reaction", "reactor", "temperature"),
    E("StirredTankReaction", "Reaction", "reactor", "temperature"),
    E("FixedBedReaction", "Reaction", "reactor", "temperature", note="PFR when kinetics and a residence time exist, else the equilibrium or conversion reactor"),
    E("TubularReaction", "Reaction", "reactor", "temperature", note="PFR when kinetics and a residence time exist"),
    E("GauzeReaction", "Reaction", "reactor", "temperature"),
    E("SlurryReaction", "Reaction", "reactor", "temperature", note="the solid catalyst is not in the basis"),
    E("HomogeneousGasReaction", "Reaction", "reactor", "temperature", note="vapour phase; a Gibbs K is sound here"),
    E("ReactiveDistillation", "Reaction", None, reason="DWSIM 10.2 has no reactive column; overrides.surrogates.reactive_distillation = true builds a reactor + column + recycle surrogate and the report says so"),
    # heat transfer
    E("Heating", "HeatTransfer", "heater", "temperature"),
    E("Vaporisation", "HeatTransfer", "heater", "temperature", note="outlet temperature from the window; overrides.units.OutletVaporFraction switches to a vapour-fraction spec"),
    E("Cooling", "HeatTransfer", "cooler", "temperature"),
    E("Condensation", "HeatTransfer", "cooler", "temperature"),
    E("WasteHeatRecovery", "HeatTransfer", "hx", "temperature", note="a two-sided HeatExchanger on the two process streams (dwsim_hx_configure); inside a loop that fails, two halves tied by duty (dwsim_hx_tie_halves)"),
    # mixing
    E("Stirring", "Mixing", "mixer", "none"),
    E("GasMixing", "Mixing", "mixer", "none"),
    E("AcidMixing", "Mixing", "mixer", "none"),
    E("Dissolution", "Mixing", "mixer", "none"),
    # conveying
    E("Pumping", "Conveying", "pump", "pressure"),
    E("Dosing", "Conveying", "pump", "pressure"),
    E("Compression", "Conveying", "compressor", "pressure"),
    E("Storage", "Conveying", "tank", "none", note="a boundary storage step is the feed or product stream itself; an internal one passes through"),
    E("Recycling", "Conveying", "recycle", "none", note="not a unit: a Recycle block on the stream closes the loop"),
    # size change
    E("Milling", "SizeChange", None, reason="no mill in DWSIM"),
]

BY_NAME = {c.concept.lower(): c for c in CONCEPTS}
ALIASES = {"crystallization": "Crystallisation", "vaporization": "Vaporisation", "liquid-liquid extraction": "LiquidLiquidExtraction",
           "liquidliquidextraction": "LiquidLiquidExtraction", "tishchenko": "TishchenkoReaction", "stirredtank": "StirredTankReaction"}


def lookup(concept):
    """The entry for a concept name as the vocabulary spells it (case-insensitive, a few aliases)."""
    if not concept:
        return None
    key = concept.strip().split("#")[-1].split("/")[-1].lower()
    key = ALIASES.get(key, key).lower()
    return BY_NAME.get(key)


def table():
    rows = []
    for c in CONCEPTS:
        rows.append({"concept": c.concept, "parent": c.parent, "recipe": c.recipe, "knob": c.knob, "needs": c.needs,
                     "reason": c.reason, "note": c.note})
    return rows


VOCABULARY_SIZE = 58  # the vocabulary's concepts; Pervaporation is the one extra entry, a membrane stand-in the brief names
assert len(CONCEPTS) == VOCABULARY_SIZE + 1
