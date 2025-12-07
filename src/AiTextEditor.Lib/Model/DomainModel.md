# Linear document model

The linear model represents markdown content as a sequential list of items with semantic numbering derived from heading hierarchy and paragraph order.

- **LinearDocument** – container with normalized source text and ordered `LinearItem` entries.
- **LinearItem** – individual piece of content. It stores a sequential `Index`, item `Type`, optional heading `Level`, raw markdown, extracted plain `Text`, and a `LinearPointer` for navigation.
- **SemanticPointer** – describes the semantic position using heading numbers and an optional paragraph number. Heading numbers are formatted as `"1.2"` and paragraphs are appended as `"pN"` (for example `"1.2.p3"`).
- **LinearPointer** – extends `SemanticPointer` with the zero-based linear `Index` to allow stable references inside the linearized document.

Headings increase their respective numbering level (resetting deeper levels) and reset the paragraph counter. Paragraph-like elements (paragraphs, list items, code blocks, thematic breaks, and other leaf blocks) increment the paragraph counter within the current heading path.
