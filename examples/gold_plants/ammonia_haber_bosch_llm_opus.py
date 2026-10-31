"""ammonia_haber_bosch / llm_opus: natural-gas Haber-Bosch train (Opus 5.5 document).

Document values are used as stated (midpoints of windows); everything else is marked ASSUMED.
Compounds: Methane, Water, Hydrogen, Carbon monoxide, Carbon dioxide, Nitrogen, Oxygen, Argon, Ammonia
(all in the builder's list). Property package Peng-Robinson.

Units (document ids):
 U1 HDS + U2 ZnO bed: no sulphur compounds exist in the basis, so the two units are one heater to the
    stated 350-400 C with the recycled hydrogen mixed in; the sulphur chemistry is not simulated.
 U3 primary reformer: equilibrium reactor (steam reforming + water-gas shift, K from Gibbs energies),
    outlet temperature 800 C (750-850), 30 bar (25-40).
 U4 secondary reformer: air is added; combustion (conversion reactor, O2 fully consumed) followed by an
    adiabatic equilibrium reactor; the outlet temperature is a result, compared with the stated 950-1000 C.
 U5 waste heat boiler: cooler. U6 HT shift (350-450 C) and U7 LT shift (200-250 C): adiabatic equilibrium
    reactors fed at the bottom of their windows. U8 condensate separation: cooler + vessel.
 U9/U10 CO2 absorber-stripper: component separator (specified removal, not simulated).
 U11 methanator: conversion reactor, CO and CO2 fully to methane, inlet 300 C (250-350).
 U12 synthesis gas compressor: to 200 bar (100-250), recycle added in the last stage.
 U13 converter: equilibrium reactor N2 + 3 H2 <-> 2 NH3 at 450 C (400-500) and 200 bar (150-300).
 U14 effluent cooling train: cooler to -15 C (-10 to -25). U15 separator: vessel. Purge from the loop.
 U16 let-down vessel: valve + vessel to atmospheric storage (U17), which gives the -33 C product.
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcpclient import *  # noqa

OUT = os.environ.get("GOLD_OUT", os.path.join(os.path.dirname(os.path.abspath(__file__)), "out"))
os.makedirs(OUT, exist_ok=True)
STAGE = int(sys.argv[1]) if len(sys.argv) > 1 else 3

c = Client()
fs = c.call("dwsim_flowsheet_create", name="ammonia_haber_bosch_llm_opus")["flowsheet_id"]
report = {"plant": "ammonia_haber_bosch/llm_opus", "flowsheet_id": fs, "assumptions": [], "stages": {}}


def assume(text):
    report["assumptions"].append(text)
    print("  ASSUMED:", text, file=sys.stderr)


COMP = ["Methane", "Water", "Hydrogen", "Carbon monoxide", "Carbon dioxide", "Nitrogen", "Oxygen", "Argon", "Ammonia"]
c.call("dwsim_thermo_add_compounds", flowsheet_id=fs, names=COMP)
c.call("dwsim_thermo_set_property_package", flowsheet_id=fs, name="PR")

# reactions, one set per reactor so that each reactor runs only its own chemistry
c.call("dwsim_reaction_add_equilibrium", flowsheet_id=fs, name="SMR", reaction_set="REFORM",
       stoichiometry={"Methane": -1, "Water": -1, "Carbon monoxide": 1, "Hydrogen": 3}, base_compound="Methane", phase="Vapor")
c.call("dwsim_reaction_add_equilibrium", flowsheet_id=fs, name="WGS-reform", reaction_set="REFORM",
       stoichiometry={"Carbon monoxide": -1, "Water": -1, "Carbon dioxide": 1, "Hydrogen": 1}, base_compound="Carbon monoxide", phase="Vapor")
c.call("dwsim_reaction_add_equilibrium", flowsheet_id=fs, name="WGS", reaction_set="SHIFT",
       stoichiometry={"Carbon monoxide": -1, "Water": -1, "Carbon dioxide": 1, "Hydrogen": 1}, base_compound="Carbon monoxide", phase="Vapor")
c.call("dwsim_reaction_add_conversion", flowsheet_id=fs, name="H2-combustion", reaction_set="BURN",
       stoichiometry={"Hydrogen": -2, "Oxygen": -1, "Water": 2}, base_compound="Oxygen", conversion="100", phase="Vapor")
c.call("dwsim_reaction_add_conversion", flowsheet_id=fs, name="CO-methanation", reaction_set="METH",
       stoichiometry={"Carbon monoxide": -1, "Hydrogen": -3, "Methane": 1, "Water": 1}, base_compound="Carbon monoxide", conversion="100", phase="Vapor")
c.call("dwsim_reaction_add_conversion", flowsheet_id=fs, name="CO2-methanation", reaction_set="METH",
       stoichiometry={"Carbon dioxide": -1, "Hydrogen": -4, "Methane": 1, "Water": 2}, base_compound="Carbon dioxide", conversion="100", phase="Vapor")
c.call("dwsim_reaction_add_equilibrium", flowsheet_id=fs, name="NH3-synthesis", reaction_set="SYN",
       stoichiometry={"Nitrogen": -1, "Hydrogen": -3, "Ammonia": 2}, base_compound="Nitrogen", phase="Vapor")

P_REF = bar(30)
assume("natural gas = methane, 1.0 mol/s at 30 bar (reformer window 25-40 bar), 25 C; steam-to-carbon ratio 3.0 (2.5-4)")
add_feed(c, fs, "NATGAS", C(25), P_REF, 1.0, {"Methane": 1.0})
add_feed(c, fs, "H2-RECYCLE", C(25), P_REF, 0.02, {"Hydrogen": 1.0})
assume("recycled hydrogen to the desulphurisation 0.02 mol/s (2 % of the gas, not stated)")
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-HDS")
connect(c, fs, "MIX-HDS", feed="NATGAS", feed_port=0)
connect(c, fs, "MIX-HDS", feed="H2-RECYCLE", feed_port=1, product="GAS+H2")
unit(c, fs, "Heater", "U1U2-DESULF", feed="GAS+H2", product="DESULF-GAS", energy_in="Q-U1",
     mode="OutletTemperature", props={"OutletTemperature": C(375)})
add_feed(c, fs, "STEAM", C(375), P_REF, 3.0, {"Water": 1.0})
assume("process steam enters at 375 C (the gas temperature; not stated)")
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-STEAM")
connect(c, fs, "MIX-STEAM", feed="DESULF-GAS", feed_port=0)
connect(c, fs, "MIX-STEAM", feed="STEAM", feed_port=1, product="REFORMER-FEED")

unit(c, fs, "EquilibriumReactor", "U3-PRIMARY", feed="REFORMER-FEED", product="U3-OUT", product_port=0,
     energy_in="Q-U3", mode="OutletTemperature", props={"OutletTemperature": C(800), "ReactionSetID": "REFORM"})
connect(c, fs, "U3-PRIMARY", product="U3-LIQ", product_port=1)

# secondary reformer: air + combustion + adiabatic equilibrium
AIR0 = 1.35
assume("process air 1.35 mol/s (set to give H2:N2 near 3 at the converter; refined below), 25 C")
add_feed(c, fs, "AIR", C(25), P_REF, AIR0, {"Oxygen": 0.21, "Nitrogen": 0.78, "Argon": 0.01})
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-AIR")
connect(c, fs, "MIX-AIR", feed="U3-OUT", feed_port=0)
connect(c, fs, "MIX-AIR", feed="AIR", feed_port=1, product="U4-FEED")
unit(c, fs, "ConversionReactor", "U4-BURN", feed="U4-FEED", product="U4-BURNT", product_port=0, energy_in="Q-U4a",
     mode="Adiabatic", props={"ReactionSetID": "BURN"})
connect(c, fs, "U4-BURN", product="U4-BURN-LIQ", product_port=1)
unit(c, fs, "EquilibriumReactor", "U4-SECONDARY", feed="U4-BURNT", product="U4-OUT", product_port=0, energy_in="Q-U4b",
     mode="Adiabatic", props={"ReactionSetID": "REFORM"})
connect(c, fs, "U4-SECONDARY", product="U4-LIQ", product_port=1)

unit(c, fs, "Cooler", "U5-WHB", feed="U4-OUT", product="U5-OUT", energy_out="Q-U5",
     mode="OutletTemperature", props={"OutletTemperature": C(350)})
assume("waste heat boiler outlet 350 C = HT shift inlet (window 350-450 C); shift converters adiabatic")
unit(c, fs, "EquilibriumReactor", "U6-HTS", feed="U5-OUT", product="U6-OUT", product_port=0, energy_in="Q-U6",
     mode="Adiabatic", props={"ReactionSetID": "SHIFT"})
connect(c, fs, "U6-HTS", product="U6-LIQ", product_port=1)
unit(c, fs, "Cooler", "COOL-LTS", feed="U6-OUT", product="LTS-FEED", energy_out="Q-LTS",
     mode="OutletTemperature", props={"OutletTemperature": C(200)})
unit(c, fs, "EquilibriumReactor", "U7-LTS", feed="LTS-FEED", product="U7-OUT", product_port=0, energy_in="Q-U7",
     mode="Adiabatic", props={"ReactionSetID": "SHIFT"})
connect(c, fs, "U7-LTS", product="U7-LIQ", product_port=1)

unit(c, fs, "Cooler", "COOL-KO", feed="U7-OUT", product="KO-FEED", energy_out="Q-KO",
     mode="OutletTemperature", props={"OutletTemperature": C(40)})
assume("shifted gas cooled to 40 C before the condensate separation (not stated)")
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Vessel", name="U8-CONDSEP")
connect(c, fs, "U8-CONDSEP", feed="KO-FEED", feed_port=0, product="U8-GAS", product_port=0)
connect(c, fs, "U8-CONDSEP", product="CONDENSATE", product_port=1)

# CO2 removal as a specified split: 99.9 % of the CO2 and nothing else to the stripper off-gas
c.call("dwsim_unitop_add", flowsheet_id=fs, type="ComponentSeparator", name="U9U10-CO2REMOVAL")
connect(c, fs, "U9U10-CO2REMOVAL", feed="U8-GAS", feed_port=0, product="CO2-PRODUCT", product_port=0, energy_out="Q-CO2")
connect(c, fs, "U9U10-CO2REMOVAL", product="TREATED-GAS", product_port=1)
compsep(c, fs, "U9U10-CO2REMOVAL", 0, {"Carbon dioxide": 99.9})
assume("CO2 removal: 99.9 % of the CO2 to the stripper off-gas, specified not simulated (absorber-stripper is a column pair)")

unit(c, fs, "Heater", "HEAT-METH", feed="TREATED-GAS", product="METH-FEED", energy_in="Q-METHH",
     mode="OutletTemperature", props={"OutletTemperature": C(300)})
unit(c, fs, "ConversionReactor", "U11-METHANATOR", feed="METH-FEED", product="U11-OUT", product_port=0, energy_in="Q-U11",
     mode="Adiabatic", props={"ReactionSetID": "METH"})
connect(c, fs, "U11-METHANATOR", product="U11-LIQ", product_port=1)
unit(c, fs, "Cooler", "COOL-SYNGAS", feed="U11-OUT", product="SYNGAS-WET", energy_out="Q-SYNC",
     mode="OutletTemperature", props={"OutletTemperature": C(40)})
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Vessel", name="U12-KO")
connect(c, fs, "U12-KO", feed="SYNGAS-WET", feed_port=0, product="SYNGAS", product_port=0)
connect(c, fs, "U12-KO", product="U12-WATER", product_port=1)
assume("compressor intercooling and water knock-out represented by one cooler to 40 C and a vessel before compression")

r1 = solve(c, fs)
report["stages"]["1_front_end"] = {"ok": r1.get("ok"), "errors": r1.get("errors")}


def ratio_h2_n2(name="SYNGAS"):
    s = stream_summary(c, fs, name)
    return s["x"].get("Hydrogen", 0) / max(s["x"].get("Nitrogen", 1e-9), 1e-9)


# refine the air flow so that H2:N2 at the synthesis gas is 3 (the document: "the air flow is set to give
# a hydrogen-to-nitrogen ratio of about 3")
for it in range(4):
    rr = ratio_h2_n2()
    air = stream_summary(c, fs, "AIR")["mol_s"]
    print(f"  air {air:.4f} mol/s -> H2:N2 = {rr:.3f}", file=sys.stderr)
    if abs(rr - 3.0) < 0.01:
        break
    set_feed(c, fs, "AIR", mol_s=air * rr / 3.0)
    r1 = solve(c, fs)
report["air_mol_s"] = stream_summary(c, fs, "AIR")["mol_s"]
report["H2_N2_ratio"] = ratio_h2_n2()
print_streams(c, fs, ["REFORMER-FEED", "U3-OUT", "U4-FEED", "U4-OUT", "U6-OUT", "U7-OUT", "U8-GAS", "CONDENSATE",
                      "CO2-PRODUCT", "TREATED-GAS", "U11-OUT", "SYNGAS"], "front end")
for u in ("U3-PRIMARY", "U4-SECONDARY", "U6-HTS", "U7-LTS", "U11-METHANATOR"):
    rr_ = c.call("dwsim_unitop_get_results", flowsheet_id=fs, name=u)
    keep = {k: v["value"] + " " + v["units"] for k, v in rr_["properties"].items() if "Temperature" in k or "Conversion" in k}
    print("  ", u, keep)


def dry_frac(name, comp):
    s = stream_summary(c, fs, name)
    x = s["x"]
    dry = sum(v for k, v in x.items() if k != "Water")
    return x.get(comp, 0) / dry if dry else float("nan")


report["checks"] = {
    "U3_methane_dry_pct (doc ~10)": round(100 * dry_frac("U3-OUT", "Methane"), 2),
    "U4_methane_dry_pct (doc 0.2-0.5)": round(100 * dry_frac("U4-OUT", "Methane"), 3),
    "U4_outlet_T_C (doc 950-1000)": stream_summary(c, fs, "U4-OUT")["T_C"],
    "U6_CO_dry_pct (doc 2-3)": round(100 * dry_frac("U6-OUT", "Carbon monoxide"), 2),
    "U6_outlet_T_C (doc 350-450)": stream_summary(c, fs, "U6-OUT")["T_C"],
    "U7_CO_dry_pct (doc 0.1-0.3)": round(100 * dry_frac("U7-OUT", "Carbon monoxide"), 3),
    "U7_outlet_T_C (doc 200-250)": stream_summary(c, fs, "U7-OUT")["T_C"],
    "U11_outlet_T_C (doc 250-350)": stream_summary(c, fs, "U11-OUT")["T_C"],
}
print("  checks:", json.dumps(report["checks"], indent=1))
save(c, fs, os.path.join(OUT, "ammonia_haber_bosch_llm_opus_stage1.dwxmz"))

if STAGE >= 2:
    # ---- synthesis loop -----------------------------------------------------------------------
    P_SYN = bar(200)
    assume("synthesis pressure 200 bar: compressor discharge (100-250) and converter (150-300); loop pressure drop 5 bar")
    unit(c, fs, "Compressor", "U12-COMP", feed="SYNGAS", product="SYNGAS-HP", energy_in="W-U12",
         mode="OutletPressure", props={"POut": P_SYN - bar(5)})
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-LOOP")
    connect(c, fs, "MIX-LOOP", feed="SYNGAS-HP", feed_port=0, product="LOOP-MIX")
    unit(c, fs, "Compressor", "U12-RECYCLE-STAGE", feed="LOOP-MIX", product="LOOP-HP", energy_in="W-U12R",
         mode="OutletPressure", props={"POut": P_SYN})
    unit(c, fs, "Heater", "U14-FEEDPREHEAT", feed="LOOP-HP", product="CONVERTER-FEED", energy_in="Q-PREHEAT",
         mode="OutletTemperature", props={"OutletTemperature": C(400)})
    assume("converter feed preheated to 400 C by the effluent (document: effluent 'preheats the converter feed'); modelled as a heater")
    unit(c, fs, "EquilibriumReactor", "U13-CONVERTER", feed="CONVERTER-FEED", product="U13-OUT", product_port=0, energy_in="Q-U13",
         mode="OutletTemperature", props={"OutletTemperature": C(450), "ReactionSetID": "SYN", "DeltaP": bar(5)})
    connect(c, fs, "U13-CONVERTER", product="U13-LIQ", product_port=1)
    unit(c, fs, "Cooler", "U14-COOLING", feed="U13-OUT", product="U14-OUT", energy_out="Q-U14",
         mode="OutletTemperature", props={"OutletTemperature": C(-15)})
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="Vessel", name="U15-SEPARATOR")
    connect(c, fs, "U15-SEPARATOR", feed="U14-OUT", feed_port=0, product="LOOP-GAS", product_port=0)
    connect(c, fs, "U15-SEPARATOR", product="NH3-LIQUID", product_port=1)
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="Splitter", name="PURGE-SPLIT")
    connect(c, fs, "PURGE-SPLIT", feed="LOOP-GAS", feed_port=0, product="RECYCLE-GAS", product_port=0)
    connect(c, fs, "PURGE-SPLIT", product="PURGE", product_port=1)
    c.call("dwsim_object_set", flowsheet_id=fs, target="PURGE-SPLIT", path="Ratios[0]", value=0.97)
    c.call("dwsim_object_set", flowsheet_id=fs, target="PURGE-SPLIT", path="Ratios[1]", value=0.03)
    assume("purge 3 % of the separator gas (not stated)")

    # let-down to atmospheric storage
    unit(c, fs, "Valve", "U16-LETDOWN-VALVE", feed="NH3-LIQUID", product="NH3-LP", mode="OutletPressure",
         props={"OutletPressure": bar(1.1)})
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="Vessel", name="U16-LETDOWN")
    connect(c, fs, "U16-LETDOWN", feed="NH3-LP", feed_port=0, product="LETDOWN-GAS", product_port=0)
    connect(c, fs, "U16-LETDOWN", product="NH3-PRODUCT", product_port=1)
    assume("let-down vessel at 1.1 bar, so the product is at the atmospheric storage condition (U17)")

    r2a = solve(c, fs)
    report["stages"]["2a_loop_open"] = {"ok": r2a.get("ok"), "errors": r2a.get("errors")}
    print_streams(c, fs, ["SYNGAS-HP", "CONVERTER-FEED", "U13-OUT", "U14-OUT", "LOOP-GAS", "NH3-LIQUID", "PURGE", "NH3-PRODUCT", "LETDOWN-GAS"], "loop open (once-through)")

    unit(c, fs, "Recycle", "RCY-LOOP", feed="RECYCLE-GAS", product="RECYCLE-GAS-R")
    connect(c, fs, "MIX-LOOP", feed="RECYCLE-GAS-R", feed_port=1)
    c.call("dwsim_recycle_configure", flowsheet_id=fs, name="RCY-LOOP", max_iterations=100)
    r2 = solve(c, fs, timeout_s=600)
    report["stages"]["2_loop_closed"] = {"ok": r2.get("ok"), "errors": r2.get("errors")}
    print_streams(c, fs, ["SYNGAS", "RECYCLE-GAS-R", "CONVERTER-FEED", "U13-OUT", "U14-OUT", "LOOP-GAS", "NH3-LIQUID", "PURGE",
                          "NH3-PRODUCT", "LETDOWN-GAS"], "loop closed")
    rx = c.call("dwsim_unitop_get_results", flowsheet_id=fs, name="U13-CONVERTER")
    conv = {k: v["value"] for k, v in rx["properties"].items() if "Conversion" in k}
    print("  converter:", conv)
    s_out = stream_summary(c, fs, "U13-OUT")
    report["checks"].update({
        "converter_outlet_NH3_pct (doc 15-20)": round(100 * s_out["x"].get("Ammonia", 0), 2),
        "N2_conversion_per_pass_pct (doc 15-25)": conv.get("Nitrogen: Conversion"),
        "NH3_product_T_C (doc -33)": stream_summary(c, fs, "NH3-PRODUCT")["T_C"],
        "NH3_product_mol_s": stream_summary(c, fs, "NH3-PRODUCT")["mol_s"],
        "NH3_per_CH4": stream_summary(c, fs, "NH3-PRODUCT")["mol_s"] / 1.0,
    })
    print("  checks:", json.dumps(report["checks"], indent=1))
    save(c, fs, os.path.join(OUT, "ammonia_haber_bosch_llm_opus.dwxmz"))

objs = c.call("dwsim_flowsheet_list_objects", flowsheet_id=fs, type_filter="streams")
report["streams"] = {}
for o in objs.get("objects", []):
    n = o.get("name") or o.get("tag")
    if (o.get("type") or "").startswith("Material"):
        try:
            report["streams"][n] = stream_summary(c, fs, n)
        except ToolError as e:
            report["streams"][n] = {"error": str(e)[:300]}
with open(os.path.join(OUT, "ammonia_haber_bosch_llm_opus.json"), "w") as f:
    json.dump(report, f, indent=1)
print("\nreport:", os.path.join(OUT, "ammonia_haber_bosch_llm_opus.json"))
