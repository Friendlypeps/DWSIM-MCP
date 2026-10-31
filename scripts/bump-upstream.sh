#!/usr/bin/env bash
# Moves the dwsim10 submodule to a tag and prints what has to be re-synced by hand.
# Usage: scripts/bump-upstream.sh v10.2.13
set -euo pipefail
cd "$(dirname "$0")/.."
TAG="${1:?tag, e.g. v10.2.13}"
git -C external/dwsim10 fetch --depth 1 origin "tag" "$TAG"
git -C external/dwsim10 checkout -q "$TAG"
git -C external/dwsim10 submodule update --init --depth 1
echo "submodule at $(git -C external/dwsim10 describe --tags --exact-match)"
sed -i "s|<DwsimUpstreamTag>.*</DwsimUpstreamTag>|<DwsimUpstreamTag>$TAG</DwsimUpstreamTag>|" Directory.Build.props
echo
echo "Shadowed upstream files and their current hashes (paste into tests/DWSIM.MCP.Server.Tests/ShadowDriftTests.cs"
echo "after diffing each against src/DWSIM.MCP.Server/Shadow and re-applying the shadow's changes):"
for f in tools/DWSIM.MCPServer/Program.cs tools/DWSIM.MCPServer/Rpc/JsonRpcDispatcher.cs tools/DWSIM.MCPServer/McpFlowsheet.cs tools/DWSIM.MCPServer/Tools/UnitOps/UnitOpTools.cs; do
  printf '  %s  %s\n' "$(sha256sum external/dwsim10/$f | cut -d' ' -f1)" "$f"
done
echo
echo "Then: update <Version> in Directory.Build.props, dotnet build DWSIM-MCP.slnx, dotnet test."
