// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.Reflection;
using DWSIM.Automation;
using DWSIM.MCPServer.Sessions;
using DWSIM.MCPServer.Tools;
using DWSIM.MCPServer.Tools.Dynamics;
using DWSIM.MCPServer.Tools.Flowsheet;
using DWSIM.MCPServer.Tools.Graphics;
using DWSIM.MCPServer.Tools.Reactions;
using DWSIM.MCPServer.Tools.Solve;
using DWSIM.MCPServer.Tools.Streams;
using DWSIM.MCPServer.Tools.UnitOps;

namespace DWSIM.MCPServer
{
    /// <summary>
    /// Assembles the tool registry: every upstream tool class except <c>ThermoTools</c>, whose four
    /// names <c>Tools.Thermo.ThermoToolsEx</c> carries with the same signatures (plus a replace
    /// semantics the upstream one lacks), and then the extension classes. Tests call
    /// <see cref="BuildRegistry"/> directly; the entry point calls <see cref="Boot"/>.
    /// </summary>
    public static class ServerComposition
    {
        /// <summary>The informational version of this build, e.g. 10.2.12-fp.1.</summary>
        public static string Version =>
            typeof(ServerComposition).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ServerComposition).Assembly.GetName().Version?.ToString() ?? "unknown";

        /// <summary>Loads the engine (compounds, property packages) and builds the registry.</summary>
        public static ToolRegistry Boot(out SessionManager sessions)
        {
            Console.Error.WriteLine("[dwsim-mcp] Initializing DWSIM automation engine...");
            DWSIM.Automation.FluentAPI.Flowsheet.RegisterAssemblyResolver();

            Console.Error.WriteLine("[dwsim-mcp] Loading compounds and property packages...");
            var automation = new Automation3();

            sessions = new SessionManager();
            return BuildRegistry(sessions, automation, new DynamicsJobManager());
        }

        /// <summary>The upstream tool classes this server registers as they are.</summary>
        public static object[] UpstreamToolInstances(SessionManager sessions, Automation3 automation, DynamicsJobManager dynamicsJobs) => new object[]
        {
            new FlowsheetTools(sessions),
            new StreamTools(sessions),
            new UnitOpTools(sessions),
            new SolveTools(sessions),
            new GraphicTools(sessions),
            new DynamicsTools(sessions, dynamicsJobs),
            new ReactionTools(sessions),
        };

        /// <summary>The classes this server adds. Each one is a group of related tools.</summary>
        public static object[] ExtensionToolInstances(SessionManager sessions, Automation3 automation) => new object[]
        {
            new Tools.Thermo.ThermoToolsEx(sessions, automation),
            new Tools.Reactions.ReactionToolsEx(sessions),
            new Tools.Columns.ColumnTools(sessions),
            new Tools.Compounds.CompoundTools(sessions),
            new Tools.Separators.SeparatorTools(sessions),
            new Tools.Logical.LogicalTools(sessions),
            new Tools.Study.StudyTools(sessions),
            new Tools.Objects.ObjectTools(sessions),
            new Tools.Io.IoTools(sessions),
            new Tools.Thermo.CompoundResolveTools(sessions, automation),
            new Tools.HeatExchangers.HeatExchangerTools(sessions),
            new Tools.Plant.PlantTools(sessions),
        };

        public static ToolRegistry BuildRegistry(SessionManager sessions, Automation3 automation, DynamicsJobManager dynamicsJobs)
        {
            var registry = new ToolRegistry();
            foreach (var instance in UpstreamToolInstances(sessions, automation, dynamicsJobs))
                registry.RegisterToolsFrom(instance);
            foreach (var instance in ExtensionToolInstances(sessions, automation))
                registry.RegisterToolsFrom(instance);
            return registry;
        }
    }
}
