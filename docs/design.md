# Design notes

What was found in the DWSIM 10 engine while building this server, so a maintainer does not have to
read it again. Paths are relative to `external/dwsim10`.

## The tool model

`tools/DWSIM.MCPServer/Tools/ToolRegistry.cs`: a tool is a public method carrying
`[McpTool(name, description)]` on an instance passed to `RegisterToolsFrom`. `[McpParam]` shapes
the JSON schema (`Required`, `JsonType`). Duplicate names throw at startup; there is no unregister,
so a name can only be taken over by not registering the upstream class (done for `ThermoTools`).
The registry invokes by reflection and the dispatcher unwraps `TargetInvocationException`;
`ArgumentException` becomes a JSON-RPC error, anything else an `isError` tool result.

Three upstream files are compiled from `src/DWSIM.MCP.Server/Shadow` instead of the submodule
(entry point, dispatcher, headless flowsheet) and one more (`Tools/UnitOps/UnitOpTools.cs`) is the
newer upstream master version, because v10.2.12's `dwsim_unitop_connect` passes energy port
indices raw. `tests/.../ShadowDriftTests.cs` holds the hashes of the upstream originals.

## Engine facts the tools rely on

- **Property packages.** `FlowsheetBase.CreateAndAddPropertyPackage` clones and *appends*; objects
  with no package bound fall back to the first one. Catalogue keys are the long names
  (`"Peng-Robinson (PR)"`). The flash algorithm is rebuilt on every call from
  `PropertyPackage.FlashCalculationApproach` and the `FlashSettings` dictionary;
  `FlashSetting.ForceEquilibriumCalculationType` takes `Default|VLE|VLLE|SVLE|SVLLE|NoFlash`
  (`FlashAlgorithms/UniversalFlash.vb`). The Vessel never decides one vs two liquids.
- **BIPs.** NRTL/UNIQUAC: `m_uni.InteractionParameters[c1][c2]` records with public fields
  (`A12`, `A21`, `alpha12`, `B12`, `B21`); cubic: field `m_pr` with `InteractionParameters[..][..].kij`;
  Wilson: `WilsonM.BIPs` keyed by CAS. `EstimateMissingInteractionParameters(verbose)` exists on
  the NRTL, UNIQUAC and Wilson packages and fills all-zero pairs with a UNIFAC regression.
- **Compounds.** `FlowsheetBase.AddCompound` only registers; every phase of every stream needs
  the key too (the desktop app's `SimulationSetupShared.cs` does this; so does `Tools/Common/Compounds.cs`).
  `CompoundEditing.CompoundEditor.ImportJsonCompound` imports a JSON compound and updates streams.
- **Reactions.** `Flowsheet.DefineKineticReaction/DefineHetCatReaction` (Fluent API). The order
  dictionaries must contain every stoichiometry key. Rate expressions see `T`, `R1..Rn`, `P1..Pn`
  in stoichiometry order. `ReactionHeat` is computed once at creation from the ideal-gas
  enthalpies of formation. Equilibrium: `KExprType` Gibbs/Expression/Constant, `ConstantKeqValue`.
- **Columns.** The rigorous column records its streams in `MaterialStreams`/`EnergyStreams`
  with an `AssociatedStage`; only its own `ConnectFeed(stream, stage)` etc. fill that table, so a
  generic connect leaves it unvalidated. `SetCondenserSpec/SetReboilerSpec` parse the spec type
  with `TryParse` and silently keep the default on a typo. `SetNumberOfStages(n)` needs n > 3.
  Stage flows after a solve are in `GetLastSolution().LiqMolarFlows/VapMolarFlows`, not on the
  `Stage` objects. Shortcut column keys are public fields (`m_lightkey`, ...). There is no reactive
  distillation: `RigorousColumn.vb` has no reaction hooks.
- **Separators.** `ComponentSeparator.ComponentSepSpecs` entries must be created
  (`new ComponentSeparationSpec(id, type, value, unit)`); a compound with no entry reaches *neither*
  outlet (it leaves the balance), so `dwsim_compsep_set_specs` writes a 0 % entry for every compound
  not named. The feed composition given to
  `dwsim_stream_add_material` is in mass fractions. Vessel and Filter have no `GetCalculationModes`,
  which is why upstream's `PropertySetter` throws on their mode.
- **Adjust.** Headless, only `SimultaneousAdjust = true` blocks run, inside
  `FlowsheetSolver2.SolveSimultaneousAdjustsAsync`: Newton with a damping that grows 0.2 per
  iteration, a hard cap of 25 iterations, manipulated variable in SI, controlled variable compared
  in the flowsheet's display units (`AdjustValue` is SI and converted). Targets across a phase
  change do not converge well. The Fluent builder's link helper `SpecialOpLinks` is internal;
  `Tools/Common/Links.cs` mirrors it.
- **Sweeps.** `LiveSliderStudy.Specifications/Results` list the sweepable ids;
  `LiveSliderStudy.Apply` solves through the global solver, so the sweep tool runs its own loop
  with the session's solver and reads values in SI.
- **Scripts.** `FlowsheetBase.RunScript(id)` runs IronPython headlessly with `Flowsheet`, `Solver`
  and every object by tag in scope; output goes through `ShowMessage`, which the shadow
  `McpFlowsheet` forwards to a per-instance sink. Verified by `FischerPlantTests.ScriptRunsHeadless`.
- **Recycle.** A new material stream, and so a fresh Recycle outlet, is 1 kg/s equimolar at 25 C until
  something writes it; a loop's first pass sees that unless the tear stream is seeded (the gold-plant
  scripts copy the open-loop solution into it).
- **Units.** A new headless flowsheet's `SelectedUnitSystem` is DWSIM's default (C, bar, kW), not
  SI. `GetPropertyValue(prop)` without a unit system returns SI. The tools state the units they take.
- **Compound identity.** The 10.2.12 databanks carry no InChI or InChIKey (the loader reads a SMILES for
  429 ChemSep compounds and nothing else), so a key cannot be matched server-side; `dwsim_thermo_resolve_compound`
  echoes it and decides by Hill formula (computed from `Elements`, because ChemSep stores structural formulas
  such as `CH3CH2OH` while the DWSIM databank stores Hill), name and CAS. `Automation3.AvailableCompounds`
  deduplicates by name only, so the same substance under two databases is grouped by CAS as an alias.
- **Heat exchanger.** Inlet i feeds outlet i; which inlet is hot is decided by temperature at solve time;
  the settings are CLR members (`CalculationMode`, `HotSideOutletTemperature`, `ColdSideOutletTemperature`,
  `Q`, `Area`, `OverallCoefficient`, `MITA`, `FlowDir`). A Heater and a Cooler tied by a Spec block (the
  heater's heat added copied into the cooler's heat removed) stand in for an exchanger whose two sides lie
  in different places of a loop.
- **Checks the engine does not make.** The compressor only warns on a liquid inlet, the equilibrium reactor
  never checks that its reaction phase exists at the outlet, and energy streams carry an unsigned
  `EnergyFlow` whose sign is the side they are attached to; `dwsim_plant_check` and `dwsim_plant_report`
  supply all three. `FlowsheetDiagnostics` lives in the read-only upstream engine, so the plant findings are
  rendered by the tool in the same `{code, severity, object, message, fix}` shape.
- **Calculation order budget.** `FlowsheetSolver.GetSolvingList` walks *upstream* from every terminal
  stream level by level and counts every object visit; above 10 000 visits it throws "Infinite loop detected
  while obtaining flowsheet object calculation order" even for a loop-free flowsheet. A merge point whose
  two inlets come from the same upstream object (a Mixer joining a reactor's vapour and liquid outlets)
  doubles the number of paths behind it, so seven such mixers in a 60-object chain exceed the budget. The
  open-run builder therefore takes a gas-phase reactor's vapour outlet directly and merges only liquid-phase
  reactors.

## Verification

`dotnet test` builds a Fischer esterification plant through the registry: NRTL + VLLE, BIP
estimation and write-back, an equilibrium reactor with a constant K, kinetic and heterogeneous
reactions, a shortcut column, a 12-stage rigorous column, a component separator, an adjust, a
sweep, a late compound, a JSON compound round trip, XML export, the reflection tools and a script.
`scripts/smoke.sh` exercises the HTTP transport of a running image.
