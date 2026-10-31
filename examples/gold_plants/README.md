# Gold plants simulated through DWSIM-MCP

Eight reference plants from `gold_plants_for_simulation.md` (four routes: Fischer esterification,
Tishchenko, ethanol dehydrogenation, Haber-Bosch), each built from the document's own units, stated
conditions and pipes by driving this repository's MCP server over HTTP (`POST /mcp`, JSON-RPC
`tools/call`). One Python script per plant; `mcpclient.py` is the small client they share (JSON-RPC
calls, mole-to-mass composition, a `unit()` helper that adds and wires a unit in one call, recycle seeding,
the component-separator workaround and the ester/water NRTL pair).

```sh
dotnet src/DWSIM.MCP.Server/bin/Release/net10.0/dwsim-mcp.dll --http --port 5901 &
examples/gold_plants/run_all.sh          # writes out/<plant>.json, out/<plant>.dwxmz, logs/<plant>.log
```

Environment variables: `DWSIM_MCP_URL` (default `http://127.0.0.1:5901`), `DWSIM_SOLVE_TIMEOUT` (seconds
per solve, default 300; the recycle plants want 600), `GOLD_OPEN_LOOPS=1` (leave the loops that did not
converge headless open and report their tear streams; used for the final runs of the Fischer and
dehydrogenation plants). Several engines can run on different ports at once; the scripts are independent.

Every value a document states is used as stated (the midpoint of a window). Every value a document does
not state but a simulation needs (a feed rate, a pump discharge pressure, a stage count, a split) is
marked `ASSUMED` in the script, printed on stderr and listed under `assumptions` in the JSON. The
`checks` block of each JSON puts the simulated number next to the document's number where the
document states one. `out/*.dwxmz` open in the DWSIM desktop application.

See `RESULTS.md` for what solved, what did not, the engine findings and the numbers.

| plant | script | state |
|---|---|---|
| etoac_fischer / llm_opus | `etoac_fischer_llm_opus.py` | all 11 units solved; acid recycle closed, 3 loops open |
| etoac_fischer / patent US6768021B2 | `etoac_fischer_patent.py` | all 8 units solved (reactor + column surrogate); 2 loops open |
| etoac_tishchenko / llm_opus | `etoac_tishchenko_llm_opus.py` | all 11 units solved; acetaldehyde recycle open |
| etoac_tishchenko / patent CN1245794A | `etoac_tishchenko_patent.py` | all 5 units solved |
| etoac_dehydrogenation / llm_opus | `etoac_dehydrogenation_llm_opus.py` | all 14 units solved; ethanol recycle closed |
| etoac_dehydrogenation / patent US6809217B1 | `etoac_dehydrogenation_patent.py` | all 19 units solved; hydrogen loop closed, 2 loops open |
| ammonia_haber_bosch / llm_opus | `ammonia_haber_bosch_llm_opus.py` | all 17 units solved; synthesis loop closed |
| ammonia_haber_bosch / patent US9040011B2 | `ammonia_haber_bosch_patent.py` | all 14 units solved; once-through loop |
