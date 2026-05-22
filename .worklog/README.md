# Worklog

Current work documents live directly in this directory.

Archived historical documents live in `archive/`.

Use one increasing sequence for all work documents:

`NNNN.type-short-title.md`

Examples:

- `0001.spec-initial-mvp.md`
- `0002.adr-console-rendering.md`
- `archive/0003.spike-old-renderer.md`

Draft documents are not committed to the main branch.

Types:

- `spec` - feature or behavior change;
- `adr` - architecture decision;
- `spike` - investigation.

There is no `task` worklog type. Small local changes belong in commit messages unless they require a `spec`, `adr`, or `spike`.

Do not use recursive `.worklog/**/*.md` as current context.
Use only numbered files directly under `.worklog/`.

Read archived files only for explicit history questions, decision explanation, replacement links, reconcile work, or index maintenance.

When a current document needs substantial semantic changes:

1. Move the old file from `.worklog/` to `.worklog/archive/`.
2. Create one or more new current documents in `.worklog/` with new numbers.
3. In each new document, add `Replaces:` with the old document number.
4. Do not rewrite the semantic content of the archived document.

References use document numbers only, not filenames.

Do not split documents into separate folders by type.

Use templates from `_templates`.

Before creating a new document, check the latest number.

Before implementation, make sure the document has:

- clear goal;
- scope;
- non-goals if applicable;
- done criteria;
- verification plan.

After significant implementation, reconcile the work document with the actual result. Add or update `Outcome` only when there are deviations, important implementation notes, verification details, limitations, or follow-up work worth preserving.

Do not use `Outcome` as task status. Do not archive a spec only because it was implemented.

Do not add a numbered document for micro-changes, typos, formatting, obvious local fixes, small refactoring with no behavior or architecture meaning, dependency patches with no project-specific decision, or changes that do not affect the product as such. If a small change is fully explained by the diff and commit message, keep it out of `.worklog`.
