"""openrun_sim: simulate the plant an open run produces, through the DWSIM-MCP server.

Input: a plant JSON in the contract of OPEN_RUN_SIMULATION_BRIEF.md section 1 (process steps, requirements,
flowsheet with chosen modules and connections, target rate, overrides). Output: a solved flowsheet, a report
with every stream, duty and balance, the checks, and the provenance of every number.
"""
__version__ = "0.1.0"
