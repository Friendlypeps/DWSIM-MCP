"""etoac_fischer / llm_opus: the continuous Fischer esterification train (Opus 5.5 document).

Every number the document states is used as stated (midpoint of a window). Numbers the document
does not state are marked ASSUMED below; they are needed to close a simulation, not design values.

Units (document ids): U1 tanks -> feed streams; U2 pumps; U3 preheater; U4 equilibrium reactor (K = 4,
liquid phase, textbook placeholder); U5 esterification column (rigorous, 1 atm, RR 3); U6 condenser;
U7 decanter (VLLE flash); U8 drying column (rigorous, bottoms 99.5 wt% ester); U9 product cooler;
U10 tank -> product stream; U11 alcohol recovery column (rigorous, water bottoms at 1 atm).
Recycles: U5 bottoms -> U4 feed (RCY-ACID), U8 overhead -> U7 (RCY-AZEO), U11 overhead -> U3 (RCY-ETOH).
The decanter organic reflux to U5 is represented by U5's own reflux (RR 3), not as a second loop.
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcpclient import *  # noqa

OUT = os.environ.get("GOLD_OUT", os.path.join(os.path.dirname(os.path.abspath(__file__)), "out"))
os.makedirs(OUT, exist_ok=True)
STAGE = int(sys.argv[1]) if len(sys.argv) > 1 else 3
# GOLD_OPEN_LOOPS=1 leaves the organic reflux, the azeotrope return and the ethanol recycle open (their tear
# streams are reported as they leave); only the acid recycle U5 -> U4 is closed. Closing the other three
# did not converge headless (see RESULTS.md).
OPEN_LOOPS = os.environ.get("GOLD_OPEN_LOOPS", "0") == "1"

c = Client()
fs = c.call("dwsim_flowsheet_create", name="etoac_fischer_llm_opus")["flowsheet_id"]
report = {"plant": "etoac_fischer/llm_opus", "flowsheet_id": fs, "assumptions": [], "stages": {}}


def assume(text):
    report["assumptions"].append(text)
    print("  ASSUMED:", text, file=sys.stderr)


# ---- basis ------------------------------------------------------------------------------------
c.call("dwsim_thermo_add_compounds", flowsheet_id=fs, names=["Ethanol", "Acetic acid", "Ethyl acetate", "Water"])
c.call("dwsim_thermo_set_property_package", flowsheet_id=fs, name="NRTL")
c.call("dwsim_thermo_set_flash", flowsheet_id=fs, equilibrium_type="VLLE")
bips = c.call("dwsim_thermo_get_bips", flowsheet_id=fs)
report["bips_missing_before_estimate"] = bips.get("missing")
est = c.call("dwsim_thermo_estimate_bips", flowsheet_id=fs)
report["bips_estimated"] = est.get("estimated", est)
apply_lle_bips(c, fs)
report["assumptions"].append("NRTL ethyl acetate/water pair replaced by one regressed to the mutual solubilities at 25 C "
                             "(A12 349.75, A21 2176.38 cal/mol, alpha 0.2): the databank pair gives no liquid split in the decanter")

# reaction: CH3COOH + C2H5OH -> CH3COOC2H5 + H2O, equilibrium, liquid, K = 4 (document: "about 4")
c.call("dwsim_reaction_add_equilibrium", flowsheet_id=fs, name="Esterification",
       stoichiometry={"Ethanol": -1, "Acetic acid": -1, "Ethyl acetate": 1, "Water": 1},
       base_compound="Acetic acid", phase="Liquid", basis="molar fraction")
c.call("dwsim_reaction_set_equilibrium_constant", flowsheet_id=fs, name="Esterification", mode="constant",
       keq=4.0, tmin_K=C(20), tmax_K=C(150))

# ---- stage 1: feeds, pumps, preheater, reactor, esterification column, acid recycle -------------
ACID = 1.0  # mol/s basis
assume("basis 1.0 mol/s glacial acetic acid; ethanol 'slight molar excess' taken as 1.1 mol/s")
add_feed(c, fs, "ACID", C(25), ATM, ACID, {"Acetic acid": 1.0})
add_feed(c, fs, "ETOH", C(25), ATM, 1.1 * ACID, {"Ethanol": 1.0})
assume("feed temperature 25 C and storage at 1 atm (U1 storage tanks are the feed streams)")
assume("metering pumps (U2) discharge at 1.5 bar (no pressure stated)")
for s in ("ACID", "ETOH"):
    unit(c, fs, "Pump", "P-" + s, feed=s, product=s + "-P", energy_in="W-" + s, mode="OutletPressure",
         props={"Pout": bar(1.5)})

# mixer: two pumped feeds + the ethanol recycle (closed in stage 3)
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-FEED")
connect(c, fs, "MIX-FEED", feed="ACID-P", feed_port=0)
connect(c, fs, "MIX-FEED", feed="ETOH-P", feed_port=1, product="MIXED-FEED")

unit(c, fs, "Heater", "U3-PREHEAT", feed="MIXED-FEED", product="FEED-HOT", energy_in="Q-U3",
     mode="OutletTemperature", props={"OutletTemperature": C(75)})  # stated 70-80 C

unit(c, fs, "EquilibriumReactor", "U4-REACTOR", feed="FEED-HOT", product="RX-VAP", product_port=0,
     energy_in="Q-U4", mode="OutletTemperature", props={"OutletTemperature": C(85), "DeltaP": bar(0.1)})
connect(c, fs, "U4-REACTOR", product="RX-LIQ", product_port=1)
assume("reactor at 85 C (stated: boiling temperature, 80-110 C) and about 1 atm (0.1 bar drop below the "
       "column bottom pressure the recycle brings in); both reactor outlets enter the column that sits on top of it")
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-RX")
connect(c, fs, "MIX-RX", feed="RX-VAP", feed_port=0)
connect(c, fs, "MIX-RX", feed="RX-LIQ", feed_port=1, product="RX-OUT")

N5 = 12
assume(f"U5 esterification column: {N5} stages (20 stages, which would keep more acid out of the overhead, did not converge "
       f"inside the recycle loop headless), total condenser, feed on stage {N5 - 3}, 0.1 bar column pressure drop, "
       "bottoms (acid + catalyst reflux to the reactor) 1.0 mol/s")
c.call("dwsim_column_create", flowsheet_id=fs, name="U5-ESTCOL", type="Distillation", stages=N5,
       condenser_type="Total_Condenser", top_pressure_Pa=ATM, pressure_drop_Pa=bar(0.1))
for s in ("EST-DIST", "EST-BTM"):
    c.call("dwsim_stream_add_material", flowsheet_id=fs, name=s)
for s in ("QC-U5", "QR-U5"):
    c.call("dwsim_stream_add_energy", flowsheet_id=fs, name=s)
c.call("dwsim_column_connect", flowsheet_id=fs, name="U5-ESTCOL", feeds=[{"stream": "RX-OUT", "stage": N5 - 3}],
       distillate="EST-DIST", bottoms="EST-BTM", condenser_duty="QC-U5", reboiler_duty="QR-U5")
c.call("dwsim_column_set_specs", flowsheet_id=fs, name="U5-ESTCOL",
       condenser={"type": "Reflux Ratio", "value": 3.0},  # stated 2-4
       reboiler={"type": "Product_Molar_Flow_Rate", "value": 1.0, "units": "mol/s"})
# initial temperature estimates: stated head 70-72 C, acid-rich bottoms near the acetic acid boiling point
c.call("dwsim_column_set_estimates", flowsheet_id=fs, name="U5-ESTCOL",
       temperatures_K=[C(71) + (C(118) - C(71)) * i / (N5 - 1) for i in range(N5)], use_temperatures=True)

# acid recycle U5 -> U4 (through the feed mixer, so it passes the preheater; the document says the
# acid and catalyst are "refluxed back into the reactor")
unit(c, fs, "Recycle", "RCY-ACID", feed="EST-BTM", product="EST-BTM-R")
connect(c, fs, "MIX-FEED", feed="EST-BTM-R", feed_port=2)
c.call("dwsim_recycle_configure", flowsheet_id=fs, name="RCY-ACID", max_iterations=50)

r1 = solve(c, fs)
report["stages"]["1_reactor_column"] = {"ok": r1.get("ok"), "errors": r1.get("errors")}
print_streams(c, fs, ["ACID", "ETOH", "MIXED-FEED", "FEED-HOT", "RX-VAP", "RX-LIQ", "EST-DIST", "EST-BTM"], "stage 1")
col5 = c.call("dwsim_column_get", flowsheet_id=fs, name="U5-ESTCOL")
if col5.get("profile"):
    print("  U5 condenser T =", round(col5["profile"][0]["T_K"] - 273.15, 1), "C (document: head 70-72 C); duties kW:", col5.get("duties_kW"))
rx = c.call("dwsim_unitop_get_results", flowsheet_id=fs, name="U4-REACTOR")
print("  reactor:", {k: v["value"] for k, v in rx["properties"].items() if "Conversion" in k or "Extent" in k})
save(c, fs, os.path.join(OUT, "etoac_fischer_llm_opus_stage1.dwxmz"))

if STAGE >= 2:
    # ---- stage 2: condenser, water addition, decanter, drying column, product cooler ------------
    unit(c, fs, "Cooler", "U6-CONDENSER", feed="EST-DIST", product="CONDENSATE", energy_out="Q-U6",
         mode="OutletTemperature", props={"OutletTemperature": C(35)})  # stated 25-40 C
    assume("water added to the condensate: 0.5 mol/s at 25 C (amount not stated)")
    add_feed(c, fs, "WATER-ADD", C(25), ATM, 0.5, {"Water": 1.0})
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-DEC")
    connect(c, fs, "MIX-DEC", feed="CONDENSATE", feed_port=0)
    connect(c, fs, "MIX-DEC", feed="WATER-ADD", feed_port=1, product="DEC-FEED")

    c.call("dwsim_unitop_add", flowsheet_id=fs, type="Vessel", name="U7-DECANTER")
    connect(c, fs, "U7-DECANTER", feed="DEC-FEED", feed_port=0, product="DEC-VAP", product_port=0)
    connect(c, fs, "U7-DECANTER", product="ORGANIC", product_port=1)
    connect(c, fs, "U7-DECANTER", product="AQUEOUS", product_port=2)

    # the organic phase: part returns to the esterification column as reflux (document: U7 -> U5), the rest goes
    # to the drying column (U7 -> U8)
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="Splitter", name="SPLIT-ORG")
    connect(c, fs, "SPLIT-ORG", feed="ORGANIC", feed_port=0, product="ORG-TO-U5", product_port=0)
    connect(c, fs, "SPLIT-ORG", product="ORG-TO-U8", product_port=1)
    c.call("dwsim_object_set", flowsheet_id=fs, target="SPLIT-ORG", path="Ratios[0]", value=0.5)
    c.call("dwsim_object_set", flowsheet_id=fs, target="SPLIT-ORG", path="Ratios[1]", value=0.5)
    assume("half of the decanter organic phase returns to U5 as reflux, half goes to the drying column (split not stated)")

    # solve up to the decanter first: the drying column's bottoms draw is set from the organic phase
    r2pre = solve(c, fs)
    report["stages"]["2pre_decanter"] = {"ok": r2pre.get("ok"), "errors": r2pre.get("errors")}
    org = stream_summary(c, fs, "ORG-TO-U8")
    water_in_org = org["mol_s"] * org["x"].get("Water", 0.0)
    B8 = org["mol_s"] - water_in_org / 0.30  # fallback: all water leaves overhead as the ~30 mol% water azeotrope

    N8 = 12
    assume(f"U8 drying column: {N8} stages, total condenser, feed stage {N8 // 2}, reflux ratio 3, 0.05 bar pressure drop, "
           "bottoms specified at 1 wt% water (a drying column; the document's 99.5 wt% ester is then a result, the acid "
           f"that slips over U5 ends up here); fallback bottoms draw {B8:.4f} mol/s from a water balance")
    c.call("dwsim_column_create", flowsheet_id=fs, name="U8-DRYCOL", type="Distillation", stages=N8,
           condenser_type="Total_Condenser", top_pressure_Pa=ATM, pressure_drop_Pa=bar(0.05))
    for s in ("DRY-TOP", "DRY-BTM"):
        c.call("dwsim_stream_add_material", flowsheet_id=fs, name=s)
    for s in ("QC-U8", "QR-U8"):
        c.call("dwsim_stream_add_energy", flowsheet_id=fs, name=s)
    c.call("dwsim_column_connect", flowsheet_id=fs, name="U8-DRYCOL", feeds=[{"stream": "ORG-TO-U8", "stage": N8 // 2}],
           distillate="DRY-TOP", bottoms="DRY-BTM", condenser_duty="QC-U8", reboiler_duty="QR-U8")
    c.call("dwsim_column_set_specs", flowsheet_id=fs, name="U8-DRYCOL",
           condenser={"type": "Reflux Ratio", "value": 3.0},
           reboiler={"type": "Component_Fraction", "value": 0.01, "units": "Mass", "compound": "Water"})

    unit(c, fs, "Cooler", "U9-PRODCOOL", feed="DRY-BTM", product="PRODUCT", energy_out="Q-U9",
         mode="OutletTemperature", props={"OutletTemperature": C(25)})
    assume("product cooler to 25 C (document: cooled, sent to the product tank U10 = PRODUCT stream)")

    # first solve with the azeotrope recycle open, then close it
    r2a = solve(c, fs)
    if not r2a.get("ok"):
        print("  U8 water spec failed, falling back to the bottoms draw from the water balance", file=sys.stderr)
        c.call("dwsim_column_set_specs", flowsheet_id=fs, name="U8-DRYCOL",
               reboiler={"type": "Product_Molar_Flow_Rate", "value": max(B8, 0.05), "units": "mol/s"})
        r2a = solve(c, fs)
    report["stages"]["2a_open_azeo_recycle"] = {"ok": r2a.get("ok"), "errors": r2a.get("errors")}
    print_streams(c, fs, ["CONDENSATE", "DEC-FEED", "DEC-VAP", "ORGANIC", "AQUEOUS", "ORG-TO-U8", "DRY-TOP", "DRY-BTM", "PRODUCT"], "stage 2a (U8 overhead not yet recycled)")

    # the organic reflux to the top of the esterification column and the cooled overhead azeotropes back to the decanter
    unit(c, fs, "Pump", "P-REFLUX", feed="ORG-TO-U5", product="ORG-REFLUX", energy_in="W-REFLUX", mode="OutletPressure", props={"Pout": bar(1.2)})
    unit(c, fs, "Cooler", "U8-OVHD-COOLER", feed="DRY-TOP", product="DRY-TOP-COLD", energy_out="Q-U8C",
         mode="OutletTemperature", props={"OutletTemperature": C(35)})
    assume("the U8 overhead condensate is cooled to 35 C (decanter temperature) before it re-enters the decanter")
    if OPEN_LOOPS:
        r2 = solve(c, fs)
        report["stages"]["2_loops_open"] = {"ok": r2.get("ok"), "errors": r2.get("errors"),
                                            "note": "ORG-REFLUX (to U5 top) and DRY-TOP-COLD (to the decanter) left open"}
        print_streams(c, fs, ["ORG-REFLUX", "DRY-TOP-COLD", "DRY-BTM", "PRODUCT"], "stage 2 (reflux and azeotrope loops open)")
    else:
        unit(c, fs, "Recycle", "RCY-REFLUX", feed="ORG-REFLUX", product="ORG-REFLUX-R")
        c.call("dwsim_column_connect", flowsheet_id=fs, name="U5-ESTCOL", feeds=[{"stream": "ORG-REFLUX-R", "stage": 1}])
        c.call("dwsim_recycle_configure", flowsheet_id=fs, name="RCY-REFLUX", max_iterations=50)
        r2b = solve(c, fs)
        report["stages"]["2b_organic_reflux_closed"] = {"ok": r2b.get("ok"), "errors": r2b.get("errors")}
        print_streams(c, fs, ["ORG-REFLUX-R", "EST-DIST", "EST-BTM", "ORGANIC", "AQUEOUS", "DRY-TOP", "DRY-BTM"], "stage 2b (organic reflux to U5 closed)")
        unit(c, fs, "Recycle", "RCY-AZEO", feed="DRY-TOP-COLD", product="DRY-TOP-R")
        connect(c, fs, "MIX-DEC", feed="DRY-TOP-R", feed_port=2)
        c.call("dwsim_recycle_configure", flowsheet_id=fs, name="RCY-AZEO", max_iterations=50)
        r2 = solve(c, fs)
        report["stages"]["2_decanter_drying"] = {"ok": r2.get("ok"), "errors": r2.get("errors")}
        print_streams(c, fs, ["CONDENSATE", "DEC-FEED", "ORGANIC", "AQUEOUS", "DRY-TOP", "DRY-BTM", "PRODUCT"], "stage 2 (azeotrope recycle closed)")
    col8 = c.call("dwsim_column_get", flowsheet_id=fs, name="U8-DRYCOL")
    if col8.get("profile"):
        print("  U8 reboiler T =", round(col8["profile"][-1]["T_K"] - 273.15, 1), "C (document: bottom 77-78 C); duties kW:", col8.get("duties_kW"))
    save(c, fs, os.path.join(OUT, "etoac_fischer_llm_opus_stage2.dwxmz"))

if STAGE >= 3:
    # ---- stage 3: alcohol recovery column and ethanol recycle ---------------------------------
    N11 = 10
    aq = stream_summary(c, fs, "AQUEOUS")
    B11 = 0.95 * aq["mol_s"] * aq["x"].get("Water", 0.0)
    assume(f"U11 alcohol recovery column: {N11} stages, total condenser, feed stage {N11 // 2}, reflux ratio 2, "
           f"bottoms draw {B11:.4f} mol/s = 95 % of the water in the aqueous phase (document: water 'close to the purity "
           "of wastewater' at 100 C is then a result)")
    c.call("dwsim_column_create", flowsheet_id=fs, name="U11-ALCCOL", type="Distillation", stages=N11,
           condenser_type="Total_Condenser", top_pressure_Pa=ATM, pressure_drop_Pa=bar(0.05))
    for s in ("ALC-TOP", "WASTEWATER"):
        c.call("dwsim_stream_add_material", flowsheet_id=fs, name=s)
    for s in ("QC-U11", "QR-U11"):
        c.call("dwsim_stream_add_energy", flowsheet_id=fs, name=s)
    c.call("dwsim_column_connect", flowsheet_id=fs, name="U11-ALCCOL", feeds=[{"stream": "AQUEOUS", "stage": N11 // 2}],
           distillate="ALC-TOP", bottoms="WASTEWATER", condenser_duty="QC-U11", reboiler_duty="QR-U11")
    c.call("dwsim_column_set_specs", flowsheet_id=fs, name="U11-ALCCOL",
           condenser={"type": "Reflux Ratio", "value": 2.0},
           reboiler={"type": "Product_Molar_Flow_Rate", "value": B11, "units": "mol/s"})
    r3a = solve(c, fs)
    report["stages"]["3a_open_ethanol_recycle"] = {"ok": r3a.get("ok"), "errors": r3a.get("errors")}
    print_streams(c, fs, ["AQUEOUS", "ALC-TOP", "WASTEWATER"], "stage 3a (ethanol recycle open)")

    if not OPEN_LOOPS:
        unit(c, fs, "Recycle", "RCY-ETOH", feed="ALC-TOP", product="ALC-TOP-R")
        connect(c, fs, "MIX-FEED", feed="ALC-TOP-R", feed_port=3)
        c.call("dwsim_recycle_configure", flowsheet_id=fs, name="RCY-ETOH", max_iterations=50)
        r3 = solve(c, fs)
        report["stages"]["3_full_plant"] = {"ok": r3.get("ok"), "errors": r3.get("errors")}
        print_streams(c, fs, ["ACID", "ETOH", "ALC-TOP-R", "EST-BTM-R", "FEED-HOT", "RX-OUT", "EST-DIST", "ORGANIC", "AQUEOUS",
                              "DRY-TOP-R", "PRODUCT", "WASTEWATER"], "stage 3 (full plant, all recycles closed)")
    else:
        print_streams(c, fs, ["ACID", "ETOH", "EST-BTM-R", "FEED-HOT", "RX-OUT", "EST-DIST", "ORGANIC", "AQUEOUS", "ORG-REFLUX",
                              "DRY-TOP-COLD", "PRODUCT", "ALC-TOP", "WASTEWATER"], "stage 3 (all units; three loops open)")
    col11 = c.call("dwsim_column_get", flowsheet_id=fs, name="U11-ALCCOL")
    if col11.get("profile"):
        print("  U11 reboiler T =", round(col11["profile"][-1]["T_K"] - 273.15, 1), "C (document: 100 C)")
    save(c, fs, os.path.join(OUT, "etoac_fischer_llm_opus.dwxmz"))

# ---- dump ------------------------------------------------------------------------------------
objs = c.call("dwsim_flowsheet_list_objects", flowsheet_id=fs, type_filter="streams")
names = [o.get("name") or o.get("tag") for o in objs.get("objects", []) if (o.get("type") or "").startswith("Material")]
report["streams"] = {}
for n in names:
    try:
        report["streams"][n] = stream_summary(c, fs, n)
    except ToolError as e:
        report["streams"][n] = {"error": str(e)[:300]}
for col in ("U5-ESTCOL", "U8-DRYCOL", "U11-ALCCOL"):
    r = c.try_call("dwsim_column_get", flowsheet_id=fs, name=col)
    if r:
        report[col] = {"calculated": r.get("calculated"), "duties_kW": r.get("duties_kW"),
                       "T_profile_C": [round(p["T_K"] - 273.15, 1) for p in r.get("profile", [])]}
with open(os.path.join(OUT, "etoac_fischer_llm_opus.json"), "w") as f:
    json.dump(report, f, indent=1)
print("\nreport:", os.path.join(OUT, "etoac_fischer_llm_opus.json"))
