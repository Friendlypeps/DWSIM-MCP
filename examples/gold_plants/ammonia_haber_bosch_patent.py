"""ammonia_haber_bosch / patent US9040011B2: synthesis gas train of the preferred embodiment.

Stated: E1 preheat to 500-600 C; primary reformer R1 at 65 bar, tube outlet 700-770 C; secondary reformer R2
outlet 1000 C / 60 bar (air, in the patent air enriched with oxygen); E2 cool to 350 C; CO shift converters
(adiabatic, one unit); E3 cool to near ambient; separator S1; CO2 washing column C4 (component separator,
specified); E4 heat to 300 C; methanator R5 (nickel); E5 cool; separator S2; compressor K1 to 150 bar;
synthesis loop block L1 (one equilibrium reactor at 150 bar, the loop internals are not described).
Everything else is marked ASSUMED. Compounds as in the Opus plant, Peng-Robinson.
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcpclient import *  # noqa

OUT = os.environ.get("GOLD_OUT", os.path.join(os.path.dirname(os.path.abspath(__file__)), "out"))
os.makedirs(OUT, exist_ok=True)

c = Client()
fs = c.call("dwsim_flowsheet_create", name="ammonia_haber_bosch_patent")["flowsheet_id"]
report = {"plant": "ammonia_haber_bosch/patent US9040011B2", "flowsheet_id": fs, "assumptions": [], "stages": {}, "checks": {}}


def assume(text):
    report["assumptions"].append(text)
    print("  ASSUMED:", text, file=sys.stderr)


COMP = ["Methane", "Water", "Hydrogen", "Carbon monoxide", "Carbon dioxide", "Nitrogen", "Oxygen", "Argon", "Ammonia"]
c.call("dwsim_thermo_add_compounds", flowsheet_id=fs, names=COMP)
c.call("dwsim_thermo_set_property_package", flowsheet_id=fs, name="PR")
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

P1 = bar(65)
assume("natural gas = methane 1.0 mol/s with steam, S/C 3.0, both at 65 bar (stated primary reformer pressure) and 25 C / 300 C")
add_feed(c, fs, "NATGAS-7", C(25), P1, 1.0, {"Methane": 1.0})
add_feed(c, fs, "STEAM-8", C(300), P1, 3.0, {"Water": 1.0})
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-7-8")
connect(c, fs, "MIX-7-8", feed="NATGAS-7", feed_port=0)
connect(c, fs, "MIX-7-8", feed="STEAM-8", feed_port=1, product="LINE-9")
unit(c, fs, "Heater", "E1-HX10", feed="LINE-9", product="LINE-11", energy_in="Q-E1",
     mode="OutletTemperature", props={"OutletTemperature": C(550)})  # stated 500-600 C
unit(c, fs, "EquilibriumReactor", "R1-PRIMARY", feed="LINE-11", product="LINE-13", product_port=0, energy_in="Q-R1",
     mode="OutletTemperature", props={"OutletTemperature": C(735), "ReactionSetID": "REFORM", "DeltaP": bar(2.0)})  # stated 700-770 C
connect(c, fs, "R1-PRIMARY", product="R1-LIQ", product_port=1)
assume("primary reformer tube outlet 735 C (700-770), 2 bar drop; the secondary reformer gets air 14 and oxygen 15 (the "
       "patent mixes both): the air flow is set so that the synthesis gas has H2:N2 = 3 and the oxygen flow so that the "
       "secondary outlet reaches the stated 1000 C")
AIR0, O2_0 = 1.0, 0.2
add_feed(c, fs, "AIR-14", C(25), bar(63), AIR0, {"Oxygen": 0.21, "Nitrogen": 0.78, "Argon": 0.01})
add_feed(c, fs, "OXYGEN-15", C(25), bar(63), O2_0, {"Oxygen": 1.0})
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Mixer", name="MIX-R2")
connect(c, fs, "MIX-R2", feed="LINE-13", feed_port=0)
connect(c, fs, "MIX-R2", feed="AIR-14", feed_port=1)
connect(c, fs, "MIX-R2", feed="OXYGEN-15", feed_port=2, product="R2-FEED")
unit(c, fs, "ConversionReactor", "R2-BURN", feed="R2-FEED", product="R2-BURNT", product_port=0, energy_in="Q-R2a",
     mode="Adiabatic", props={"ReactionSetID": "BURN", "DeltaP": bar(1.0)})
connect(c, fs, "R2-BURN", product="R2-BURN-LIQ", product_port=1)
unit(c, fs, "EquilibriumReactor", "R2-SECONDARY", feed="R2-BURNT", product="LINE-17", product_port=0, energy_in="Q-R2b",
     mode="Adiabatic", props={"ReactionSetID": "REFORM", "DeltaP": bar(2.0)})
connect(c, fs, "R2-SECONDARY", product="R2-LIQ", product_port=1)
unit(c, fs, "Cooler", "E2-HX16", feed="LINE-17", product="LINE-18", energy_out="Q-E2",
     mode="OutletTemperature", props={"OutletTemperature": C(350)})  # stated 350 C
unit(c, fs, "EquilibriumReactor", "R3-SHIFT-A", feed="LINE-18", product="R3A-OUT", product_port=0, energy_in="Q-R3A",
     mode="Adiabatic", props={"ReactionSetID": "SHIFT"})
connect(c, fs, "R3-SHIFT-A", product="R3A-LIQ", product_port=1)
unit(c, fs, "Cooler", "R3-INTERCOOLER", feed="R3A-OUT", product="R3B-FEED", energy_out="Q-R3IC",
     mode="OutletTemperature", props={"OutletTemperature": C(200)})
unit(c, fs, "EquilibriumReactor", "R3-SHIFT-B", feed="R3B-FEED", product="LINE-20", product_port=0, energy_in="Q-R3B",
     mode="Adiabatic", props={"ReactionSetID": "SHIFT"})
connect(c, fs, "R3-SHIFT-B", product="R3B-LIQ", product_port=1)
assume("CO shift converters 3 = the 'series (normally two)': two adiabatic equilibrium beds with an intercooler to 200 C between "
       "them (the second bed's inlet temperature is not stated); the stated outlet of around 220 C is then a result")
unit(c, fs, "Cooler", "E3-HX19", feed="LINE-20", product="LINE-22", energy_out="Q-E3",
     mode="OutletTemperature", props={"OutletTemperature": C(35)})
assume("'near ambient' after heat exchanger 19 = 35 C")
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Vessel", name="S1-SEP21")
connect(c, fs, "S1-SEP21", feed="LINE-22", feed_port=0, product="LINE-22A", product_port=0)
connect(c, fs, "S1-SEP21", product="CONDENSATE-21", product_port=1)
c.call("dwsim_unitop_add", flowsheet_id=fs, type="ComponentSeparator", name="C4-CO2-WASH")
connect(c, fs, "C4-CO2-WASH", feed="LINE-22A", feed_port=0, product="CO2-OFFGAS", product_port=0, energy_out="Q-C4")
connect(c, fs, "C4-CO2-WASH", product="LINE-24", product_port=1)
compsep(c, fs, "C4-CO2-WASH", 0, {"Carbon dioxide": 99.9})
assume("CO2 washing column 4: 99.9 % CO2 removal, specified not simulated")
unit(c, fs, "Heater", "E4-HX23", feed="LINE-24", product="LINE-25", energy_in="Q-E4",
     mode="OutletTemperature", props={"OutletTemperature": C(300)})  # stated 300 C
unit(c, fs, "ConversionReactor", "R5-METHANATOR", feed="LINE-25", product="LINE-27", product_port=0, energy_in="Q-R5",
     mode="Adiabatic", props={"ReactionSetID": "METH"})
connect(c, fs, "R5-METHANATOR", product="R5-LIQ", product_port=1)
unit(c, fs, "Cooler", "E5-HX26", feed="LINE-27", product="LINE-29", energy_out="Q-E5",
     mode="OutletTemperature", props={"OutletTemperature": C(35)})
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Vessel", name="S2-SEP28")
connect(c, fs, "S2-SEP28", feed="LINE-29", feed_port=0, product="LINE-30-SYNGAS", product_port=0)
connect(c, fs, "S2-SEP28", product="CONDENSATE-28", product_port=1)
unit(c, fs, "Compressor", "K1-COMP33", feed="LINE-30-SYNGAS", product="LINE-31", energy_in="W-K1",
     mode="OutletPressure", props={"POut": bar(150)})  # stated 150 bar
unit(c, fs, "Heater", "L1-PREHEAT", feed="LINE-31", product="L1-FEED", energy_in="Q-L1H",
     mode="OutletTemperature", props={"OutletTemperature": C(400)})
unit(c, fs, "EquilibriumReactor", "L1-SYNTHESIS-LOOP", feed="L1-FEED", product="L1-OUT", product_port=0, energy_in="Q-L1",
     mode="OutletTemperature", props={"OutletTemperature": C(450), "ReactionSetID": "SYN"})
connect(c, fs, "L1-SYNTHESIS-LOOP", product="L1-LIQ", product_port=1)
unit(c, fs, "Cooler", "L1-CONDENSE", feed="L1-OUT", product="L1-COLD", energy_out="Q-L1C",
     mode="OutletTemperature", props={"OutletTemperature": C(-15)})
c.call("dwsim_unitop_add", flowsheet_id=fs, type="Vessel", name="L1-SEPARATOR")
connect(c, fs, "L1-SEPARATOR", feed="L1-COLD", feed_port=0, product="L1-UNCONVERTED", product_port=0)
connect(c, fs, "L1-SEPARATOR", product="AMMONIA-32", product_port=1)
assume("synthesis loop block 6 drawn once-through: preheat to 400 C, equilibrium converter at 450 C and 150 bar, "
       "condensation at -15 C, separator; the loop recycle is not described in the patent and not closed here")

r = solve(c, fs)
report["stages"]["1_once_through"] = {"ok": r.get("ok"), "errors": r.get("errors")}


def t_out(name):
    return stream_summary(c, fs, name)["T_C"]


# air for H2:N2 = 3 in the synthesis gas, oxygen for the stated 1000 C secondary outlet; the two are solved by
# alternating a direct update of the air (nitrogen passes straight through) and a secant step on the oxygen
hist = []
air, o2 = AIR0, O2_0
h2_avail = stream_summary(c, fs, "LINE-13")["mol_s"] * stream_summary(c, fs, "LINE-13")["x"].get("Hydrogen", 0)
for it in range(20):
    T = t_out("LINE-17")
    sg = stream_summary(c, fs, "LINE-30-SYNGAS")
    ratio = sg["x"].get("Hydrogen", 0) / max(sg["x"].get("Nitrogen", 1e-9), 1e-9)
    hist.append((o2, T))
    print(f"  air {air:.4f} O2 {o2:.4f} mol/s -> secondary outlet {T:.1f} C, H2:N2 = {ratio:.3f}", file=sys.stderr)
    if abs(T - 1000.0) < 2.0 and abs(ratio - 3.0) < 0.02:
        break
    air = max(0.2, min(air * (1.0 + 0.7 * (ratio / 3.0 - 1.0)), 4.0))
    if len(hist) >= 2 and abs(hist[-1][1] - hist[-2][1]) > 1e-6:
        (a0, t0), (a1, t1) = hist[-2], hist[-1]
        step = (1000.0 - t1) * (a1 - a0) / (t1 - t0)
    else:
        step = 0.003 * (1000.0 - T)
    o2 = o2 + max(-0.08, min(0.08, step))
    # the oxygen must stay below what the hydrogen in the primary reformer gas can burn, or it passes through
    o2 = max(0.0, min(o2, 0.4 * h2_avail - 0.21 * air))
    set_feed(c, fs, "AIR-14", mol_s=air)
    set_feed(c, fs, "OXYGEN-15", mol_s=max(o2, 1e-6))
    r = solve(c, fs)
report["stages"]["1_once_through"] = {"ok": r.get("ok"), "errors": r.get("errors")}
sg = stream_summary(c, fs, "LINE-30-SYNGAS")
report["checks"] = {
    "air_mol_s": stream_summary(c, fs, "AIR-14")["mol_s"],
    "oxygen_mol_s_for_1000C": stream_summary(c, fs, "OXYGEN-15")["mol_s"],
    "R1_outlet_T_C (doc 700-770)": t_out("LINE-13"),
    "R2_outlet_T_C (doc ~1000)": t_out("LINE-17"),
    "R2_outlet_P_bar (doc ~60)": stream_summary(c, fs, "LINE-17")["P_bar"],
    "R3_shift_outlet_T_C (doc ~220 after the series)": t_out("LINE-20"),
    "R5_methanator_outlet_T_C": t_out("LINE-27"),
    "syngas_H2_N2_ratio": sg["x"].get("Hydrogen", 0) / max(sg["x"].get("Nitrogen", 1e-9), 1e-9),
    "syngas_x": sg["x"],
    "ammonia_32_mol_s": stream_summary(c, fs, "AMMONIA-32")["mol_s"],
    "ammonia_32_x": stream_summary(c, fs, "AMMONIA-32")["x"],
}
print_streams(c, fs, ["LINE-11", "LINE-13", "R2-FEED", "LINE-17", "LINE-20", "LINE-22A", "CONDENSATE-21", "CO2-OFFGAS", "LINE-24",
                      "LINE-27", "LINE-30-SYNGAS", "LINE-31", "L1-OUT", "L1-UNCONVERTED", "AMMONIA-32"], "patent train")
print("  checks:", json.dumps(report["checks"], indent=1))
save(c, fs, os.path.join(OUT, "ammonia_haber_bosch_patent.dwxmz"))
objs = c.call("dwsim_flowsheet_list_objects", flowsheet_id=fs, type_filter="streams")
report["streams"] = {}
for o in objs.get("objects", []):
    n = o.get("name") or o.get("tag")
    if (o.get("type") or "").startswith("Material"):
        try:
            report["streams"][n] = stream_summary(c, fs, n)
        except ToolError as e:
            report["streams"][n] = {"error": str(e)[:300]}
with open(os.path.join(OUT, "ammonia_haber_bosch_patent.json"), "w") as f:
    json.dump(report, f, indent=1)
print("\nreport:", os.path.join(OUT, "ammonia_haber_bosch_patent.json"))
