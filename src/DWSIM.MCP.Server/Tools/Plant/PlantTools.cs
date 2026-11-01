// Copyright 2026 friendlypeps and contributors.
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
using DWSIM.Interfaces.Enums.GraphicObjects;
using DWSIM.MCPServer.Sessions;
using DWSIM.MCPServer.Tools.Common;
using DWSIM.UnitOperations.Reactors;
using DWSIM.UnitOperations.SpecialOps;
using DWSIM.UnitOperations.Streams;
using DWSIM.UnitOperations.UnitOperations;
using FluentFlowsheet = DWSIM.Automation.FluentAPI.Flowsheet;

namespace DWSIM.MCPServer.Tools.Plant
{
    /// <summary>
    /// Plant-level views of a solved flowsheet: one report of every stream and duty, the mass and element
    /// balance over the plant boundary, and the checks the engine's own diagnostics do not make (a
    /// compressor fed liquid, a reactor outlet without the reaction's phase, a unit outside the window it
    /// was selected for). These are what VDI 2776 step 5c reads back after the solve.
    /// </summary>
    public class PlantTools
    {
        private readonly SessionManager _sessions;
        public PlantTools(SessionManager sessions) { _sessions = sessions; }

        // ------------------------------------------------------------------ boundary helpers

        internal static ISimulationObject Upstream(IFlowsheet f, ISimulationObject stream)
        {
            var cons = stream.GraphicObject?.InputConnectors;
            if (cons == null || cons.Count == 0) return null;
            var c = cons[0];
            var other = c.IsAttached ? c.AttachedConnector?.AttachedFrom : null;
            return other != null && f.SimulationObjects.TryGetValue(other.Name, out var o) ? o : null;
        }

        internal static ISimulationObject Downstream(IFlowsheet f, ISimulationObject stream)
        {
            var cons = stream.GraphicObject?.OutputConnectors;
            if (cons == null || cons.Count == 0) return null;
            var c = cons[0];
            var other = c.IsAttached ? c.AttachedConnector?.AttachedTo : null;
            return other != null && f.SimulationObjects.TryGetValue(other.Name, out var o) ? o : null;
        }

        internal static IEnumerable<IMaterialStream> Attached(IFlowsheet f, ISimulationObject unit, bool inlets)
        {
            var g = unit.GraphicObject;
            if (g == null) yield break;
            var ports = inlets ? g.InputConnectors : g.OutputConnectors;
            foreach (var port in ports.Where(p => p.IsAttached && (inlets ? p.Type == ConType.ConIn : p.Type == ConType.ConOut)))
            {
                var other = inlets ? port.AttachedConnector?.AttachedFrom : port.AttachedConnector?.AttachedTo;
                if (other == null || !f.SimulationObjects.TryGetValue(other.Name, out var s)) continue;
                if (s is IMaterialStream ms) yield return ms;
            }
        }

        private static string Tag(ISimulationObject o) => o?.GraphicObject?.Tag ?? o?.Name;

        private static double Frac(IMaterialStream ms, int phase)
        {
            try { return ms.Phases[phase].Properties.molarfraction.GetValueOrDefault(); } catch { return double.NaN; }
        }

        private static double Safe(Func<double> f) { try { return f(); } catch { return double.NaN; } }

        /// <summary>Molar flow of each compound in the mixture, mol/s.</summary>
        private static Dictionary<string, double> CompoundFlows(IMaterialStream ms)
        {
            var total = Safe(ms.GetMolarFlow);
            var d = new Dictionary<string, double>();
            foreach (var c in ms.Phases[0].Compounds.Values)
                d[c.Name] = total * c.MoleFraction.GetValueOrDefault();
            return d;
        }

        private static JObject StreamJson(IFlowsheet f, IMaterialStream ms, bool phases)
        {
            var up = Upstream(f, ms as ISimulationObject);
            var down = Downstream(f, ms as ISimulationObject);
            var o = new JObject
            {
                ["tag"] = Tag(ms as ISimulationObject),
                ["from"] = up == null ? null : Tag(up),
                ["to"] = down == null ? null : Tag(down),
                ["boundary"] = up == null && down != null ? "feed" : up != null && down == null ? "product" : up == null && down == null ? "unconnected" : "internal",
                ["calculated"] = (ms as ISimulationObject).Calculated,
                ["T_K"] = Json.Num(Safe(ms.GetTemperature)),
                ["P_Pa"] = Json.Num(Safe(ms.GetPressure)),
                ["mass_flow_kg_s"] = Json.Num(Safe(ms.GetMassFlow)),
                ["molar_flow_mol_s"] = Json.Num(Safe(ms.GetMolarFlow)),
                ["vapor_fraction"] = Json.Num(Frac(ms, 2)),
                ["liquid1_fraction"] = Json.Num(Frac(ms, 3)),
                ["liquid2_fraction"] = Json.Num(Frac(ms, 4)),
                ["solid_fraction"] = Json.Num(Frac(ms, 7)),
                ["enthalpy_kJ_kg"] = Json.Num(Safe(() => ms.Phases[0].Properties.enthalpy.GetValueOrDefault())),
                ["x"] = new JObject(ms.Phases[0].Compounds.Values.Select(c => new JProperty(c.Name, Json.Num(c.MoleFraction.GetValueOrDefault())))),
                ["w"] = new JObject(ms.Phases[0].Compounds.Values.Select(c => new JProperty(c.Name, Json.Num(c.MassFraction.GetValueOrDefault())))),
            };
            if (phases)
            {
                var ph = new JObject();
                foreach (var (idx, name) in new[] { (2, "Vapor"), (3, "Liquid1"), (4, "Liquid2"), (7, "Solid") })
                {
                    if (!(Frac(ms, idx) > 1e-9)) continue;
                    ph[name] = new JObject
                    {
                        ["fraction"] = Json.Num(Frac(ms, idx)),
                        ["x"] = new JObject(ms.Phases[idx].Compounds.Values.Select(c => new JProperty(c.Name, Json.Num(c.MoleFraction.GetValueOrDefault())))),
                    };
                }
                o["phases"] = ph;
            }
            return o;
        }

        // ------------------------------------------------------------------ report

        [McpTool("dwsim_plant_report",
            "One report of a solved flowsheet: every material stream (from/to unit, boundary role, T, p, flows, phase " +
            "fractions, x and w), every duty with its sign (+ added to the process, - removed), every unit's state, " +
            "every Recycle's convergence, and the product rate against the rate asked when product_stream (and " +
            "optionally product_compound, asked_product_rate_kg_s) is given. Values are SI.")]
        public JObject Report(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Tag of the product stream", Required = false)] string product_stream = null,
            [McpParam("Compound whose flow in the product stream is the product (default: the whole stream)", Required = false)] string product_compound = null,
            [McpParam("Product rate asked, kg/s, for the achieved/asked ratio", Required = false, JsonType = "number")] double? asked_product_rate_kg_s = null,
            [McpParam("Include per-phase compositions (default false)", Required = false, JsonType = "boolean")] bool include_phase_compositions = false,
            [McpParam("Only these stream tags (default all)", Required = false, JsonType = "array")] string[] streams = null)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var f = fs.Inner;
            var filter = streams == null || streams.Length == 0 ? null : new HashSet<string>(streams, StringComparer.OrdinalIgnoreCase);

            var streamArr = new JArray();
            foreach (var ms in f.SimulationObjects.Values.OfType<IMaterialStream>().OrderBy(s => Tag(s as ISimulationObject), StringComparer.Ordinal))
            {
                if (filter != null && !filter.Contains(Tag(ms as ISimulationObject))) continue;
                streamArr.Add(StreamJson(f, ms, include_phase_compositions));
            }

            var duties = new JArray();
            double sumIn = 0, sumOut = 0;
            foreach (var es in f.SimulationObjects.Values.OfType<EnergyStream>())
            {
                var feedsUnit = Downstream(f, es);  // the stream flows into a unit: heat added to the process
                var madeByUnit = Upstream(f, es);   // a unit produces it: heat removed
                var q = es.EnergyFlow.GetValueOrDefault();
                double signed = feedsUnit != null ? q : madeByUnit != null ? -q : q;
                if (feedsUnit != null) sumIn += q; else if (madeByUnit != null) sumOut += q;
                duties.Add(new JObject
                {
                    ["tag"] = Tag(es), ["unit"] = Tag(feedsUnit ?? madeByUnit), ["side"] = feedsUnit != null ? "feed" : madeByUnit != null ? "product" : "unconnected",
                    ["duty_kW"] = Json.Num(q), ["signed_kW"] = Json.Num(signed), ["calculated"] = es.Calculated,
                });
            }
            foreach (var hx in f.SimulationObjects.Values.OfType<HeatExchanger>())
                duties.Add(new JObject { ["tag"] = Tag(hx), ["unit"] = Tag(hx), ["side"] = "internal", ["exchanged_kW"] = Json.Num(hx.Q.GetValueOrDefault()), ["calculated"] = hx.Calculated });
            foreach (var col in f.SimulationObjects.Values.OfType<Column>())
            {
                duties.Add(new JObject { ["tag"] = Tag(col) + ":condenser", ["unit"] = Tag(col), ["side"] = "product", ["duty_kW"] = Json.Num(Math.Abs(col.CondenserDuty)), ["signed_kW"] = Json.Num(-Math.Abs(col.CondenserDuty)), ["calculated"] = col.Calculated });
                duties.Add(new JObject { ["tag"] = Tag(col) + ":reboiler", ["unit"] = Tag(col), ["side"] = "feed", ["duty_kW"] = Json.Num(Math.Abs(col.ReboilerDuty)), ["signed_kW"] = Json.Num(Math.Abs(col.ReboilerDuty)), ["calculated"] = col.Calculated });
            }

            var units = new JArray();
            bool allSolved = true;
            foreach (var o in f.SimulationObjects.Values)
            {
                if (o is IMaterialStream || o is EnergyStream) continue;
                if (!o.Calculated || !string.IsNullOrEmpty(o.ErrorMessage)) allSolved = false;
                units.Add(new JObject { ["tag"] = Tag(o), ["type"] = o.GraphicObject?.ObjectType.ToString(), ["calculated"] = o.Calculated, ["error"] = o.ErrorMessage ?? "" });
            }

            var recycles = new JArray(f.SimulationObjects.Values.OfType<Recycle>().Select(r => new JObject
            {
                ["tag"] = Tag(r), ["converged"] = r.Converged, ["iterations"] = r.IterationCount,
                ["outlet_attached"] = r.GraphicObject.OutputConnectors.Count > 0 && r.GraphicObject.OutputConnectors[0].IsAttached,
            }));

            var result = new JObject
            {
                ["solved"] = allSolved,
                ["streams"] = streamArr,
                ["duties"] = duties,
                ["energy_kW"] = new JObject { ["added"] = Json.Num(sumIn), ["removed"] = Json.Num(sumOut), ["net"] = Json.Num(sumIn - sumOut) },
                ["units"] = units,
                ["recycles"] = recycles,
            };

            if (!string.IsNullOrWhiteSpace(product_stream))
            {
                var ps = Find.As<IMaterialStream>(fs, product_stream, "product stream");
                var mass = Safe(ps.GetMassFlow);
                var mol = Safe(ps.GetMolarFlow);
                double pMass = mass, pMol = mol, purityW = 1, purityX = 1;
                if (!string.IsNullOrWhiteSpace(product_compound))
                {
                    var cp = Find.Compound(fs, product_compound);
                    var c = ps.Phases[0].Compounds[cp.Name];
                    purityW = c.MassFraction.GetValueOrDefault(); purityX = c.MoleFraction.GetValueOrDefault();
                    pMass = mass * purityW; pMol = mol * purityX;
                }
                var prod = new JObject
                {
                    ["stream"] = product_stream, ["compound"] = product_compound,
                    ["mass_flow_kg_s"] = Json.Num(pMass), ["molar_flow_mol_s"] = Json.Num(pMol),
                    ["purity_mass"] = Json.Num(purityW), ["purity_mole"] = Json.Num(purityX),
                };
                if (asked_product_rate_kg_s.HasValue && asked_product_rate_kg_s.Value > 0)
                {
                    prod["asked_kg_s"] = asked_product_rate_kg_s.Value;
                    prod["ratio_to_asked"] = Json.Num(pMass / asked_product_rate_kg_s.Value);
                }
                result["product"] = prod;
            }
            return result;
        }

        // ------------------------------------------------------------------ balance

        [McpTool("dwsim_plant_balance",
            "Mass, compound and element balance over the plant boundary of a solved flowsheet: feeds are material " +
            "streams no unit produces, products are streams no unit takes. Compounds that take part in a reaction " +
            "are marked, so the element rows are the hard check. open_loops = [{out, in}] names tear pairs (in may be omitted when nothing re-enters; both stay in the plant sums; a " +
            "product that would feed a recycle inlet) that are netted out and reported as the mismatch of a loop " +
            "left open. Energy in/out of boundary energy streams is reported as information.")]
        public JObject Balance(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Open loops as [{out: product stream tag, in: feed stream tag}]", Required = false, JsonType = "array")] JArray open_loops = null,
            [McpParam("Relative tolerance for 'ok' (default 1e-3)", Required = false, JsonType = "number")] double tolerance_rel = 1e-3)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var f = fs.Inner;
            var all = f.SimulationObjects.Values.OfType<IMaterialStream>().ToList();
            var feeds = all.Where(s => Upstream(f, s as ISimulationObject) == null && Downstream(f, s as ISimulationObject) != null).ToList();
            var products = all.Where(s => Upstream(f, s as ISimulationObject) != null && Downstream(f, s as ISimulationObject) == null).ToList();

            var loopPairs = new List<(IMaterialStream outS, IMaterialStream inS)>();
            if (open_loops != null)
            {
                foreach (var t in open_loops.OfType<JObject>())
                {
                    var o = Find.As<IMaterialStream>(fs, t.Value<string>("out"), "open-loop outlet stream");
                    var inTag = t.Value<string>("in");
                    var i = string.IsNullOrWhiteSpace(inTag) ? null : Find.As<IMaterialStream>(fs, inTag, "open-loop inlet stream");
                    loopPairs.Add((o, i));
                    // the tear streams stay in the plant sums: what leaves through an open loop does leave the plant, so the
                    // closure below is the physical balance; the pair's own mismatch is listed in open_loop_mismatch
                }
            }

            var compounds = Find.CompoundNames(fs);
            var reacting = new HashSet<string>(f.Reactions.Values.SelectMany(r => r.Components.Keys));
            var inC = compounds.ToDictionary(c => c, c => 0.0);
            var outC = compounds.ToDictionary(c => c, c => 0.0);
            double massIn = 0, massOut = 0;
            foreach (var s in feeds) { massIn += Safe(s.GetMassFlow); foreach (var kv in CompoundFlows(s)) inC[kv.Key] += kv.Value; }
            foreach (var s in products) { massOut += Safe(s.GetMassFlow); foreach (var kv in CompoundFlows(s)) outC[kv.Key] += kv.Value; }

            var elIn = new Dictionary<string, double>();
            var elOut = new Dictionary<string, double>();
            foreach (var c in compounds)
            {
                var els = f.SelectedCompounds[c].Elements;
                if (els == null) continue;
                foreach (DictionaryEntry e in els)
                {
                    var k = (string)e.Key; var n = Convert.ToDouble(e.Value);
                    elIn[k] = elIn.GetValueOrDefault(k) + n * inC[c];
                    elOut[k] = elOut.GetValueOrDefault(k) + n * outC[c];
                }
            }

            double Rel(double a, double b) { var m = Math.Max(Math.Abs(a), Math.Abs(b)); return m < 1e-12 ? 0 : Math.Abs(a - b) / m; }
            var compoundRows = new JArray(compounds.Select(c => new JObject
            {
                ["compound"] = c, ["in_mol_s"] = Json.Num(inC[c]), ["out_mol_s"] = Json.Num(outC[c]),
                ["difference_mol_s"] = Json.Num(outC[c] - inC[c]), ["reacting"] = reacting.Contains(c),
                ["rel"] = Json.Num(Rel(inC[c], outC[c])),
            }));
            var elementRows = new JArray(elIn.Keys.Union(elOut.Keys).OrderBy(k => k).Select(k => new JObject
            {
                ["element"] = k, ["in_mol_s"] = Json.Num(elIn.GetValueOrDefault(k)), ["out_mol_s"] = Json.Num(elOut.GetValueOrDefault(k)),
                ["rel"] = Json.Num(Rel(elIn.GetValueOrDefault(k), elOut.GetValueOrDefault(k))),
            }));
            double massRel = Rel(massIn, massOut);
            double worstEl = elementRows.Select(r => (double?)r["rel"] ?? 0).DefaultIfEmpty(0).Max();
            double worstNonReacting = compoundRows.Where(r => !(bool)r["reacting"]).Select(r => (double?)r["rel"] ?? 0).DefaultIfEmpty(0).Max();

            var loops = new JArray();
            foreach (var (outS, inS) in loopPairs)
            {
                var fo = CompoundFlows(outS); var fi = inS == null ? new Dictionary<string, double>() : CompoundFlows(inS);
                double inFlow = inS == null ? 0 : Safe(inS.GetMolarFlow);
                loops.Add(new JObject
                {
                    ["out"] = Tag(outS as ISimulationObject), ["in"] = inS == null ? null : Tag(inS as ISimulationObject),
                    ["out_mol_s"] = Json.Num(Safe(outS.GetMolarFlow)), ["in_mol_s"] = Json.Num(inFlow),
                    ["out_kg_s"] = Json.Num(Safe(outS.GetMassFlow)),
                    ["mismatch_mol_s"] = new JObject(compounds.Select(c => new JProperty(c, Json.Num(fo.GetValueOrDefault(c) - fi.GetValueOrDefault(c))))),
                    ["mismatch_rel"] = Json.Num(inS == null ? 1.0 : Rel(Safe(outS.GetMolarFlow), inFlow)),
                    ["note"] = inS == null ? "loop left open: the stream leaves the plant and nothing re-enters; the fresh feeds cover it" : null,
                });
            }

            double eIn = 0, eOut = 0;
            foreach (var es in f.SimulationObjects.Values.OfType<EnergyStream>())
            {
                if (Downstream(f, es) != null && Upstream(f, es) == null) eIn += es.EnergyFlow.GetValueOrDefault();
                else if (Upstream(f, es) != null && Downstream(f, es) == null) eOut += es.EnergyFlow.GetValueOrDefault();
            }
            double hIn = feeds.Sum(s => Safe(() => s.Phases[0].Properties.enthalpy.GetValueOrDefault() * s.GetMassFlow()));
            double hOut = products.Sum(s => Safe(() => s.Phases[0].Properties.enthalpy.GetValueOrDefault() * s.GetMassFlow()));

            return new JObject
            {
                ["feeds"] = Json.Strings(feeds.Select(s => Tag(s as ISimulationObject))),
                ["products"] = Json.Strings(products.Select(s => Tag(s as ISimulationObject))),
                ["mass_kg_s"] = new JObject { ["in"] = Json.Num(massIn), ["out"] = Json.Num(massOut), ["rel"] = Json.Num(massRel) },
                ["compounds"] = compoundRows,
                ["elements"] = elementRows,
                ["closure"] = new JObject
                {
                    ["mass_rel"] = Json.Num(massRel), ["worst_element_rel"] = Json.Num(worstEl), ["worst_nonreacting_compound_rel"] = Json.Num(worstNonReacting),
                    ["ok"] = massRel <= tolerance_rel && worstEl <= tolerance_rel && worstNonReacting <= tolerance_rel, ["tolerance_rel"] = tolerance_rel,
                },
                ["open_loop_mismatch"] = loops,
                ["energy_kW"] = new JObject { ["duties_in"] = Json.Num(eIn), ["duties_out"] = Json.Num(eOut), ["enthalpy_in"] = Json.Num(hIn), ["enthalpy_out"] = Json.Num(hOut), ["residual"] = Json.Num(hIn + eIn - hOut - eOut) },
            };
        }

        // ------------------------------------------------------------------ check

        private static readonly Dictionary<string, string> Explanations = new Dictionary<string, string>
        {
            ["PUMP_VAPOR_INLET"] = "A pump moves liquid; vapour at its inlet means the stream upstream is too hot or at too low a pressure.",
            ["COMPRESSOR_LIQUID_INLET"] = "A compressor takes vapour; liquid at its inlet means a knock-out or a heater is missing upstream. DWSIM converges anyway, so this is only visible here.",
            ["REACTOR_PHASE_MISMATCH"] = "The reaction is defined in a phase the reactor outlet does not hold; the engine computes with the overall composition and does not notice.",
            ["DECANTER_ONE_LIQUID"] = "A vessel with two liquid outlets whose feed has one liquid phase: check the flash (VLLE) and the binary parameters.",
            ["WINDOW_VIOLATION"] = "The quantity is outside the window the unit was selected for (module window or document requirement).",
            ["WINDOW_NEAR"] = "The quantity misses its window by less than the engine's resolution (1 K, or 2 % of a pressure; near_si overrides).",
            ["WINDOW_OK"] = "The quantity is inside its window.",
            ["UNIT_NOT_CONVERGED"] = "The unit did not finish its calculation; everything downstream is a casualty.",
            ["RECYCLE_NOT_CONVERGED"] = "The tear stream did not settle; the loop's values are not a steady state.",
            ["OPEN_LOOP"] = "A Recycle block whose outlet feeds nothing: the loop is open and its outlet is a product of the open-loop plant.",
        };

        private static JObject F(string code, string severity, string obj, string message, string fix = "") =>
            new JObject { ["code"] = code, ["severity"] = severity, ["object"] = obj, ["message"] = message, ["fix"] = fix };

        [McpTool("dwsim_plant_check",
            "Checks after a solve that the engine does not make: a pump with vapour at its inlet, a compressor or " +
            "expander with liquid, a reactor whose combined outlet lacks the phase its reactions are defined in, a " +
            "vessel with two liquid outlets but one liquid, units not converged, Recycle blocks not converged or " +
            "left open, and any quantity against a window: windows = [{object, quantity, min_si, max_si, stream?}] " +
            "with quantity temperature | pressure | inlet_temperature | outlet_temperature | inlet_pressure | " +
            "outlet_pressure | top_temperature | bottom_temperature | top_pressure | bottom_pressure (a null bound " +
            "is open). Findings use the same shape as dwsim_flowsheet_check.")]
        public JObject Check(
            [McpParam("Flowsheet handle")] string flowsheet_id,
            [McpParam("Windows to check as [{object, quantity, min_si, max_si, stream}]", Required = false, JsonType = "array")] JArray windows = null,
            [McpParam("Minimum molar fraction of the reaction phase at a reactor outlet (default 0.5 warns, 0.001 errors)", Required = false, JsonType = "number")] double reaction_phase_tolerance = 0.5)
        {
            var fs = _sessions.GetFlowsheet(flowsheet_id);
            var f = fs.Inner;
            var findings = new List<JObject>();

            foreach (var o in f.SimulationObjects.Values)
            {
                var tag = Tag(o);
                if (o is IMaterialStream || o is EnergyStream) continue;
                if (!(o is Recycle) && (!o.Calculated || !string.IsNullOrEmpty(o.ErrorMessage)))
                    findings.Add(F("UNIT_NOT_CONVERGED", "error", tag, string.IsNullOrEmpty(o.ErrorMessage) ? "not calculated" : o.ErrorMessage, "Solve the flowsheet; read dwsim_solve_diagnostics."));

                if (o is Pump)
                {
                    var inlet = Attached(f, o, true).FirstOrDefault();
                    var vf = inlet == null ? double.NaN : Frac(inlet, 2);
                    if (vf > 0.001)
                        findings.Add(F("PUMP_VAPOR_INLET", vf > 0.999 ? "error" : "warning", tag, $"vapour molar fraction {vf:0.####} at the pump inlet ({Tag(inlet as ISimulationObject)})", "Cool or pressurise the stream upstream, or use a compressor."));
                }
                else if (o is Compressor || o is Expander)
                {
                    var inlet = Attached(f, o, true).FirstOrDefault();
                    var lf = inlet == null ? double.NaN : Frac(inlet, 1);
                    if (lf > 0.001)
                        findings.Add(F("COMPRESSOR_LIQUID_INLET", lf > 0.05 ? "error" : "warning", tag, $"liquid molar fraction {lf:0.####} at the inlet ({Tag(inlet as ISimulationObject)})", "Put a knock-out vessel or a heater upstream, or use a pump for a liquid."));
                }
                else if (o is Reactor rx)
                {
                    var outs = Attached(f, o, false).ToList();
                    double tot = outs.Sum(s => Safe(s.GetMolarFlow));
                    if (tot > 0 && f.ReactionSets.TryGetValue(rx.ReactionSetID ?? "", out var set))
                    {
                        double liq = outs.Sum(s => Safe(s.GetMolarFlow) * Frac(s, 1)) / tot;
                        double vap = outs.Sum(s => Safe(s.GetMolarFlow) * Frac(s, 2)) / tot;
                        foreach (var rb in set.Reactions.Values.Where(r => r.IsActive))
                        {
                            if (!f.Reactions.TryGetValue(rb.ReactionID, out var r)) continue;
                            double have = r.ReactionPhase == ReactionPhase.Liquid ? liq : r.ReactionPhase == ReactionPhase.Vapor ? vap : double.NaN;
                            if (double.IsNaN(have)) continue;
                            if (have < reaction_phase_tolerance)
                                findings.Add(F("REACTOR_PHASE_MISMATCH", have < 0.001 ? "error" : "warning", tag,
                                    $"reaction '{r.Name}' is defined in the {r.ReactionPhase} phase but the outlet holds {have:0.###} of it (liquid {liq:0.###}, vapour {vap:0.###})",
                                    "Change the reactor temperature or pressure, or the reaction phase."));
                        }
                    }
                }
                else if (o is Vessel)
                {
                    var outs = o.GraphicObject.OutputConnectors;
                    bool twoLiquids = outs.Count > 2 && outs[1].IsAttached && outs[2].IsAttached;
                    var inlet = Attached(f, o, true).FirstOrDefault();
                    if (twoLiquids && inlet != null && !(Frac(inlet, 4) > 1e-6))
                        findings.Add(F("DECANTER_ONE_LIQUID", "warning", tag, "two liquid outlets are connected but the feed has one liquid phase", "Set dwsim_thermo_set_flash equilibrium_type=VLLE and check dwsim_thermo_get_bips for the pair that should split."));
                }
                else if (o is Recycle rc)
                {
                    bool attached = rc.GraphicObject.OutputConnectors.Count > 0 && rc.GraphicObject.OutputConnectors[0].IsAttached;
                    if (!attached) findings.Add(F("OPEN_LOOP", "info", tag, "the Recycle outlet feeds nothing: the loop is open"));
                    else if (!rc.Converged) findings.Add(F("RECYCLE_NOT_CONVERGED", "error", tag, $"not converged after {rc.IterationCount} iterations", "Loosen dwsim_recycle_configure tolerances, seed the tear stream, or leave the loop open and report it."));
                }
            }

            int checkedWindows = 0;
            if (windows != null)
            {
                foreach (var w in windows.OfType<JObject>())
                {
                    checkedWindows++;
                    var objTag = w.Value<string>("object");
                    var q = (w.Value<string>("quantity") ?? "temperature").Trim().ToLowerInvariant();
                    double? min = w["min_si"]?.Type == JTokenType.Null ? null : w["min_si"]?.Value<double?>();
                    double? max = w["max_si"]?.Type == JTokenType.Null ? null : w["max_si"]?.Value<double?>();
                    var obj = Find.ByTagOrId(fs, objTag, "object");
                    double value = WindowValue(f, fs, obj, q, w.Value<string>("stream"));
                    double miss = Math.Max(min.HasValue ? min.Value - value : 0, max.HasValue ? value - max.Value : 0);
                    bool outside = miss > 1e-9;
                    // a miss inside the engine's own resolution (1 K, or 2 % of a pressure) is a warning, not an error: the
                    // column bottom at 78.1 C against a document window of 77-78 C is a reading to report, not a wrong plant
                    double near = w["near_si"]?.Value<double?>() ?? (q.Contains("pressure") ? 0.02 * Math.Abs(value) : 1.0);
                    bool nearMiss = outside && miss <= near;
                    var window = $"[{(min.HasValue ? min.Value.ToString("0.###") : "open")}, {(max.HasValue ? max.Value.ToString("0.###") : "open")}]";
                    findings.Add(F(!outside ? "WINDOW_OK" : nearMiss ? "WINDOW_NEAR" : "WINDOW_VIOLATION", !outside ? "info" : nearMiss ? "warning" : "error", objTag,
                        $"{q} = {value:0.###} SI, window {window}" + (outside ? $", miss {miss:0.###}" : ""),
                        !outside ? "" : nearMiss ? "Inside the engine's resolution of the window; state it in the report." : "Move the setpoint inside the window or choose another module."));
                }
            }

            var errors = findings.Count(x => (string)x["severity"] == "error");
            var warns = findings.Count(x => (string)x["severity"] == "warning");
            return new JObject
            {
                ["ok"] = errors == 0,
                ["errors"] = errors,
                ["warnings"] = warns,
                ["windows_checked"] = checkedWindows,
                ["findings"] = new JArray(findings),
                ["explanations"] = new JObject(findings.Select(x => (string)x["code"]).Distinct().Select(c => new JProperty(c, Explanations.GetValueOrDefault(c, "")))),
            };
        }

        private static double WindowValue(IFlowsheet f, FluentFlowsheet fs, ISimulationObject obj, string q, string streamTag)
        {
            IMaterialStream pick(bool inlet)
            {
                if (!string.IsNullOrWhiteSpace(streamTag)) return Find.As<IMaterialStream>(fs, streamTag, "stream");
                if (obj is IMaterialStream ms) return ms;
                return Attached(f, obj, inlet).FirstOrDefault() ?? throw new ArgumentException($"'{Tag(obj)}' has no attached {(inlet ? "inlet" : "outlet")} stream to read {q} from.");
            }
            switch (q)
            {
                case "temperature": case "outlet_temperature": return Safe(pick(false).GetTemperature);
                case "inlet_temperature": return Safe(pick(true).GetTemperature);
                case "pressure": case "outlet_pressure": return Safe(pick(false).GetPressure);
                case "inlet_pressure": return Safe(pick(true).GetPressure);
                case "top_temperature": return Col(obj).Stages[0].T;
                case "bottom_temperature": { var c = Col(obj); return c.Stages[c.Stages.Count - 1].T; }
                case "top_pressure": return Col(obj).Stages[0].P;
                case "bottom_pressure": { var c = Col(obj); return c.Stages[c.Stages.Count - 1].P; }
                default: throw new ArgumentException($"'{q}' is not a window quantity. Use temperature, pressure, inlet_/outlet_temperature, inlet_/outlet_pressure, top_/bottom_temperature, top_/bottom_pressure.");
            }
        }

        private static Column Col(ISimulationObject obj) =>
            obj as Column ?? throw new ArgumentException($"'{Tag(obj)}' is not a rigorous column; top_/bottom_ quantities need one.");
    }
}
