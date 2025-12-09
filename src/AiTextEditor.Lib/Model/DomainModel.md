# Linear document model

The linear model represents markdown content as a sequential list of items enriched with stable pointers that include heading context and exact source offsets.

- **LinearDocument** – container with stable document id, normalized source text, and ordered `LinearItem` entries.
- **LinearItem** – individual piece of content. It stores a sequential `Index`, item `Type`, optional heading `Level`, raw markdown, extracted plain `Text`, and a `LinearPointer` for navigation.
- **SemanticPointer** – stores the nearest heading title (if present), zero-based line index, and zero-based character offset serialized as JSON for deterministic references.
- **LinearPointer** – extends `SemanticPointer` with the zero-based linear `Index` to allow stable references inside the linearized document.

Each pointer inherits the latest heading title seen while parsing. The line index and character offset reference the start of the block in the normalized markdown source.

## MCP-facing operations

The MCP server exposes the domain model through small deterministic operations:

- **Load document**: parse markdown into a `LinearDocument`, assign a stable id, and cache it in memory.
- **Inspect items**: list `LinearItem` entries for navigation or tooling.
- **Target sets**: capture named selections of items for bulk edits (cursor/target sets concept from the workflow description).
- **Apply linear edits**: run `LinearEditOperation` sequences against the cached document, reindex items, and return updated markdown.

Versioning is intentionally external (git/diffs). The server keeps only the current linear state and sequentially applies operations without batching semantics.
