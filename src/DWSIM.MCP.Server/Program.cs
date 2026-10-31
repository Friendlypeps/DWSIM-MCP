// Copyright 2026 friendlypeps and contributors. Derived from DWSIM 10's tools/DWSIM.MCPServer/Program.cs,
// copyright 2008-2026 Daniel Wagner Oliveira de Medeiros and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using DWSIM.Automation;
using DWSIM.MCPServer.Rpc;
using DWSIM.MCPServer.Sessions;
using DWSIM.MCPServer.Transport;

namespace DWSIM.MCPServer
{
    /// <summary>
    /// The entry point. Same flags as the upstream server (--stdio, --http, --port, --host, --token,
    /// --python-path, DWSIM_PYTHON_PATH), so the image is a drop-in replacement; the tool set is
    /// assembled by <see cref="ServerComposition"/>.
    /// </summary>
    class Program
    {
        /// <summary>
        /// The real stdout writer, used exclusively for JSON-RPC responses. Console.Out is redirected
        /// to Null so engine output cannot corrupt the stdio protocol. StdioTransport reads this field.
        /// </summary>
        internal static TextWriter StdoutWriter;

        static void Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

            StdoutWriter = Console.Out;
            Console.SetOut(TextWriter.Null);

            Console.Error.WriteLine($"[dwsim-mcp] Process started. PID={System.Diagnostics.Process.GetCurrentProcess().Id} version={ServerComposition.Version}");

            var mode = "stdio";
            int port = 5901;
            string token = null;
            string host = "localhost";
            string pythonPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--stdio": mode = "stdio"; break;
                    case "--http": mode = "http"; break;
                    case "--port": if (i + 1 < args.Length) port = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--token": if (i + 1 < args.Length) token = args[++i]; break;
                    case "--host": if (i + 1 < args.Length) host = args[++i]; break;
                    case "--python-path": if (i + 1 < args.Length) pythonPath = args[++i]; break;
                    case "--version":
                        StdoutWriter.WriteLine(ServerComposition.Version);
                        return;
                    case "--list-tools":
                        // Prints the tool names and exits: for the README catalogue and the smoke test.
                        ListTools();
                        return;
                }
            }

            pythonPath ??= Environment.GetEnvironmentVariable("DWSIM_PYTHON_PATH");
            if (!string.IsNullOrEmpty(pythonPath))
            {
                DWSIM.GlobalSettings.Settings.PythonPath = pythonPath;
                Console.Error.WriteLine($"[dwsim-mcp] Python path set to {pythonPath}");
            }

            var registry = ServerComposition.Boot(out var sessions);
            var dispatcher = new JsonRpcDispatcher(registry);

            Console.Error.WriteLine($"[dwsim-mcp] Registered {registry.ListTools().Count} tools");

            ITransport transport = mode == "http"
                ? new HttpSseTransport(dispatcher, port, token, host)
                : new StdioTransport(dispatcher);
            transport.Run();
        }

        private static void ListTools()
        {
            var registry = ServerComposition.Boot(out _);
            foreach (var t in registry.ListTools())
                StdoutWriter.WriteLine(t["name"]);
        }
    }
}
