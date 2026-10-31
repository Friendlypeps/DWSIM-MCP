#!/usr/bin/env bash
# Build every gold plant against a running DWSIM-MCP server (HTTP, default http://127.0.0.1:5901).
# Each script writes out/<plant>.json (streams, checks, assumptions) and out/<plant>.dwxmz (the flowsheet).
# Usage: scripts run one after another; the engine solves one flowsheet at a time.
set -u
cd "$(dirname "$0")"
mkdir -p out logs
for p in etoac_fischer_llm_opus etoac_fischer_patent \
         etoac_tishchenko_llm_opus etoac_tishchenko_patent \
         etoac_dehydrogenation_llm_opus etoac_dehydrogenation_patent \
         ammonia_haber_bosch_llm_opus ammonia_haber_bosch_patent; do
  echo "== $p"
  timeout 2400 python3 -I "$p.py" > "logs/$p.log" 2>&1
  echo "   exit $? (log: logs/$p.log)"
done
