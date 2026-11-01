// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

namespace DWSIM.MCPServer.Rpc
{
    /// <summary>
    /// The part of the handshake instructions that describes the tools this server adds to the
    /// upstream set. The text is the one channel the server controls on the client's side, so it
    /// routes the workflows the upstream tools cannot do to the tools that can.
    /// </summary>
    public static class ExtensionInstructions
    {
        public const string Text =
            "\n\nEXTENSIONS (this server adds tools beyond the official DWSIM MCP server; prefer them where named):\n\n" +
            "PROPERTY PACKAGE AND FLASH: dwsim_thermo_set_property_package REPLACES the package in use and rebinds every " +
            "object (the official tool only appended one). Short names work: NRTL, UNIQUAC, PR, SRK, Raoult, SteamTables. " +
            "Two liquid phases (a decanter, a liquid-liquid extractor, ethyl acetate/water, ester/alcohol/water) appear only " +
            "when the flash looks for them: dwsim_thermo_set_flash equilibrium_type=VLLE, before solving. Activity models need " +
            "binary parameters for every pair: dwsim_thermo_get_bips lists the missing pairs, dwsim_thermo_estimate_bips fills " +
            "them with UNIFAC, dwsim_thermo_set_bips writes literature values. Compounds can be added at any time: " +
            "dwsim_thermo_add_compounds updates existing streams. User-defined compounds: dwsim_compound_export_json a similar " +
            "compound, edit, dwsim_compound_import_json(_inline).\n\n" +
            "REACTIONS: beyond dwsim_reaction_add_conversion/_equilibrium, use dwsim_reaction_add_kinetic (Arrhenius, for CSTR " +
            "and PFR), dwsim_reaction_add_hetcat (Langmuir-Hinshelwood), dwsim_reaction_set_equilibrium_constant (a literature " +
            "K as a number, or ln K(T)), dwsim_reaction_set_kinetics, dwsim_reactionset_add_reaction (second set for a side " +
            "reactor), dwsim_reaction_delete. Reactors take their set from ReactionSetID (dwsim_unitop_set) and their mode from " +
            "ReactorOperationMode: Isothermic, Adiabatic, OutletTemperature.\n\n" +
            "COLUMNS: a ShortcutColumn needs dwsim_column_shortcut_configure (keys, split, reflux, pressures) after " +
            "dwsim_unitop_add. A rigorous column is built with dwsim_column_create (type, stages, condenser, pressures), " +
            "wired with dwsim_column_connect (feeds WITH their stage, products, duties: never dwsim_unitop_connect, which leaves " +
            "the column unvalidated), specified with dwsim_column_set_specs (condenser Reflux_Ratio plus reboiler product flow " +
            "or purity is the robust pair), rescued with dwsim_column_set_estimates, inspected with dwsim_column_get. " +
            "Reactive distillation does not exist in DWSIM: dwsim_column_create refuses it; model reactor + column + Recycle " +
            "and say so.\n\n" +
            "SEPARATIONS WITHOUT A MODEL (pervaporation, adsorption, extraction, drying): ComponentSeparator with " +
            "dwsim_compsep_set_specs, and report the split as specified, not simulated. Solids (filter, solids separator) need " +
            "dwsim_thermo_set_forced_solids. dwsim_unitop_set_mode sets any unit's calculation mode, including the Vessel's and " +
            "the Filter's, where dwsim_unitop_set CalcMode fails. dwsim_vessel_configure sets a flash T/P override.\n\n" +
            "DESIGN QUESTIONS: dwsim_adjust_create makes the solver vary a specification until a result meets a target " +
            "(one solve instead of a sweep; simultaneous adjusts only, 25-iteration cap, give bounds). dwsim_spec_create ties " +
            "one specification to another by an expression. dwsim_recycle_configure sets a loop's tolerances. " +
            "dwsim_study_list_variables names the sweepable specifications and watchable results; dwsim_study_sweep runs a " +
            "1-D or 2-D grid and returns a table.\n\n" +
            "ESCAPE HATCH: dwsim_object_describe shows every public property, field and method of an object (streams, units, " +
            "'flowsheet', 'options', 'pp:<tag>', 'reaction:<name>', 'compound:<name>'); dwsim_object_get / dwsim_object_set " +
            "read and write by path (Specs[\"C\"].SpecValue, m_pr.InteractionParameters[\"Water\"][\"Ethanol\"].kij); " +
            "dwsim_object_invoke calls a method; dwsim_object_list_properties lists the PROP_* ids used by adjust, spec and " +
            "sweep. Prefer the typed tools; after any object_set run dwsim_flowsheet_check. dwsim_object_delete removes an object.\n\n" +
            "FILES: dwsim_flowsheet_export_xml writes the complete XML to a path (dwsim_flowsheet_get_xml truncates at 500000 " +
            "characters); dwsim_flowsheet_get_xml_section returns one section or one object's XML.\n\n" +
            "PLANTS FROM A DOCUMENT: dwsim_thermo_resolve_compound turns a formula + name + CAS into the one databank name " +
            "(an InChIKey is echoed only: the databanks carry no InChI; 'ambiguous' and 'conflict' are answers, ask for the " +
            "name or CAS rather than guessing). dwsim_reaction_from_equation builds a reaction from '2 Ethanol -> Ethyl " +
            "acetate + 2 Hydrogen' with the element balance checked and the model chosen from the data given (conversion_pct, " +
            "keq, lnkeq_expression, Arrhenius pairs; nothing given and phase Vapor -> Gibbs; nothing given and Liquid is " +
            "refused: pass a labelled placeholder yourself). dwsim_hx_configure sets a HeatExchanger by what is specified " +
            "(specify_hot_outlet, specify_cold_outlet, specify_duty, specify_ua, pinch) and refuses a temperature cross; " +
            "dwsim_hx_tie_halves builds the heater + cooler + Spec surrogate for an exchanger whose sides lie across a loop. " +
            "dwsim_recycle_seed copies the open-loop solution into a Recycle outlet before the loop closes. After the solve: " +
            "dwsim_plant_report (every stream, every duty with its sign, product vs asked), dwsim_plant_balance (mass, " +
            "per-compound and per-element closure; open_loops quantifies loops left open), dwsim_plant_check (pump fed vapour, " +
            "compressor fed liquid, reactor outlet phase, caller windows, unconverged units and recycles). A Mixer that joins " +
            "both outlets of a reactor doubles the solver's calculation-order paths; use the vapour outlet of a gas-phase reactor.";
    }
}
