"""etoac_dehydrogenation / patent US6809217B1: ethanol dehydrogenation to ethyl acetate.

Four adiabatic dehydrogenation reactors with interstage reheating, knock-out at -10 C, hydrogen recycle
compressor, selective hydrogenation of acetaldehyde, pressure-swing columns 40 (1.5 bar, RR 5) and
52 (12 bar, RR 2.8), water removal package 46, ethanol recycle.

Document values are used as stated; everything else is marked ASSUMED. Compounds: Ethanol, Ethyl acetate,
Hydrogen, Acetaldehyde, Water (butanone and higher by-products are left out; they are not in the
builder's compound list except butanone, which no reaction here produces).

Reactor chemistry (the overall reaction the document gives plus the acetaldehyde intermediate it names):
  2 C2H5OH <-> CH3COOC2H5 + 2 H2   (equilibrium, K from Gibbs energies of formation)
  C2H5OH   <-> CH3CHO + H2         (equilibrium, K from Gibbs energies; gives the acetaldehyde the
                                    hydrogenation reactor 38 removes)
  CH3CHO + H2 -> C2H5OH            (conversion 100 % in reactor 38)
Property package: Peng-Robinson for the reactor section, NRTL for the column section is not possible in
one flowsheet, so NRTL (with a Peng-Robinson vapour phase would be the rigorous choice) is used for the
whole plant; the gas-phase reactors at 15 bar see ideal-gas fugacities under NRTL.
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcpclient import *  # noqa

OUT = os.environ.get("GOLD_OUT", os.path.join(os.path.dirname(os.path.abspath(__file__)), "out"))
os.makedirs(OUT, exist_ok=True)
STAGE = int(sys.argv[1]) if len(sys.argv) > 1 else 3
OPEN_LOOPS = os.environ.get("GOLD_OPEN_LOOPS", "0") == "1"  # leave the column-52 return and the ethanol recycle open

c = Client()
fs = c.call("dwsim_flowsheet_create", name="etoac_dehydrogenation_patent")["flowsheet_id"]
report = {"plant": "etoac_dehydrogenation/patent US6809217B1", "flowsheet_id": fs, "assumptions": [], "stages": {}, "checks": {}}


def assume(text):
    report["assumptions"].append(text)
    print("  ASSUMED:", text, file=sys.stderr)


c.call("dwsim_thermo_add_compounds", flowsheet_id=fs, names=["Ethanol", "Ethyl acetate", "Hydrogen", "Acetaldehyde", "Water"])
c.call("dwsim_thermo_set_property_package", flowsheet_id=fs, name="NRTL")
c.call("dwsim_thermo_set_flash", flowsheet_id=fs, equilibrium_type="VLE")
report["bips_missing_before_estimate"] = c.call("dwsim_thermo_get_bips", flowsheet_id=fs).get("missing")
c.call("dwsim_thermo_estimate_bips", flowsheet_id=fs)

c.call("dwsim_reaction_add_equilibrium", flowsheet_id=fs, name="Dehydrogenation", reaction_set="DEHYD",
       stoichiometry={"Ethanol": -2, "Ethyl acetate": 1, "Hydrogen": 2}, base_compound="Ethanol", phase="Vapor")
c.call("dwsim_reaction_add_equilibrium", flowsheet_id=fs, name="Acetaldehyde-formation", reaction_set="DEHYD",
       stoichiometry={"Ethanol": -1, "Acetaldehyde": 1, "Hydrogen": 1}, base_compound="Ethanol", phase="Vapor")
c.call("dwsim_reaction_add_conversion", flowsheet_id=fs, name="Acetaldehyde-hydrogenation", reaction_set="HYDRO",
       stoichiometry={"Acetaldehyde": -1, "Hydrogen": -1, "Ethanol": 1}, base_compound="Acetaldehyde", conversion="100", phase="Mixture")

# ---- stage 1: reactor section ------------------------------------------------------------------
P_IN = bar(16.2)   # stated feed condition from the holding tank
assume("fresh ethanol 1.0 mol/s at 16.2 bar / 30 C (stated boundary conditions); feed ethanol anhydrous")
add_feed(c, fs, "ETHANOL-FEED", C(30), P_IN, 1.0, {"Ethanol": 1.0})
# recycle ethanol (line 2) and recycle hydrogen (line 6) enter before heat exchanger 4 / superheater 8
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-E4")
connect(c, fs, "MIX-E4", feed="ETHANOL-FEED", feed_port=0, product="E4-FEED")
unit(c, fs, "Heater", "E4", feed="E4-FEED", product="E4-OUT", energy_in="Q-E4",
     mode="OutletTemperature", props={"OutletTemperature": C(166)})  # stated 166 C
assume("hydrogen recycle to the superheater starts as 0.3 mol/s (document: hydrogen-rich gas from knockout pot 26, "
       "compressed to 15.5 bar); the closed loop sets its real value")
add_feed(c, fs, "H2-RCY-GUESS", C(40), bar(15.5), 0.3, {"Hydrogen": 0.97, "Ethanol": 0.02, "Ethyl acetate": 0.01})
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-SH8")
connect(c, fs, "MIX-SH8", feed="E4-OUT", feed_port=0)
connect(c, fs, "MIX-SH8", feed="H2-RCY-GUESS", feed_port=1, product="SH8-FEED")
unit(c, fs, "Heater", "SH8", feed="SH8-FEED", product="LINE-9", energy_in="Q-SH8",
     mode="OutletTemperature", props={"OutletTemperature": C(235), "DeltaP": 0.0})  # stated 235 C
assume("superheater outlet pressure 14.8 bar is stated; here the pressure follows the feed (16.2 bar) minus "
       "0.35 bar per reactor so reactor 22 leaves at about 14.8 bar")

prev = "LINE-9"
for i, (rx, reheater) in enumerate([("R10", "H12"), ("R14", None), ("R18", "E20"), ("R22", None)]):
    unit(c, fs, "EquilibriumReactor", rx, feed=prev, product=rx + "-OUT", product_port=0, energy_in="Q-" + rx,
         mode="OutletTemperature", props={"ReactionSetID": "DEHYD", "DeltaP": bar(0.35), "OutletTemperature": C(212.5)})
    connect(c, fs, rx, product=rx + "-LIQ", product_port=1)
    prev = rx + "-OUT"
    if reheater:
        unit(c, fs, "Heater", reheater, feed=prev, product=reheater + "-OUT", energy_in="Q-" + reheater,
             mode="OutletTemperature", props={"OutletTemperature": C(235)})
        prev = reheater + "-OUT"
    elif rx == "R14":
        # the document names no exchanger between reactors 14 and 18 in the text (figure-only item 16);
        # the reactor 18 feed is reheated to 235 C the same way
        unit(c, fs, "Heater", "HX16-figure-only", feed=prev, product="HX16-OUT", energy_in="Q-HX16",
             mode="OutletTemperature", props={"OutletTemperature": C(235)})
        prev = "HX16-OUT"
assume("reheaters 12, 16 (figure only) and 20 bring the gas back to 235 C, the stated reactor 10 inlet temperature; each bed "
       "leaves at 212.5 C, the stated reactor 10 exit window 205-220 C (the beds are adiabatic in the patent; with DWSIM's "
       "NRTL enthalpies an adiabatic equilibrium bed warmed up instead, so the stated exit temperature is imposed and the "
       "duty the engine then reports is a measure of that inconsistency)")

unit(c, fs, "Cooler", "COOL-KO26", feed=prev, product="KO26-FEED", energy_out="Q-KO26",
     mode="OutletTemperature", props={"OutletTemperature": C(-10)})  # stated: uncondensed gases at -10 C
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Vessel", name="KO26")
connect(c, fs, "KO26", feed="KO26-FEED", feed_port=0, product="LINE-27-GAS", product_port=0)
connect(c, fs, "KO26", product="LINE-32-CONDENSATE", product_port=1)
# gas split: recycle to compressor 29 (line 28), purge (line 31), hydrogenation hydrogen to compressor 35 (line 30)
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Splitter", name="SPLIT-27")
connect(c, fs, "SPLIT-27", feed="LINE-27-GAS", feed_port=0, product="LINE-28", product_port=0)
connect(c, fs, "SPLIT-27", product="LINE-31-PURGE", product_port=1)
connect(c, fs, "SPLIT-27", product="LINE-30", product_port=2)
assume("gas split: 20 % recycled to compressor 29, 75 % purged (hydrogen is the co-product), 5 % to compressor 35 (not stated)")
for i, v in enumerate([0.20, 0.75, 0.05]):
    c.call("dwsim_object_set", flowsheet_id=fs, target="SPLIT-27", path=f"Ratios[{i}]", value=v)
unit(c, fs, "Compressor", "K29", feed="LINE-28", product="LINE-6-H2RCY", energy_in="W-K29",
     mode="OutletPressure", props={"POut": bar(15.5)})  # stated 15.5 bar
unit(c, fs, "Compressor", "K35", feed="LINE-30", product="LINE-36", energy_in="W-K35",
     mode="OutletPressure", props={"POut": bar(43.1)})  # stated 43.1 bar
unit(c, fs, "Heater", "E33", feed="LINE-32-CONDENSATE", product="LINE-34", energy_in="Q-E33",
     mode="OutletTemperature", props={"OutletTemperature": C(70)})  # stated 60-80 C
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-R38")
connect(c, fs, "MIX-R38", feed="LINE-34", feed_port=0)
connect(c, fs, "MIX-R38", feed="LINE-36", feed_port=1, product="R38-FEED")
unit(c, fs, "ConversionReactor", "R38", feed="R38-FEED", product="R38-VAP", product_port=0, energy_in="Q-R38",
     mode="Adiabatic", props={"ReactionSetID": "HYDRO"})
connect(c, fs, "R38", product="R38-LIQ", product_port=1)
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-39")
connect(c, fs, "MIX-39", feed="R38-VAP", feed_port=0)
connect(c, fs, "MIX-39", feed="R38-LIQ", feed_port=1, product="LINE-39")
assume("hydrogenation reactor 38 adiabatic at the mixed-feed pressure; acetaldehyde fully hydrogenated")

r1 = solve(c, fs)
report["stages"]["1_reactor_section_open"] = {"ok": r1.get("ok"), "errors": r1.get("errors")}
print_streams(c, fs, ["LINE-9", "R10-OUT", "R14-OUT", "R18-OUT", "R22-OUT", "KO26-FEED", "LINE-27-GAS", "LINE-32-CONDENSATE",
                      "LINE-6-H2RCY", "LINE-36", "LINE-39"], "stage 1: reactor section, hydrogen recycle open")
for rx in ("R10", "R14", "R18", "R22"):
    rr_ = c.call("dwsim_unitop_get_results", flowsheet_id=fs, name=rx)
    print("  ", rx, {k: v["value"][:7] + " " + v["units"] for k, v in rr_["properties"].items() if "Temperature" in k or "Ethanol: Conversion" in k})
report["checks"]["R10_outlet_T_C (doc 205-220)"] = stream_summary(c, fs, "R10-OUT")["T_C"]

# close the hydrogen recycle
unit(c, fs, "Recycle", "RCY-H2", feed="LINE-6-H2RCY", product="LINE-6-R")
connect(c, fs, "MIX-SH8", feed="LINE-6-R", feed_port=2)
set_feed(c, fs, "H2-RCY-GUESS", mol_s=1e-6)
c.call("dwsim_recycle_configure", flowsheet_id=fs, name="RCY-H2", max_iterations=100)
r1b = solve(c, fs)
report["stages"]["1b_hydrogen_recycle_closed"] = {"ok": r1b.get("ok"), "errors": r1b.get("errors")}
print_streams(c, fs, ["SH8-FEED", "R22-OUT", "LINE-27-GAS", "LINE-6-R", "LINE-31-PURGE", "LINE-32-CONDENSATE", "LINE-39"], "stage 1b: hydrogen recycle closed")
s22 = stream_summary(c, fs, "R22-OUT")
report["checks"]["R22_outlet_P_bar (doc 14.8)"] = s22["P_bar"]
report["checks"]["ethanol_conversion_overall_pct"] = None
save(c, fs, os.path.join(OUT, "etoac_dehydrogenation_patent_stage1.dwxmz"))

if STAGE >= 2:
    # ---- stage 2: column 40 at 1.5 bar, RR 5, overhead lights, side draw to column 52, ethanol-rich bottoms
    unit(c, fs, "Valve", "V-40", feed="LINE-39", product="LINE-39-LP", mode="OutletPressure",
         props={"OutletPressure": bar(1.6)})
    # the uncondensed hydrogen leaves as the purge of condenser 48 (line 49); drawn as a knock-out ahead of the
    # column so that the column itself has a total condenser (the rigorous column with hydrogen in a partial
    # condenser did not converge)
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="ComponentSeparator", name="C48-KNOCKOUT")
    connect(c, fs, "C48-KNOCKOUT", feed="LINE-39-LP", feed_port=0, product="LINE-49-PURGE", product_port=0, energy_out="Q-C48")
    connect(c, fs, "C48-KNOCKOUT", product="COL40-FEED", product_port=1)
    compsep(c, fs, "C48-KNOCKOUT", 0, {"Hydrogen": 100.0})
    assume("the dissolved hydrogen is removed completely ahead of column 40 (condenser 48 purge, line 49) as a specified split: "
           "a trace of hydrogen in the total condenser pushed the column's top temperature to 4 C")
    N40 = 20
    assume(f"column 40: {N40} stages, total condenser (the hydrogen leaves in the knock-out ahead of it as the line-49 purge), "
           f"feed stage {N40 // 2}, top 1.5 bar (stated), 0.1 bar drop; the ester/ethanol azeotrope is taken as the liquid "
           "distillate and sent to column 52 (the patent's side draw 51); the side draw itself is not drawn")
    c.call("dwsim_column_create", flowsheet_id=fs, name="D40", type="Distillation", stages=N40,
           condenser_type="Total_Condenser", top_pressure_Pa=bar(1.5), pressure_drop_Pa=bar(0.1), max_iterations=300)
    for s_ in ("LINE-51-SIDE", "LINE-41-BTM"):
        c.call("dwsim_stream_add_material", flowsheet_id=fs, name=s_)
    for s_ in ("QC-40", "QR-43"):
        c.call("dwsim_stream_add_energy", flowsheet_id=fs, name=s_)
    r2pre = solve(c, fs, timeout_s=600)
    f39 = stream_summary(c, fs, "COL40-FEED")
    ester = f39["mol_s"] * f39["x"].get("Ethyl acetate", 0)
    side = max(1.3 * ester, 0.05)  # the azeotrope with ethanol carries about 0.3 mol ethanol per mol ester at 1.5 bar
    c.call("dwsim_column_connect", flowsheet_id=fs, name="D40", feeds=[{"stream": "COL40-FEED", "stage": N40 // 2}],
           distillate="LINE-51-SIDE", bottoms="LINE-41-BTM", condenser_duty="QC-40", reboiler_duty="QR-43")
    c.call("dwsim_column_set_specs", flowsheet_id=fs, name="D40",
           condenser={"type": "Reflux Ratio", "value": 5.0},  # stated 5:1
           reboiler={"type": "Product_Molar_Flow_Rate", "value": max(f39["mol_s"] - side, 0.05), "units": "mol/s"})
    c.call("dwsim_column_set_estimates", flowsheet_id=fs, name="D40",
           temperatures_K=[C(78) + (C(90) - C(78)) * i / (N40 - 1) for i in range(N40)], use_temperatures=True)
    assume("column 40 bottoms draw = feed - 1.3 x the ester fed; reboiler 43 is the column's own reboiler")
    r2 = solve(c, fs, timeout_s=600)
    report["stages"]["2_column_40"] = {"ok": r2.get("ok"), "errors": r2.get("errors")}
    print_streams(c, fs, ["LINE-49-PURGE", "COL40-FEED", "LINE-51-SIDE", "LINE-41-BTM"], "stage 2: column 40")
    col = c.try_call("dwsim_column_get", flowsheet_id=fs, name="D40")
    if col and col.get("calculated"):
        report["checks"]["D40_top_T_C (doc 72)"] = round(col["profile"][0]["T_K"] - 273.15, 1)
        report["checks"]["D40_bottom_T_C (doc 73)"] = round(col["profile"][-1]["T_K"] - 273.15, 1)
        print("  D40 T top/bottom:", report["checks"]["D40_top_T_C (doc 72)"], report["checks"]["D40_bottom_T_C (doc 73)"], "C")
    save(c, fs, os.path.join(OUT, "etoac_dehydrogenation_patent_stage2.dwxmz"))

if STAGE >= 3:
    # ---- stage 3: column 52 at 12 bar, RR 2.8, pure ester bottoms; water removal 46; ethanol recycle
    unit(c, fs, "Pump", "P-52", feed="LINE-51-SIDE", product="COL52-FEED", energy_in="W-P52",
         mode="OutletPressure", props={"Pout": bar(12.3)})
    solve(c, fs, timeout_s=600)  # the pump outlet must be solved before the column 52 draw is computed from it
    N52 = 20
    assume(f"column 52: {N52} stages, total condenser, feed stage {N52 // 2}, top 12 bar (stated), 0.2 bar drop; "
           "bottoms draw from the azeotrope balance below (the stated product is 'substantially pure ethyl acetate')")
    c.call("dwsim_column_create", flowsheet_id=fs, name="D52", type="Distillation", stages=N52,
           condenser_type="Total_Condenser", top_pressure_Pa=bar(12.0), pressure_drop_Pa=bar(0.2), max_iterations=300)
    for s in ("LINE-53-PRODUCT", "D52-DIST"):
        c.call("dwsim_stream_add_material", flowsheet_id=fs, name=s)
    for s in ("QC-52", "QR-55"):
        c.call("dwsim_stream_add_energy", flowsheet_id=fs, name=s)
    f52 = stream_summary(c, fs, "COL52-FEED")
    # pressure swing: at 12 bar the ester/ethanol azeotrope holds about 28 mol% ester (the engine's own value from a
    # first pass), so the bottoms draw that leaves pure ester is (ester fed - 0.28 x feed) / (1 - 0.28)
    X_AZEO_12BAR = 0.28
    B52 = max((f52["mol_s"] * f52["x"].get("Ethyl acetate", 0) - X_AZEO_12BAR * f52["mol_s"]) / (1.0 - X_AZEO_12BAR), 0.02)
    assume(f"column 52 bottoms draw {B52:.4f} mol/s from the ester balance with a 28 mol% ester azeotrope overhead at 12 bar")
    c.call("dwsim_column_connect", flowsheet_id=fs, name="D52", feeds=[{"stream": "COL52-FEED", "stage": N52 // 2}],
           distillate="D52-DIST", bottoms="LINE-53-PRODUCT", condenser_duty="QC-52", reboiler_duty="QR-55")
    c.call("dwsim_column_set_specs", flowsheet_id=fs, name="D52",
           condenser={"type": "Reflux Ratio", "value": 2.8},  # stated 2.8:1
           reboiler={"type": "Product_Molar_Flow_Rate", "value": B52, "units": "mol/s"})
    c.call("dwsim_column_set_estimates", flowsheet_id=fs, name="D52",  # stated 160 C top, 204 C bottom
           temperatures_K=[C(160) + (C(204) - C(160)) * i / (N52 - 1) for i in range(N52)], use_temperatures=True)
    r3a = solve(c, fs, timeout_s=600)
    report["stages"]["3a_column_52_open"] = {"ok": r3a.get("ok"), "errors": r3a.get("errors")}
    print_streams(c, fs, ["COL52-FEED", "D52-DIST", "LINE-53-PRODUCT"], "stage 3a: column 52 (overhead not yet returned)")
    col = c.try_call("dwsim_column_get", flowsheet_id=fs, name="D52")
    if col and col.get("calculated"):
        report["checks"]["D52_top_T_C (doc 160)"] = round(col["profile"][0]["T_K"] - 273.15, 1)
        report["checks"]["D52_bottom_T_C (doc 204)"] = round(col["profile"][-1]["T_K"] - 273.15, 1)
        print("  D52 T top/bottom:", report["checks"]["D52_top_T_C (doc 160)"], report["checks"]["D52_bottom_T_C (doc 204)"], "C")

    # water removal package 46 on the column 40 bottoms: specified split (adsorption / distillation package)
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="ComponentSeparator", name="P46-WATER-REMOVAL")
    connect(c, fs, "P46-WATER-REMOVAL", feed="LINE-41-BTM", feed_port=0, product="P46-WATER", product_port=0, energy_out="Q-P46")
    connect(c, fs, "P46-WATER-REMOVAL", product="LINE-2-ETOH-RCY", product_port=1)
    compsep(c, fs, "P46-WATER-REMOVAL", 0, {"Water": 99.0})
    assume("water removal package 46: 99 % of the water removed, specified not simulated")
    unit(c, fs, "Pump", "P-2", feed="LINE-2-ETOH-RCY", product="LINE-2-HP", energy_in="W-P2",
         mode="OutletPressure", props={"Pout": P_IN})

    # column 52 overhead back to column 40, ethanol recycle back to exchanger 4
    unit(c, fs, "Valve", "V-52D", feed="D52-DIST", product="D52-DIST-LP", mode="OutletPressure", props={"OutletPressure": bar(1.6)})
    if OPEN_LOOPS:
        r3 = solve(c, fs, timeout_s=900)
        report["stages"]["3_loops_open"] = {"ok": r3.get("ok"), "errors": r3.get("errors"),
                                            "note": "D52-DIST-LP (return to column 40) and LINE-2-HP (ethanol recycle) left open"}
        print_streams(c, fs, ["ETHANOL-FEED", "LINE-6-R", "LINE-9", "R22-OUT", "LINE-31-PURGE", "LINE-39", "LINE-49-PURGE",
                              "LINE-51-SIDE", "LINE-41-BTM", "P46-WATER", "LINE-2-HP", "D52-DIST-LP", "LINE-53-PRODUCT"], "stage 3: all units (two loops open)")
    else:
        unit(c, fs, "Recycle", "RCY-52", feed="D52-DIST-LP", product="D52-DIST-R", seed_from="D52-DIST")
        c.call("dwsim_column_connect", flowsheet_id=fs, name="D40", feeds=[{"stream": "D52-DIST-R", "stage": N40 // 2 - 2}])
        unit(c, fs, "Recycle", "RCY-ETOH", feed="LINE-2-HP", product="LINE-2-R", seed_from="LINE-2-ETOH-RCY")
        connect(c, fs, "MIX-E4", feed="LINE-2-R", feed_port=1)
        for rc in ("RCY-52", "RCY-ETOH"):
            c.call("dwsim_recycle_configure", flowsheet_id=fs, name=rc, max_iterations=100)
        r3 = solve(c, fs, timeout_s=900)
        report["stages"]["3_full_plant"] = {"ok": r3.get("ok"), "errors": r3.get("errors")}
        print_streams(c, fs, ["ETHANOL-FEED", "LINE-2-R", "LINE-6-R", "LINE-9", "R22-OUT", "LINE-31-PURGE", "LINE-39", "LINE-49-PURGE",
                              "LINE-51-SIDE", "LINE-41-BTM", "P46-WATER", "D52-DIST-R", "LINE-53-PRODUCT"], "stage 3: full plant")
    prod = stream_summary(c, fs, "LINE-53-PRODUCT")
    report["checks"]["product_ester_wt_frac"] = prod["w"].get("Ethyl acetate")
    report["checks"]["product_mol_s_per_mol_ethanol_feed"] = prod["mol_s"]
    save(c, fs, os.path.join(OUT, "etoac_dehydrogenation_patent.dwxmz"))

print("  checks:", json.dumps(report["checks"], indent=1))
objs = c.call("dwsim_flowsheet_list_objects", flowsheet_id=fs, type_filter="streams")
report["streams"] = {}
for o in objs.get("objects", []):
    n = o.get("name") or o.get("tag")
    if (o.get("type") or "").startswith("Material"):
        try:
            report["streams"][n] = stream_summary(c, fs, n)
        except ToolError as e:
            report["streams"][n] = {"error": str(e)[:300]}
with open(os.path.join(OUT, "etoac_dehydrogenation_patent.json"), "w") as f:
    json.dump(report, f, indent=1)
print("\nreport:", os.path.join(OUT, "etoac_dehydrogenation_patent.json"))
