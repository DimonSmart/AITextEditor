# AI Text Editor

MCP-oriented toolkit around a linear Markdown domain model. The server keeps a single normalized document in memory and exposes deterministic operations that match the workflow described in the schema: navigation through linear items, named target sets for bulk edits, and sequential application of linear edit operations.

## Domain model
- **LinearDocument** - `Id`, normalized `SourceText`, ordered `LinearItem` list.
- **LinearItem** - contains `Index`, `Type`, raw `Markdown`, extracted `Text`, and a `SemanticPointer` (label like `1.1.p21`).
- **TargetSet/TargetRef** - named selections of items used for cursor/target set workflows.

## MCP server surface
- Load markdown into a cached `LinearDocument` (`EditorSession.LoadDefaultDocument`).
- Inspect items for navigation or filtering (`EditorSession.GetItems`).
- Create and manage named target sets (`EditorSession.CreateTargetSet`, `EditorSession.ListDefaultTargetSets`, `EditorSession.GetTargetSet`, `EditorSession.DeleteTargetSet`).
- Apply `LinearEditOperation` batches to the cached document with consistent reindexing (`EditorSession.ApplyOperations`).
- Semantic Kernel plugin `editor-create_targets` accepts an array of pointer strings (`string[]`/`IReadOnlyList<string>`) and returns a compact payload with the created `targetSetId`, matched targets (`pointer` as a serialized `SemanticPointer`, `excerpt`), plus `invalidPointers` and `warnings` instead of the raw `TargetSet`.

Versioning, diffing, and long-running orchestration remain outside of the server; it only maintains the latest in-memory state.

## Streaming navigation agent
Long scans run through a streaming navigation agent that consumes an existing named cursor instead of sending the full book to the LLM. The agent walks cursor batches and returns a `CursorAgentResult` based on collected evidence; keyword cursors provide a fast, LLM-free filter for literal matches. See `docs/streaming-agent.md` for details on cursor types, prompt shape, and plugin functions.

## Console demo
Run the console sample with optional parameters for custom inputs:

```bash
dotnet run --project src/AiTextEditor.Console -- <input-path> <output-path>
```

- `input-path` — markdown file to load (defaults to `sample.md`).
- `output-path` — file that will receive the edited document (defaults to `sample_edited.md`).

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
dotnet test --filter "Category!=Manual"
```

To run long-running LLM tests (marked as `Manual`), use:
```bash
dotnet test --filter "Category=Manual"
```
