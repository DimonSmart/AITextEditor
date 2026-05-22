---
id: worklog-rules
scope: [codex, copilot]
category: process
requires: [ai-rules]
---
# Worklog

This project uses worklog-driven development.

Before significant project work:
- read `docs/worklog-method.md`
- inspect relevant numbered current `.worklog/NNNN.*.md` files
- do not inspect `.worklog/archive/` unless explicitly needed
- decide whether the change needs a new work document
- if yes, create the next `NNNN.type-short-title.md` file from `.worklog/_templates/` using `spec`, `adr`, or `spike`

Do not create a work document for micro-changes, small product-neutral changes, or fixes: typos, formatting, obvious local fixes, small refactoring with no behavior or architecture meaning, dependency patches with no project-specific decision, or anything fully explained by a commit message.

If a change does not introduce architecture decisions, library choices, experiments, new capabilities, changed requirements, or user-visible behavior changes, keep it out of `.worklog/` and describe it in the commit message. When unsure about a small change, do not create a work document by default.

Use only numbered files directly under `.worklog/` as current context. Files under `.worklog/archive/` are history.

Read archived documents only when a current document references them through `Replaces`, the user asks for history, or the task is to explain a decision.

When a current document needs substantial semantic changes, move the old file to `.worklog/archive/`, create one or more new current documents in `.worklog/` with new numbers, and add `Replaces:` with the old document number in each new document. Do not rewrite the semantic content of archived documents.

After significant work tracked by a work document, reconcile the document with the actual result. Add or update `Outcome` only when the implementation, verification, deviations, limitations, or follow-up contain durable information that should remain visible in the repository.

Do not use `Outcome` as task status. Do not archive a spec only because implementation is complete.

Accepted ADRs and old requirements are immutable. If a decision or requirement changes, create a new current document and archive the old one.
