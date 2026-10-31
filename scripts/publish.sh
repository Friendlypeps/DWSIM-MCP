#!/usr/bin/env bash
# Publishes the server self-contained for one or more Linux RIDs into build/publish-mcp/<arch>, the
# layout packaging/docker/Dockerfile expects. Usage: scripts/publish.sh [linux-x64] [linux-arm64]
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION="${VERSION:-$(grep -oPm1 '(?<=<Version>)[^<]+' Directory.Build.props)}"
RIDS=("$@"); [ ${#RIDS[@]} -eq 0 ] && RIDS=(linux-x64)
for rid in "${RIDS[@]}"; do
  case "$rid" in linux-x64) arch=amd64;; linux-arm64) arch=arm64;; *) echo "unknown rid $rid" >&2; exit 1;; esac
  out="build/publish-mcp/$arch"
  rm -rf "$out"
  # -p:CoolPropRid is a global property that reaches the CoolProp project reference; without it the
  # publish carries the build host's native library instead of the target's.
  dotnet publish src/DWSIM.MCP.Server/DWSIM.MCP.Server.csproj -c Release -r "$rid" --self-contained true \
    -p:Version="$VERSION" -p:CoolPropRid="$rid" -o "$out"
  find "$out" -name '*.dylib' -delete
  echo "published $rid -> $out ($(du -sh "$out" | cut -f1))"
done
echo "docker build -f packaging/docker/Dockerfile --build-arg VERSION=$VERSION -t dwsim-mcp:$VERSION build"
