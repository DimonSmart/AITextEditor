# Worklog-driven development

## Goal

Keep project intent, decisions, experiments, and significant changes in repository files instead of chat history.

The worklog is not a replacement for commits. It records meaningful engineering intent.

Small product-neutral changes belong in commit messages, not in `.worklog`.

## Directory

All work documents live in `.worklog`.

Current documents live directly in `.worklog/`.

Retired documents live in `.worklog/archive/`.

Use one increasing numeric sequence across both current and archived documents:

`NNNN.type-short-title.md`

Examples:

- `.worklog/0001.spec-initial-mvp.md`
- `.worklog/0002.spike-console-double-buffering.md`
- `.worklog/archive/0003.adr-old-rendering-model.md`

Do not include lifecycle markers such as `active` or `retired` in file names.

Do not split documents into `/specs`, `/adr`, or `/spikes`. The sequence matters more than classification folders.

When finding the next number, scan numbered worklog files in both `.worklog/` and `.worklog/archive/`, then use the maximum `NNNN` prefix plus one.

Ignore support files such as `.worklog/README.md`, `.worklog/INDEX.md`, `.worklog/_templates/*.md`, and `.worklog/archive/README.md`.

## Document Types

### spec

Use `spec` when the system gets a new capability or user-visible behavior changes.

Question answered: what should the system do?

### adr

Use `adr` for architectural or long-lived technical decisions.

Question answered: why did we choose this solution?

ADRs are immutable once they describe an accepted decision. If the decision changes, create a new ADR and archive the old one.

### spike

Use `spike` for research, experiments, feasibility checks, or technical uncertainty.

Question answered: what should be checked before making a decision?

## When Not To Create A Work Document

Do not create a numbered work document for:

- micro-changes;
- typo fixes;
- formatting-only changes;
- obvious bug fixes fully explained by the commit message;
- small refactoring with no behavior or architectural meaning;
- dependency patch updates with no project-specific decision.

Also do not create a work document for small changes or fixes that do not affect the product as such: no architecture change, no library choice, no experiment, no new capability, no changed requirement, and no user-visible behavior change. Describe those changes at the commit-message level.

If the change is completely understandable from a diff and a commit message, keep it out of `.worklog`.

When unsure whether a small change deserves a work document, do not create one by default. Create a document only when the change needs durable requirements, decision history, or investigation notes.

There is no `task` work document type. Small local tasks stay in commit messages unless they are really a `spec`, `adr`, or `spike`.

## Current And Archived Documents

A document is current when it is stored directly under `.worklog/`.

A document is archived when it is stored under `.worklog/archive/`.

There are no committed filename states named `draft`, `changed`, `deleted`, `outdated`, or `superseded`. Archived history is represented by location under `.worklog/archive/`, not by a filename state. Drafts live outside the main branch or remain uncommitted.

The agent must use only numbered current documents in `.worklog/` as the default work context.

The agent must not use recursive `.worklog/**/*.md` search as current context, because it includes archived documents and support files.

Implementation completion does not automatically archive a spec. A spec may stay current after implementation if it still describes current intended behavior, requirements, decisions, or constraints.

Archived documents are historical. Read them only when:

- a current document references them through `Replaces`;
- the user explicitly asks to inspect history;
- the task is to explain why a decision was made;
- `worklog-reconcile` compares old and new meaning;
- `worklog-index` updates links and replacement history.

When a current document needs substantial semantic changes:

1. Move the old file from `.worklog/` to `.worklog/archive/`.
2. Create one or more new current documents in `.worklog/` with new numbers.
3. In each new document, add `Replaces:` with the old document number.
4. Do not rewrite the semantic content of the archived document.

Minor factual completion of a current work document is allowed only when it records durable context about that same document without changing its requirement or decision. Do not change old requirements in place to mean something new.

If a feature is removed from scope, archive the old feature spec and create a new current spec describing the current requirement that the feature is out of scope.

References use document numbers only:

```md
Replaces:
- 0008

Related:
- 0002
- 0007
```

Do not reference work documents by filename in `Replaces` or `Related`; filenames can change when a document is archived.

## Required Sections

Every work document should have:

- title;
- type;
- related documents;
- goal;
- context;
- done criteria.

Specs should also record scope and non-goals. For spikes, `Result` and `Recommendation` are required.

When a new document replaces an older one, it must include:

- `Replaces`.

## Outcome

`Outcome` is optional for `spec` and `adr`.

Add `Outcome` only when the actual implementation or investigation produced durable engineering context that is not obvious from the final code, commits, pull request, or tests.

Good reasons to add `Outcome`:

- implementation differs from the original plan;
- only part of the scope was implemented;
- an important technical nuance appeared during implementation;
- verification was partial or manual;
- a limitation was discovered;
- follow-up work should be preserved;
- the original spec remains mostly valid, but a small nuance changes how it should be understood.

Do not use `Outcome` to mark a spec as done.

A spec may remain current after implementation if it still describes current intended behavior.

If the meaning of the requirement or decision changed substantially, do not patch it through `Outcome`. Archive the old document and create a new current document with `Replaces:`.

Accepted ADR decisions must not be rewritten after acceptance. `Outcome` may record small execution notes only when they do not change the accepted decision.

For `spike`, `Result` and `Recommendation` remain required.

## Agent Workflow

Before significant implementation:

1. Read numbered current `.worklog/NNNN.*.md` documents relevant to the task.
2. Decide whether a new work document is needed.
3. If needed, create the next numbered document from `.worklog/_templates`.
4. Confirm scope, non-goals, and done criteria.
5. Implement only the described scope.

After significant implementation or investigation:

1. Run verification commands.
2. Reconcile the work document with the actual result.
3. Decide whether no worklog update is needed, `Outcome` should be added or updated, or a new work document should replace the old one.
4. Record deviations, limitations, partial verification, or follow-up only when they add durable context.
5. Do not silently rewrite existing requirements or accepted ADRs.
