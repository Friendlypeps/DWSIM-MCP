// Copyright 2026 friendlypeps and contributors. The validation mirrors the private checks in DWSIM 10's
// tools/DWSIM.MCPServer/Tools/Reactions/ReactionTools.cs, copyright 2008-2026 Daniel Wagner Oliveira de
// Medeiros and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums;
using DWSIM.MCPServer.Sessions;
using DWSIM.MCPServer.Tools.Common;
using DWSIM.UnitOperations.Reactors;
using FluentFlowsheet = DWSIM.Automation.FluentAPI.Flowsheet;

namespace DWSIM.MCPServer.Tools.Reactions
{
    /// <summary>
    /// Reaction tools beyond the upstream set (conversion, equilibrium with ln K(T), reaction sets,
    /// Gibbs compounds): kinetic and heterogeneous catalytic reactions, the equilibrium constant as a
    /// number, editing the kinetics of an existing reaction, set membership, deletion, and a custom
    /// Gibbs element matrix.
    /// </summary>
    public class ReactionToolsEx
    {
        private readonly SessionManager _sessions;

        private static readonly string[] Phases = { "Mixture", "Vapor", "Liquid", "Solid" };

        private static readonly string[] Bases =
        {
            "activity", "fugacity", "partial pressure", "molar concentration",
            "mass concentration", "molar fraction", "mass fraction"
        };

        public ReactionToolsEx(SessionManager sessions) { _sessions = sessions; }

        [McpTool("dwsim_reaction_add_kinetic",
            "Define a kinetic (power-law, Arrhenius) reaction for a CSTR or PFR and put it in a reaction set. " +
            "Stoichiometry is {compound: coefficient}, reactants negative. Rate r = kf*prod(C_i^direct_order) - " +
            "kr*prod(C_i^reverse_order) with k = A*exp(-E/(R*T)); the default orders are the reactant " +
            "coefficients forward and the product coefficients reverse (a_reverse 0 makes it irreversible). " +
            "amount_units are the concentration units of the C_i (mol/m3, kmol/m3, mol/L, mol/cm3; partial " +
            "pressure in Pa, atm, bar when basis is 'partial pressure'), rate_units the units of r (mol/[m3.s], " +
            "kmol/[m3.h], mol/[L.s], mol/[kgcat.s] ...). e_forward_unit is J/mol, kJ/mol, cal/mol, kcal/mol " +
            "or BTU/lbmol. Instead of Arrhenius, forward_expression/reverse_expression give k as an expression " +
            "in T (K). A PFR or CSTR runs every kinetic and heterogeneous reaction of its set.")]
        public JObject AddKinetic(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Reaction name, unique in the flowsheet")] string name,
            [McpParam("Stoichiometry as {compound: coefficient}; reactants negative", JsonType = "object")] JObject stoichiometry,
            [McpParam("Base compound (a reactant); the rate refers to it")] string base_compound,
            [McpParam("Forward pre-exponential factor A, in rate_units over amount_units^order", JsonType = "number")] double a_forward,
            [McpParam("Forward activation energy E, in e_forward_unit", JsonType = "number")] double e_forward,
            [McpParam("Reverse pre-exponential factor (default 0: irreversible)", Required = false, JsonType = "number")] double a_reverse = 0,
            [McpParam("Reverse activation energy (default 0)", Required = false, JsonType = "number")] double e_reverse = 0,
            [McpParam("Unit of the activation energies: J/mol (default), kJ/mol, cal/mol, kcal/mol, BTU/lbmol", Required = false)] string e_forward_unit = "J/mol",
            [McpParam("Forward reaction orders as {compound: order}; default: |coefficient| for reactants, 0 for products", Required = false, JsonType = "object")] JObject direct_orders = null,
            [McpParam("Reverse reaction orders as {compound: order}; default: coefficient for products, 0 for reactants", Required = false, JsonType = "object")] JObject reverse_orders = null,
            [McpParam("Reacting phase: Mixture, Vapor, Liquid or Solid (default Liquid)", Required = false)] string phase = "Liquid",
            [McpParam("Concentration basis: molar concentration (default), mass concentration, molar fraction, mass fraction, partial pressure, activity, fugacity", Required = false)] string basis = "molar concentration",
            [McpParam("Units of the concentrations (default mol/m3)", Required = false)] string amount_units = "mol/m3",
            [McpParam("Units of the rate (default mol/[m3.s])", Required = false)] string rate_units = "mol/[m3.s]",
            [McpParam("Forward rate constant as an expression in T, instead of Arrhenius", Required = false)] string forward_expression = "",
            [McpParam("Reverse rate constant as an expression in T, instead of Arrhenius", Required = false)] string reverse_expression = "",
            [McpParam("Lowest temperature (K) at which the reaction runs; the rate is zero below (default 0)", Required = false, JsonType = "number")] double? tmin_K = null,
            [McpParam("Highest temperature (K) at which the reaction runs (default 2000)", Required = false, JsonType = "number")] double? tmax_K = null,
            [McpParam("Reaction set id (default DefaultSet; created if missing)", Required = false)] string reaction_set = "DefaultSet",
            [McpParam("Order in the set (default 0)", Required = false)] int rank = 0,
            [McpParam("Description", Required = false)] string description = "")
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var stoich = Validate(fs, name, stoichiometry, base_compound, phase);
            var b = Basis(basis);

            var fwd = Orders(stoich, direct_orders, forward: true);
            var rev = Orders(stoich, reverse_orders, forward: false);
            if (a_forward <= 0 && string.IsNullOrWhiteSpace(forward_expression))
                throw new ArgumentException("a_forward must be positive, or give forward_expression.");
            if (a_reverse < 0) throw new ArgumentException("a_reverse cannot be negative.");
            var eUnit = EnergyUnit(e_forward_unit);
            CheckRange(tmin_K, tmax_K);

            var r = fs.DefineKineticReaction(name, stoich, fwd, rev, base_compound, Canonical(phase), b,
                amount_units ?? "mol/m3", rate_units ?? "mol/[m3.s]", a_forward, e_forward, a_reverse, e_reverse,
                forward_expression ?? "", reverse_expression ?? "", description ?? "");
            r.E_Forward_Unit = eUnit;
            r.E_Reverse_Unit = eUnit;
            if (!string.IsNullOrWhiteSpace(forward_expression)) { r.ReactionKinFwdType = ReactionKineticType.UserDefined; r.ReactionKinFwdExpression = forward_expression; }
            if (!string.IsNullOrWhiteSpace(reverse_expression)) { r.ReactionKinRevType = ReactionKineticType.UserDefined; r.ReactionKinRevExpression = reverse_expression; }
            if (tmin_K.HasValue) r.Tmin = tmin_K.Value;
            if (tmax_K.HasValue) r.Tmax = tmax_K.Value;

            return AddToSet(fs, r, reaction_set, rank);
        }

        [McpTool("dwsim_reaction_add_hetcat",
            "Define a heterogeneous catalytic reaction (Langmuir-Hinshelwood / Hougen-Watson form) for a CSTR or " +
            "PFR: r = numerator / denominator, both expressions in T (K) and in the concentrations R1..Rn of the " +
            "reactants and P1..Pn of the products, numbered in stoichiometry order (e.g. numerator " +
            "'2.5e4*exp(-6000/T)*R1*R2', denominator '(1+0.8*R1+1.2*P1)^2'). The reactor needs a catalyst amount " +
            "(CSTR CatalystAmount, PFR CatalystLoading) when rate_units are per kg of catalyst.")]
        public JObject AddHetCat(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Reaction name, unique in the flowsheet")] string name,
            [McpParam("Stoichiometry as {compound: coefficient}; reactants negative", JsonType = "object")] JObject stoichiometry,
            [McpParam("Base compound (a reactant)")] string base_compound,
            [McpParam("Rate numerator, an expression in T, R1..Rn, P1..Pn")] string numerator_expression,
            [McpParam("Rate denominator, an expression in T, R1..Rn, P1..Pn (use 1 for none)")] string denominator_expression,
            [McpParam("Reacting phase (default Vapor)", Required = false)] string phase = "Vapor",
            [McpParam("Concentration basis (default partial pressure)", Required = false)] string basis = "partial pressure",
            [McpParam("Units of the concentrations (default Pa)", Required = false)] string amount_units = "Pa",
            [McpParam("Units of the rate (default mol/[kgcat.s])", Required = false)] string rate_units = "mol/[kgcat.s]",
            [McpParam("Reaction set id (default DefaultSet)", Required = false)] string reaction_set = "DefaultSet",
            [McpParam("Order in the set (default 0)", Required = false)] int rank = 0,
            [McpParam("Description", Required = false)] string description = "")
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var stoich = Validate(fs, name, stoichiometry, base_compound, phase);
            var b = Basis(basis);
            if (string.IsNullOrWhiteSpace(numerator_expression)) throw new ArgumentException("numerator_expression is required.");
            if (string.IsNullOrWhiteSpace(denominator_expression)) denominator_expression = "1";

            var r = fs.DefineHetCatReaction(name, stoich, base_compound, Canonical(phase), b, amount_units ?? "Pa",
                rate_units ?? "mol/[kgcat.s]", numerator_expression, denominator_expression, description ?? "");
            var result = AddToSet(fs, r, reaction_set, rank);
            result["rate_variables"] = RateVariables(r);
            return result;
        }

        [McpTool("dwsim_reaction_set_equilibrium_constant",
            "Set how an equilibrium reaction gets its constant: mode 'gibbs' (from the Gibbs energies of formation, " +
            "the default), 'expression' (ln(Keq) as an expression in T, e.g. '-5.2 + 2900/T'), or 'constant' " +
            "(a fixed Keq, the only way to use a literature K at one temperature). Optionally the temperature " +
            "range [tmin_K, tmax_K] outside which the reaction is off, and the approach to equilibrium in K.")]
        public JObject SetEquilibriumConstant(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Reaction name")] string name,
            [McpParam("gibbs, expression or constant")] string mode,
            [McpParam("Keq for mode constant (> 0, in the reaction's basis units)", Required = false, JsonType = "number")] double? keq = null,
            [McpParam("ln(Keq) as an expression in T for mode expression", Required = false)] string lnkeq_expression = null,
            [McpParam("Lowest temperature (K) at which the reaction runs", Required = false, JsonType = "number")] double? tmin_K = null,
            [McpParam("Highest temperature (K) at which the reaction runs", Required = false, JsonType = "number")] double? tmax_K = null,
            [McpParam("Temperature approach to equilibrium in K (0 = full equilibrium)", Required = false, JsonType = "number")] double? approach_K = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var r = Reaction(fs, name);
            if (r.ReactionType != ReactionType.Equilibrium)
                throw new ArgumentException($"'{name}' is a {r.ReactionType} reaction; the equilibrium constant applies to Equilibrium reactions.");

            switch ((mode ?? "").Trim().ToLowerInvariant())
            {
                case "gibbs":
                    r.KExprType = KOpt.Gibbs;
                    break;
                case "expression":
                    if (string.IsNullOrWhiteSpace(lnkeq_expression)) throw new ArgumentException("mode expression needs lnkeq_expression.");
                    r.KExprType = KOpt.Expression;
                    r.Expression = lnkeq_expression;
                    break;
                case "constant":
                    if (!keq.HasValue || keq.Value <= 0) throw new ArgumentException("mode constant needs keq > 0.");
                    r.KExprType = KOpt.Constant;
                    r.ConstantKeqValue = keq.Value;
                    break;
                default:
                    throw new ArgumentException($"'{mode}' is not a mode. Use gibbs, expression or constant.");
            }
            CheckRange(tmin_K, tmax_K);
            if (tmin_K.HasValue) r.Tmin = tmin_K.Value;
            if (tmax_K.HasValue) r.Tmax = tmax_K.Value;
            if (approach_K.HasValue) r.Approach = approach_K.Value;
            fs.Inner.ResetCalculationStatus();
            return Describe(r);
        }

        [McpTool("dwsim_reaction_set_kinetics",
            "Edit the kinetics of an existing kinetic or heterogeneous catalytic reaction: Arrhenius A and E (with " +
            "their unit), the reaction orders, rate-constant expressions, the heterogeneous numerator and " +
            "denominator, the units, or the temperature range. Only the fields given are changed.")]
        public JObject SetKinetics(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Reaction name")] string name,
            [McpParam("Forward pre-exponential factor", Required = false, JsonType = "number")] double? a_forward = null,
            [McpParam("Forward activation energy", Required = false, JsonType = "number")] double? e_forward = null,
            [McpParam("Reverse pre-exponential factor", Required = false, JsonType = "number")] double? a_reverse = null,
            [McpParam("Reverse activation energy", Required = false, JsonType = "number")] double? e_reverse = null,
            [McpParam("Unit of the activation energies: J/mol, kJ/mol, cal/mol, kcal/mol, BTU/lbmol", Required = false)] string e_unit = null,
            [McpParam("Forward orders as {compound: order}", Required = false, JsonType = "object")] JObject direct_orders = null,
            [McpParam("Reverse orders as {compound: order}", Required = false, JsonType = "object")] JObject reverse_orders = null,
            [McpParam("Forward rate constant as an expression in T; empty string returns to Arrhenius", Required = false)] string forward_expression = null,
            [McpParam("Reverse rate constant as an expression in T; empty string returns to Arrhenius", Required = false)] string reverse_expression = null,
            [McpParam("Heterogeneous rate numerator", Required = false)] string numerator_expression = null,
            [McpParam("Heterogeneous rate denominator", Required = false)] string denominator_expression = null,
            [McpParam("Units of the concentrations", Required = false)] string amount_units = null,
            [McpParam("Units of the rate", Required = false)] string rate_units = null,
            [McpParam("Lowest temperature (K)", Required = false, JsonType = "number")] double? tmin_K = null,
            [McpParam("Highest temperature (K)", Required = false, JsonType = "number")] double? tmax_K = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var r = Reaction(fs, name);
            if (r.ReactionType != ReactionType.Kinetic && r.ReactionType != ReactionType.Heterogeneous_Catalytic)
                throw new ArgumentException($"'{name}' is a {r.ReactionType} reaction; kinetics apply to Kinetic and Heterogeneous_Catalytic reactions.");

            var changed = new JArray();
            void Set(string what, Action a) { a(); changed.Add(what); }
            if (a_forward.HasValue) Set("A_Forward", () => r.A_Forward = a_forward.Value);
            if (e_forward.HasValue) Set("E_Forward", () => r.E_Forward = e_forward.Value);
            if (a_reverse.HasValue) Set("A_Reverse", () => r.A_Reverse = a_reverse.Value);
            if (e_reverse.HasValue) Set("E_Reverse", () => r.E_Reverse = e_reverse.Value);
            if (e_unit != null) Set("E_Unit", () => { var u = EnergyUnit(e_unit); r.E_Forward_Unit = u; r.E_Reverse_Unit = u; });
            if (direct_orders != null) Set("DirectOrder", () => ApplyOrders(fs, r, direct_orders, forward: true));
            if (reverse_orders != null) Set("ReverseOrder", () => ApplyOrders(fs, r, reverse_orders, forward: false));
            if (forward_expression != null) Set("ReactionKinFwdExpression", () =>
            {
                r.ReactionKinFwdExpression = forward_expression;
                r.ReactionKinFwdType = string.IsNullOrWhiteSpace(forward_expression) ? ReactionKineticType.Arrhenius : ReactionKineticType.UserDefined;
            });
            if (reverse_expression != null) Set("ReactionKinRevExpression", () =>
            {
                r.ReactionKinRevExpression = reverse_expression;
                r.ReactionKinRevType = string.IsNullOrWhiteSpace(reverse_expression) ? ReactionKineticType.Arrhenius : ReactionKineticType.UserDefined;
            });
            if (numerator_expression != null) Set("RateEquationNumerator", () => r.RateEquationNumerator = numerator_expression);
            if (denominator_expression != null) Set("RateEquationDenominator", () => r.RateEquationDenominator = denominator_expression);
            if (amount_units != null) Set("ConcUnit", () => r.ConcUnit = amount_units);
            if (rate_units != null) Set("VelUnit", () => r.VelUnit = rate_units);
            CheckRange(tmin_K ?? r.Tmin, tmax_K ?? r.Tmax);
            if (tmin_K.HasValue) Set("Tmin", () => r.Tmin = tmin_K.Value);
            if (tmax_K.HasValue) Set("Tmax", () => r.Tmax = tmax_K.Value);

            fs.Inner.ResetCalculationStatus();
            var o = Describe(r);
            o["changed"] = changed;
            return o;
        }

        [McpTool("dwsim_reactionset_add_reaction",
            "Put an existing reaction into a reaction set (created if missing), or change its rank or enable/disable " +
            "it there. A reactor runs the active reactions of the set its ReactionSetID names, by rank. " +
            "Use this to move a side reaction to a second set that only one reactor uses.")]
        public JObject AddToSetTool(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Reaction set id")] string reaction_set,
            [McpParam("Reaction name")] string reaction,
            [McpParam("Rank in the set (default 0; same rank runs in parallel, higher after)", Required = false)] int rank = 0,
            [McpParam("Active in the set (default true)", Required = false, JsonType = "boolean")] bool active = true)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var r = Reaction(fs, reaction);
            if (string.IsNullOrWhiteSpace(reaction_set)) throw new ArgumentException("Name the reaction set.");
            var set = fs.ReactionSet(reaction_set);
            var inner = fs.Inner.ReactionSets[reaction_set];
            if (inner.Reactions.TryGetValue(r.ID, out var member))
            {
                member.Rank = rank;
                member.IsActive = active;
            }
            else
            {
                set.Add(r, rank, active);
            }
            fs.Inner.ResetCalculationStatus();
            return new JObject
            {
                ["reaction_set"] = reaction_set,
                ["reactions"] = new JArray(inner.Reactions.Values.Select(m => new JObject
                {
                    ["reaction"] = fs.Inner.Reactions.TryGetValue(m.ReactionID, out var rr) ? rr.Name : m.ReactionID,
                    ["rank"] = m.Rank, ["active"] = m.IsActive
                })),
                ["reactors_using_set"] = Json.Strings(fs.Inner.SimulationObjects.Values.OfType<Reactor>()
                    .Where(x => x.ReactionSetID == reaction_set && x.GraphicObject != null).Select(x => x.GraphicObject.Tag))
            };
        }

        [McpTool("dwsim_reaction_delete",
            "Delete a reaction from the flowsheet and from every reaction set that lists it.")]
        public JObject Delete(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Reaction name")] string name)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var r = Reaction(fs, name);
            var sets = new JArray();
            foreach (var set in fs.Inner.ReactionSets.Values)
                if (set.Reactions.Remove(r.ID)) sets.Add(set.ID);
            var key = fs.Inner.Reactions.FirstOrDefault(kv => ReferenceEquals(kv.Value, r)).Key ?? r.ID;
            fs.Inner.Reactions.Remove(key);
            fs.Inner.ResetCalculationStatus();
            return new JObject { ["deleted"] = r.Name, ["removed_from_sets"] = sets, ["remaining"] = Json.Strings(fs.Inner.Reactions.Values.Select(x => x.Name)) };
        }

        [McpTool("dwsim_reactor_set_element_matrix",
            "Give a Gibbs reactor an explicit element matrix instead of the one dwsim_reactor_set_compounds builds " +
            "from the compound formulas: elements is the list of conserved species (atoms, or charge, or a " +
            "pseudo-element), matrix[e][c] the amount of element e in one mole of compound c, in the order of the " +
            "reactor's compounds (dwsim_reactor_set_compounds first). total_elements[e] is the feed inventory of " +
            "each element in mol/s; when omitted it is computed from the connected feed and the matrix.")]
        public JObject SetElementMatrix(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Gibbs reactor tag")] string name,
            [McpParam("Element names, one per matrix row", JsonType = "array")] string[] elements,
            [McpParam("Matrix as [[per element: amount in each compound]], rows = elements, columns = reactor compounds", JsonType = "array")] JArray matrix,
            [McpParam("Total of each element entering with the feed, mol/s", Required = false, JsonType = "array")] double[] total_elements = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var gibbs = Find.As<Reactor_Gibbs>(fs, name, "Gibbs reactor");
            var comps = gibbs.ComponentIDs ?? new List<string>();
            if (comps.Count == 0) throw new ArgumentException($"Set the compounds of '{name}' first with dwsim_reactor_set_compounds.");
            if (elements == null || elements.Length == 0) throw new ArgumentException("List the elements.");
            if (matrix == null || matrix.Count != elements.Length)
                throw new ArgumentException($"matrix needs one row per element ({elements.Length}).");

            var m = new double[elements.Length, comps.Count];
            for (int e = 0; e < elements.Length; e++)
            {
                var row = matrix[e] as JArray ?? throw new ArgumentException($"Row {e} ('{elements[e]}') must be an array.");
                if (row.Count != comps.Count)
                    throw new ArgumentException($"Row '{elements[e]}' has {row.Count} values; the reactor has {comps.Count} compounds: {string.Join(", ", comps)}.");
                for (int c = 0; c < comps.Count; c++) m[e, c] = row[c].Value<double>();
            }

            double[] totals;
            if (total_elements != null)
            {
                if (total_elements.Length != elements.Length) throw new ArgumentException("total_elements needs one value per element.");
                totals = total_elements;
            }
            else
            {
                var feed = gibbs.GraphicObject.InputConnectors[0];
                if (!feed.IsAttached) throw new ArgumentException($"Connect the feed of '{name}' first, or give total_elements.");
                var ms = (IMaterialStream)fs.Inner.SimulationObjects[feed.AttachedConnector.AttachedFrom.Name];
                totals = new double[elements.Length];
                for (int c = 0; c < comps.Count; c++)
                {
                    var comp = ms.Phases[0].Compounds[comps[c]];
                    var n = comp.MolarFlow.GetValueOrDefault();
                    for (int e = 0; e < elements.Length; e++) totals[e] += m[e, c] * n;
                }
            }

            gibbs.Elements = elements;
            gibbs.ElementMatrix = m;
            gibbs.TotalElements = totals;
            gibbs.InitialEstimates = new List<double>();
            gibbs.PreviousSolution = new List<double>();
            fs.Inner.ResetCalculationStatus();
            return new JObject
            {
                ["reactor"] = name,
                ["compounds"] = Json.Strings(comps),
                ["elements"] = Json.Strings(elements),
                ["total_elements_mol_s"] = new JArray(totals.Select(Json.Num))
            };
        }

        // ------------------------------------------------------------------ helpers

        [McpTool("dwsim_reaction_from_equation",
            "Define a reaction from a balanced equation written with databank names, e.g. '2 Ethanol -> Ethyl acetate + " +
            "2 Hydrogen' or 'Acetic acid + Ethanol <-> Ethyl acetate + Water', and the data the document states: " +
            "conversion_pct -> conversion reaction; keq (with keq_basis) or lnkeq_expression -> equilibrium; a_forward and " +
            "e_forward (optionally reverse) -> kinetic; nothing stated and phase Vapor -> equilibrium with K from the " +
            "Gibbs energies of formation; nothing stated and phase Liquid -> refused (the caller must pass a labelled " +
            "placeholder; this tool never invents a number). The phase must be given. Compounds must already be in the " +
            "flowsheet. Returns the reaction and model_chosen.")]
        public JObject FromEquation(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Reaction name, unique in the flowsheet")] string name,
            [McpParam("Equation with databank names; arrows -> => <-> <=> = are accepted")] string equation,
            [McpParam("Reacting phase: Liquid or Vapor (Mixture allowed for conversion)")] string phase,
            [McpParam("Base compound (default: the first reactant)", Required = false)] string base_compound = null,
            [McpParam("Conversion of the base compound in % (conversion reaction)", Required = false, JsonType = "number")] double? conversion_pct = null,
            [McpParam("Constant equilibrium constant (equilibrium reaction)", Required = false, JsonType = "number")] double? keq = null,
            [McpParam("Basis of keq: molar fraction (default), activity, fugacity, partial pressure, molar concentration, mass concentration, mass fraction", Required = false)] string keq_basis = null,
            [McpParam("ln(Keq) as an expression in T (K)", Required = false)] string lnkeq_expression = null,
            [McpParam("Arrhenius pre-exponential factor, forward", Required = false, JsonType = "number")] double? a_forward = null,
            [McpParam("Activation energy, forward (e_forward_unit, default J/mol)", Required = false, JsonType = "number")] double? e_forward = null,
            [McpParam("Energy unit: J/mol, kJ/mol, cal/mol, kcal/mol, BTU/lbmol", Required = false)] string e_forward_unit = null,
            [McpParam("Arrhenius pre-exponential factor, reverse", Required = false, JsonType = "number")] double? a_reverse = null,
            [McpParam("Activation energy, reverse", Required = false, JsonType = "number")] double? e_reverse = null,
            [McpParam("Reaction set id (default DefaultSet; created if missing)", Required = false)] string reaction_set = "DefaultSet",
            [McpParam("Lower temperature limit, K", Required = false, JsonType = "number")] double? tmin_K = null,
            [McpParam("Upper temperature limit, K", Required = false, JsonType = "number")] double? tmax_K = null,
            [McpParam("Description", Required = false)] string description = "")
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            if (string.IsNullOrWhiteSpace(equation)) throw new ArgumentException("Give the equation.");
            if (Canonical(phase) == null || string.IsNullOrWhiteSpace(phase))
                throw new ArgumentException("Give the reacting phase explicitly: Liquid, Vapor (or Mixture for a conversion reaction).");

            var parsed = ParseEquation(fs, equation);
            var stoichJson = new JObject(parsed.Select(kv => new JProperty(kv.Key, kv.Value)));
            var firstReactant = parsed.First(kv => kv.Value < 0).Key;
            var baseC = string.IsNullOrWhiteSpace(base_compound) ? firstReactant : Find.Compound(fs, base_compound).Name;
            var stoich = Validate(fs, name, stoichJson, baseC, phase);

            string model;
            IReaction r;
            bool kinetic = a_forward.HasValue && e_forward.HasValue;
            if (conversion_pct.HasValue)
            {
                if (conversion_pct <= 0 || conversion_pct > 100) throw new ArgumentException("conversion_pct is between 0 and 100.");
                r = fs.DefineConversionReaction(name, stoich, baseC, Canonical(phase), conversion_pct.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), description ?? "");
                model = "conversion";
            }
            else if (kinetic)
            {
                var b = Basis(null);
                r = fs.DefineKineticReaction(name, stoich, Orders(stoich, null, true), Orders(stoich, null, false), baseC, Canonical(phase), b, "mol/m3", "mol/[m3.s]",
                                             a_forward.Value, e_forward.Value, a_reverse ?? 0.0, e_reverse ?? 0.0, "", "", description ?? "");
                r.E_Forward_Unit = EnergyUnit(e_forward_unit); r.E_Reverse_Unit = EnergyUnit(e_forward_unit);
                model = "kinetic";
            }
            else if (keq.HasValue || !string.IsNullOrWhiteSpace(lnkeq_expression))
            {
                var b = string.IsNullOrWhiteSpace(keq_basis) ? "molar fraction" : keq_basis.Trim().ToLowerInvariant();
                if (!Bases.Contains(b)) throw new ArgumentException($"'{keq_basis}' is not an equilibrium basis. Use one of: {string.Join(", ", Bases)}.");
                r = fs.DefineEquilibriumReaction(name, stoich, baseC, Canonical(phase), b, "", lnkeq_expression ?? "", 0.0, description ?? "");
                if (keq.HasValue)
                {
                    if (keq.Value <= 0) throw new ArgumentException("keq must be positive.");
                    r.KExprType = KOpt.Constant; r.ConstantKeqValue = keq.Value; model = "equilibrium_constant";
                }
                else { r.KExprType = KOpt.Expression; model = "equilibrium_expression"; }
            }
            else if (string.Equals(Canonical(phase), "Vapor", StringComparison.OrdinalIgnoreCase))
            {
                r = fs.DefineEquilibriumReaction(name, stoich, baseC, Canonical(phase), "fugacity", "", "", 0.0, description ?? "");
                r.KExprType = KOpt.Gibbs; model = "equilibrium_gibbs";
            }
            else
            {
                throw new ArgumentException("No conversion, equilibrium constant or kinetics were given and the phase is not Vapor: a Gibbs-energy K is " +
                                            "only sound for the gas phase. Pass the document's conversion or K, or a labelled placeholder from the caller.");
            }
            if (tmin_K.HasValue || tmax_K.HasValue) { CheckRange(tmin_K, tmax_K); if (tmin_K.HasValue) r.Tmin = tmin_K.Value; if (tmax_K.HasValue) r.Tmax = tmax_K.Value; }

            var o = AddToSet(fs, r, string.IsNullOrWhiteSpace(reaction_set) ? "DefaultSet" : reaction_set, 0);
            o["model_chosen"] = model;
            o["equation_parsed"] = new JObject
            {
                ["reactants"] = new JObject(parsed.Where(kv => kv.Value < 0).Select(kv => new JProperty(kv.Key, -kv.Value))),
                ["products"] = new JObject(parsed.Where(kv => kv.Value > 0).Select(kv => new JProperty(kv.Key, kv.Value))),
            };
            o["placeholder"] = false;
            return o;
        }

        /// <summary>"2 A + B -> C + 3 D" with databank names into {name: coefficient}, reactants negative.</summary>
        private static Dictionary<string, double> ParseEquation(FluentFlowsheet fs, string equation)
        {
            var eq = equation.Replace("\u2192", "->").Replace("\u21cc", "<->").Replace("\u21c4", "<->");
            string[] arrows = { "<->", "<=>", "->", "=>", "→", "⇌", "=" };
            string arrow = arrows.FirstOrDefault(a => eq.Contains(a));
            if (arrow == null) throw new ArgumentException("The equation needs an arrow between reactants and products (->, <->, =).");
            var sides = eq.Split(new[] { arrow }, 2, StringSplitOptions.None);
            var result = new Dictionary<string, double>();
            void Side(string text, double sign)
            {
                foreach (var raw in System.Text.RegularExpressions.Regex.Split(text, @"\s\+\s|^\+\s|\s\+$"))
                {
                    var term = raw.Trim();
                    if (term.Length == 0) continue;
                    var m = System.Text.RegularExpressions.Regex.Match(term, @"^(?<c>\d+(\.\d+)?)\s+(?<n>.+)$");
                    double coeff = 1; string nm = term;
                    if (m.Success) { coeff = double.Parse(m.Groups["c"].Value, System.Globalization.CultureInfo.InvariantCulture); nm = m.Groups["n"].Value.Trim(); }
                    var cp = Find.Compound(fs, nm);
                    if (result.ContainsKey(cp.Name) && Math.Sign(result[cp.Name]) != Math.Sign(sign))
                        throw new ArgumentException($"'{cp.Name}' appears on both sides of the equation.");
                    result[cp.Name] = result.GetValueOrDefault(cp.Name) + sign * coeff;
                }
            }
            Side(sides[0], -1); Side(sides[1], +1);
            if (!result.Values.Any(v => v < 0) || !result.Values.Any(v => v > 0)) throw new ArgumentException("The equation needs at least one reactant and one product.");
            return result;
        }

        private static IReaction Reaction(FluentFlowsheet fs, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name the reaction.");
            if (fs.Inner.Reactions.TryGetValue(name, out var byKey)) return byKey;
            var r = fs.Inner.Reactions.Values.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                 ?? fs.Inner.Reactions.Values.FirstOrDefault(x => x.ID == name);
            if (r != null) return r;
            throw new ArgumentException($"No reaction '{name}'. Reactions: {string.Join(", ", fs.Inner.Reactions.Values.Select(x => x.Name))}.");
        }

        private static Dictionary<string, double> Validate(FluentFlowsheet fs, string name, JObject stoichiometry, string baseCompound, string phase)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A reaction needs a name.");
            if (fs.Inner.Reactions.ContainsKey(name) || fs.Inner.Reactions.Values.Any(r => r.Name == name))
                throw new ArgumentException($"Reaction '{name}' already exists. Use dwsim_reaction_list to see it, or dwsim_reaction_delete.");
            if (stoichiometry == null || stoichiometry.Count < 2)
                throw new ArgumentException("Stoichiometry needs at least two compounds.");

            var stoich = new Dictionary<string, double>();
            foreach (var p in stoichiometry.Properties())
            {
                if (p.Value.Type != JTokenType.Float && p.Value.Type != JTokenType.Integer)
                    throw new ArgumentException($"The coefficient of '{p.Name}' must be a number.");
                var coeff = p.Value.Value<double>();
                if (coeff == 0) throw new ArgumentException($"The coefficient of '{p.Name}' is zero; leave the compound out.");
                stoich[Find.Compound(fs, p.Name).Name] = coeff;
            }
            if (!stoich.TryGetValue(baseCompound ?? "", out var baseCoeff))
            {
                var ci = stoich.Keys.FirstOrDefault(k => string.Equals(k, baseCompound, StringComparison.OrdinalIgnoreCase));
                if (ci == null) throw new ArgumentException($"Base compound '{baseCompound}' is not in the stoichiometry.");
                baseCoeff = stoich[ci];
            }
            if (baseCoeff > 0) throw new ArgumentException($"Base compound '{baseCompound}' must be a reactant (negative coefficient).");
            if (Canonical(phase) == null) throw new ArgumentException($"'{phase}' is not a reaction phase. Use one of: {string.Join(", ", Phases)}.");

            var imbalance = ElementImbalance(fs, stoich);
            if (imbalance.Count > 0)
                throw new ArgumentException("The reaction does not balance: " +
                    string.Join(", ", imbalance.Select(e => $"{e.Key} {e.Value:+0.####;-0.####}")) + " (products minus reactants).");
            return stoich;
        }

        private static string Canonical(string phase) =>
            Phases.FirstOrDefault(p => string.Equals(p, string.IsNullOrWhiteSpace(phase) ? "Vapor" : phase.Trim(), StringComparison.OrdinalIgnoreCase));

        private static string Basis(string basis)
        {
            var b = string.IsNullOrWhiteSpace(basis) ? "molar concentration" : basis.Trim().ToLowerInvariant();
            if (!Bases.Contains(b)) throw new ArgumentException($"'{basis}' is not a basis. Use one of: {string.Join(", ", Bases)}.");
            return b;
        }

        private static readonly string[] EnergyUnits = { "J/mol", "kJ/mol", "cal/mol", "kcal/mol", "BTU/lbmol" };

        private static string EnergyUnit(string u)
        {
            if (string.IsNullOrWhiteSpace(u)) return "J/mol";
            var hit = EnergyUnits.FirstOrDefault(x => string.Equals(x, u.Trim(), StringComparison.OrdinalIgnoreCase));
            return hit ?? throw new ArgumentException($"'{u}' is not an activation energy unit. Use one of: {string.Join(", ", EnergyUnits)}.");
        }

        private static void CheckRange(double? tmin, double? tmax)
        {
            if (tmin.HasValue && tmin < 0) throw new ArgumentException("tmin_K cannot be negative.");
            if (tmin.HasValue && tmax.HasValue && tmin >= tmax) throw new ArgumentException("tmin_K must be below tmax_K.");
        }

        /// <summary>Orders for every stoichiometry key: the caller's where given, the convention otherwise.</summary>
        private static Dictionary<string, double> Orders(Dictionary<string, double> stoich, JObject given, bool forward)
        {
            var orders = new Dictionary<string, double>();
            foreach (var kv in stoich)
            {
                bool reactant = kv.Value < 0;
                orders[kv.Key] = forward ? (reactant ? Math.Abs(kv.Value) : 0) : (reactant ? 0 : kv.Value);
            }
            if (given != null)
            {
                foreach (var p in given.Properties())
                {
                    var key = stoich.Keys.FirstOrDefault(k => string.Equals(k, p.Name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"Order given for '{p.Name}', which is not in the stoichiometry.");
                    if (p.Value.Type != JTokenType.Float && p.Value.Type != JTokenType.Integer) throw new ArgumentException($"The order of '{p.Name}' must be a number.");
                    var v = p.Value.Value<double>();
                    if (v < 0) throw new ArgumentException($"The order of '{p.Name}' cannot be negative.");
                    orders[key] = v;
                }
            }
            return orders;
        }

        private static void ApplyOrders(FluentFlowsheet fs, IReaction r, JObject given, bool forward)
        {
            foreach (var p in given.Properties())
            {
                var comp = r.Components.Values.FirstOrDefault(c => string.Equals(c.CompName, p.Name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"'{p.Name}' is not in the stoichiometry of '{r.Name}'.");
                var v = p.Value.Value<double>();
                if (v < 0) throw new ArgumentException($"The order of '{p.Name}' cannot be negative.");
                if (forward) comp.DirectOrder = v; else comp.ReverseOrder = v;
            }
        }

        private static Dictionary<string, double> ElementImbalance(FluentFlowsheet fs, Dictionary<string, double> stoich)
        {
            var sum = new Dictionary<string, double>();
            foreach (var entry in stoich)
            {
                var elements = fs.Inner.SelectedCompounds[entry.Key].Elements;
                if (elements == null || elements.Count == 0) return new Dictionary<string, double>(); // a pseudo-compound has no formula
                foreach (DictionaryEntry el in elements)
                {
                    var key = (string)el.Key;
                    sum[key] = (sum.TryGetValue(key, out var v) ? v : 0) + entry.Value * Convert.ToDouble(el.Value);
                }
            }
            return sum.Where(e => Math.Abs(e.Value) > 1e-9).ToDictionary(e => e.Key, e => e.Value);
        }

        private static JObject AddToSet(FluentFlowsheet fs, IReaction r, string set, int rank)
        {
            var id = string.IsNullOrWhiteSpace(set) ? "DefaultSet" : set;
            fs.ReactionSet(id).Add(r, rank);
            fs.Inner.ResetCalculationStatus();
            var o = Describe(r);
            o["reaction_set"] = id;
            return o;
        }

        /// <summary>The R1..Rn / P1..Pn names the engine binds in a rate expression, in stoichiometry order.</summary>
        private static JObject RateVariables(IReaction r)
        {
            var o = new JObject { ["T"] = "temperature, K" };
            int ir = 1, ip = 1;
            foreach (var c in r.Components.Values)
            {
                if (c.StoichCoeff < 0) o[$"R{ir++}"] = c.CompName;
                else o[$"P{ip++}"] = c.CompName;
            }
            return o;
        }

        internal static JObject Describe(IReaction r)
        {
            var o = new JObject
            {
                ["name"] = r.Name,
                ["type"] = r.ReactionType.ToString(),
                ["equation"] = r.Equation,
                ["stoichiometry"] = new JObject(r.Components.Values.Select(c => new JProperty(c.CompName, c.StoichCoeff))),
                ["base_compound"] = r.BaseReactant,
                ["phase"] = r.ReactionPhase.ToString(),
                ["basis"] = r.ReactionBasis.ToString(),
                ["heat_of_reaction_kJ_kmol"] = Json.Num(r.ReactionHeat),
                ["mass_balance_kg_kmol"] = Json.Num(r.StoichBalance),
                ["t_range_K"] = new JArray(r.Tmin, r.Tmax),
            };
            switch (r.ReactionType)
            {
                case ReactionType.Conversion:
                    o["conversion_expression"] = r.Expression;
                    break;
                case ReactionType.Equilibrium:
                    o["keq_mode"] = r.KExprType.ToString();
                    if (r.KExprType == KOpt.Expression) o["lnkeq_expression"] = r.Expression;
                    if (r.KExprType == KOpt.Constant) o["keq"] = r.ConstantKeqValue;
                    o["approach_K"] = r.Approach;
                    o["basis_units"] = r.EquilibriumReactionBasisUnits;
                    break;
                case ReactionType.Kinetic:
                    o["arrhenius"] = new JObject
                    {
                        ["A_forward"] = r.A_Forward, ["E_forward"] = r.E_Forward, ["E_forward_unit"] = r.E_Forward_Unit,
                        ["A_reverse"] = r.A_Reverse, ["E_reverse"] = r.E_Reverse, ["E_reverse_unit"] = r.E_Reverse_Unit,
                    };
                    o["forward_rate_type"] = r.ReactionKinFwdType.ToString();
                    o["reverse_rate_type"] = r.ReactionKinRevType.ToString();
                    if (!string.IsNullOrEmpty(r.ReactionKinFwdExpression)) o["forward_expression"] = r.ReactionKinFwdExpression;
                    if (!string.IsNullOrEmpty(r.ReactionKinRevExpression)) o["reverse_expression"] = r.ReactionKinRevExpression;
                    o["direct_orders"] = new JObject(r.Components.Values.Select(c => new JProperty(c.CompName, c.DirectOrder)));
                    o["reverse_orders"] = new JObject(r.Components.Values.Select(c => new JProperty(c.CompName, c.ReverseOrder)));
                    o["amount_units"] = r.ConcUnit;
                    o["rate_units"] = r.VelUnit;
                    break;
                case ReactionType.Heterogeneous_Catalytic:
                    o["numerator_expression"] = r.RateEquationNumerator;
                    o["denominator_expression"] = r.RateEquationDenominator;
                    o["rate_variables"] = RateVariables(r);
                    o["amount_units"] = r.ConcUnit;
                    o["rate_units"] = r.VelUnit;
                    break;
            }
            return o;
        }
    }
}
