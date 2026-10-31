// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System.Linq;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums;
using FluentFlowsheet = DWSIM.Automation.FluentAPI.Flowsheet;

namespace DWSIM.MCPServer.Tools.Common
{
    /// <summary>
    /// Adding a compound the way the desktop app does (ui/.../SimulationSetupShared.cs): the engine's
    /// AddCompound only registers it, and a material stream that lacks a compound key throws inside
    /// every unit operation downstream. So every phase of every existing stream gets the key too.
    /// </summary>
    public static class Compounds
    {
        /// <summary>Adds the compound (already resolved to a database or user name) and returns how many streams were updated.</summary>
        public static int Add(FluentFlowsheet fs, string name)
        {
            var flowsheet = fs.Inner;
            ICompoundConstantProperties cp;
            if (flowsheet.SelectedCompounds.ContainsKey(name))
                cp = flowsheet.SelectedCompounds[name];
            else
            {
                try { flowsheet.RegisterSnapshot(SnapshotType.Compounds); } catch { }
                cp = flowsheet.AddCompound(name);
            }
            return AddToStreams(fs, cp);
        }

        /// <summary>Gives every phase of every material stream the compound, where it is missing.</summary>
        public static int AddToStreams(FluentFlowsheet fs, ICompoundConstantProperties cp)
        {
            int streams = 0;
            foreach (var obj in fs.Inner.SimulationObjects.Values)
            {
                if (!(obj is IMaterialStream ms)) continue;
                bool touched = false;
                foreach (var phase in ms.Phases.Values)
                {
                    if (phase.Compounds.ContainsKey(cp.Name)) continue;
                    var c = new DWSIM.Thermodynamics.BaseClasses.Compound(cp.Name, "") { ConstantProperties = cp };
                    phase.Compounds.Add(cp.Name, c);
                    touched = true;
                }
                if (touched) { streams++; obj.Calculated = false; }
            }
            if (streams > 0) fs.Inner.ResetCalculationStatus();
            return streams;
        }

        /// <summary>Material streams whose phases lack one of the selected compounds (a loaded or edited flowsheet).</summary>
        public static int Repair(FluentFlowsheet fs)
        {
            int streams = 0;
            foreach (var cp in fs.Inner.SelectedCompounds.Values.ToList())
                streams += AddToStreams(fs, cp);
            return streams;
        }
    }
}
