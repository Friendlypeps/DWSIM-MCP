"""etoac_dehydrogenation / llm_opus: ethanol dehydrogenation to ethyl acetate (Opus 5.5 document).

Units (document ids): T1 feed tank -> feed stream; E1 feed/effluent exchanger (a real two-sided HeatExchanger here,
which the builder refused for lack of a direction: the feed is the cold side, the reactor effluent the hot side);
V1 steam vaporiser; R1 dehydrogenation reactor (equilibrium, K from Gibbs energies, 200-260 C -> 230 C);
E2 water-cooled condenser; S1 gas-liquid separator; K1 hydrogen compressor; R2 selective hydrogenation
(acetaldehyde -> ethanol, conversion 100 %); D1 first column (lights overhead); D2/D3 pressure-swing pair
(not simulated: the document gives neither pressures nor which column the feed and product streams attach to;
a specified ethanol/ester split stands in for the pair so the recycle can be closed); D4 product column
(heavies purge: no heavy compound exists in the basis, so it is a pass-through); E3 product cooler; T2 storage.

The document states no pressure anywhere; the reactor section is run at 1 atm (ASSUMED). The feed pump is
only a verb in the text and is not drawn. Compounds: Ethanol, Ethyl acetate, Hydrogen, Acetaldehyde,
Diethyl ether (light end, formed by a 0.5 % side conversion of ethanol, ASSUMED), Water. NRTL.
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
fs = c.call("dwsim_flowsheet_create", name="etoac_dehydrogenation_llm_opus")["flowsheet_id"]
report = {"plant": "etoac_dehydrogenation/llm_opus", "flowsheet_id": fs, "assumptions": [], "stages": {}, "checks": {}}


def assume(text):
    report["assumptions"].append(text)
    print("  ASSUMED:", text, file=sys.stderr)


c.call("dwsim_thermo_add_compounds", flowsheet_id=fs, names=["Ethanol", "Ethyl acetate", "Hydrogen", "Acetaldehyde", "Diethyl ether", "Water"])
c.call("dwsim_thermo_set_property_package", flowsheet_id=fs, name="NRTL")
report["bips_missing_before_estimate"] = c.call("dwsim_thermo_get_bips", flowsheet_id=fs).get("missing")
c.call("dwsim_thermo_estimate_bips", flowsheet_id=fs)

c.call("dwsim_reaction_add_equilibrium", flowsheet_id=fs, name="Dehydrogenation", reaction_set="DEHYD",
       stoichiometry={"Ethanol": -2, "Ethyl acetate": 1, "Hydrogen": 2}, base_compound="Ethanol", phase="Vapor")
c.call("dwsim_reaction_add_equilibrium", flowsheet_id=fs, name="Acetaldehyde-formation", reaction_set="DEHYD",
       stoichiometry={"Ethanol": -1, "Acetaldehyde": 1, "Hydrogen": 1}, base_compound="Ethanol", phase="Vapor")
c.call("dwsim_reaction_add_conversion", flowsheet_id=fs, name="Ether-formation", reaction_set="ETHER",
       stoichiometry={"Ethanol": -2, "Diethyl ether": 1, "Water": 1}, base_compound="Ethanol", conversion="0.5", phase="Vapor")
c.call("dwsim_reaction_add_conversion", flowsheet_id=fs, name="Acetaldehyde-hydrogenation", reaction_set="HYDRO",
       stoichiometry={"Acetaldehyde": -1, "Hydrogen": -1, "Ethanol": 1}, base_compound="Acetaldehyde", conversion="100", phase="Mixture")

P = bar(1.5)
assume("ethanol feed 1.0 mol/s anhydrous at 25 C; the plant runs at about 1.5 bar in the reactor section (no pressure stated)")
add_feed(c, fs, "ETHANOL-T1", C(25), P, 1.0, {"Ethanol": 1.0})
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-FEED")
connect(c, fs, "MIX-FEED", feed="ETHANOL-T1", feed_port=0, product="E1-COLD-IN")

# E1 feed/effluent exchanger: the feed side is a heater to 90 C, the effluent side a cooler whose duty a Spec block
# ties to the heater's duty, so the two halves exchange exactly the same heat (a two-sided HeatExchanger with its
# hot inlet inside a recycle loop did not solve headless)
unit(c, fs, "Heater", "E1-FEED-SIDE", feed="E1-COLD-IN", product="E1-COLD-OUT", energy_in="Q-E1",
     mode="OutletTemperature", props={"OutletTemperature": C(80)})
assume("E1 feed/effluent exchanger: feed heated to 80 C (liquid, just below its boiling point at 1.5 bar); the effluent side removes the same duty (tied by a Spec block)")

unit(c, fs, "Heater", "V1-VAPORISER", feed="E1-COLD-OUT", product="ETHANOL-VAPOUR", energy_in="Q-V1",
     mode="OutletTemperature", props={"OutletTemperature": C(230)})
assume("vaporiser outlet = reactor temperature 230 C (document: superheated vapour; reactor 200-260 C)")
unit(c, fs, "ConversionReactor", "R1-ETHER-SIDE", feed="ETHANOL-VAPOUR", product="R1A-OUT", product_port=0, energy_in="Q-R1A",
     mode="Isothermic", props={"ReactionSetID": "ETHER"})
connect(c, fs, "R1-ETHER-SIDE", product="R1A-LIQ", product_port=1)
unit(c, fs, "EquilibriumReactor", "R1-DEHYDROGENATION", feed="R1A-OUT", product="R1-OUT", product_port=0, energy_in="Q-R1",
     mode="Isothermic", props={"ReactionSetID": "DEHYD"})
connect(c, fs, "R1-DEHYDROGENATION", product="R1-LIQ", product_port=1)
assume("reactor isothermal at 230 C (fixed bed with the stated 200-260 C window; the document calls it endothermic and "
       "equilibrium limited, so the duty is reported); a 0.5 % ethanol side conversion to diethyl ether gives the light end "
       "the first column removes")

r1 = solve(c, fs)
report["stages"]["1_reactor_open"] = {"ok": r1.get("ok"), "errors": r1.get("errors")}
print_streams(c, fs, ["ETHANOL-T1", "E1-COLD-OUT", "ETHANOL-VAPOUR", "R1-OUT"], "stage 1: reactor")
rx = c.call("dwsim_unitop_get_results", flowsheet_id=fs, name="R1-DEHYDROGENATION")
print("  R1:", {k: v["value"][:8] + " " + v["units"] for k, v in rx["properties"].items() if "Conversion" in k or "Heat" in k})
report["checks"]["R1_ethanol_conversion_pct"] = rx["properties"].get("Ethanol: Conversion", {}).get("value")

# effluent side of E1: a cooler whose heat removed equals the heat the feed side added
unit(c, fs, "Cooler", "E1-EFFLUENT-SIDE", feed="R1-OUT", product="E1-HOT-OUT", energy_out="Q-E1H", mode="HeatRemoved",
     props={"DeltaQ": 40.0})
def prop_id(obj, text, kind="ALL"):
    for pr in c.call("dwsim_object_list_properties", flowsheet_id=fs, target=obj, type=kind)["properties"]:
        if text.lower() in pr["description"].lower():
            return pr["id"]
    raise ToolError(f"no property '{text}' on {obj}")
q_added = prop_id("E1-FEED-SIDE", "Heat Added")
q_removed = prop_id("E1-EFFLUENT-SIDE", "Heat Removed", "WR")
c.call("dwsim_spec_create", flowsheet_id=fs, name="SPEC-E1", source={"object": "E1-FEED-SIDE", "property": q_added},
       target={"object": "E1-EFFLUENT-SIDE", "property": q_removed}, expression="X")
r1b = solve(c, fs)
report["stages"]["1b_effluent_side_tied"] = {"ok": r1b.get("ok"), "errors": r1b.get("errors")}
print_streams(c, fs, ["E1-COLD-OUT", "R1-OUT", "E1-HOT-OUT"], "stage 1b: effluent side tied to the feed side")
hx = c.call("dwsim_unitop_get_results", flowsheet_id=fs, name="E1-EFFLUENT-SIDE")
print("  E1 effluent side:", {k: v["value"][:8] + " " + v["units"] for k, v in hx["properties"].items() if "Heat" in k or "Temperature" in k})

unit(c, fs, "Cooler", "E2-CONDENSER", feed="E1-HOT-OUT", product="CONDENSED", energy_out="Q-E2",
     mode="OutletTemperature", props={"OutletTemperature": C(35)})
assume("water-cooled condenser outlet 35 C")
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Vessel", name="S1-GL-SEPARATOR")
connect(c, fs, "S1-GL-SEPARATOR", feed="CONDENSED", feed_port=0, product="H2-GAS", product_port=0)
connect(c, fs, "S1-GL-SEPARATOR", product="CRUDE-LIQUID", product_port=1)
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Splitter", name="H2-SPLIT")
connect(c, fs, "H2-SPLIT", feed="H2-GAS", feed_port=0, product="H2-TO-K1", product_port=0)
connect(c, fs, "H2-SPLIT", product="H2-COPRODUCT", product_port=1)
c.call("dwsim_object_set", flowsheet_id=fs, target="H2-SPLIT", path="Ratios[0]", value=0.05)
c.call("dwsim_object_set", flowsheet_id=fs, target="H2-SPLIT", path="Ratios[1]", value=0.95)
assume("5 % of the hydrogen goes to the hydrogenation step, 95 % is exported (document: 'partly compressed')")
unit(c, fs, "Compressor", "K1-H2-COMPRESSOR", feed="H2-TO-K1", product="H2-HP", energy_in="W-K1",
     mode="OutletPressure", props={"POut": bar(5.0)})
unit(c, fs, "Pump", "P-CRUDE", feed="CRUDE-LIQUID", product="CRUDE-HP", energy_in="W-PC", mode="OutletPressure", props={"Pout": bar(5.0)})
assume("hydrogenation at 5 bar (no pressure stated)")
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-R2")
connect(c, fs, "MIX-R2", feed="CRUDE-HP", feed_port=0)
connect(c, fs, "MIX-R2", feed="H2-HP", feed_port=1, product="R2-FEED")
unit(c, fs, "ConversionReactor", "R2-HYDROGENATION", feed="R2-FEED", product="R2-VAP", product_port=0, energy_in="Q-R2",
     mode="Adiabatic", props={"ReactionSetID": "HYDRO"})
connect(c, fs, "R2-HYDROGENATION", product="R2-LIQ", product_port=1)
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-R2OUT")
connect(c, fs, "MIX-R2OUT", feed="R2-VAP", feed_port=0)
connect(c, fs, "MIX-R2OUT", feed="R2-LIQ", feed_port=1, product="HYDROGENATED-CRUDE")
r1c = solve(c, fs)
report["stages"]["1c_separation_and_hydrogenation"] = {"ok": r1c.get("ok"), "errors": r1c.get("errors")}
print_streams(c, fs, ["CONDENSED", "H2-GAS", "CRUDE-LIQUID", "H2-COPRODUCT", "R2-FEED", "HYDROGENATED-CRUDE"], "stage 1c: condenser, separator, hydrogenation")
save(c, fs, os.path.join(OUT, "etoac_dehydrogenation_llm_opus_stage1.dwxmz"))

if STAGE >= 2:
    # D1 first column: lights (dissolved gas, acetaldehyde, ether) overhead; ester + ethanol + heavier at the bottom
    unit(c, fs, "Valve", "V-D1", feed="HYDROGENATED-CRUDE", product="D1-FEED-WET", mode="OutletPressure", props={"OutletPressure": bar(1.6)})
    # the dissolved hydrogen leaves as the column's gas purge (specified split): a hydrogen trace in a rigorous
    # column's condenser did not converge
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="ComponentSeparator", name="D1-GAS-PURGE")
    connect(c, fs, "D1-GAS-PURGE", feed="D1-FEED-WET", feed_port=0, product="D1-GAS", product_port=0, energy_out="Q-D1G")
    connect(c, fs, "D1-GAS-PURGE", product="D1-FEED", product_port=1)
    compsep(c, fs, "D1-GAS-PURGE", 0, {"Hydrogen": 100.0})
    N1 = 12
    assume(f"D1 first column: {N1} stages, total condenser (the dissolved hydrogen is purged ahead of it), feed stage {N1 // 2}, "
           "1.5 bar top, reflux ratio 3; liquid distillate = ether + acetaldehyde left + 2 % of the ester")
    c.call("dwsim_column_create", flowsheet_id=fs, name="D1-FIRST-COL", type="Distillation", stages=N1,
           condenser_type="Total_Condenser", top_pressure_Pa=bar(1.5), pressure_drop_Pa=bar(0.1), max_iterations=300)
    for s_ in ("D1-LIGHTS", "D1-BTM"):
        c.call("dwsim_stream_add_material", flowsheet_id=fs, name=s_)
    for s_ in ("QC-D1", "QR-D1"):
        c.call("dwsim_stream_add_energy", flowsheet_id=fs, name=s_)
    solve(c, fs)
    f = stream_summary(c, fs, "D1-FEED")
    lights = max(f["mol_s"] * (f["x"].get("Diethyl ether", 0) + f["x"].get("Acetaldehyde", 0) + 0.02 * f["x"].get("Ethyl acetate", 0)), 0.01)
    c.call("dwsim_column_connect", flowsheet_id=fs, name="D1-FIRST-COL", feeds=[{"stream": "D1-FEED", "stage": N1 // 2}],
           distillate="D1-LIGHTS", bottoms="D1-BTM", condenser_duty="QC-D1", reboiler_duty="QR-D1")
    c.call("dwsim_column_set_specs", flowsheet_id=fs, name="D1-FIRST-COL",
           condenser={"type": "Reflux Ratio", "value": 3.0},
           reboiler={"type": "Product_Molar_Flow_Rate", "value": f["mol_s"] - lights, "units": "mol/s"})
    c.call("dwsim_column_set_estimates", flowsheet_id=fs, name="D1-FIRST-COL",
           temperatures_K=[C(45) + (C(85) - C(45)) * i / (N1 - 1) for i in range(N1)], use_temperatures=True)
    r2 = solve(c, fs, timeout_s=600)
    report["stages"]["2_first_column"] = {"ok": r2.get("ok"), "errors": r2.get("errors")}
    print_streams(c, fs, ["D1-FEED", "D1-GAS", "D1-LIGHTS", "D1-BTM"], "stage 2: first column")
    save(c, fs, os.path.join(OUT, "etoac_dehydrogenation_llm_opus_stage2.dwxmz"))

if STAGE >= 3:
    # D2/D3 pressure-swing pair: no pressures or attachments stated -> specified ester/ethanol split (not simulated)
    c.call("dwsim_unitop_add", flowsheet_id=fs, type="ComponentSeparator", name="D2D3-PRESSURE-SWING")
    connect(c, fs, "D2D3-PRESSURE-SWING", feed="D1-BTM", feed_port=0, product="ETHANOL-RECYCLE", product_port=0, energy_out="Q-D2D3")
    connect(c, fs, "D2D3-PRESSURE-SWING", product="ESTER-TO-D4", product_port=1)
    compsep(c, fs, "D2D3-PRESSURE-SWING", 0, {"Ethanol": 99.0, "Water": 99.0, "Ethyl acetate": 1.0})
    assume("pressure-swing pair D2/D3: 99 % of the ethanol and water to the recycle, 1 % of the ester with it; specified, "
           "not simulated (the document gives no pressures and does not say which column takes the feed)")
    # D4 product column: heavies purge; without heavy compounds the ester passes with a 1 % bottoms purge
    N4 = 10
    assume(f"D4 product column: {N4} stages, total condenser, 1 atm, reflux ratio 2, bottoms purge 1 % of the feed (no heavy "
           "compound exists in the basis, so the purge is ester)")
    c.call("dwsim_column_create", flowsheet_id=fs, name="D4-PRODUCT-COL", type="Distillation", stages=N4,
           condenser_type="Total_Condenser", top_pressure_Pa=ATM, pressure_drop_Pa=bar(0.05))
    for s in ("D4-DIST", "HEAVIES-PURGE"):
        c.call("dwsim_stream_add_material", flowsheet_id=fs, name=s)
    for s in ("QC-D4", "QR-D4"):
        c.call("dwsim_stream_add_energy", flowsheet_id=fs, name=s)
    r3pre = solve(c, fs, timeout_s=600)
    f4 = stream_summary(c, fs, "ESTER-TO-D4")
    c.call("dwsim_column_connect", flowsheet_id=fs, name="D4-PRODUCT-COL", feeds=[{"stream": "ESTER-TO-D4", "stage": N4 // 2}],
           distillate="D4-DIST", bottoms="HEAVIES-PURGE", condenser_duty="QC-D4", reboiler_duty="QR-D4")
    c.call("dwsim_column_set_specs", flowsheet_id=fs, name="D4-PRODUCT-COL",
           condenser={"type": "Reflux Ratio", "value": 2.0},
           reboiler={"type": "Product_Molar_Flow_Rate", "value": 0.01 * f4["mol_s"], "units": "mol/s"})
    unit(c, fs, "Cooler", "E3-PRODUCT-COOLER", feed="D4-DIST", product="PRODUCT-T2", energy_out="Q-E3",
         mode="OutletTemperature", props={"OutletTemperature": C(25)})
    r3a = solve(c, fs, timeout_s=600)
    report["stages"]["3a_product_column_recycle_open"] = {"ok": r3a.get("ok"), "errors": r3a.get("errors")}
    print_streams(c, fs, ["D1-BTM", "ETHANOL-RECYCLE", "ESTER-TO-D4", "D4-DIST", "HEAVIES-PURGE", "PRODUCT-T2"], "stage 3a: product column (ethanol recycle open)")

    unit(c, fs, "Pump", "P-RCY", feed="ETHANOL-RECYCLE", product="ETHANOL-RCY-P", energy_in="W-PR", mode="OutletPressure", props={"Pout": P})
    unit(c, fs, "Recycle", "RCY-ETOH", feed="ETHANOL-RCY-P", product="ETHANOL-RCY-R", seed_from="ETHANOL-RECYCLE")
    connect(c, fs, "MIX-FEED", feed="ETHANOL-RCY-R", feed_port=1)
    c.call("dwsim_recycle_configure", flowsheet_id=fs, name="RCY-ETOH", max_iterations=100)
    r3 = solve(c, fs, timeout_s=900)
    report["stages"]["3_full_plant"] = {"ok": r3.get("ok"), "errors": r3.get("errors")}
    print_streams(c, fs, ["ETHANOL-T1", "ETHANOL-RCY-R", "E1-COLD-IN", "ETHANOL-VAPOUR", "R1-OUT", "H2-COPRODUCT", "HYDROGENATED-CRUDE",
                          "D1-LIGHTS", "D1-BTM", "ETHANOL-RECYCLE", "PRODUCT-T2", "HEAVIES-PURGE"], "stage 3: full plant")
    prod = stream_summary(c, fs, "PRODUCT-T2")
    report["checks"]["product_ester_wt_frac (doc >= 0.995)"] = prod["w"].get("Ethyl acetate")
    report["checks"]["product_mol_s_per_mol_fresh_ethanol"] = prod["mol_s"]
    rx = c.call("dwsim_unitop_get_results", flowsheet_id=fs, name="R1-DEHYDROGENATION")
    report["checks"]["R1_ethanol_conversion_per_pass_pct"] = rx["properties"].get("Ethanol: Conversion", {}).get("value")
    save(c, fs, os.path.join(OUT, "etoac_dehydrogenation_llm_opus.dwxmz"))

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
with open(os.path.join(OUT, "etoac_dehydrogenation_llm_opus.json"), "w") as f:
    json.dump(report, f, indent=1)
print("\nreport:", os.path.join(OUT, "etoac_dehydrogenation_llm_opus.json"))
