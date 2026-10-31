// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using DWSIM.Interfaces;
using DWSIM.MCPServer.Sessions;
using DWSIM.MCPServer.Tools.Common;
using DWSIM.Thermodynamics.CompoundEditing;
using FluentFlowsheet = DWSIM.Automation.FluentAPI.Flowsheet;

namespace DWSIM.MCPServer.Tools.Compounds
{
    /// <summary>
    /// User-defined compounds and compound maintenance. The upstream server can only add database
    /// compounds, and the engine's AddCompound leaves existing streams without the new key; the
    /// engine's CompoundEditor (what the desktop compound creator uses) does both, so it is called here.
    /// </summary>
    public class CompoundTools
    {
        private readonly SessionManager _sessions;

        public CompoundTools(SessionManager sessions) { _sessions = sessions; }

        [McpTool("dwsim_compound_import_json",
            "Import a user-defined compound from a DWSIM compound JSON file (the format the compound creator " +
            "exports, also what dwsim_compound_export_json writes) into the flowsheet, adding it to every existing " +
            "material stream. For a catalyst, a salt, a sugar or anything the databases lack: export a similar " +
            "database compound, edit the properties, import. Returns the validator's findings: a compound missing " +
            "ideal-gas heat capacity or vapour pressure data will solve wrong or not at all.")]
        public JObject ImportJson(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Path of the compound JSON file on the server")] string path)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new ArgumentException($"No file at '{path}'.");
            return Import(fs, path);
        }

        [McpTool("dwsim_compound_import_json_inline",
            "Import a user-defined compound from JSON text instead of a file (same format as dwsim_compound_import_json). " +
            "The text is a serialised DWSIM ConstantProperties object: at least Name, CAS_Number, Formula, Molar_Weight, " +
            "Critical_Temperature (K), Critical_Pressure (Pa), Acentric_Factor, Normal_Boiling_Point (K), " +
            "IG_Enthalpy_of_Formation_25C and IG_Gibbs_Energy_of_Formation_25C (kJ/kg), and the ideal-gas Cp and " +
            "vapour pressure coefficient sets. Get a template with dwsim_compound_export_json of a database compound.")]
        public JObject ImportJsonInline(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("The compound as JSON text")] string json)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("json is empty.");
            try { JObject.Parse(json); } catch (Exception ex) { throw new ArgumentException("json is not valid JSON: " + ex.Message); }
            var tmp = Path.Combine(Path.GetTempPath(), $"dwsim-mcp-compound-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(tmp, json);
                return Import(fs, tmp);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        [McpTool("dwsim_compound_get",
            "The constant properties of a flowsheet compound: formula, CAS, molar mass, critical point, acentric " +
            "factor, normal boiling and melting points, ideal-gas enthalpy and Gibbs energy of formation (what the " +
            "heat of reaction and the equilibrium constant come from), elements, charge, source database. Use it " +
            "to check what a reaction's heat or a Gibbs reactor's element matrix is built from.")]
        public JObject Get(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Compound name")] string name)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var cp = Find.Compound(fs, name);
            var o = Describe(cp);
            o["validation"] = Issues(cp);
            return o;
        }

        [McpTool("dwsim_compound_export_json",
            "Write a flowsheet compound to a DWSIM compound JSON file, the template for a user-defined compound: " +
            "export the closest database compound, edit, import with dwsim_compound_import_json.")]
        public JObject ExportJson(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Compound name")] string name,
            [McpParam("Destination path on the server")] string path)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var cp = Find.Compound(fs, name);
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Give a destination path.");
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            CompoundEditor.SaveToJson(cp, path);
            return new JObject { ["compound"] = cp.Name, ["path"] = Path.GetFullPath(path), ["bytes"] = new FileInfo(path).Length };
        }

        [McpTool("dwsim_compound_remove",
            "Remove a compound from the flowsheet and from every material stream, renormalising the stream " +
            "compositions. Refused while a reaction or a Gibbs reactor still names the compound.")]
        public JObject Remove(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Compound name")] string name)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var cp = Find.Compound(fs, name);
            var inReactions = fs.Inner.Reactions.Values.Where(r => r.Components.ContainsKey(cp.Name)).Select(r => r.Name).ToList();
            if (inReactions.Count > 0)
                throw new ArgumentException($"'{cp.Name}' is in reaction(s) {string.Join(", ", inReactions)}; delete them first (dwsim_reaction_delete).");
            var inGibbs = fs.Inner.SimulationObjects.Values.OfType<DWSIM.UnitOperations.Reactors.Reactor_Gibbs>()
                .Where(g => g.ComponentIDs != null && g.ComponentIDs.Contains(cp.Name)).Select(g => g.GraphicObject.Tag).ToList();
            if (inGibbs.Count > 0)
                throw new ArgumentException($"'{cp.Name}' is in the compound list of Gibbs reactor(s) {string.Join(", ", inGibbs)}; reset them with dwsim_reactor_set_compounds first.");

            int streams = 0;
            foreach (var obj in fs.Inner.SimulationObjects.Values)
            {
                if (!(obj is IMaterialStream ms)) continue;
                bool touched = false;
                foreach (var phase in ms.Phases.Values)
                    if (phase.Compounds.Remove(cp.Name)) touched = true;
                if (touched)
                {
                    streams++;
                    var mix = ms.Phases[0].Compounds.Values.ToList();
                    double sumMol = mix.Sum(c => c.MoleFraction.GetValueOrDefault());
                    double sumMass = mix.Sum(c => c.MassFraction.GetValueOrDefault());
                    foreach (var c in mix)
                    {
                        if (sumMol > 0) c.MoleFraction = c.MoleFraction.GetValueOrDefault() / sumMol;
                        if (sumMass > 0) c.MassFraction = c.MassFraction.GetValueOrDefault() / sumMass;
                    }
                    obj.Calculated = false;
                }
            }
            var options = fs.Inner.FlowsheetOptions as DWSIM.SharedClasses.DWSIM.Flowsheet.FlowsheetVariables;
            if (options != null)
            {
                options.SelectedComponents.Remove(cp.Name);
                if (!options.NotSelectedComponents.ContainsKey(cp.Name)) options.NotSelectedComponents.Add(cp.Name, cp);
            }
            else fs.Inner.SelectedCompounds.Remove(cp.Name);
            fs.Inner.ResetCalculationStatus();
            return new JObject { ["removed"] = cp.Name, ["streams_updated"] = streams, ["compounds"] = Json.Strings(Find.CompoundNames(fs)) };
        }

        [McpTool("dwsim_compound_repair_streams",
            "Give every material stream every flowsheet compound it lacks (a flowsheet edited by hand or an older " +
            "file). A stream missing a compound key throws inside the unit operation downstream with a " +
            "KeyNotFoundException naming the compound; this is the fix.")]
        public JObject RepairStreams(
            [McpParam("Flowsheet handle")] string flowsheet_id)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var streams = Common.Compounds.Repair(fs);
            return new JObject { ["streams_updated"] = streams, ["compounds"] = Json.Strings(Find.CompoundNames(fs)) };
        }

        // ------------------------------------------------------------------ helpers

        private static JObject Import(FluentFlowsheet fs, string path)
        {
            var before = fs.Inner.SelectedCompounds.Keys.ToHashSet();
            ICompoundConstantProperties cp;
            try { cp = CompoundEditor.ImportJsonCompound(fs.Inner, path); }
            catch (Exception ex) { throw new ArgumentException($"The file could not be imported as a DWSIM compound: {(ex.InnerException ?? ex).Message}"); }
            // ImportJsonCompound adds the compound to existing streams; a loaded flowsheet's catalogue may still miss it.
            var streams = Common.Compounds.AddToStreams(fs, cp);
            fs.Inner.ResetCalculationStatus();
            var o = Describe(cp);
            o["outcome"] = before.Contains(cp.Name) ? "updated_in_place" : "added";
            o["streams_updated"] = streams;
            o["validation"] = Issues(cp);
            o["compounds"] = Json.Strings(Find.CompoundNames(fs));
            return o;
        }

        private static JArray Issues(ICompoundConstantProperties cp)
        {
            try
            {
                return new JArray(CompoundValidator.Validate(cp).Select(i => new JObject
                {
                    ["severity"] = i.Severity.ToString(), ["code"] = i.Code, ["property"] = i.PropertyKey, ["message"] = i.Message
                }));
            }
            catch (Exception ex) { return new JArray(new JObject { ["severity"] = "Unknown", ["message"] = "Validator failed: " + ex.Message }); }
        }

        internal static JObject Describe(ICompoundConstantProperties cp)
        {
            var elements = new JObject();
            if (cp.Elements != null)
                foreach (DictionaryEntry e in cp.Elements) elements[(string)e.Key] = Convert.ToDouble(e.Value);
            return new JObject
            {
                ["name"] = cp.Name,
                ["formula"] = cp.Formula,
                ["cas"] = cp.CAS_Number,
                ["database"] = cp.OriginalDB,
                ["molar_weight_g_mol"] = Json.Num(cp.Molar_Weight),
                ["critical_temperature_K"] = Json.Num(cp.Critical_Temperature),
                ["critical_pressure_Pa"] = Json.Num(cp.Critical_Pressure),
                ["critical_volume_m3_kmol"] = Json.Num(cp.Critical_Volume),
                ["acentric_factor"] = Json.Num(cp.Acentric_Factor),
                ["normal_boiling_point_K"] = Json.Num(cp.Normal_Boiling_Point),
                ["melting_point_K"] = Json.Num(cp.TemperatureOfFusion),
                ["ig_enthalpy_of_formation_25C_kJ_kg"] = Json.Num(cp.IG_Enthalpy_of_Formation_25C),
                ["ig_gibbs_energy_of_formation_25C_kJ_kg"] = Json.Num(cp.IG_Gibbs_Energy_of_Formation_25C),
                ["elements"] = elements,
                ["is_ion"] = cp.IsIon,
                ["charge"] = cp.Charge,
                ["is_pseudocompound"] = cp.IsPF == 1,
            };
        }
    }
}
