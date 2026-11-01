// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System.Collections.Generic;
using System.Linq;
using DWSIM.MCPServer;
using DWSIM.MCPServer.Sessions;
using DWSIM.MCPServer.Tools;
using DWSIM.MCPServer.Tools.Thermo;
using Xunit;

namespace DWSIM.MCP.Server.Tests
{
    [Collection("engine")]
    public class ToolInventoryTests
    {
        private readonly EngineFixture _f;
        public ToolInventoryTests(EngineFixture f) { _f = f; }

        /// <summary>Every upstream tool name is still served, so the image is a drop-in replacement.</summary>
        [Fact]
        public void ServesEveryUpstreamToolName()
        {
            var upstream = new ToolRegistry();
            foreach (var instance in ServerComposition.UpstreamToolInstances(_f.Sessions, _f.Automation, new DynamicsJobManager()))
                upstream.RegisterToolsFrom(instance);
            upstream.RegisterToolsFrom(new ThermoTools(_f.Sessions, _f.Automation)); // the one upstream class we do not register

            var ours = _f.Registry.ListTools().Select(t => t["name"].ToString()).ToHashSet();
            var theirs = upstream.ListTools().Select(t => t["name"].ToString()).ToList();
            var missing = theirs.Where(n => !ours.Contains(n)).ToList();
            Assert.True(missing.Count == 0, "Upstream tools not served: " + string.Join(", ", missing));
            Assert.True(ours.Count > theirs.Count, $"Expected more tools than upstream ({theirs.Count}), got {ours.Count}.");
        }

        [Fact]
        public void EveryToolHasDescriptionAndSchema()
        {
            foreach (var t in _f.Registry.ListTools())
            {
                Assert.False(string.IsNullOrWhiteSpace(t["description"]?.ToString()), $"{t["name"]} has no description");
                Assert.Equal("object", t["inputSchema"]["type"].ToString());
                Assert.StartsWith("dwsim_", t["name"].ToString());
            }
        }

        [Fact]
        public void ExtensionToolsArePresent()
        {
            var names = _f.Registry.ListTools().Select(t => t["name"].ToString()).ToHashSet();
            var expected = new[]
            {
                "dwsim_thermo_set_flash", "dwsim_thermo_get_bips", "dwsim_thermo_set_bips", "dwsim_thermo_estimate_bips",
                "dwsim_reaction_add_kinetic", "dwsim_reaction_add_hetcat", "dwsim_reaction_set_equilibrium_constant",
                "dwsim_column_create", "dwsim_column_connect", "dwsim_column_set_specs", "dwsim_column_shortcut_configure",
                "dwsim_compsep_set_specs", "dwsim_unitop_set_mode", "dwsim_vessel_configure",
                "dwsim_compound_import_json", "dwsim_adjust_create", "dwsim_spec_create", "dwsim_recycle_configure",
                "dwsim_study_sweep", "dwsim_object_get", "dwsim_object_set", "dwsim_object_invoke", "dwsim_object_describe",
                "dwsim_flowsheet_export_xml", "dwsim_flowsheet_get_xml_section", "dwsim_script_run",
                "dwsim_thermo_resolve_compound", "dwsim_hx_configure", "dwsim_hx_tie_halves", "dwsim_plant_report",
                "dwsim_plant_balance", "dwsim_plant_check", "dwsim_reaction_from_equation", "dwsim_recycle_seed",
            };
            var missing = expected.Where(n => !names.Contains(n)).ToList();
            Assert.True(missing.Count == 0, "Missing: " + string.Join(", ", missing));
        }
    }
}
