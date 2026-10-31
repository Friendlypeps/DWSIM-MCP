// Copyright 2026 friendlypeps and contributors. The four tools that keep the upstream names are
// derived from DWSIM 10's tools/DWSIM.MCPServer/Tools/Thermo/ThermoTools.cs, copyright 2008-2026
// Daniel Wagner Oliveira de Medeiros and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using DWSIM.Automation;
using DWSIM.Automation.FluentAPI;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums;
using DWSIM.Interfaces.Enums.GraphicObjects;
using DWSIM.MCPServer.Sessions;
using DWSIM.MCPServer.Tools.Common;
using PP = DWSIM.Thermodynamics.PropertyPackages;
using FluentFlowsheet = DWSIM.Automation.FluentAPI.Flowsheet;

namespace DWSIM.MCPServer.Tools.Thermo
{
    /// <summary>
    /// Thermodynamics tools. Carries the four upstream names (ThermoTools is not registered) with
    /// the same parameters, and adds what the upstream server cannot do: replace the property
    /// package in use, choose the flash (VLE/VLLE), read, write and estimate binary interaction
    /// parameters, and force solids.
    /// </summary>
    public class ThermoToolsEx
    {
        private readonly SessionManager _sessions;
        private readonly Automation3 _automation;

        public ThermoToolsEx(SessionManager sessions, Automation3 automation)
        {
            _sessions = sessions;
            _automation = automation;
        }

        // ------------------------------------------------------------------ upstream names

        [McpTool("dwsim_thermo_list_compounds",
            "List the compounds of the DWSIM databases, optionally filtered by a case-insensitive substring of " +
            "the name, formula or CAS number. The names are the ones dwsim_thermo_add_compounds takes.")]
        public JObject ListCompounds(
            [McpParam("Substring to filter by (name, formula or CAS); empty lists everything", Required = false)] string search = null,
            [McpParam("At most this many names (default 500, 0 = all)", Required = false, JsonType = "integer")] int limit = 500)
        {
            var compounds = _automation.AvailableCompounds;
            IEnumerable<ICompoundConstantProperties> q = compounds.Values;
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                q = q.Where(c => (c.Name ?? "").IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0
                              || (c.Formula ?? "").Equals(s, StringComparison.OrdinalIgnoreCase)
                              || (c.CAS_Number ?? "").Equals(s, StringComparison.OrdinalIgnoreCase));
            }
            var all = q.OrderBy(c => c.Name).ToList();
            var shown = limit > 0 ? all.Take(limit) : all;
            var arr = new JArray(shown.Select(c => string.IsNullOrWhiteSpace(search)
                ? (JToken)c.Name
                : new JObject { ["name"] = c.Name, ["formula"] = c.Formula, ["cas"] = c.CAS_Number, ["database"] = c.OriginalDB }));
            return new JObject { ["compounds"] = arr, ["count"] = all.Count, ["shown"] = arr.Count, ["total_in_databases"] = compounds.Count };
        }

        [McpTool("dwsim_thermo_add_compounds",
            "Add compounds to the flowsheet by database name (e.g. 'Water', 'Ethanol', 'Acetic acid', 'Ethyl acetate'); " +
            "a CAS number or a formula is accepted when it is unambiguous. Names are checked before anything is " +
            "added, a compound already present is skipped, and an unknown name comes back with the closest matches. " +
            "Material streams that already exist get the new compound too, so this is safe at any point.")]
        public JObject AddCompounds(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Compound names to add", JsonType = "array")] string[] names)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            if (names == null || names.Length == 0) throw new ArgumentException("List at least one compound name.");

            var resolved = new List<ICompoundConstantProperties>();
            var skipped = new JArray();
            foreach (var raw in names)
            {
                var name = (raw ?? "").Trim();
                if (name.Length == 0) continue;
                if (fs.Inner.SelectedCompounds.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped.Add(name);
                    continue;
                }
                var cp = ResolveCompound(fs, name);
                if (!resolved.Contains(cp)) resolved.Add(cp);
            }

            var added = new JArray();
            int streams = 0;
            foreach (var cp in resolved)
            {
                streams = Common.Compounds.Add(fs, cp.Name);
                added.Add(cp.Name);
            }
            var result = new JObject { ["added"] = added, ["already_present"] = skipped, ["compounds"] = Json.Strings(Find.CompoundNames(fs)) };
            if (streams > 0) result["streams_updated"] = streams;
            return result;
        }

        [McpTool("dwsim_thermo_list_property_packages",
            "List the property packages available to this flowsheet, with the short aliases dwsim_thermo_set_property_package also accepts.")]
        public JObject ListPropertyPackages(
            [McpParam("Flowsheet handle")] string flowsheet_id)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var arr = new JArray(fs.AvailablePropertyPackages.Select(n => (JToken)n));
            var aliases = new JObject(Aliases.GroupBy(a => a.Value).OrderBy(g => g.Key)
                .Select(g => new JProperty(g.Key, Json.Strings(g.Select(a => a.Key)))));
            return new JObject { ["property_packages"] = arr, ["aliases"] = aliases, ["in_use"] = Json.Strings(fs.Inner.PropertyPackages.Values.Select(p => p.Tag)) };
        }

        [McpTool("dwsim_thermo_set_property_package",
            "Set the property package of the flowsheet. By default it REPLACES the package in use and rebinds every " +
            "stream and unit operation to the new one (the official server only appended a second package, which " +
            "objects never picked up). Takes the catalogue name or a short alias: NRTL, UNIQUAC, Wilson, UNIFAC, " +
            "PR, PR78, SRK, Raoult, SteamTables, CoolProp, LKP. Choose NRTL or UNIQUAC for polar liquids " +
            "(esters, alcohols, water), PR or SRK for gases and hydrocarbons; then check the binary parameters " +
            "with dwsim_thermo_get_bips and, for a decanter or extractor, set VLLE with dwsim_thermo_set_flash.")]
        public JObject SetPropertyPackage(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Property package name or alias")] string name,
            [McpParam("Replace the package(s) in use and rebind every object (default true); false appends a second package", Required = false, JsonType = "boolean")] bool replace = true)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var key = ResolvePackageName(fs, name);
            var old = fs.Inner.PropertyPackages.Values.ToList();

            IPropertyPackage pp;
            var rebound = new JArray();
            if (replace && old.Count > 0)
            {
                pp = fs.Inner.CreateAndAddPropertyPackage(key);
                foreach (var obj in fs.Inner.SimulationObjects.Values)
                {
                    if (obj.GraphicObject == null) continue;
                    if (obj.GraphicObject.ObjectType == ObjectType.EnergyStream) continue;
                    var current = obj.PropertyPackage;
                    if (current == null || old.Any(o => ReferenceEquals(o, current) || o.UniqueID == current.UniqueID))
                    {
                        obj.PropertyPackage = pp;
                        rebound.Add(obj.GraphicObject.Tag);
                    }
                }
                foreach (var o in old)
                    if (!ReferenceEquals(o, pp)) fs.Inner.PropertyPackages.Remove(o.UniqueID);
            }
            else
            {
                fs.WithPropertyPackage(key);
                pp = fs.Inner.PropertyPackages.Values.Last();
            }

            var result = Describe(fs, pp);
            result["replaced"] = Json.Strings(replace ? old.Select(o => o.Tag) : Enumerable.Empty<string>());
            result["objects_rebound"] = rebound;
            return result;
        }

        // ------------------------------------------------------------------ new tools

        [McpTool("dwsim_thermo_get_property_package",
            "Describe a property package in use: type, flash approach and settings (equilibrium type VLE/VLLE, " +
            "immiscible water, inside-out, fail-safe mode), forced solids, the binary-parameter model it carries, " +
            "and the objects bound to it. With one package in the flowsheet the name can be omitted.")]
        public JObject GetPropertyPackage(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Package tag or UniqueID; omit when there is one", Required = false)] string package = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            return Describe(fs, Find.Package(fs, package));
        }

        [McpTool("dwsim_thermo_set_flash",
            "Configure how the property package flashes. equilibrium_type decides which phases the flash looks for: " +
            "Default (DWSIM's heuristics by compound names), VLE (one liquid), VLLE (two liquids: needed for a " +
            "decanter, a liquid-liquid extractor, or any ester/alcohol/water split; under VLE a Vessel reports one " +
            "liquid and the plant converges to the wrong answer), SVLE, SVLLE (with solids), NoFlash. approach is " +
            "NestedLoops (default), InsideOut (faster, less robust) or GibbsMinimization (most robust for LLE). " +
            "fail_safe_mode: 0 rigorous VLE on failure, 1 ideal Raoult (default), 2 no flash, 3 raise the error. " +
            "settings takes any other FlashSetting by name, e.g. {\"PTFlash_External_Loop_Tolerance\": \"1e-6\"}.")]
        public JObject SetFlash(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Package tag or UniqueID; omit when there is one", Required = false)] string package = null,
            [McpParam("NestedLoops, InsideOut or GibbsMinimization", Required = false)] string approach = null,
            [McpParam("Default, VLE, VLLE, SVLE, SVLLE or NoFlash", Required = false)] string equilibrium_type = null,
            [McpParam("Treat water as an immiscible second liquid (fast VLLE for water + hydrocarbons)", Required = false, JsonType = "boolean")] bool? immiscible_water = null,
            [McpParam("Use the inside-out flash inside the nested-loops approach", Required = false, JsonType = "boolean")] bool? use_io_flash = null,
            [McpParam("What to do when the flash fails: 0, 1, 2 or 3", Required = false, JsonType = "integer")] int? fail_safe_mode = null,
            [McpParam("Other FlashSetting entries as {name: value}", Required = false, JsonType = "object")] JObject settings = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var pp = Engine(Find.Package(fs, package));
            var changed = new JArray();

            if (!string.IsNullOrWhiteSpace(approach))
            {
                pp.FlashCalculationApproach = Json.Enum<PP.PropertyPackage.FlashCalculationApproachType>(approach, "flash approach");
                changed.Add("FlashCalculationApproach");
            }
            if (!string.IsNullOrWhiteSpace(equilibrium_type))
            {
                var canonical = EquilibriumTypes.FirstOrDefault(t => string.Equals(t, equilibrium_type.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"'{equilibrium_type}' is not an equilibrium type. Use one of: {string.Join(", ", EquilibriumTypes)}.");
                pp.FlashSettings[FlashSetting.ForceEquilibriumCalculationType] = canonical;
                changed.Add("ForceEquilibriumCalculationType");
            }
            if (immiscible_water.HasValue)
            {
                pp.FlashSettings[FlashSetting.ImmiscibleWaterOption] = immiscible_water.Value ? "True" : "False";
                changed.Add("ImmiscibleWaterOption");
            }
            if (use_io_flash.HasValue)
            {
                pp.FlashSettings[FlashSetting.UseIOFlash] = use_io_flash.Value ? "True" : "False";
                changed.Add("UseIOFlash");
            }
            if (fail_safe_mode.HasValue)
            {
                if (fail_safe_mode < 0 || fail_safe_mode > 3) throw new ArgumentException("fail_safe_mode is 0, 1, 2 or 3.");
                pp.FlashSettings[FlashSetting.FailSafeCalculationMode] = fail_safe_mode.Value.ToString();
                changed.Add("FailSafeCalculationMode");
            }
            if (settings != null)
            {
                foreach (var p in settings.Properties())
                {
                    var key = Json.Enum<FlashSetting>(p.Name, "flash setting");
                    pp.FlashSettings[key] = p.Value.Type == JTokenType.Boolean ? (p.Value.Value<bool>() ? "True" : "False") : p.Value.ToString();
                    changed.Add(key.ToString());
                }
            }
            // The flash object is rebuilt from the settings on every call, so no reset is needed; objects
            // solved under the old settings are stale, though.
            fs.Inner.ResetCalculationStatus();

            var result = Describe(fs, pp);
            result["changed"] = changed;
            return result;
        }

        [McpTool("dwsim_thermo_set_forced_solids",
            "Mark compounds the flash must keep in the solid phase whatever the temperature (a catalyst, a salt, " +
            "biomass). The Filter and the SolidsSeparator read that solid phase; without forced solids or a " +
            "melting-point model they see nothing to separate. The Crystallizer needs none of this.")]
        public JObject SetForcedSolids(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Compounds to force solid", JsonType = "array")] string[] compounds,
            [McpParam("Package tag or UniqueID; omit when there is one", Required = false)] string package = null,
            [McpParam("Clear the list before adding (default false)", Required = false, JsonType = "boolean")] bool clear = false)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var pp = Engine(Find.Package(fs, package));
            if (pp.ForcedSolids == null) pp.ForcedSolids = new List<string>();
            if (clear) pp.ForcedSolids.Clear();
            foreach (var c in compounds ?? Array.Empty<string>())
            {
                var cp = Find.Compound(fs, c);
                if (!pp.ForcedSolids.Contains(cp.Name)) pp.ForcedSolids.Add(cp.Name);
            }
            fs.Inner.ResetCalculationStatus();
            return new JObject { ["package"] = pp.Tag, ["forced_solids"] = Json.Strings(pp.ForcedSolids) };
        }

        [McpTool("dwsim_thermo_get_bips",
            "Read the binary interaction parameters of the package for every pair of flowsheet compounds: NRTL " +
            "(A12, A21 in cal/mol, alpha12), UNIQUAC (A12, A21 in cal/mol), Peng-Robinson/SRK (kij), Wilson " +
            "(A12, A21). A pair with no entry, or an activity pair with all zeros, is listed under 'missing': the " +
            "model then treats it as ideal, silently. Fill missing pairs with dwsim_thermo_estimate_bips (UNIFAC) " +
            "or dwsim_thermo_set_bips (literature values). Packages without binary parameters (Raoult, UNIFAC, " +
            "steam tables) report model 'none'.")]
        public JObject GetBips(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Package tag or UniqueID; omit when there is one", Required = false)] string package = null,
            [McpParam("Only pairs among these compounds (default: all flowsheet compounds)", Required = false, JsonType = "array")] string[] compounds = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var pp = Find.Package(fs, package);
            var names = compounds != null && compounds.Length > 0
                ? compounds.Select(c => Find.Compound(fs, c).Name).Distinct().ToList()
                : Find.CompoundNames(fs);
            return BipReport(fs, pp, names);
        }

        [McpTool("dwsim_thermo_set_bips",
            "Write binary interaction parameters. pairs is a list of {c1, c2, ...} objects: for NRTL A12, A21 " +
            "(cal/mol) and alpha12 (default 0.3), optionally B12, B21; for UNIQUAC A12, A21 (cal/mol), optionally " +
            "B12, B21; for Peng-Robinson/SRK kij (written symmetrically); for Wilson A12, A21. Values are written " +
            "in the c1->c2 direction, so give them as the source quotes them. Call dwsim_thermo_get_bips afterwards " +
            "to see the table the solver will use.")]
        public JObject SetBips(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Pairs as [{c1, c2, A12, A21, alpha12, B12, B21} | {c1, c2, kij}]", JsonType = "array")] JArray pairs,
            [McpParam("Package tag or UniqueID; omit when there is one", Required = false)] string package = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var pp = Find.Package(fs, package);
            var model = Bip.Detect(pp);
            if (model == Bip.Model.None)
                throw new ArgumentException($"'{pp.Tag}' ({pp.GetType().Name}) carries no binary interaction parameters.");
            if (pairs == null || pairs.Count == 0) throw new ArgumentException("Give at least one pair.");

            var written = new JArray();
            foreach (var tok in pairs)
            {
                if (!(tok is JObject p)) throw new ArgumentException("Each pair is an object with c1, c2 and the parameters.");
                var c1 = Find.Compound(fs, p.Value<string>("c1")).Name;
                var c2 = Find.Compound(fs, p.Value<string>("c2")).Name;
                if (c1 == c2) throw new ArgumentException($"A pair needs two different compounds ({c1}).");
                double Get(string key, double? dflt = null)
                {
                    var v = p[key];
                    if (v == null || v.Type == JTokenType.Null)
                    {
                        if (dflt.HasValue) return dflt.Value;
                        throw new ArgumentException($"Pair {c1}/{c2}: '{key}' is required for a {model} package.");
                    }
                    return v.Value<double>();
                }
                switch (model)
                {
                    case Bip.Model.NRTL:
                        Bip.SetNrtl(pp, c1, c2, Get("A12"), Get("A21"), Get("alpha12", 0.3), Get("B12", 0), Get("B21", 0));
                        break;
                    case Bip.Model.UNIQUAC:
                        Bip.SetUniquac(pp, c1, c2, Get("A12"), Get("A21"), Get("B12", 0), Get("B21", 0));
                        break;
                    case Bip.Model.Cubic:
                        Bip.SetKij(pp, c1, c2, Get("kij"));
                        break;
                    case Bip.Model.Wilson:
                        Bip.SetWilson(fs, pp, c1, c2, Get("A12"), Get("A21"));
                        break;
                }
                written.Add($"{c1} / {c2}");
            }
            fs.Inner.ResetCalculationStatus();
            var result = BipReport(fs, pp, Find.CompoundNames(fs));
            result["written"] = written;
            return result;
        }

        [McpTool("dwsim_thermo_estimate_bips",
            "Estimate the missing binary interaction parameters of an NRTL, UNIQUAC or Wilson package with UNIFAC " +
            "(DWSIM's own routine: it regresses UNIFAC activity coefficients at 25 C; pairs with a boiling point " +
            "below 200 K get small placeholders). Only pairs that are missing or all-zero are touched. Returns the " +
            "pairs it filled and the full table. Estimated parameters are a fallback, not data: quote them as such.")]
        public JObject EstimateBips(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Package tag or UniqueID; omit when there is one", Required = false)] string package = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var pp = Find.Package(fs, package);
            var model = Bip.Detect(pp);
            var names = Find.CompoundNames(fs);
            if (names.Count < 2) throw new ArgumentException("Add at least two compounds first.");

            var before = Bip.Pairs(fs, pp, names).Where(x => !x.Present).Select(x => x.Key).ToHashSet();
            switch (pp)
            {
                case PP.NRTLPropertyPackage nrtl: nrtl.EstimateMissingInteractionParameters(false); break;
                case PP.UNIQUACPropertyPackage uniquac: uniquac.EstimateMissingInteractionParameters(false); break;
                default:
                    if (model == Bip.Model.Wilson)
                    {
                        var m = pp.GetType().GetMethod("EstimateMissingInteractionParameters");
                        m.Invoke(pp, new object[] { false });
                    }
                    else
                        throw new ArgumentException($"'{pp.Tag}' is a {pp.GetType().Name}; UNIFAC estimation exists for NRTL, UNIQUAC and Wilson packages only.");
                    break;
            }
            fs.Inner.ResetCalculationStatus();
            var result = BipReport(fs, pp, names);
            result["estimated"] = Json.Strings(Bip.Pairs(fs, pp, names).Where(x => x.Present && before.Contains(x.Key)).Select(x => x.Key));
            return result;
        }

        // ------------------------------------------------------------------ helpers

        private static readonly string[] EquilibriumTypes = { "Default", "VLE", "VLLE", "SVLE", "SVLLE", "NoFlash" };

        /// <summary>Short names a caller is likely to use, mapped to the catalogue keys.</summary>
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PR"] = PropertyPackages.PengRobinson, ["Peng-Robinson"] = PropertyPackages.PengRobinson, ["PengRobinson"] = PropertyPackages.PengRobinson,
            ["PR78"] = PropertyPackages.PengRobinson1978, ["Peng-Robinson 1978"] = PropertyPackages.PengRobinson1978,
            ["SRK"] = PropertyPackages.SoaveRedlichKwong, ["Soave-Redlich-Kwong"] = PropertyPackages.SoaveRedlichKwong,
            ["LKP"] = PropertyPackages.LeeKeslerPlocker, ["Lee-Kesler-Plocker"] = PropertyPackages.LeeKeslerPlocker,
            ["Raoult"] = PropertyPackages.Raoult, ["Ideal"] = PropertyPackages.Raoult, ["Raoult's Law"] = PropertyPackages.Raoult,
            ["SteamTables"] = PropertyPackages.SteamTables, ["Steam Tables"] = PropertyPackages.SteamTables, ["IAPWS-IF97"] = PropertyPackages.SteamTables,
            ["CoolProp"] = PropertyPackages.CoolProp,
            ["NRTL"] = PropertyPackages.NRTL, ["UNIQUAC"] = PropertyPackages.UNIQUAC, ["Wilson"] = PropertyPackages.Wilson,
            ["UNIFAC"] = PropertyPackages.UNIFAC, ["UNIFAC-LL"] = PropertyPackages.UNIFAC_LL,
            ["Modified UNIFAC"] = PropertyPackages.ModifiedUNIFAC, ["MODFAC"] = PropertyPackages.ModifiedUNIFAC, ["Dortmund"] = PropertyPackages.ModifiedUNIFAC,
            ["Chao-Seader"] = PropertyPackages.ChaoSeader, ["Grayson-Streed"] = PropertyPackages.GraysonStreed,
            ["GERG-2008"] = PropertyPackages.GERG2008, ["GERG2008"] = PropertyPackages.GERG2008,
            ["PC-SAFT"] = PropertyPackages.PCSAFT, ["PCSAFT"] = PropertyPackages.PCSAFT,
        };

        /// <summary>The catalogue key for a name, an alias, or an unambiguous fragment.</summary>
        internal static string ResolvePackageName(FluentFlowsheet fs, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name the property package.");
            var available = fs.Inner.AvailablePropertyPackages.Keys.ToList();
            var n = name.Trim();
            if (available.Contains(n)) return n;
            if (Aliases.TryGetValue(n, out var alias) && available.Contains(alias)) return alias;
            var ci = available.FirstOrDefault(k => string.Equals(k, n, StringComparison.OrdinalIgnoreCase));
            if (ci != null) return ci;
            var contains = available.Where(k => k.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (contains.Count == 1) return contains[0];
            var hint = contains.Count > 1 ? $" Did you mean: {string.Join(", ", contains)}?" : "";
            throw new ArgumentException($"'{name}' is not a property package.{hint} Call dwsim_thermo_list_property_packages for the catalogue; aliases: {string.Join(", ", Aliases.Keys.OrderBy(k => k))}.");
        }

        /// <summary>A database compound by name, CAS or formula; the error lists the closest names.</summary>
        private ICompoundConstantProperties ResolveCompound(FluentFlowsheet fs, string name)
        {
            var db = _automation.AvailableCompounds;
            if (db.TryGetValue(name, out var exact)) return exact;
            var ci = db.Values.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (ci != null) return ci;
            var byCas = db.Values.Where(c => string.Equals(c.CAS_Number, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byCas.Count == 1) return byCas[0];
            var byFormula = db.Values.Where(c => string.Equals(c.Formula, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byFormula.Count == 1) return byFormula[0];
            var close = db.Keys.Where(k => k.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(k => k.Length).Take(10).ToList();
            if (close.Count == 0)
            {
                // a misspelling: the closest names by edit distance, as the compound selector would show them
                close = db.Keys.Select(k => (k, d: EditDistance(k.ToLowerInvariant(), name.ToLowerInvariant())))
                    .Where(x => x.d <= Math.Max(2, name.Length / 3))
                    .OrderBy(x => x.d).ThenBy(x => x.k.Length).Take(10).Select(x => x.k).ToList();
            }
            if (byFormula.Count > 1) close = byFormula.Select(c => c.Name).Concat(close).Distinct().Take(10).ToList();
            var hint = close.Count > 0 ? $" Closest names: {string.Join(", ", close)}." : " Search with dwsim_thermo_list_compounds.";
            throw new ArgumentException($"No compound '{name}' in the databases.{hint}");
        }

        /// <summary>Levenshtein distance, for suggesting a compound name after a misspelling.</summary>
        private static int EditDistance(string a, string b)
        {
            if (Math.Abs(a.Length - b.Length) > 6) return int.MaxValue;
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                (prev, cur) = (cur, prev);
            }
            return prev[b.Length];
        }

        private static PP.PropertyPackage Engine(IPropertyPackage pp) =>
            pp as PP.PropertyPackage ?? throw new ArgumentException($"'{pp.Tag}' ({pp.GetType().Name}) is not a DWSIM property package; its flash cannot be configured here.");

        internal static JObject Describe(FluentFlowsheet fs, IPropertyPackage pp)
        {
            var o = new JObject
            {
                ["property_package"] = (pp as PP.PropertyPackage)?.ComponentName ?? pp.Tag,
                ["tag"] = pp.Tag,
                ["unique_id"] = pp.UniqueID,
                ["type"] = pp.GetType().Name,
                ["bip_model"] = Bip.Detect(pp).ToString(),
            };
            if (pp is PP.PropertyPackage e)
            {
                string S(FlashSetting k) => e.FlashSettings.TryGetValue(k, out var v) ? v : null;
                o["flash_approach"] = e.FlashCalculationApproach.ToString();
                o["flash"] = new JObject
                {
                    ["equilibrium_type"] = S(FlashSetting.ForceEquilibriumCalculationType) ?? "Default",
                    ["immiscible_water"] = S(FlashSetting.ImmiscibleWaterOption),
                    ["use_io_flash"] = S(FlashSetting.UseIOFlash),
                    ["fail_safe_mode"] = S(FlashSetting.FailSafeCalculationMode),
                    ["handle_solids_in_default_mode"] = S(FlashSetting.HandleSolidsInDefaultEqCalcMode),
                    ["nested_loops_v2"] = S(FlashSetting.NestedLoops_v2),
                    ["three_phase_stability_test_severity"] = S(FlashSetting.ThreePhaseFlashStabTestSeverity),
                };
                o["forced_solids"] = Json.Strings(e.ForcedSolids ?? new List<string>());
                o["immiscible_liquids"] = Json.Strings(e.ImmiscibleLiquids ?? new List<string>());
            }
            o["used_by"] = Json.Strings(fs.Inner.SimulationObjects.Values
                .Where(x => x.GraphicObject != null && x.PropertyPackage != null && x.PropertyPackage.UniqueID == pp.UniqueID)
                .Select(x => x.GraphicObject.Tag));
            o["packages_in_flowsheet"] = fs.Inner.PropertyPackages.Count;
            return o;
        }

        private static JObject BipReport(FluentFlowsheet fs, IPropertyPackage pp, List<string> names)
        {
            var model = Bip.Detect(pp);
            var pairs = Bip.Pairs(fs, pp, names);
            return new JObject
            {
                ["package"] = pp.Tag,
                ["model"] = model.ToString(),
                ["units"] = model == Bip.Model.NRTL || model == Bip.Model.UNIQUAC ? "A12, A21 in cal/mol; alpha12 dimensionless" :
                            model == Bip.Model.Cubic ? "kij dimensionless" : model == Bip.Model.Wilson ? "A12, A21 as stored (cal/mol)" : "",
                ["pairs"] = new JArray(pairs.Select(x => x.ToJson())),
                ["missing"] = Json.Strings(pairs.Where(x => !x.Present).Select(x => x.Key)),
            };
        }

        /// <summary>Binary interaction parameter storage of the engine's models, read and written in place.</summary>
        internal static class Bip
        {
            public enum Model { None, NRTL, UNIQUAC, Cubic, Wilson }

            public static Model Detect(IPropertyPackage pp)
            {
                switch (pp)
                {
                    case PP.NRTLPropertyPackage _: return Model.NRTL;
                    case PP.UNIQUACPropertyPackage _: return Model.UNIQUAC;
                }
                if (pp.GetType().Name == "WilsonPropertyPackage") return Model.Wilson;
                if (pp is PP.ActivityCoefficientPropertyPackage) return Model.None; // UNIFAC, Raoult: no binary table
                return pp.GetType().GetField("m_pr", BindingFlags.Public | BindingFlags.Instance) != null ? Model.Cubic : Model.None;
            }

            public sealed class Pair
            {
                public string C1, C2, Direction;
                public bool Present;
                public JObject Values;
                public string Key => $"{C1} / {C2}";
                public JObject ToJson()
                {
                    var o = new JObject { ["c1"] = C1, ["c2"] = C2, ["present"] = Present };
                    if (Direction != null) o["stored_as"] = Direction;
                    if (Values != null) o["values"] = Values;
                    return o;
                }
            }

            public static List<Pair> Pairs(FluentFlowsheet fs, IPropertyPackage pp, List<string> names)
            {
                var model = Detect(pp);
                var list = new List<Pair>();
                for (int i = 0; i < names.Count; i++)
                    for (int j = i + 1; j < names.Count; j++)
                        list.Add(Read(fs, pp, model, names[i], names[j]));
                return list;
            }

            private static Pair Read(FluentFlowsheet fs, IPropertyPackage pp, Model model, string c1, string c2)
            {
                var pair = new Pair { C1 = c1, C2 = c2 };
                switch (model)
                {
                    case Model.NRTL:
                    {
                        var ip = ((PP.NRTLPropertyPackage)pp).m_uni.InteractionParameters;
                        if (TryGet(ip, c1, c2, out var d, out var fwd))
                        {
                            pair.Direction = fwd ? $"{c1}->{c2}" : $"{c2}->{c1}";
                            pair.Values = new JObject { ["A12"] = fwd ? d.A12 : d.A21, ["A21"] = fwd ? d.A21 : d.A12, ["alpha12"] = d.alpha12, ["B12"] = fwd ? d.B12 : d.B21, ["B21"] = fwd ? d.B21 : d.B12 };
                            pair.Present = !(d.A12 == 0 && d.A21 == 0 && d.alpha12 == 0);
                        }
                        break;
                    }
                    case Model.UNIQUAC:
                    {
                        var ip = ((PP.UNIQUACPropertyPackage)pp).m_uni.InteractionParameters;
                        if (TryGet(ip, c1, c2, out var d, out var fwd))
                        {
                            pair.Direction = fwd ? $"{c1}->{c2}" : $"{c2}->{c1}";
                            pair.Values = new JObject { ["A12"] = fwd ? d.A12 : d.A21, ["A21"] = fwd ? d.A21 : d.A12, ["B12"] = fwd ? d.B12 : d.B21, ["B21"] = fwd ? d.B21 : d.B12 };
                            pair.Present = !(d.A12 == 0 && d.A21 == 0);
                        }
                        break;
                    }
                    case Model.Cubic:
                    {
                        var ip = CubicTable(pp);
                        var rec = Nested(ip, c1, c2) ?? Nested(ip, c2, c1);
                        if (rec != null)
                        {
                            pair.Present = true;
                            pair.Values = new JObject { ["kij"] = Kij(rec) };
                        }
                        break;
                    }
                    case Model.Wilson:
                    {
                        var bips = WilsonTable(pp);
                        var cas1 = Find.Compound(fs, c1).CAS_Number ?? "";
                        var cas2 = Find.Compound(fs, c2).CAS_Number ?? "";
                        if (bips.TryGetValue(cas1, out var inner) && inner.TryGetValue(cas2, out var v) && v.Length >= 2)
                        { pair.Present = true; pair.Direction = $"{c1}->{c2}"; pair.Values = new JObject { ["A12"] = v[0], ["A21"] = v[1] }; }
                        else if (bips.TryGetValue(cas2, out inner) && inner.TryGetValue(cas1, out v) && v.Length >= 2)
                        { pair.Present = true; pair.Direction = $"{c2}->{c1}"; pair.Values = new JObject { ["A12"] = v[1], ["A21"] = v[0] }; }
                        break;
                    }
                }
                return pair;
            }

            private static bool TryGet<T>(Dictionary<string, Dictionary<string, T>> ip, string c1, string c2, out T data, out bool forward)
            {
                forward = true;
                if (ip.TryGetValue(c1, out var inner) && inner.TryGetValue(c2, out data)) return true;
                forward = false;
                if (ip.TryGetValue(c2, out inner) && inner.TryGetValue(c1, out data)) return true;
                data = default;
                return false;
            }

            public static void SetNrtl(IPropertyPackage pp, string c1, string c2, double a12, double a21, double alpha12, double b12, double b21)
            {
                var ip = ((PP.NRTLPropertyPackage)pp).m_uni.InteractionParameters;
                // An entry stored in the other direction would shadow this one in the engine's lookup, so it goes.
                if (ip.TryGetValue(c2, out var reverse)) reverse.Remove(c1);
                if (!ip.TryGetValue(c1, out var inner)) ip[c1] = inner = new Dictionary<string, PP.Auxiliary.NRTL_IPData>();
                if (!inner.TryGetValue(c2, out var d)) inner[c2] = d = new PP.Auxiliary.NRTL_IPData();
                d.ID1 = c1; d.ID2 = c2;
                d.A12 = a12; d.A21 = a21; d.alpha12 = alpha12; d.B12 = b12; d.B21 = b21;
                d.comment = "set over MCP";
            }

            public static void SetUniquac(IPropertyPackage pp, string c1, string c2, double a12, double a21, double b12, double b21)
            {
                var ip = ((PP.UNIQUACPropertyPackage)pp).m_uni.InteractionParameters;
                if (ip.TryGetValue(c2, out var reverse)) reverse.Remove(c1);
                if (!ip.TryGetValue(c1, out var inner)) ip[c1] = inner = new Dictionary<string, PP.Auxiliary.UNIQUAC_IPData>();
                if (!inner.TryGetValue(c2, out var d)) inner[c2] = d = new PP.Auxiliary.UNIQUAC_IPData();
                d.Name1 = c1; d.Name2 = c2;
                d.A12 = a12; d.A21 = a21; d.B12 = b12; d.B21 = b21;
                d.comment = "set over MCP";
            }

            public static void SetKij(IPropertyPackage pp, string c1, string c2, double kij)
            {
                var ip = CubicTable(pp);
                foreach (var (a, b) in new[] { (c1, c2), (c2, c1) })
                {
                    var rec = Nested(ip, a, b);
                    if (rec == null)
                    {
                        var innerType = ip.GetType().GetGenericArguments()[1];
                        var recType = innerType.GetGenericArguments()[1];
                        if (!ip.Contains(a)) ip[a] = Activator.CreateInstance(innerType);
                        rec = Activator.CreateInstance(recType);
                        ((IDictionary)ip[a])[b] = rec;
                    }
                    var kijMember = (MemberInfo)rec.GetType().GetProperty("kij") ?? rec.GetType().GetField("kij");
                    if (kijMember is PropertyInfo pi) pi.SetValue(rec, kij); else ((FieldInfo)kijMember).SetValue(rec, kij);
                }
            }

            public static void SetWilson(FluentFlowsheet fs, IPropertyPackage pp, string c1, string c2, double a12, double a21)
            {
                var bips = WilsonTable(pp);
                var cas1 = Find.Compound(fs, c1).CAS_Number ?? throw new ArgumentException($"{c1} has no CAS number; Wilson parameters are keyed by CAS.");
                var cas2 = Find.Compound(fs, c2).CAS_Number ?? throw new ArgumentException($"{c2} has no CAS number; Wilson parameters are keyed by CAS.");
                if (!bips.ContainsKey(cas1)) bips[cas1] = new Dictionary<string, double[]>();
                if (!bips.ContainsKey(cas2)) bips[cas2] = new Dictionary<string, double[]>();
                bips[cas1][cas2] = new[] { a12, a21 };
                bips[cas2][cas1] = new[] { a21, a12 };
            }

            /// <summary>The cubic model's Dictionary(Of String, Dictionary(Of String, *_IPData)), whatever the record type.</summary>
            private static IDictionary CubicTable(IPropertyPackage pp)
            {
                var f = pp.GetType().GetField("m_pr", BindingFlags.Public | BindingFlags.Instance)
                        ?? throw new ArgumentException($"'{pp.Tag}' has no cubic interaction table.");
                var model = f.GetValue(pp);
                var prop = model.GetType().GetProperty("InteractionParameters") ?? throw new ArgumentException("The cubic model has no InteractionParameters.");
                return (IDictionary)prop.GetValue(model);
            }

            private static object Nested(IDictionary outer, string k1, string k2)
            {
                if (!outer.Contains(k1)) return null;
                var inner = outer[k1] as IDictionary;
                return inner != null && inner.Contains(k2) ? inner[k2] : null;
            }

            private static double Kij(object rec)
            {
                var m = (MemberInfo)rec.GetType().GetProperty("kij") ?? rec.GetType().GetField("kij");
                return m is PropertyInfo pi ? Convert.ToDouble(pi.GetValue(rec)) : Convert.ToDouble(((FieldInfo)m).GetValue(rec));
            }

            private static Dictionary<string, Dictionary<string, double[]>> WilsonTable(IPropertyPackage pp)
            {
                var prop = pp.GetType().GetProperty("WilsonM") ?? throw new ArgumentException($"'{pp.Tag}' is not a Wilson package.");
                var model = prop.GetValue(pp);
                var bips = model.GetType().GetProperty("BIPs").GetValue(model) as Dictionary<string, Dictionary<string, double[]>>;
                if (bips == null) throw new ArgumentException("The Wilson model has no BIP table.");
                return bips;
            }
        }
    }
}
