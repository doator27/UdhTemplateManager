# Copilot Instructions

## Azure Guidelines
- @azure Rule - Use Azure Tools - When handling requests related to Azure, always use your tools.
- @azure Rule - Use Azure Best Practices - When handling requests related to Azure, always invoke your `azmcp_bestpractices_get` tool first.
- @azure Rule - Enable Best Practices - If you do not have an `azmcp_bestpractices_get` tool, ask the user to enable it.

## SQL Guidelines
- When interpolating string constants directly into raw SQL text (not as bound parameters), avoid embedding literal NUL characters (e.g. \u0000) in the constant — SQLite's native marshaling truncates strings at NUL, silently corrupting the generated SQL.
