---
name: worklog-index
description: Rebuild or update `.worklog/INDEX.md` from current worklog documents. Use after creating, archiving, replacing, importing, or reconciling worklog documents.
---

# Worklog index

You are helping maintain the project worklog index.

Read:

1. `docs/worklog-method.md`
2. `.worklog/README.md` if it exists
3. numbered current work documents directly under `.worklog/`
4. `.worklog/archive/` only for archived document references, replacements, and index consistency checks

Do not treat `.worklog/INDEX.md`, `.worklog/README.md`, `.worklog/archive/README.md`, or `.worklog/_templates/*.md` as work documents.

Current work documents are files matching:

```text
.worklog/[0-9][0-9][0-9][0-9].*.md
```

Archived work documents are files matching:

```text
.worklog/archive/[0-9][0-9][0-9][0-9].*.md
```

Task:

1. Build or update `.worklog/INDEX.md`.
2. Include current documents in the main table.
3. Include archived documents only in a compact archived/replaced section.
4. Preserve document numbers exactly.
5. Extract type from the filename: `spec`, `adr`, or `spike`.
6. Extract title from the document heading or filename.
7. Summarize each document in one short sentence.
8. Include `Related`, `Replaces`, and `Replaced by` references when present.
9. Detect broken document number references.
10. Report inconsistencies instead of silently guessing.

Rules:

- The index is not the source of truth.
- Do not change numbered work documents unless the user explicitly asks.
- Do not rewrite requirements, accepted ADRs, or archived documents.
- Do not include micro-changes, local task notes, or commit-level details.
- Do not use archived documents as current requirements.
- If a document is hard to summarize safely, write `TBD` in the summary and report it.

Output:

- Update `.worklog/INDEX.md`.
- Report:
  - number of current documents indexed;
  - number of archived documents observed;
  - broken references, if any;
  - documents with missing required sections, if noticed.
