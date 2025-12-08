# AI Text Editor

MCP-oriented toolkit around a linear Markdown domain model. The server keeps a single normalized document in memory and exposes deterministic operations that match the workflow described in the schema: navigation through linear items, named target sets for bulk edits, and sequential application of linear edit operations.

## Domain model
- **LinearDocument** — stable document id, normalized `SourceText`, ordered `LinearItem` list.
- **LinearItem** — contains `Index`, `Type`, optional heading `Level`, raw `Markdown`, extracted `Text`, and a `LinearPointer` (`Index` + semantic number).
- **TargetSet/TargetRef** — named selections of items used for cursor/target set workflows.

## MCP server surface
- Load markdown into a cached `LinearDocument` (`McpServer.LoadDocument`).
- Inspect items for navigation or filtering (`McpServer.GetItems`).
- Create and manage named target sets (`McpServer.CreateTargetSet`, `ListTargetSets`, `GetTargetSet`, `DeleteTargetSet`).
- Apply `LinearEditOperation` batches to the cached document with consistent reindexing (`McpServer.ApplyOperations`).

Versioning, diffing, and long-running orchestration remain outside of the server; it only maintains the latest in-memory state.

## Running LLM-backed tests
Functional and MCP integration tests call a live LLM endpoint. Configure the client through environment variables before runnin
g tests:

- `LLM_BASE_URL` (or `OLLAMA_HOST`) — base URL of the Ollama endpoint, for example `https://api.ollama.com` or `http://localhos
t:11434`.
- `LLM_API_KEY` (or `OLLAMA_API_KEY`) — bearer token required by Ollama Cloud.
- `LLM_MODEL` (or `OLLAMA_MODEL`) — model identifier to pass into `/api/generate`.

Example:

```bash
export LLM_BASE_URL="https://api.ollama.com"
export LLM_API_KEY="<your_token>"
export LLM_MODEL="llama3"
dotnet test
```
