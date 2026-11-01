# openrun_sim: simulate the plant an open run produces

`python3 -m openrun_sim run plant.json` takes a plant in the contract of `OPEN_RUN_SIMULATION_BRIEF.md`
(process steps carrying one of the 58 vocabulary concepts, streams as Hill formulas, a balanced equation,
windows, chosen modules with offered ranges, connections, a target rate) and runs it on the DWSIM-MCP
server: concept to recipe, formula to databank name, equation to reaction model, windows to setpoints,
feeds from the target, loops closed one at a time, and a report with the provenance of every number.
Standard library only, Python 3.10+, run from this folder.

```sh
# server (109 tools) on 127.0.0.1:5901
python3 -m openrun_sim validate samples/*.plant.json                  # offline: contract, mapping, setpoints, feeds, refusals
python3 -m openrun_sim explain-concepts                                # the 58 concepts and what each becomes
DWSIM_SOLVE_TIMEOUT=600 python3 -m openrun_sim run samples/ammonia_haber_bosch_llm_opus.plant.json \
    --overrides samples/overrides/ammonia_haber_bosch_llm_opus.overrides.json --sweep
python3 -m openrun_sim run samples/etoac_fischer_llm_opus.plant.json \
    --overrides samples/overrides/etoac_fischer_llm_opus.overrides.json --open-loops auto
python3 -m unittest discover -s tests                                  # offline tests; OPENRUN_LIVE=1 adds tests/test_live.py (minutes)
```

Output per run in `out/<plant_id>/`: `report.md`, `report.json` (plant report, balance, checks, run
summary with assumptions, placeholders, surrogates, loops, provenance), `calls.json` (every tool call),
`<plant_id>.dwxmz`, and with `--sweep` `operating_range.json`.

## The input

`samples/*.plant.json` show the contract (`openrun_sim/contracts.py` is the strict loader: unknown keys are
refused). Per step: `unit_operation` (vocabulary concept), `equation` (Hill formulas, `;` separates several),
`t_window` / `p_window` as the document writes them (`"70 to 80 degC"`, `"25-40 bar"`), `slots` (stages,
feed_stage, reflux_ratio, top/bottom temperature, phase, ...), `streams_in` / `streams_out` with
components, role and the other end (`step: ""` = plant boundary, `"?"` = unknown and refused), `substances`
with name / CAS / InChIKey. `requirements` carry the closed `{min_si, max_si}` ranges (None = open, never
zero); `flowsheet.steps` the chosen module with `offered_ranges`; `flowsheet.connections` the ports.

Ports the recipes understand: vessels `vapor | liquid | liquid2` (aliases gas, organic, aqueous, light,
heavy, ...); columns `feed`, `reflux`, `distillate | top`, `bottoms | bottom`; absorbers `gas`, `liquid`,
`top`, `bottom`; exchangers `hot_in`, `cold_in`, `hot_out`, `cold_out`. Unnamed ports fall back to the
order given and the report says so.

`overrides` (inline or `--overrides file.json`) is what a person adds; every value from there is reported
with source `person`: `setpoints` (a value inside the window, needed when a bound is open), `reaction`
(`keq`, `conversion_pct`, `kinetics`, `feed_conversion`, with a `source` sentence), `feeds` (amount,
`excess`, `ratio_to` another feed, composition, T, p), `column` (stages, feed_stage, reflux_ratio,
condenser_type, pressure_drop_Pa, `reboiler_spec`, including `{"type": "feed_component_fraction", ...}`,
`solving_method` (`Wang-Henke`, `Naphtali-Sandholm`, `Burningham-Otto`), `max_iterations`,
`top_temperature_K` / `bottom_temperature_K` for the stage-temperature estimates), `splits` for the
specified-split stand-ins, `split_ratios`, `loops` (`close`, `leave_open`), `compounds` (formula to databank
name), `units` (free `dwsim_unitop_set` properties, `OutletVaporFraction`, `hot_outlet_T_K`,
`rigorous_extractor`), `surrogates` (`reactive_distillation`), `package`, `windows`.

## The rules it applies

- **Concepts** (`concepts.py`, brief section 2 plus the three it left open): the generic roots and the
  solid-phase units are refused with the reason; reaction concepts become Conversion / Equilibrium / CSTR /
  PFR reactors by the data; distillation a rigorous column; Decantation and Sedimentation a VLLE vessel;
  Absorption an absorber (one feed and `overrides.splits` → specified split); Stripping a reboiled absorber
  or an absorber with the stripping gas; Extraction, Adsorption, Drying, Pervaporation a specified split
  (`rigorous_extractor` builds the column); WasteHeatRecovery a two-sided HeatExchanger, or across a loop two
  halves tied by duty; Storage at the boundary is the feed or product stream; Recycling with two outlets is a
  splitter, with one a tear; ReactiveDistillation only as the flagged reactor + column surrogate.
- **Compounds** (`compounds.py`): `dwsim_thermo_resolve_compound` with formula, name, CAS; InChIKey → CAS
  through `data/inchikey_cas.json`; ambiguous, conflicting or unknown formulas refuse the run before a
  flowsheet exists. PR with a permanent gas, NRTL otherwise; a liquid-liquid recipe switches the flash to
  VLLE, estimates missing pairs and writes the regressed pairs of `data/lle_bips.json`.
- **Reactions** (`reactions.py`): override → stated conversion / K → Gibbs for a gas phase → labelled
  placeholder (`data/defaults.json`: K = 4 for liquid equilibrium chemistry, 98 % conversion for
  irreversible), reported as PLACEHOLDER at run start and in the report.
- **Setpoints** (`setpoints.py`): window = document ∩ requirement ∩ module; value = midpoint; an open side
  is refused unless an override gives a value inside; an empty intersection is refused with both windows
  quoted. Window checks go to `dwsim_plant_check`.
- **Feeds** (`feeds.py`): target kg/h → mol/s with the engine molar mass → educts by stoichiometry through
  the producing steps; utility streams need an amount; after the open-loop solve and again after the loops
  close, the derived feeds are scaled so the product meets the target.
- **Columns** (`builder.py`, `r_column`): the flowsheet is solved before each column and a failure there is a
  refusal naming the first unit that did not converge and its error (the column's feed is unsolved; a zero
  draw derived from it is never sent). The solver is the person's `solving_method`, else Naphtali-Sandholm when
  the feed is a liquid phase of a decanter (the bubble-point method does not converge on the water-saturated
  layer), else the server default Wang-Henke; a Wang-Henke column that does not converge is switched to
  Naphtali-Sandholm once and the stage solved again, reported as assumed. The bottoms draw from the component
  lists counts the components listed only in the bottoms plus every feed compound the lists do not mention that
  boils above all of them (normal boiling points from `dwsim_compound_get`); a compound without a boiling point is
  forced overhead and the assumption says so. With one end of the temperature profile stated, the other is the
  feed's bubble point (top) or the boiling point of the heaviest bottoms component (bottom), from a single-stream
  flash on a scratch flowsheet or the boiling points, reported as assumed.
- **Loops** (`recycles.py`): open-loop first; each tear seeded with `dwsim_recycle_seed`; closed one at a
  time (gas loops first); a loop that does not converge is reopened and reported with what leaves the plant.
  A tear whose open-loop source has no solved state (zero flow, NaN composition) is not closed at all; a reopened
  tear into a column is also removed from the column's own connection table (`SyncConnectedStreams`), without
  which the engine refuses the column with "streams specified in the column connections window is missing"; when
  the flowsheet does not solve after a reopen, the remaining loops are left open and the report says why.
- **Operating range** (`sweep.py`, `--sweep` or `python3 -m openrun_sim sweep ... --report out/<plant>/report.json`
  on a flowsheet still on the server): the reaction steps' closed windows (`options.sweep_steps` for others) are swept
  with `dwsim_study_sweep`, three points each from the midpoint outwards, every point a full plant solve with the loops
  closed (`options.sweep_timeout_s`, default 300 s); `operating_range.json` lists each point as solved or not with
  the engine's error, and the solved sub-range.
- **Report** (`report.py`): `dwsim_plant_report` + `dwsim_plant_balance` (open loops quantified) +
  `dwsim_plant_check` (windows), plus assumptions, placeholders, stand-ins and provenance.

## Results against the gold plants

| sample | gold result (`gold_plants/RESULTS.md`) | this builder |
|---|---|---|
| ammonia_haber_bosch_llm_opus | secondary reformer 950 °C, converter outlet 23 % NH3, loop closed, product −33 °C | 950 °C, 23.1 % NH3, loop closed, −36 °C at 1.1 bar; feeds scaled to 1000 kg/h; 0 check errors; balance closed |
| ammonia_haber_bosch_llm_opus, operating range | converter 400-500 °C | all three points solve with the loop closed: 1028 / 1006 / 972 kg/h NH3 at 400 / 450 / 500 °C; the primary-reformer window (750-850 °C) reaches the recycle's 100-iteration cap at every point within 300 s (the 3 % purge gives the inert build-up a long time constant), so `operating_range.json` lists those points as unsolved with the engine's message |
| etoac_fischer_llm_opus | column head 70.7 °C, drying column bottom 78.4 °C, wastewater 101.3 °C, acid recycle closed, three loops open | 70.7 / 78.1 / 101.3 °C, acid loop closed early with its column (`loops.close_early`), three loops open and quantified (156 + 97 + 26 kg/h leave through them), product 50 kg/h of the 300 asked at 85 wt% ester: the builder refuses to scale feeds six-fold into open loops and reports achieved / asked |

Live tests (`OPENRUN_LIVE=1 python3 -m unittest tests.test_live`) run both samples and the refusal sample against
these expectations (`samples/expected/*.summary.json`): Haber-Bosch solved with the loop closed, the target met and
the balance closed; Fischer solved with the acid loop closed, three loops open and quantified, balance closed, no
check errors (two column bottoms miss the document's windows by 0.1 and 0.3 K and are reported as near misses);
the refusal sample refused with the ambiguous formula, the open bound and the unmapped concept named.

## Known limits

InChIKey cannot be matched in the engine (no InChI in the 10.2 databanks). Solids stay refused. Reactive
distillation only as the flagged surrogate. Specified splits are not simulations. Nested loops around
azeotropic columns may stay open; they are then quantified, not hidden. The PFR / CSTR, stripper and
rigorous extractor recipes are implemented but not exercised by the samples.
