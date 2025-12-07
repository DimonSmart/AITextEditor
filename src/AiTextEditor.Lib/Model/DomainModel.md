# Linear document model

The linear model represents markdown content as a sequential list of items with semantic numbering derived from heading hierarchy and paragraph order.

- **LinearDocument** – container with stable document id, normalized source text, and ordered `LinearItem` entries.
- **LinearItem** – individual piece of content. It stores a sequential `Index`, item `Type`, optional heading `Level`, raw markdown, extracted plain `Text`, and a `LinearPointer` for navigation.
- **SemanticPointer** – describes the semantic position using heading numbers and an optional paragraph number. Heading numbers are formatted as `"1.2"` and paragraphs are appended as `"pN"` (for example `"1.2.p3"`).
- **LinearPointer** – extends `SemanticPointer` with the zero-based linear `Index` to allow stable references inside the linearized document.

Headings increase their respective numbering level (resetting deeper levels) and reset the paragraph counter. Paragraph-like elements (paragraphs, list items, code blocks, thematic breaks, and other leaf blocks) increment the paragraph counter within the current heading path.

## MCP-facing operations

The MCP server exposes the domain model through small deterministic operations:

- **Load document**: parse markdown into a `LinearDocument`, assign a stable id, and cache it in memory.
- **Inspect items**: list `LinearItem` entries for navigation or tooling.
- **Target sets**: capture named selections of items for bulk edits (cursor/target sets concept from the workflow description).
- **Apply linear edits**: run `LinearEditOperation` sequences against the cached document, reindex items, and return updated markdown.

Versioning is intentionally external (git/diffs). The server keeps only the current linear state and sequentially applies operations without batching semantics.
