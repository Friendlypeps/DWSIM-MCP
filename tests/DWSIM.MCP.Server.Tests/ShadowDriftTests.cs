// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace DWSIM.MCP.Server.Tests
{
    /// <summary>
    /// Four upstream files are replaced by copies under src/DWSIM.MCP.Server/Shadow. When a submodule
    /// bump changes one of them, this test fails so the shadow is re-synced on purpose, not forgotten.
    /// Update the hashes with scripts/bump-upstream.sh after re-syncing.
    /// </summary>
    public class ShadowDriftTests
    {
        private static readonly (string path, string sha256)[] Shadowed =
        {
            ("tools/DWSIM.MCPServer/Program.cs", "f93ed9d3c6f7f3d4377254acf38c087f069b6f57538478ace0a7cb226887e72e"),
            ("tools/DWSIM.MCPServer/Rpc/JsonRpcDispatcher.cs", "7ebc112ecec6b5fcd3ec5f910e1f6cde7a8cf69aee7d253e77792c23ea361607"),
            ("tools/DWSIM.MCPServer/Tools/UnitOps/UnitOpTools.cs", "c2b12b016c8169966ac7824a74b5a7b2a8ad6d33cf414403b64d95c48f9dbe0d"),
            ("tools/DWSIM.MCPServer/McpFlowsheet.cs", "e87edd594ec7771850860802d39fc29c483d2f43a6c5f2b832d133e69cc8753d"),
        };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DWSIM-MCP.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
        }

        [Fact]
        public void UpstreamShadowedFilesAreUnchanged()
        {
            var root = RepoRoot();
            foreach (var (path, expected) in Shadowed)
            {
                var full = Path.Combine(root, "external", "dwsim10", path);
                Assert.True(File.Exists(full), $"Upstream file missing: {path} (run git submodule update --init --recursive)");
                using var sha = SHA256.Create();
                var actual = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(full))).ToLowerInvariant();
                Assert.True(actual == expected, $"{path} changed upstream (sha256 {actual}); re-sync src/DWSIM.MCP.Server/Shadow and update ShadowDriftTests.");
            }
        }
    }
}
