# Ethyl acetate plant: build sequence

A tool-call sequence that builds an ethyl acetate plant with this server. Ethanol and acetic acid
are esterified over sulfuric acid in a stirred reactor. A column sits on top of the reactor, its
overhead goes through a condenser to a decanter, and two more columns purify the ester and recover
the alcohol. There are four recycle loops.

```
FEED-ACID ┐                                   ┌──────────── REFLUX (organic) ◄─ SP-1 ◄─┐
FEED-ETOH ┼─ M-1 ─ U03 ─ M-2 ─ U04 ─vap─► U05 ─ovhd─► U06 ─► U07 decanter ─ organic ┘   │
ETOH-REC ─┘             ▲       │ liq          │ bottoms        ▲   └─ aqueous ─► U11 ─► WATER
                        │      PURGE           │ (acid)         │                 │
                        └──────────────────────┘          D08 ◄─┴─ U08 ◄─ ORG     └─ D11 (ethanol) ─► M-1
                                                                     └─► PRODUCT (ethyl acetate)
```

It is written for an MCP client driving this server. Every call below also takes `flowsheet_id`,
which is left out to keep the calls short. The tool names and parameters come from the sources in
`src/` and `external/dwsim10/tools/DWSIM.MCPServer`. The port numbers come from the engine
(`CSTR.vb`, `Vessel.vb`, `Splitter.vb`).

**Status:** this sequence has not been solved end to end. The kinetics are placeholders (see
step 1) and the initial guesses are rough. Expect to adjust guesses, specs and column estimates
while you work through stages 4 and 5.

## How the plant maps to DWSIM

| Block | DWSIM object | Notes |
|---|---|---|
| U03 feed preheater | `Heater` | Outlet temperature 343–353 K. |
| U04 esterification reactor | `CSTR`, kinetic reaction, outlet-temperature mode | **Outlet 0 is the liquid (the purge), outlet 1 is the vapour.** The CSTR has a single material inlet, so mixer M-2 combines the feed with the acid return. |
| U05 esterification column | rigorous column, type `Absorption` (no condenser, no reboiler) | The reactor acts as its reboiler: reactor vapour enters the bottom stage and the organic reflux from the decanter enters the top stage. The bottoms (acid-rich) return to U04. |
| U06 condenser | `Cooler` | 298–313 K. |
| U07 decanter | `Vessel` under a **VLLE** flash | Inlet 0 takes the condensate, inlet 1 the U08 tops. Outlet 0 is vapour, 1 is liquid 1, 2 is liquid 2. |
| organic split | `Splitter` | Splits the organic phase into reflux to U05 and feed to U08. The diagram's "reflux ratio 2–4" is set here. |
| U08 ester column | rigorous `Distillation` | Bottoms: ethyl acetate at 350–351 K. Tops: near the ternary azeotrope, returned to U07. |
| U11 alcohol recovery column | rigorous `Distillation` | Bottoms: water. Tops: ethanol, returned to M-1. |
| T02 purge | end stream `PURGE` | |
| 4 loops | `Recycle` blocks | REC-REFLUX, REC-ACID, REC-D08, REC-ETOH |

The reaction stays inside U04, so DWSIM's lack of reactive distillation does not matter here.

The basis is 1 mol/s of acetic acid. Temperatures are in K and pressures in Pa. Stream compositions
are **mass fractions**.

---

## Stage 0: flowsheet and thermodynamics

```json
dwsim_flowsheet_create        {"name": "Ethyl acetate plant"}
dwsim_thermo_add_compounds    {"names": ["Ethanol", "Acetic acid", "Ethyl acetate", "Water"]}
dwsim_thermo_set_property_package {"name": "NRTL"}
dwsim_thermo_set_flash        {"equilibrium_type": "VLLE"}
dwsim_thermo_get_bips         {}
```

`get_bips` lists all six pairs and shows which are zero. Fill the missing ones:

```json
dwsim_thermo_estimate_bips    {}
```

UNIFAC estimates are not good enough for the decanter. Replace at least **Ethyl acetate–Water**,
and preferably also **Ethanol–Water** and **Ethanol–Ethyl acetate**, with NRTL parameters that
were regressed on liquid-liquid data, for example from the DECHEMA LLE Data Collection.
Parameters are A12 and A21 in cal/mol:

```json
dwsim_thermo_set_bips {"pairs": [
  {"c1": "Ethyl acetate", "c2": "Water", "A12": <lit>, "A21": <lit>, "alpha12": <lit>}
]}
```

**Check the liquid-liquid split before building anything else.** Flash a test stream that is half
ester and half water at 303 K:

```json
dwsim_stream_add_material {"name": "LLE-TEST", "temperature_K": 303.15, "pressure_Pa": 101325,
                           "molar_flow_mol_s": 1, "composition": {"Ethyl acetate": 0.5, "Water": 0.5}}
dwsim_solve_run           {"timeout_s": 60}
dwsim_stream_get_results  {"name": "LLE-TEST"}
```

You should see two liquid phases, each roughly matching measured solubilities at 20–30 °C. The
ester-rich phase holds about 3 wt% water, and the water-rich phase about 8 wt% ester. If you get a
single liquid, or compositions far from these, fix the BIPs now. Every downstream number depends on
them. Then delete the test stream:

```json
dwsim_object_delete {"target": "LLE-TEST"}
```

## Stage 1: reaction

The reaction is CH₃COOH + C₂H₅OH ⇌ CH₃COOC₂H₅ + H₂O, in the liquid phase. The rate is
pseudo-homogeneous, with the catalyst concentration folded into k. Sulfuric acid is not added as a
compound: it would only add missing BIPs and change nothing in the balance.

```json
dwsim_reaction_add_kinetic {
  "name": "Esterification", "reaction_set": "ESTER",
  "stoichiometry": {"Acetic acid": -1, "Ethanol": -1, "Ethyl acetate": 1, "Water": 1},
  "base_compound": "Acetic acid", "phase": "Liquid",
  "basis": "molar concentration", "amount_units": "mol/m3", "rate_units": "mol/[m3.s]",
  "a_forward": 2.5,   "e_forward": 50000,
  "a_reverse": 0.625, "e_reverse": 50000, "e_forward_unit": "J/mol",
  "tmin_K": 300, "tmax_K": 400
}
```

> **Placeholder kinetics.** These numbers give k_f(353 K) ≈ 1·10⁻⁷ m³/(mol·s) and K = k_f/k_r = 4
> at every temperature, the usual liquid-phase value for this reaction. Replace them with kinetics
> measured for your catalyst loading, using `dwsim_reaction_set_kinetics`. The ratio A_f/A_r sets
> the equilibrium constant.

## Stage 2: front end, loops open

Each stream that a loop will later return is created now as an ordinary feed holding a **guess**.
In stage 5, the Recycle block takes that stream over as its outlet.

```json
dwsim_stream_add_material {"name": "FEED-ACID", "temperature_K": 298.15, "pressure_Pa": 101325,
                           "molar_flow_mol_s": 1.0,  "composition": {"Acetic acid": 1}}
dwsim_stream_add_material {"name": "FEED-ETOH", "temperature_K": 298.15, "pressure_Pa": 101325,
                           "molar_flow_mol_s": 1.02, "composition": {"Ethanol": 1}}
dwsim_stream_add_material {"name": "ETOH-REC-R", "temperature_K": 351, "pressure_Pa": 101325,
                           "molar_flow_mol_s": 0.3,
                           "composition": {"Ethanol": 0.80, "Water": 0.12, "Ethyl acetate": 0.08}}
dwsim_stream_add_material {"name": "ACID-REC-R", "temperature_K": 370, "pressure_Pa": 101325,
                           "molar_flow_mol_s": 0.5,
                           "composition": {"Acetic acid": 0.60, "Ethanol": 0.15, "Water": 0.15, "Ethyl acetate": 0.10}}
```

**M-1, the mixer before the preheater** (inlets 0, 1 and 2):

```json
dwsim_unitop_add     {"type": "Mixer", "name": "M-1"}
dwsim_stream_add_material {"name": "S-MIX1"}
dwsim_unitop_connect {"unitop": "M-1", "feed_stream": "FEED-ACID",  "feed_port": 0, "product_stream": "S-MIX1"}
dwsim_unitop_connect {"unitop": "M-1", "feed_stream": "FEED-ETOH",  "feed_port": 1}
dwsim_unitop_connect {"unitop": "M-1", "feed_stream": "ETOH-REC-R", "feed_port": 2}
```

**U03, the feed preheater:**

```json
dwsim_unitop_add     {"type": "Heater", "name": "U03"}
dwsim_stream_add_material {"name": "S-HOT"}
dwsim_stream_add_energy   {"name": "Q-U03"}
dwsim_unitop_connect {"unitop": "U03", "feed_stream": "S-MIX1", "product_stream": "S-HOT", "energy_feed": "Q-U03"}
dwsim_unitop_set     {"name": "U03", "properties": {"CalcMode": "OutletTemperature", "OutletTemperature": 348.15}}
```

**M-2 and U04, the reactor.** The CSTR's outlet 0 is the liquid and outlet 1 the vapour:

```json
dwsim_unitop_add     {"type": "Mixer", "name": "M-2"}
dwsim_stream_add_material {"name": "S-RXIN"}
dwsim_unitop_connect {"unitop": "M-2", "feed_stream": "S-HOT",      "feed_port": 0, "product_stream": "S-RXIN"}
dwsim_unitop_connect {"unitop": "M-2", "feed_stream": "ACID-REC-R", "feed_port": 1}

dwsim_unitop_add     {"type": "CSTR", "name": "U04"}
dwsim_stream_add_material {"name": "PURGE"}
dwsim_stream_add_material {"name": "S-VAP"}
dwsim_stream_add_energy   {"name": "Q-U04"}
dwsim_unitop_connect {"unitop": "U04", "feed_stream": "S-RXIN", "product_stream": "PURGE", "product_port": 0, "energy_feed": "Q-U04"}
dwsim_unitop_connect {"unitop": "U04", "product_stream": "S-VAP", "product_port": 1}
dwsim_unitop_set     {"name": "U04", "properties": {"ReactionSetID": "ESTER", "Volume": 3.0,
                      "ReactorOperationMode": "OutletTemperature", "OutletTemperature": 365}}
dwsim_solve_run      {"timeout_s": 120}
```

Check the reactor: `dwsim_unitop_get_results {"name": "U04"}` and `dwsim_stream_get_results` on
`S-VAP` and `PURGE`.

In this model the reactor temperature decides how much leaves as vapour and how much as purge. Run
a sweep to see the relationship before fixing it with an Adjust in stage 6:

```json
dwsim_study_list_variables {"object": "U04"}
dwsim_study_sweep {"variables": [{"object": "U04", "property": "<outlet T id>", "from": 358, "to": 378, "steps": 6}],
                   "watches":   [{"object": "PURGE", "property": "<molar flow id>"}]}
```

Look up the `PROP_*` ids with `dwsim_study_list_variables`, or with `dwsim_object_list_properties`
on `PURGE`.

## Stage 3: esterification column, condenser and decanter

The reflux and the U08 tops get guess streams too:

```json
dwsim_stream_add_material {"name": "REFLUX-R", "temperature_K": 303.15, "pressure_Pa": 101325,
                           "molar_flow_mol_s": 3.0,
                           "composition": {"Ethyl acetate": 0.88, "Ethanol": 0.08, "Water": 0.04}}
dwsim_stream_add_material {"name": "D08-R", "temperature_K": 344, "pressure_Pa": 101325,
                           "molar_flow_mol_s": 0.5,
                           "composition": {"Ethyl acetate": 0.826, "Ethanol": 0.084, "Water": 0.090}}
```

**U05**, an absorber-type column. Stage 1 is the top and stage 10 the bottom:

```json
dwsim_column_create  {"name": "U05", "type": "Absorption", "stages": 10, "top_pressure_Pa": 101325,
                      "pressure_drop_Pa": 0, "solving_method": "Burningham-Otto (Sum Rates)"}
dwsim_stream_add_material {"name": "S-OVHD"}
dwsim_stream_add_material {"name": "ACID-REC"}
dwsim_column_connect {"name": "U05", "feeds": [{"stream": "REFLUX-R", "stage": 1}, {"stream": "S-VAP", "stage": 10}],
                      "top_product": "S-OVHD", "bottoms": "ACID-REC"}
```

If U05 does not converge, try `"solving_method": "Naphtali-Sandholm (Simultaneous Correction)"`,
and/or give it a temperature profile from about 345 K at the top to 365 K at the bottom with
`dwsim_column_set_estimates`.

**U06, the condenser:**

```json
dwsim_unitop_add     {"type": "Cooler", "name": "U06"}
dwsim_stream_add_material {"name": "S-COND"}
dwsim_stream_add_energy   {"name": "Q-U06"}
dwsim_unitop_connect {"unitop": "U06", "feed_stream": "S-OVHD", "product_stream": "S-COND", "energy_product": "Q-U06"}
dwsim_unitop_set     {"name": "U06", "properties": {"CalcMode": "OutletTemperature", "OutletTemperature": 303.15}}
```

**U07, the decanter.** It has two feeds and three outlets, and holds 303 K so the warm U08 tops do
not shift the split:

```json
dwsim_unitop_add     {"type": "Vessel", "name": "U07"}
dwsim_stream_add_material {"name": "DEC-V"}
dwsim_stream_add_material {"name": "DEC-L1"}
dwsim_stream_add_material {"name": "DEC-L2"}
dwsim_stream_add_energy   {"name": "Q-U07"}
dwsim_unitop_connect {"unitop": "U07", "feed_stream": "S-COND", "feed_port": 0, "product_stream": "DEC-V",  "product_port": 0}
dwsim_unitop_connect {"unitop": "U07", "feed_stream": "D08-R",  "feed_port": 1, "product_stream": "DEC-L1", "product_port": 1}
dwsim_unitop_connect {"unitop": "U07", "product_stream": "DEC-L2", "product_port": 2, "energy_feed": "Q-U07"}
dwsim_vessel_configure {"name": "U07", "override_t_K": 303.15}
dwsim_solve_run      {"timeout_s": 180}
```

**Find the organic phase** with `dwsim_stream_get_results` on `DEC-L1` and `DEC-L2`. The ester-rich
one (the diagram expects 90–95 wt% ester) is called **ORG-PHASE** below, and the water-rich one
**AQ-PHASE**. Rename them so the rest of the sequence reads the same:

```json
dwsim_object_rename {"name": "DEC-L1", "new_name": "ORG-PHASE"}   // or DEC-L2, whichever is organic
dwsim_object_rename {"name": "DEC-L2", "new_name": "AQ-PHASE"}
```

If `DEC-V` carries a meaningful flow, the decanter is boiling. Lower the U06 outlet temperature or
check the BIPs. If either liquid is empty, the VLLE flash is not finding two phases (go back to
stage 0).

## Stage 4: splitter, ester column, alcohol column (still open)

**SP-1, the organic split.** Outlet 0 is the reflux and outlet 1 goes to U08. SR1 is the fraction
sent to outlet 0, so a reflux ratio R (reflux over product) gives SR1 = R/(R+1). For R = 3 that is
0.75. Set SR1 after both outlets are connected.

```json
dwsim_unitop_add     {"type": "Splitter", "name": "SP-1"}
dwsim_stream_add_material {"name": "REFLUX"}
dwsim_stream_add_material {"name": "ORG"}
dwsim_unitop_connect {"unitop": "SP-1", "feed_stream": "ORG-PHASE", "product_stream": "REFLUX", "product_port": 0}
dwsim_unitop_connect {"unitop": "SP-1", "product_stream": "ORG", "product_port": 1}
dwsim_unitop_set     {"name": "SP-1", "properties": {"SR1": 0.75}}
```

**U08, the ester purification column.** Start with a product-flow spec, which converges easily, and
switch to a purity spec once it solves:

```json
dwsim_column_create  {"name": "U08", "type": "Distillation", "stages": 20, "condenser_type": "Total_Condenser",
                      "top_pressure_Pa": 101325, "pressure_drop_Pa": 0}
dwsim_stream_add_material {"name": "D08"}
dwsim_stream_add_material {"name": "PRODUCT"}
dwsim_stream_add_energy   {"name": "QC-U08"}
dwsim_stream_add_energy   {"name": "QR-U08"}
dwsim_column_connect {"name": "U08", "feeds": [{"stream": "ORG", "stage": 8}],
                      "distillate": "D08", "bottoms": "PRODUCT", "condenser_duty": "QC-U08", "reboiler_duty": "QR-U08"}
dwsim_column_set_specs {"name": "U08",
                        "condenser": {"type": "Reflux_Ratio", "value": 1.5},
                        "reboiler":  {"type": "Product_Molar_Flow_Rate", "value": 0.6, "units": "mol/s"}}
```

**U11, the alcohol recovery column:**

```json
dwsim_column_create  {"name": "U11", "type": "Distillation", "stages": 20, "condenser_type": "Total_Condenser",
                      "top_pressure_Pa": 101325, "pressure_drop_Pa": 0}
dwsim_stream_add_material {"name": "D11"}
dwsim_stream_add_material {"name": "WATER"}
dwsim_stream_add_energy   {"name": "QC-U11"}
dwsim_stream_add_energy   {"name": "QR-U11"}
dwsim_column_connect {"name": "U11", "feeds": [{"stream": "AQ-PHASE", "stage": 10}],
                      "distillate": "D11", "bottoms": "WATER", "condenser_duty": "QC-U11", "reboiler_duty": "QR-U11"}
dwsim_column_set_specs {"name": "U11",
                        "condenser": {"type": "Reflux_Ratio", "value": 2.0},
                        "reboiler":  {"type": "Product_Molar_Flow_Rate", "value": 0.9, "units": "mol/s"}}
dwsim_solve_run {"timeout_s": 300}
dwsim_column_get {"name": "U08"}
dwsim_column_get {"name": "U11"}
```

Once both columns solve, change the reboiler specs to purities. **Do not use a 373.15 K bottoms
temperature for U11:** that is exactly the boiling point of pure water, so the spec is degenerate.

```json
dwsim_column_set_specs {"name": "U08", "reboiler": {"type": "Component_Fraction", "value": 0.995, "units": "Mass", "compound": "Ethyl acetate"}}
dwsim_column_set_specs {"name": "U11", "reboiler": {"type": "Component_Fraction", "value": 0.995, "units": "Mass", "compound": "Water"}}
dwsim_column_set_estimates {"name": "U08", "auto_update": true}
dwsim_column_set_estimates {"name": "U11", "auto_update": true}
dwsim_solve_run {"timeout_s": 300}
```

The U08 bottoms should come out at 350–351 K, matching the diagram. The U08 tops should sit close to
the D08-R guess, which is the ternary azeotrope at about 343.5 K. If they don't, update the guess
before closing that loop.

At this point every stream is connected and the four torn pairs are open:

| Real stream | Guess stream it will drive |
|---|---|
| REFLUX (SP-1 out 0) | REFLUX-R (U05 stage 1) |
| ACID-REC (U05 bottoms) | ACID-REC-R (M-2 in 1) |
| D08 (U08 distillate) | D08-R (U07 in 1) |
| D11 (U11 distillate) | ETOH-REC-R (M-1 in 2) |

Before closing each loop, copy the latest solved values of the real stream into its guess stream
with `dwsim_stream_set_conditions`. That one step does more for convergence than any tolerance
setting.

## Stage 5: close the loops, innermost first

Close one loop at a time, with a solve after each. Start with loose tolerances and tighten them
once everything is closed.

```json
// 1. Organic reflux: U05 → U06 → U07 → SP-1 → U05
dwsim_unitop_add        {"type": "Recycle", "name": "REC-REFLUX"}
dwsim_unitop_connect    {"unitop": "REC-REFLUX", "feed_stream": "REFLUX", "product_stream": "REFLUX-R"}
dwsim_recycle_configure {"name": "REC-REFLUX", "max_iterations": 100, "tolerances": {"mass_flow": 0.001, "temperature": 0.5}}
dwsim_solve_run         {"timeout_s": 600}

// 2. Acid return: U04 → U05 → M-2 → U04
dwsim_unitop_add        {"type": "Recycle", "name": "REC-ACID"}
dwsim_unitop_connect    {"unitop": "REC-ACID", "feed_stream": "ACID-REC", "product_stream": "ACID-REC-R"}
dwsim_recycle_configure {"name": "REC-ACID", "max_iterations": 100, "tolerances": {"mass_flow": 0.001, "temperature": 0.5}}
dwsim_solve_run         {"timeout_s": 600}

// 3. U08 tops back to the decanter: U07 → SP-1 → U08 → U07
dwsim_unitop_add        {"type": "Recycle", "name": "REC-D08"}
dwsim_unitop_connect    {"unitop": "REC-D08", "feed_stream": "D08", "product_stream": "D08-R"}
dwsim_recycle_configure {"name": "REC-D08", "max_iterations": 100, "tolerances": {"mass_flow": 0.001, "temperature": 0.5}}
dwsim_solve_run         {"timeout_s": 600}

// 4. Ethanol recycle: the whole plant
dwsim_unitop_add        {"type": "Recycle", "name": "REC-ETOH"}
dwsim_unitop_connect    {"unitop": "REC-ETOH", "feed_stream": "D11", "product_stream": "ETOH-REC-R"}
dwsim_recycle_configure {"name": "REC-ETOH", "max_iterations": 100, "tolerances": {"mass_flow": 0.001, "temperature": 0.5}}
dwsim_solve_run         {"timeout_s": 900}
```

After each solve, read the `dwsim_recycle_configure` response (it reports the last convergence
state) and `dwsim_solve_diagnostics`. If a loop oscillates, try a `smoothing_factor` of 0.5 or one of
the methods listed in the response's `acceleration_methods`. Once all four loops are closed, tighten
the tolerances (for example `mass_flow` 1e-5) and solve again.

**Check the overall balance.** At steady state:

- fresh acid + fresh ethanol in = PRODUCT + WATER + PURGE + DEC-V out, by mass;
- moles of ester in PRODUCT ≈ moles of acid fed − acid lost in PURGE.

If REC-ETOH will not converge and the ethanol recycle keeps growing, the fresh ethanol is more than
the plant consumes and loses. Lower `FEED-ETOH` with `dwsim_stream_set_conditions`, or let an Adjust
set it (stage 6).

## Stage 6: design specifications

**Purge rate.** The reactor temperature sets the purge. Hold the purge at the size you want, for
example 2% of the acid feed:

```json
dwsim_object_list_properties {"target": "U04", "type": "WR"}
dwsim_object_list_properties {"target": "PURGE", "type": "ALL"}
dwsim_adjust_create {"name": "ADJ-PURGE",
                     "manipulated": {"object": "U04",   "property": "<outlet temperature id>"},
                     "controlled":  {"object": "PURGE", "property": "<molar flow id>"},
                     "target_value": 0.02, "target_units": "mol/s", "tolerance": 0.001,
                     "min": 355, "max": 383}
dwsim_solve_run  {"timeout_s": 900}
dwsim_adjust_get {"name": "ADJ-PURGE"}
```

The Adjust has a hard cap of 25 iterations and dislikes phase changes. Use the stage 2 sweep to pick
a reachable target and tight bounds.

**Ethanol make-up (optional).** If you want a fixed ethanol recycle instead of a fixed fresh feed,
add a second Adjust. Manipulate the `FEED-ETOH` molar flow, and control the `D11` molar flow (or the
ethanol content of the U05 overhead). Two Adjusts inside four loops is ambitious, so add this one
only after ADJ-PURGE converges.

**Reflux ratio.** Explore R = 2–4 by sweeping `SP-1` SR1 from 0.67 to 0.80, watching the ester in
the U05 overhead and the U04 duty.

## Stage 7: save

```json
dwsim_flowsheet_save       {"filepath": "/data/ethyl-acetate-plant.dwxmz"}
dwsim_flowsheet_export_xml {"path": "/data/ethyl-acetate-plant.xml"}
```

## Simplifications to state in a report

- **Catalyst:** sulfuric acid is folded into the rate constants and does not appear in the
  balance. The purge stands in for heavy and catalyst removal.
- **Kinetics:** the stage 1 numbers are placeholders with K = 4. Replace them before quoting reactor
  volume, conversion or purge composition.
- **Vapour phase:** plain NRTL treats the vapour as ideal. Acetic acid dimerizes in the vapour, so
  compositions at the U05 top and in the acid return are approximate.
- **Hydraulics:** all units are at 101325 Pa with no pressure drop. A real reactor must sit slightly
  above the column bottom so the vapour can flow up.
- **Reactor + column:** U04 and U05 are a reactor with a column on top, not reactive distillation.
  The reaction happens only in U04, as in the diagram.
