# DWSIM-MCP

A [Model Context Protocol](https://modelcontextprotocol.io) server for the [DWSIM](https://dwsim.org)
chemical process simulator, built on DWSIM 10's own headless MCP server and extending it with the
tools needed to build whole plants from an LLM: rigorous columns, kinetic reactions, flash and
binary-parameter control, user compounds, Adjust/Spec blocks, parameter sweeps, and a generic
reflection escape hatch.

It is a **drop-in replacement** for `ghcr.io/danwbr/dwsim-mcp`: same entrypoint, same endpoints,
every upstream tool name unchanged, plus 43 more (101 tools in all).

```sh
docker run -d --name dwsim-mcp -p 5901:5901 ghcr.io/friendlypeps/dwsim-mcp:10.2
curl http://localhost:5901/health
```

Endpoints: `POST /mcp` (JSON-RPC), `GET /sse`, `GET /health`. Add `--token <secret>` after the
image name on an untrusted network. Stdio transport: run the binary with `--stdio`.

## Why

The official server exposes DWSIM's flowsheet, streams, unit operations, thermodynamics and
solver, but not everything a full plant needs. Reading the engine shows that none of the gaps is
an engine limit: the Fluent API and the engine classes already do all of it headlessly, only the
MCP tool surface was missing. This repository compiles the upstream tool sources unchanged (from a
git submodule of [DanWBR/dwsim10](https://github.com/DanWBR/dwsim10)) and adds tool classes beside
them.

## What is added

| Group | Tools | What they unlock |
|---|---|---|
| Property package and flash | `dwsim_thermo_set_property_package` (now *replaces* the package and rebinds every object; short names NRTL, PR, SRK ...), `dwsim_thermo_get_property_package`, `dwsim_thermo_set_flash`, `dwsim_thermo_set_forced_solids` | VLE vs **VLLE** (decanters, extractors), inside-out, fail-safe mode; solids for filters |
| Binary interaction parameters | `dwsim_thermo_get_bips`, `dwsim_thermo_set_bips`, `dwsim_thermo_estimate_bips` | see which pairs are missing, write literature values, estimate with UNIFAC |
| Reactions | `dwsim_reaction_add_kinetic`, `dwsim_reaction_add_hetcat`, `dwsim_reaction_set_equilibrium_constant`, `dwsim_reaction_set_kinetics`, `dwsim_reactionset_add_reaction`, `dwsim_reaction_delete`, `dwsim_reactor_set_element_matrix` | Arrhenius and Langmuir-Hinshelwood kinetics for CSTR/PFR, a literature K as a number, second reaction sets |
| Columns | `dwsim_column_shortcut_configure`, `dwsim_column_create`, `dwsim_column_connect`, `dwsim_column_set_specs`, `dwsim_column_set_estimates`, `dwsim_column_get` | shortcut keys and split; rigorous distillation, absorption, extraction columns from scratch with feed stages, specs and estimates |
| Separators | `dwsim_compsep_set_specs`, `dwsim_unitop_set_mode`, `dwsim_vessel_configure` | component-separator split table; Vessel and Filter modes the upstream setter cannot reach; flash T/P override |
| Compounds | `dwsim_thermo_add_compounds` (now updates existing streams), `dwsim_compound_import_json`, `dwsim_compound_import_json_inline`, `dwsim_compound_export_json`, `dwsim_compound_get`, `dwsim_compound_remove`, `dwsim_compound_repair_streams` | user-defined compounds (catalysts, salts, sugars) at any point in the build |
| Logical blocks | `dwsim_adjust_create`, `dwsim_adjust_get`, `dwsim_spec_create`, `dwsim_recycle_configure` | design questions as one solve; tied specifications; loop tolerances |
| Studies | `dwsim_study_list_variables`, `dwsim_study_sweep` | 1-D / 2-D parameter sweeps with a results table |
| Escape hatch | `dwsim_object_describe`, `dwsim_object_get`, `dwsim_object_set`, `dwsim_object_invoke`, `dwsim_object_list_properties`, `dwsim_object_delete`, `dwsim_script_run` | any public property, field or method of any live engine object, by path; flowsheet scripts |
| Files | `dwsim_flowsheet_export_xml`, `dwsim_flowsheet_get_xml_section` | the complete XML (no 500 000-character cut), or one section / one object |

Reactive distillation is **not** available in DWSIM 10.2 (the rigorous column has no reactive-stage
model); `dwsim_column_create` refuses it with that explanation so a model can report the reactor +
column surrogate honestly.

The handshake `instructions` tell an MCP client when to use which tool. Print the tool names with
`dwsim-mcp --list-tools`.

## Build

Requirements: .NET 10 SDK, git.

```sh
git clone --recursive https://github.com/friendlypeps/dwsim-mcp
cd dwsim-mcp
dotnet build DWSIM-MCP.slnx -c Release
dotnet test DWSIM-MCP.slnx -c Release --no-build
dotnet src/DWSIM.MCP.Server/bin/Release/net10.0/dwsim-mcp.dll --http --port 5901
```

The `--recursive` matters: the engine's Automation project needs `external/dwsim10/external/ReoGrid`.

Publish and containerise:

```sh
scripts/publish.sh linux-x64            # or: scripts/publish.sh linux-x64 linux-arm64
docker build -f packaging/docker/Dockerfile -t dwsim-mcp:local build
docker run --rm -p 5901:5901 dwsim-mcp:local
scripts/smoke.sh http://localhost:5901
```

## Layout

```
external/dwsim10/            DWSIM 10 engine, pinned to a release tag (git submodule)
src/DWSIM.MCP.Server/        the server
  Shadow/                    three upstream files compiled in place of the originals (entry point,
                             JSON-RPC dispatcher with the handshake text, headless flowsheet)
  Tools/Common/              Find, Reflect, Links, Units, Compounds helpers
  Tools/<Group>/             one class per tool group
tests/DWSIM.MCP.Server.Tests xunit: upstream parity, shadow drift, an end-to-end esterification plant
packaging/docker/            Dockerfile and compose file
scripts/                     publish.sh, smoke.sh, bump-upstream.sh
```

The upstream tool sources under `external/dwsim10/tools/DWSIM.MCPServer` are compiled into the
server by a glob in the csproj. `ServerComposition.cs` registers every upstream tool class except
`ThermoTools`, whose four names `ThermoToolsEx` carries with the same parameters (and the added
replace semantics).

## Versioning

`10.2.12-fp.1` = DWSIM engine `v10.2.12` (the submodule tag) + this repository's revision `fp.1`.
Image tags: `10.2.12-fp.1`, `10.2.12`, `10.2`, `latest`. The release workflow refuses to build if
the submodule is not at the tag the version names.

To move to a newer engine: `scripts/bump-upstream.sh v10.2.13`, diff the three shadowed files
against `src/DWSIM.MCP.Server/Shadow`, re-apply the shadow's small changes, update the hashes in
`tests/DWSIM.MCP.Server.Tests/ShadowDriftTests.cs`, build and test.

## Client configuration

Claude Desktop / Claude Code (stdio, local build):

```json
{
  "mcpServers": {
    "dwsim": {
      "command": "dotnet",
      "args": ["/path/to/dwsim-mcp/src/DWSIM.MCP.Server/bin/Release/net10.0/dwsim-mcp.dll", "--stdio"]
    }
  }
}
```

Any client that speaks MCP over HTTP: `http://<host>:5901/mcp`, header `X-MCP-Token: <secret>` when a
token is set.

## Licence

GPL-3.0-or-later. DWSIM is copyright Daniel Wagner Oliveira de Medeiros and contributors, GPL-3.0;
this repository links and compiles parts of it, so the whole is GPL. The corresponding source of any
published image is this repository at the tagged commit together with the pinned submodule.
