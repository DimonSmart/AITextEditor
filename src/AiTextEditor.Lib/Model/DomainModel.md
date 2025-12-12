# Linear document model

The linear model represents markdown content as a sequential list of items enriched with stable pointers.

- **LinearDocument** – container with stable document id, normalized source text, and ordered `LinearItem` entries.
- **LinearItem** – individual piece of content. It stores a sequential `Index`, item `Type`, optional heading `Level`, raw markdown, extracted plain `Text`, and a `SemanticPointer` for navigation.
- **SemanticPointer** – stable identifier with numeric `Id` and human-readable `Label` (e.g., `1.1.1.p21`). No source offsets are stored.

Labels are derived from heading/paragraph numbering during parsing, but only Id+Label are persisted.

## MCP-facing operations

The MCP server exposes the domain model through small deterministic operations:

- **Load document**: parse markdown into a `LinearDocument`, assign a stable id, and cache it in memory.
- **Inspect items**: list `LinearItem` entries for navigation or tooling.
- **Target sets**: capture named selections of items for bulk edits.
- **Apply linear edits**: run `LinearEditOperation` sequences against the cached document, reindex items, and return updated markdown.

Versioning is intentionally external (git/diffs). The server keeps only the current linear state and sequentially applies operations without batching semantics.
