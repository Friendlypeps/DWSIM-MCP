// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using DWSIM.Automation;
using DWSIM.Interfaces;
using DWSIM.MCPServer.Sessions;
using DWSIM.MCPServer.Tools.Common;

namespace DWSIM.MCPServer.Tools.Thermo
{
    /// <summary>
    /// Turns what a document carries (a Hill formula, a name, a CAS number) into the exact databank name,
    /// and refuses when the data does not settle it. The databanks hold formulas in two conventions (Hill in
    /// DWSIM's own, structural like CH3COOH in ChemSep's), so formulas are compared through the element counts
    /// every compound carries. There is no InChI in the 10.2.12 databanks, so an InChIKey cannot decide anything
    /// here; it is echoed back for the caller's own bookkeeping.
    /// </summary>
    public class CompoundResolveTools
    {
        private readonly Automation3 _automation;
        public CompoundResolveTools(SessionManager sessions, Automation3 automation) { _automation = automation; }

        private sealed class Entry
        {
            public ICompoundConstantProperties Cp;
            public string Name, Hill, NormName, Cas, Db;
            public Dictionary<string, int> Elements;
        }

        private static readonly string[] DbOrder = { "ChemSep", "DWSIM", "CoolProp", "ChEDL", "Biodiesel" };
        private List<Entry> _index;
        private readonly object _lock = new object();

        private List<Entry> Index()
        {
            lock (_lock)
            {
                if (_index != null) return _index;
                var list = new List<Entry>();
                foreach (var cp in _automation.AvailableCompounds.Values)
                {
                    var els = ElementsOf(cp);
                    list.Add(new Entry
                    {
                        Cp = cp, Name = cp.Name ?? "", Hill = els.Count > 0 ? Hill(els) : "", NormName = Norm(cp.Name),
                        Cas = (cp.CAS_Number ?? "").Trim(), Db = cp.OriginalDB ?? "", Elements = els,
                    });
                }
                _index = list;
                return list;
            }
        }

        private static Dictionary<string, int> ElementsOf(ICompoundConstantProperties cp)
        {
            var d = new Dictionary<string, int>();
            try
            {
                if (cp.Elements != null && cp.Elements.Count > 0)
                {
                    foreach (DictionaryEntry e in cp.Elements)
                    {
                        var n = Convert.ToDouble(e.Value, CultureInfo.InvariantCulture);
                        if (n > 0) d[(string)e.Key] = (int)Math.Round(n);
                    }
                    return d;
                }
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(cp.Formula))
            {
                try { return ParseFormula(cp.Formula); } catch { }
            }
            return d;
        }

        /// <summary>Element counts of a molecular formula (Hill or structural, parentheses allowed).</summary>
        internal static Dictionary<string, int> ParseFormula(string formula)
        {
            var s = (formula ?? "").Trim();
            if (s.Length == 0) throw new ArgumentException("empty formula");
            var stack = new Stack<Dictionary<string, int>>();
            stack.Push(new Dictionary<string, int>());
            int i = 0;
            while (i < s.Length)
            {
                char ch = s[i];
                if (ch == '(' || ch == '[') { stack.Push(new Dictionary<string, int>()); i++; }
                else if (ch == ')' || ch == ']')
                {
                    i++;
                    int mult = ReadInt(s, ref i, 1);
                    var group = stack.Pop();
                    if (stack.Count == 0) throw new ArgumentException($"unbalanced parentheses in '{formula}'");
                    foreach (var kv in group) stack.Peek()[kv.Key] = stack.Peek().GetValueOrDefault(kv.Key) + kv.Value * mult;
                }
                else if (char.IsUpper(ch))
                {
                    var sym = ch.ToString();
                    i++;
                    while (i < s.Length && char.IsLower(s[i])) { sym += s[i]; i++; }
                    int n = ReadInt(s, ref i, 1);
                    stack.Peek()[sym] = stack.Peek().GetValueOrDefault(sym) + n;
                }
                else if (ch == '.' || ch == '·' || ch == ' ' || ch == '-') i++;
                else throw new ArgumentException($"'{formula}' is not a molecular formula (unexpected '{ch}').");
            }
            if (stack.Count != 1) throw new ArgumentException($"unbalanced parentheses in '{formula}'");
            return stack.Pop();
        }

        private static int ReadInt(string s, ref int i, int dflt)
        {
            int start = i;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            return i > start ? int.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture) : dflt;
        }

        /// <summary>Hill order: C, then H, then the rest alphabetically; without carbon everything alphabetical.</summary>
        internal static string Hill(Dictionary<string, int> els)
        {
            var sb = new StringBuilder();
            IEnumerable<string> order;
            if (els.ContainsKey("C"))
                order = new[] { "C", "H" }.Where(els.ContainsKey).Concat(els.Keys.Where(k => k != "C" && k != "H").OrderBy(k => k, StringComparer.Ordinal));
            else order = els.Keys.OrderBy(k => k, StringComparer.Ordinal);
            foreach (var k in order) { sb.Append(k); if (els[k] != 1) sb.Append(els[k]); }
            return sb.ToString();
        }

        private static string Norm(string name) =>
            Regex.Replace((name ?? "").ToLowerInvariant(), @"[\s\-,()\[\]']", "");

        private static JObject Describe(Entry e) => new JObject
        {
            ["name"] = e.Name, ["hill_formula"] = e.Hill, ["formula_as_stored"] = e.Cp.Formula, ["cas"] = e.Cas,
            ["database"] = e.Db, ["molar_weight_g_mol"] = Json.Num(e.Cp.Molar_Weight), ["smiles"] = e.Cp.SMILES ?? "",
            ["elements"] = new JObject(e.Elements.OrderBy(k => k.Key).Select(k => new JProperty(k.Key, k.Value))),
        };

        [McpTool("dwsim_thermo_resolve_compound",
            "Resolve a substance to the exact databank name from what a document carries: a molecular formula " +
            "(Hill or structural, e.g. C2H6O or CH3CH2OH), a name, a CAS number, in any combination. Returns " +
            "status resolved | ambiguous | conflict | not_found | unsupported_key with the candidates; an ambiguous " +
            "formula (C2H6O is ethanol or dimethyl ether) is never settled by guessing: add the name or CAS. " +
            "InChI/InChIKey are accepted and echoed but cannot decide: the 10.2.12 databanks carry no InChI.")]
        public JObject Resolve(
            [McpParam("Molecular formula, Hill or structural", Required = false)] string formula = null,
            [McpParam("Name as the document gives it", Required = false)] string name = null,
            [McpParam("CAS registry number", Required = false)] string cas = null,
            [McpParam("InChIKey (echoed only; see description)", Required = false)] string inchikey = null,
            [McpParam("InChI (echoed only)", Required = false)] string inchi = null,
            [McpParam("At most this many candidates (default 10)", Required = false, JsonType = "integer")] int limit = 10)
        {
            var query = new JObject { ["formula"] = formula, ["name"] = name, ["cas"] = cas, ["inchikey"] = inchikey, ["inchi"] = inchi };
            string limitation = "The 10.2.12 databanks carry no InChI, so an InChIKey or InChI is echoed back and never used to decide; " +
                                "resolution is by CAS, by element counts of the formula and by name.";
            bool any = !string.IsNullOrWhiteSpace(formula) || !string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(cas);
            if (!any)
            {
                if (!string.IsNullOrWhiteSpace(inchikey) || !string.IsNullOrWhiteSpace(inchi))
                    return new JObject { ["status"] = "unsupported_key", ["query"] = query, ["limitation"] = limitation, ["candidates"] = new JArray() };
                throw new ArgumentException("Give a formula, a name or a CAS number.");
            }

            var idx = Index();
            IEnumerable<Entry> cands = idx;
            string casQ = (cas ?? "").Trim();
            if (casQ.Length > 0) cands = cands.Where(e => string.Equals(e.Cas, casQ, StringComparison.OrdinalIgnoreCase));

            string hillQ = null;
            if (!string.IsNullOrWhiteSpace(formula))
            {
                var els = ParseFormula(formula);
                hillQ = Hill(els);
                cands = cands.Where(e => e.Hill == hillQ);
            }
            var byFormula = cands.ToList();

            string status;
            List<Entry> result = byFormula;
            JArray conflict = null;
            if (!string.IsNullOrWhiteSpace(name))
            {
                var n = name.Trim(); var nn = Norm(n);
                Func<IEnumerable<Entry>, List<Entry>> byName = pool =>
                {
                    var exact = pool.Where(e => string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (exact.Count > 0) return exact;
                    var norm = pool.Where(e => e.NormName == nn).ToList();
                    if (norm.Count > 0) return norm;
                    return pool.Where(e => e.NormName.Contains(nn) && nn.Length >= 4).ToList();
                };
                var inSet = byName(byFormula);
                if (inSet.Count > 0) result = inSet;
                else
                {
                    var outside = byName(idx.Where(e => casQ.Length == 0 || string.Equals(e.Cas, casQ, StringComparison.OrdinalIgnoreCase)));
                    if (outside.Count > 0 && hillQ != null)
                    {
                        conflict = new JArray(outside.Take(limit).Select(Describe));
                        result = new List<Entry>();
                    }
                    else if (hillQ == null) result = outside;
                }
            }

            if (conflict != null)
            {
                return new JObject
                {
                    ["status"] = "conflict", ["query"] = query,
                    ["message"] = $"the name matches {string.Join(", ", conflict.Select(c => (string)c["name"]))} (formula {string.Join(", ", conflict.Select(c => (string)c["hill_formula"]).Distinct())}) but the formula given is {hillQ}",
                    ["candidates"] = conflict, ["name_matches"] = conflict, ["limitation"] = limitation,
                };
            }

            // one substance under several databases is an alias, not an ambiguity: the same normalised name, or
            // the same CAS number, puts two entries in one group
            var groups = GroupSubstances(result);
            if (groups.Count == 1)
            {
                var g = groups[0].OrderBy(e => { var i = Array.IndexOf(DbOrder, e.Db); return i < 0 ? 99 : i; }).ToList();
                var best = g[0];
                var o = Describe(best);
                o["status"] = "resolved";
                o["aliases_same_cas"] = Json.Strings(g.Skip(1).Select(e => $"{e.Name} [{e.Db}]"));
                o["inchikey_echo"] = inchikey; o["inchikey_used"] = false;
                o["query"] = query; o["limitation"] = limitation;
                o["candidates"] = new JArray(g.Take(limit).Select(Describe));
                return o;
            }
            if (groups.Count > 1)
            {
                status = "ambiguous";
                var cand = groups.Select(g => g.OrderBy(e => { var i = Array.IndexOf(DbOrder, e.Db); return i < 0 ? 99 : i; }).First()).Take(limit).Select(Describe);
                return new JObject
                {
                    ["status"] = status, ["query"] = query, ["hill_formula"] = hillQ,
                    ["message"] = $"{groups.Count} databank compounds match; add the name or the CAS number to settle it",
                    ["candidates"] = new JArray(cand), ["limitation"] = limitation,
                };
            }

            // nothing: the closest names, as dwsim_thermo_add_compounds suggests them
            var closest = new List<string>();
            if (!string.IsNullOrWhiteSpace(name))
            {
                var n = name.Trim().ToLowerInvariant();
                closest = idx.Where(e => e.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(e => e.Name.Length).Take(10).Select(e => e.Name).ToList();
                if (closest.Count == 0)
                    closest = idx.Select(e => (e.Name, d: EditDistance(e.Name.ToLowerInvariant(), n))).Where(x => x.d <= Math.Max(2, n.Length / 3))
                                 .OrderBy(x => x.d).ThenBy(x => x.Name.Length).Take(10).Select(x => x.Name).ToList();
            }
            return new JObject
            {
                ["status"] = "not_found", ["query"] = query, ["hill_formula"] = hillQ,
                ["message"] = hillQ != null && closest.Count == 0 ? $"no databank compound has the formula {hillQ}" : "no databank compound matches",
                ["closest"] = Json.Strings(closest), ["candidates"] = new JArray(), ["limitation"] = limitation,
            };
        }

        private static List<List<Entry>> GroupSubstances(List<Entry> entries)
        {
            var groups = new List<List<Entry>>();
            foreach (var e in entries)
            {
                var hit = groups.FirstOrDefault(g => g.Any(x => x.NormName == e.NormName || (e.Cas.Length > 0 && x.Cas == e.Cas)));
                if (hit == null) groups.Add(new List<Entry> { e }); else hit.Add(e);
            }
            // merge transitively (A~B by name, B~C by CAS)
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < groups.Count && !merged; i++)
                    for (int j = i + 1; j < groups.Count && !merged; j++)
                        if (groups[i].Any(a => groups[j].Any(b => a.NormName == b.NormName || (a.Cas.Length > 0 && a.Cas == b.Cas))))
                        { groups[i].AddRange(groups[j]); groups.RemoveAt(j); merged = true; }
            }
            return groups;
        }

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
    }
}
