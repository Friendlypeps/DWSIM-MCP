// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.Globalization;
using System.Threading;
using DWSIM.Automation;
using DWSIM.MCPServer;
using DWSIM.MCPServer.Sessions;
using DWSIM.MCPServer.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DWSIM.MCP.Server.Tests
{
    /// <summary>
    /// One engine per test run: loading the compound databases takes seconds, and the Automation3
    /// catalogue is static anyway. Tests call tools through the registry, so argument binding and the
    /// JSON schema are exercised the way a client exercises them.
    /// </summary>
    public sealed class EngineFixture : IDisposable
    {
        public Automation3 Automation { get; }
        public SessionManager Sessions { get; }
        public ToolRegistry Registry { get; }

        public EngineFixture()
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
            DWSIM.Automation.FluentAPI.Flowsheet.RegisterAssemblyResolver();
            Automation = new Automation3();
            Sessions = new SessionManager();
            Registry = ServerComposition.BuildRegistry(Sessions, Automation, new DynamicsJobManager());
        }

        /// <summary>Invokes a tool as a client would, from an anonymous object of arguments.</summary>
        public JObject Call(string tool, object args = null)
        {
            var jo = args == null ? new JObject() : JObject.FromObject(args);
            try { return Registry.Invoke(tool, jo); }
            catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null)
            {
                // The registry invokes by reflection; the dispatcher unwraps the same way before answering a client.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw;
            }
        }

        public string NewFlowsheet(string name = "test") => Call("dwsim_flowsheet_create", new { name })["flowsheet_id"].ToString();

        public void Dispose() { }
    }

    [CollectionDefinition("engine")]
    public sealed class EngineCollection : ICollectionFixture<EngineFixture> { }
}
