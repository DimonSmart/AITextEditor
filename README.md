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

## Streaming navigation agent
Long scans run through a streaming navigation agent that works in bounded batches instead of sending the full book to the LLM. The agent builds a local cursor inside a single `RunCursorAgent` call, streams items forward or backward with per-call limits, and finishes with a concise JSON action. See `docs/streaming-agent.md` for details on prompt shape and plugin functions.

## Console demo
Run the console sample with optional parameters for custom inputs:

```bash
dotnet run --project src/AiTextEditor.Console -- <input-path> <output-path> [scenario]
```

- `input-path` — markdown file to load (defaults to `sample.md`).
- `output-path` — file that will receive the edited document (defaults to `sample_edited.md`).
- `scenario` — optional identifier used for document and target set naming (defaults to `sample`).

When running without arguments the demo reads `src/AiTextEditor.Console/sample.md` and writes the edited copy to `sample_edited.md` in the same folder.

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
