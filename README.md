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
