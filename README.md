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

## Cursor-powered search
Long scans now use named cursors that stream the document in bounded portions instead of sending the full book to the LLM. Default cursors `CUR_WHOLE_BOOK_FORWARD` and `CUR_WHOLE_BOOK_BACKWARD` are always available for full traversal, and `CursorQueryExecutor` orchestrates applying LLM prompts over cursor data. See `docs/cursors.md` for details.

## Running LLM-backed tests
Functional and MCP integration tests call a live LLM endpoint. Configure the client through environment variables before running tests:

- `LLM_BASE_URL` — base URL of the Ollama endpoint (defaults to `http://localhost:11434`), for example `https://ollama.com/api` or `http://localhost:11434`.
- `LLM_API_KEY` — bearer token required by Ollama Cloud.
- `LLM_MODEL` — model identifier to pass into `/api/generate`.

Example:

```bash
export LLM_BASE_URL="https://ollama.com/api"
export LLM_API_KEY="<your_token>"
export LLM_MODEL="gpt-oss:8b"
dotnet test
```
