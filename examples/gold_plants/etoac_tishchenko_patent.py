"""etoac_tishchenko / patent CN1245794A: acetaldehyde condensation to ethyl acetate.

Units: CAT catalyst preparation (aluminium alkoxide, no compound: not simulated); R1 condensation reactor
(-30 to 20 C, about 100 min residence time); D1 catalyst decomposition by water addition; E1 evaporation
(volatiles flashed off the solid catalyst residue); C1 rectifying tower. Document values are used as stated,
the rest is marked ASSUMED. Compounds: Acetaldehyde, Ethyl acetate, Ethanol, Water. NRTL.
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcpclient import *  # noqa

OUT = os.environ.get("GOLD_OUT", os.path.join(os.path.dirname(os.path.abspath(__file__)), "out"))
os.makedirs(OUT, exist_ok=True)

c = Client()
fs = c.call("dwsim_flowsheet_create", name="etoac_tishchenko_patent")["flowsheet_id"]
report = {"plant": "etoac_tishchenko/patent CN1245794A", "flowsheet_id": fs, "assumptions": [], "stages": {}, "checks": {}}


def assume(text):
    report["assumptions"].append(text)
    print("  ASSUMED:", text, file=sys.stderr)


c.call("dwsim_thermo_add_compounds", flowsheet_id=fs, names=["Acetaldehyde", "Ethyl acetate", "Ethanol", "Water"])
c.call("dwsim_thermo_set_property_package", flowsheet_id=fs, name="NRTL")
report["bips_missing_before_estimate"] = c.call("dwsim_thermo_get_bips", flowsheet_id=fs).get("missing")
c.call("dwsim_thermo_estimate_bips", flowsheet_id=fs)
c.call("dwsim_reaction_add_conversion", flowsheet_id=fs, name="Tishchenko", reaction_set="TISH",
       stoichiometry={"Acetaldehyde": -2, "Ethyl acetate": 1}, base_compound="Acetaldehyde", conversion="98", phase="Liquid")
assume("conversion 98 % (builder placeholder; the patent gives yields per example, not a conversion)")

assume("acetaldehyde feed 1.0 mol/s at 5 C and 1.5 bar; catalyst solution 1/8 of the feed by volume is drawn as "
       "0.1 mol/s ethyl acetate carrier (the alkoxide has no compound)")
add_feed(c, fs, "ACETALDEHYDE", C(5), bar(1.5), 1.0, {"Acetaldehyde": 1.0})
add_feed(c, fs, "CATALYST", C(5), bar(1.5), 0.1, {"Ethyl acetate": 1.0})
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-R1")
connect(c, fs, "MIX-R1", feed="ACETALDEHYDE", feed_port=0)
connect(c, fs, "MIX-R1", feed="CATALYST", feed_port=1, product="R1-FEED")
unit(c, fs, "ConversionReactor", "R1-CONDENSATION", feed="R1-FEED", product="R1-VAP", product_port=0, energy_in="Q-R1",
     mode="Isothermic", props={"ReactionSetID": "TISH"})
connect(c, fs, "R1-CONDENSATION", product="R1-LIQ", product_port=1)
assume("reactor isothermal at 5 C (stated -30 to 20 C, preferably -10 to 15 C, example 0-10 C); the 100 min residence time "
       "only sizes the kettles and does not enter a conversion reactor")
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-R1OUT")
connect(c, fs, "MIX-R1OUT", feed="R1-VAP", feed_port=0)
connect(c, fs, "MIX-R1OUT", feed="R1-LIQ", feed_port=1, product="REACTION-MIXTURE")
assume("catalyst decomposition D1: 0.03 mol/s water (patent: 0.08-8 ml per 100 ml; the alkoxide hydrolysis is not modelled)")
add_feed(c, fs, "WATER-D1", C(20), bar(1.5), 0.03, {"Water": 1.0})
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="D1-DECOMPOSITION")
connect(c, fs, "D1-DECOMPOSITION", feed="REACTION-MIXTURE", feed_port=0)
connect(c, fs, "D1-DECOMPOSITION", feed="WATER-D1", feed_port=1, product="SOLUTION+SOLIDS")
# E1 evaporation: heat and flash; the Al(OH)3 residue is not a compound, so the liquid left is the heavy fraction
unit(c, fs, "Heater", "E1-HEAT", feed="SOLUTION+SOLIDS", product="E1-HOT", energy_in="Q-E1",
     mode="OutletVaporFraction", props={"OutletVaporFraction": 0.97, "DeltaP": bar(0.4)})
assume("evaporation E1: 97 % of the solution vaporised at 1.1 bar (nothing stated); the 3 % liquid stands for the catalyst residue")
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Vessel", name="E1-EVAPORATOR")
connect(c, fs, "E1-EVAPORATOR", feed="E1-HOT", feed_port=0, product="VAPOUR-TO-C1", product_port=0)
connect(c, fs, "E1-EVAPORATOR", product="CATALYST-RESIDUE", product_port=1)
r1 = solve(c, fs)
report["stages"]["1_reaction_evaporation"] = {"ok": r1.get("ok"), "errors": r1.get("errors")}
print_streams(c, fs, ["R1-FEED", "REACTION-MIXTURE", "SOLUTION+SOLIDS", "E1-HOT", "VAPOUR-TO-C1", "CATALYST-RESIDUE"], "stage 1")
rx = c.call("dwsim_unitop_get_results", flowsheet_id=fs, name="R1-CONDENSATION")
report["checks"]["R1_duty_kW"] = rx["properties"].get("Heat Duty", rx["properties"].get("Heat Added", {})).get("value")
report["checks"]["E1_T_C"] = stream_summary(c, fs, "E1-HOT")["T_C"]

# the evaporator vapour is condensed at its bubble point ahead of the tower (a rigorous column with the saturated
# vapour feed did not converge headless)
unit(c, fs, "Cooler", "C1-FEED-CONDENSER", feed="VAPOUR-TO-C1", product="C1-FEED", energy_out="Q-C1FC",
     mode="OutletVaporFraction", props={"OutletVaporFraction": 0.0})
assume("the evaporated gas phase is condensed to its bubble point before the rectifying tower (not in the patent; "
       "the rigorous column did not converge with the vapour feed)")
N = 15
assume(f"rectifying tower C1: {N} stages, total condenser, feed stage {N // 2}, 1 atm, reflux ratio 2; distillate = "
       "1.05 x the acetaldehyde fed (unreacted acetaldehyde overhead), ethyl acetate bottoms")
c.call("dwsim_column_create", flowsheet_id=fs, name="C1-RECTIFYING-TOWER", type="Distillation", stages=N,
       condenser_type="Total_Condenser", top_pressure_Pa=ATM, pressure_drop_Pa=bar(0.1), max_iterations=300)
for s_ in ("C1-DIST", "C1-BTM-ESTER"):
    c.call("dwsim_stream_add_material", flowsheet_id=fs, name=s_)
for s_ in ("QC-C1", "QR-C1"):
    c.call("dwsim_stream_add_energy", flowsheet_id=fs, name=s_)
solve(c, fs, timeout_s=300)
v = stream_summary(c, fs, "C1-FEED")
D = 1.05 * v["mol_s"] * v["x"].get("Acetaldehyde", 0)
c.call("dwsim_column_connect", flowsheet_id=fs, name="C1-RECTIFYING-TOWER", feeds=[{"stream": "C1-FEED", "stage": N // 2}],
       distillate="C1-DIST", bottoms="C1-BTM-ESTER", condenser_duty="QC-C1", reboiler_duty="QR-C1")
c.call("dwsim_column_set_specs", flowsheet_id=fs, name="C1-RECTIFYING-TOWER",
       condenser={"type": "Reflux Ratio", "value": 2.0},
       reboiler={"type": "Product_Molar_Flow_Rate", "value": v["mol_s"] - D, "units": "mol/s"})
c.call("dwsim_column_set_estimates", flowsheet_id=fs, name="C1-RECTIFYING-TOWER",
       temperatures_K=[C(25) + (C(78) - C(25)) * i / (N - 1) for i in range(N)], use_temperatures=True)
r2 = solve(c, fs, timeout_s=600)
report["stages"]["2_rectifying_tower"] = {"ok": r2.get("ok"), "errors": r2.get("errors")}
if not r2.get("ok"):
    # fall back to the shortcut (Fenske-Underwood-Gilliland) column with the same keys and split
    print("  rigorous tower did not converge; replacing it with a ShortcutColumn", file=sys.stderr)
    c.call("dwsim_object_delete", flowsheet_id=fs, target="C1-RECTIFYING-TOWER")
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="ShortcutColumn", name="C1-RECTIFYING-TOWER")
    c.call("dwsim_unitop_connect", flowsheet_id=fs, unitop="C1-RECTIFYING-TOWER", feed_stream="C1-FEED", product_stream="C1-DIST", product_port=0, energy_product="QC-C1")
    c.call("dwsim_unitop_connect", flowsheet_id=fs, unitop="C1-RECTIFYING-TOWER", product_stream="C1-BTM-ESTER", product_port=1, energy_feed="QR-C1")
    c.call("dwsim_column_shortcut_configure", flowsheet_id=fs, name="C1-RECTIFYING-TOWER", light_key="Acetaldehyde", heavy_key="Ethyl acetate",
           light_key_in_bottoms=0.005, heavy_key_in_distillate=0.05, reflux_ratio=2.0, condenser_pressure_Pa=ATM, reboiler_pressure_Pa=ATM + bar(0.1),
           condenser_type="TotalCond")
    report["assumptions"].append("rectifying tower C1 is a ShortcutColumn (FUG) because the rigorous column did not converge: "
                                 "light key acetaldehyde 0.5 mol% in the bottoms, heavy key ethyl acetate 5 mol% in the distillate, reflux ratio 2")
    r2 = solve(c, fs, timeout_s=600)
    report["stages"]["2b_shortcut_tower"] = {"ok": r2.get("ok"), "errors": r2.get("errors")}
print_streams(c, fs, ["C1-FEED", "C1-DIST", "C1-BTM-ESTER"], "stage 2: rectifying tower")
col = c.try_call("dwsim_column_get", flowsheet_id=fs, name="C1-RECTIFYING-TOWER")
if col and col.get("calculated"):
    report["checks"]["C1_top_T_C"] = round(col["profile"][0]["T_K"] - 273.15, 1)
    report["checks"]["C1_bottom_T_C"] = round(col["profile"][-1]["T_K"] - 273.15, 1)
    report["checks"]["C1_duties_kW"] = col.get("duties_kW")
b = stream_summary(c, fs, "C1-BTM-ESTER")
report["checks"]["bottoms_ester_wt_frac"] = b["w"].get("Ethyl acetate")
print("  checks:", json.dumps(report["checks"], indent=1))
save(c, fs, os.path.join(OUT, "etoac_tishchenko_patent.dwxmz"))
objs = c.call("dwsim_flowsheet_list_objects", flowsheet_id=fs, type_filter="streams")
report["streams"] = {}
for o in objs.get("objects", []):
    n = o.get("name") or o.get("tag")
    if (o.get("type") or "").startswith("Material"):
        try:
            report["streams"][n] = stream_summary(c, fs, n)
        except ToolError as e:
            report["streams"][n] = {"error": str(e)[:300]}
with open(os.path.join(OUT, "etoac_tishchenko_patent.json"), "w") as f:
    json.dump(report, f, indent=1)
print("\nreport:", os.path.join(OUT, "etoac_tishchenko_patent.json"))
