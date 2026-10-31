"""etoac_fischer / patent US6768021B2: reaction/distillation tower train.

The reaction/distillation tower 2 is reactive distillation, which DWSIM 10.2 does not have. Surrogate: an
equilibrium reactor at the stated base temperature (117 C) feeding a rigorous column at the stated top
pressure (2.1 atm), with the column bottoms (acid and catalyst) returned to the reactor. Then condenser 14,
water addition, decanter 16, pervap unit 34 (a specified split: the document gives no membrane data),
finishing column 38 (1-4 atm, 25-50 trays), condenser 46, decanter 50, organic recovery column 32.
Stated numbers are used as stated; everything else is marked ASSUMED. Compounds Ethanol, Acetic acid,
Ethyl acetate, Water; NRTL with a VLLE flash; the equilibrium constant K = 4 is the builder's placeholder.
Catalyst (methane sulfonic acid) has no compound and stays in the reactor.
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcpclient import *  # noqa

OUT = os.environ.get("GOLD_OUT", os.path.join(os.path.dirname(os.path.abspath(__file__)), "out"))
os.makedirs(OUT, exist_ok=True)
STAGE = int(sys.argv[1]) if len(sys.argv) > 1 else 4
# GOLD_OPEN_LOOPS=1: the organic reflux to the tower and the alcohol recycle stay open (their tear streams are
# reported as they leave); only the tower-internal recycle (bottoms -> reaction zone) is closed.
OPEN_LOOPS = os.environ.get("GOLD_OPEN_LOOPS", "0") == "1"

c = Client()
fs = c.call("dwsim_flowsheet_create", name="etoac_fischer_patent")["flowsheet_id"]
report = {"plant": "etoac_fischer/patent US6768021B2", "flowsheet_id": fs, "assumptions": [], "stages": {}, "checks": {}}


def assume(text):
    report["assumptions"].append(text)
    print("  ASSUMED:", text, file=sys.stderr)


def column(name, stages, feeds, dist, btm, qc, qr, top_P, dP, rr, btm_flow, cond="Total_Condenser", T_est=None):
    c.call("dwsim_column_create", flowsheet_id=fs, name=name, type="Distillation", stages=stages, condenser_type=cond,
           top_pressure_Pa=top_P, pressure_drop_Pa=dP)
    for s in (dist, btm):
        c.call("dwsim_stream_add_material", flowsheet_id=fs, name=s)
    for s in (qc, qr):
        c.call("dwsim_stream_add_energy", flowsheet_id=fs, name=s)
    c.call("dwsim_column_connect", flowsheet_id=fs, name=name, feeds=feeds, distillate=dist, bottoms=btm, condenser_duty=qc, reboiler_duty=qr)
    c.call("dwsim_column_set_specs", flowsheet_id=fs, name=name, condenser={"type": "Reflux Ratio", "value": rr},
           reboiler={"type": "Product_Molar_Flow_Rate", "value": btm_flow, "units": "mol/s"})
    if T_est:
        c.call("dwsim_column_set_estimates", flowsheet_id=fs, name=name,
               temperatures_K=[T_est[0] + (T_est[1] - T_est[0]) * i / (stages - 1) for i in range(stages)], use_temperatures=True)


c.call("dwsim_thermo_add_compounds", flowsheet_id=fs, names=["Ethanol", "Acetic acid", "Ethyl acetate", "Water"])
c.call("dwsim_thermo_set_property_package", flowsheet_id=fs, name="NRTL")
c.call("dwsim_thermo_set_flash", flowsheet_id=fs, equilibrium_type="VLLE")
report["bips_missing_before_estimate"] = c.call("dwsim_thermo_get_bips", flowsheet_id=fs).get("missing")
c.call("dwsim_thermo_estimate_bips", flowsheet_id=fs)
apply_lle_bips(c, fs)
report["assumptions"].append("NRTL ethyl acetate/water pair replaced by one regressed to the mutual solubilities at 25 C "
                             "(A12 349.75, A21 2176.38 cal/mol, alpha 0.2): the databank pair gives no liquid split in the decanters")
c.call("dwsim_reaction_add_equilibrium", flowsheet_id=fs, name="Esterification",
       stoichiometry={"Ethanol": -1, "Acetic acid": -1, "Ethyl acetate": 1, "Water": 1},
       base_compound="Acetic acid", phase="Liquid", basis="molar fraction")
c.call("dwsim_reaction_set_equilibrium_constant", flowsheet_id=fs, name="Esterification", mode="constant", keq=4.0, tmin_K=C(20), tmax_K=C(160))

P_TOP = 2.1 * ATM           # stated 2.1 atm overhead
P_BOT = P_TOP + bar(0.3)
assume("feeds 4 (acetic acid) and 6 (ethanol) equimolar, 1.0 mol/s each, 25 C, at the tower pressure (the patent: equimolar "
       "or slight excess of either)")
add_feed(c, fs, "FEED-4-ACID", C(25), P_BOT, 1.0, {"Acetic acid": 1.0})
add_feed(c, fs, "FEED-6-ETHANOL", C(25), P_BOT, 1.0, {"Ethanol": 1.0})
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-REACTION-ZONE")
connect(c, fs, "MIX-REACTION-ZONE", feed="FEED-4-ACID", feed_port=0)
connect(c, fs, "MIX-REACTION-ZONE", feed="FEED-6-ETHANOL", feed_port=1, product="RZ-FEED")
unit(c, fs, "EquilibriumReactor", "T1-REACTION-ZONE-8", feed="RZ-FEED", product="RZ-VAP", product_port=0, energy_in="Q-RZ",
     mode="OutletTemperature", props={"OutletTemperature": C(117), "DeltaP": 0.0})  # stated base temperature 117 C
connect(c, fs, "T1-REACTION-ZONE-8", product="RZ-LIQ", product_port=1)
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-RZ-OUT")
connect(c, fs, "MIX-RZ-OUT", feed="RZ-VAP", feed_port=0)
connect(c, fs, "MIX-RZ-OUT", feed="RZ-LIQ", feed_port=1, product="RZ-OUT")
N = 24
assume(f"tower 2 distillation zone: {N} stages (the stated 35-70 trays make the NRTL column too slow to converge headless), "
       f"feed on stage {N - 3}, top 2.1 atm (stated), 0.3 bar drop, reflux ratio 2 (the reflux is the decanter organic "
       "phase in the patent), bottoms 1.0 mol/s back to the reaction zone")
column("T1-DISTILLATION-ZONE", N, [{"stream": "RZ-OUT", "stage": N - 3}], "LINE-12-OVERHEAD", "T1-BOTTOMS", "QC-T1", "QR-T1",
       P_TOP, bar(0.3), 2.0, 1.0, T_est=(C(93), C(140)))
unit(c, fs, "Recycle", "RCY-T1", feed="T1-BOTTOMS", product="T1-BOTTOMS-R")
connect(c, fs, "MIX-REACTION-ZONE", feed="T1-BOTTOMS-R", feed_port=2)
c.call("dwsim_recycle_configure", flowsheet_id=fs, name="RCY-T1", max_iterations=50)

r1 = solve(c, fs, timeout_s=600)
report["stages"]["1_tower"] = {"ok": r1.get("ok"), "errors": r1.get("errors")}
print_streams(c, fs, ["RZ-FEED", "RZ-OUT", "LINE-12-OVERHEAD", "T1-BOTTOMS"], "stage 1: reaction/distillation tower surrogate")
col = c.try_call("dwsim_column_get", flowsheet_id=fs, name="T1-DISTILLATION-ZONE")
if col and col.get("calculated"):
    report["checks"]["T1_top_T_C (doc 93 at 2.1 atm)"] = round(col["profile"][0]["T_K"] - 273.15, 1)
    report["checks"]["T1_bottom_T_C (doc 117)"] = round(col["profile"][-1]["T_K"] - 273.15, 1)
    print("  T1 top/bottom T:", report["checks"]["T1_top_T_C (doc 93 at 2.1 atm)"], report["checks"]["T1_bottom_T_C (doc 117)"])
rx = c.call("dwsim_unitop_get_results", flowsheet_id=fs, name="T1-REACTION-ZONE-8")
report["checks"]["reaction_zone_acid_conversion_pct"] = rx["properties"].get("Acetic acid: Conversion", {}).get("value")
save(c, fs, os.path.join(OUT, "etoac_fischer_patent_stage1.dwxmz"))

if STAGE >= 2:
    unit(c, fs, "Cooler", "C1-CONDENSER-14", feed="LINE-12-OVERHEAD", product="LINE-18", energy_out="Q-C14",
         mode="OutletTemperature", props={"OutletTemperature": C(30)})
    assume("condenser 14 outlet and decanters at 30 C (stated 5-50 C, 'as cold as possible'); water added at port 20: 0.3 mol/s")
    add_feed(c, fs, "WATER-22", C(25), P_TOP, 0.3, {"Water": 1.0})
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="PORT-20")
    connect(c, fs, "PORT-20", feed="LINE-18", feed_port=0)
    connect(c, fs, "PORT-20", feed="WATER-22", feed_port=1, product="D16-FEED")
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="Vessel", name="D1-DECANTER-16")
    connect(c, fs, "D1-DECANTER-16", feed="D16-FEED", feed_port=0, product="D16-VAP", product_port=0)
    connect(c, fs, "D1-DECANTER-16", product="D16-ORGANIC", product_port=1)
    connect(c, fs, "D1-DECANTER-16", product="LINE-24-AQUEOUS", product_port=2)
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="Splitter", name="SPLITTER-28")
    connect(c, fs, "SPLITTER-28", feed="D16-ORGANIC", feed_port=0, product="LINE-26-REFLUX", product_port=0)
    connect(c, fs, "SPLITTER-28", product="LINE-36-TO-PERVAP", product_port=1)
    connect(c, fs, "SPLITTER-28", product="LINE-17-TO-FINISHING", product_port=2)
    for i, v in enumerate([0.5, 0.5, 0.0]):
        c.call("dwsim_object_set", flowsheet_id=fs, target="SPLITTER-28", path=f"Ratios[{i}]", value=v)
    assume("organic phase split: 50 % reflux to the tower, 50 % to the pervap unit, 0 % directly to the finishing column (not stated)")
    r2a = solve(c, fs, timeout_s=600)
    report["stages"]["2a_decanter_open"] = {"ok": r2a.get("ok"), "errors": r2a.get("errors")}
    print_streams(c, fs, ["LINE-18", "D16-FEED", "D16-ORGANIC", "LINE-24-AQUEOUS", "LINE-26-REFLUX"], "stage 2a: condenser and decanter 16")
    # the organic reflux returns to the top of the tower (line 27)
    unit(c, fs, "Pump", "P-REFLUX", feed="LINE-26-REFLUX", product="LINE-27", energy_in="W-REFLUX", mode="OutletPressure", props={"Pout": P_TOP + bar(0.2)})
    if OPEN_LOOPS:
        r2 = solve(c, fs, timeout_s=600)
        report["stages"]["2_organic_reflux_open"] = {"ok": r2.get("ok"), "errors": r2.get("errors"), "note": "LINE-27 (reflux to the tower top) left open"}
        print_streams(c, fs, ["LINE-27"], "stage 2: organic reflux (left open)")
    else:
        unit(c, fs, "Recycle", "RCY-REFLUX", feed="LINE-27", product="LINE-27-R", seed_from="LINE-26-REFLUX")
        c.call("dwsim_column_connect", flowsheet_id=fs, name="T1-DISTILLATION-ZONE", feeds=[{"stream": "LINE-27-R", "stage": 1}])
        c.call("dwsim_recycle_configure", flowsheet_id=fs, name="RCY-REFLUX", max_iterations=50)
        r2 = solve(c, fs, timeout_s=900)
        report["stages"]["2_organic_reflux_closed"] = {"ok": r2.get("ok"), "errors": r2.get("errors")}
        print_streams(c, fs, ["RZ-OUT", "LINE-12-OVERHEAD", "T1-BOTTOMS", "D16-ORGANIC", "LINE-24-AQUEOUS", "LINE-27-R"], "stage 2: organic reflux closed")
    col = c.try_call("dwsim_column_get", flowsheet_id=fs, name="T1-DISTILLATION-ZONE")
    if col and col.get("calculated"):
        report["checks"]["T1_top_T_C_with_reflux (doc 93)"] = round(col["profile"][0]["T_K"] - 273.15, 1)
        report["checks"]["T1_bottom_T_C_with_reflux (doc 117)"] = round(col["profile"][-1]["T_K"] - 273.15, 1)
    save(c, fs, os.path.join(OUT, "etoac_fischer_patent_stage2.dwxmz"))

if STAGE >= 3:
    # pervap unit 34: specified split, water to the permeate
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="ComponentSeparator", name="P1-PERVAP-34")
    connect(c, fs, "P1-PERVAP-34", feed="LINE-36-TO-PERVAP", feed_port=0, product="LINE-44-PERMEATE", product_port=0, energy_out="Q-PV")
    connect(c, fs, "P1-PERVAP-34", product="LINE-40-DRIED", product_port=1)
    compsep(c, fs, "P1-PERVAP-34", 0, {"Water": 95.0, "Ethanol": 30.0})
    assume("pervap unit 34: 95 % of the water and 30 % of the ethanol permeate; specified, not simulated; the azeotropic-agent "
           "return to the tower (line 42) is not drawn")
    NF = 20
    assume(f"finishing column 38: {NF} stages (stated 25-50), feed stage {NF // 2}, 1.5 atm (stated 1-4 atm), reflux ratio 2, "
           "bottoms = 98 % of the ester fed = finished product (line 60); the residue return to the reaction zone is not drawn")
    r3pre = solve(c, fs, timeout_s=600)
    d = stream_summary(c, fs, "LINE-40-DRIED")
    column("F1-FINISHING-38", NF, [{"stream": "LINE-40-DRIED", "stage": NF // 2}], "LINE-48-OVERHEAD", "LINE-60-PRODUCT", "QC-F1", "QR-F1",
           1.5 * ATM, bar(0.1), 2.0, 0.98 * d["mol_s"] * d["x"].get("Ethyl acetate", 0), T_est=(C(80), C(95)))
    unit(c, fs, "Cooler", "C2-CONDENSER-46", feed="LINE-48-OVERHEAD", product="LINE-49", energy_out="Q-C46",
         mode="OutletTemperature", props={"OutletTemperature": C(30)})
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="Vessel", name="D2-DECANTER-50")
    connect(c, fs, "D2-DECANTER-50", feed="LINE-49", feed_port=0, product="D50-VAP", product_port=0)
    connect(c, fs, "D2-DECANTER-50", product="LINE-54-ORGANIC", product_port=1)
    connect(c, fs, "D2-DECANTER-50", product="LINE-56-AQUEOUS", product_port=2)
    assume("decanter 50 organic phase (stream 54) is the finishing column's reflux: represented by the column's reflux ratio, not returned as a loop")
    r3 = solve(c, fs, timeout_s=900)
    report["stages"]["3_pervap_finishing"] = {"ok": r3.get("ok"), "errors": r3.get("errors")}
    print_streams(c, fs, ["LINE-36-TO-PERVAP", "LINE-44-PERMEATE", "LINE-40-DRIED", "LINE-48-OVERHEAD", "LINE-60-PRODUCT", "LINE-54-ORGANIC", "LINE-56-AQUEOUS"], "stage 3: pervap, finishing column, decanter 50")
    p = stream_summary(c, fs, "LINE-60-PRODUCT")
    report["checks"]["product_ester_wt_frac (doc < 0.5 % impurities)"] = p["w"].get("Ethyl acetate")
    report["checks"]["product_acid_ppm_mass (doc < 50)"] = round(1e6 * p["w"].get("Acetic acid", 0.0), 1)
    save(c, fs, os.path.join(OUT, "etoac_fischer_patent_stage3.dwxmz"))

if STAGE >= 4:
    NR = 15
    assume(f"organic recovery column 32: {NR} stages, 1 atm, reflux ratio 2, feeds: decanter 16 aqueous phase, permeate 44, "
           "decanter 50 aqueous stream; bottoms = 97 % of the water fed; overhead alcohol recycled to the reaction zone")
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-R32")
    connect(c, fs, "MIX-R32", feed="LINE-24-AQUEOUS", feed_port=0)
    connect(c, fs, "MIX-R32", feed="LINE-44-PERMEATE", feed_port=1)
    connect(c, fs, "MIX-R32", feed="LINE-56-AQUEOUS", feed_port=2, product="R32-FEED")
    unit(c, fs, "Valve", "V-R32", feed="R32-FEED", product="R32-FEED-LP", mode="OutletPressure", props={"OutletPressure": bar(1.1)})
    r4pre = solve(c, fs, timeout_s=600)
    f = stream_summary(c, fs, "R32-FEED-LP")
    column("R1-RECOVERY-32", NR, [{"stream": "R32-FEED-LP", "stage": NR // 2}], "LINE-ALCOHOL-RCY", "WATER-OUT", "QC-R32", "QR-R32",
           ATM, bar(0.1), 2.0, 0.97 * f["mol_s"] * f["x"].get("Water", 0), T_est=(C(72), C(100)))
    r4a = solve(c, fs, timeout_s=900)
    report["stages"]["4a_recovery_column_open"] = {"ok": r4a.get("ok"), "errors": r4a.get("errors")}
    print_streams(c, fs, ["R32-FEED-LP", "LINE-ALCOHOL-RCY", "WATER-OUT"], "stage 4a: recovery column (alcohol recycle open)")
    unit(c, fs, "Pump", "P-ALC", feed="LINE-ALCOHOL-RCY", product="ALC-HP", energy_in="W-ALC", mode="OutletPressure", props={"Pout": P_BOT})
    if OPEN_LOOPS:
        r4 = solve(c, fs, timeout_s=600)
        report["stages"]["4_alcohol_recycle_open"] = {"ok": r4.get("ok"), "errors": r4.get("errors"), "note": "ALC-HP (alcohol recycle to the reaction zone) left open"}
        print_streams(c, fs, ["FEED-4-ACID", "FEED-6-ETHANOL", "T1-BOTTOMS-R", "RZ-OUT", "LINE-12-OVERHEAD", "D16-ORGANIC", "LINE-27",
                              "LINE-24-AQUEOUS", "LINE-40-DRIED", "LINE-60-PRODUCT", "ALC-HP", "WATER-OUT"], "stage 4: all units (two loops open)")
    else:
        unit(c, fs, "Recycle", "RCY-ALCOHOL", feed="ALC-HP", product="ALC-R", seed_from="LINE-ALCOHOL-RCY")
        connect(c, fs, "MIX-REACTION-ZONE", feed="ALC-R", feed_port=3)
        c.call("dwsim_recycle_configure", flowsheet_id=fs, name="RCY-ALCOHOL", max_iterations=50)
        r4 = solve(c, fs, timeout_s=900)
        report["stages"]["4_full_plant"] = {"ok": r4.get("ok"), "errors": r4.get("errors")}
        print_streams(c, fs, ["FEED-4-ACID", "FEED-6-ETHANOL", "ALC-R", "T1-BOTTOMS-R", "RZ-OUT", "LINE-12-OVERHEAD", "D16-ORGANIC",
                              "LINE-24-AQUEOUS", "LINE-40-DRIED", "LINE-60-PRODUCT", "WATER-OUT"], "stage 4: full plant")
    p = stream_summary(c, fs, "LINE-60-PRODUCT")
    report["checks"]["product_mol_s_per_mol_acid_fed"] = p["mol_s"]
    save(c, fs, os.path.join(OUT, "etoac_fischer_patent.dwxmz"))

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
with open(os.path.join(OUT, "etoac_fischer_patent.json"), "w") as f:
    json.dump(report, f, indent=1)
print("\nreport:", os.path.join(OUT, "etoac_fischer_patent.json"))
