---
name: worklog-review
description: Review whether current changes follow the worklog-driven development rules. Use before commits or when checking worklog structure.
---

# Worklog review

Read:

1. `docs/worklog-method.md`
2. relevant numbered current work documents directly under `.worklog/`
3. current git diff
4. `AGENTS.md`
5. `CLAUDE.md` if present

Check:

1. Does this change require a work document?
2. If yes, does the document exist?
3. Is the type correct: `spec`, `adr`, or `spike`?
4. Is the numeric sequence correct across `.worklog/` and `.worklog/archive/`?
5. Are current work documents stored directly under `.worklog/`?
6. Are archived work documents stored under `.worklog/archive/`?
7. Do work document filenames avoid old lifecycle markers?
8. Does `.worklog/INDEX.md` exist and reflect the current numbered documents?
9. Does the document have clear Goal, Context, and done criteria?
10. For specs, are scope and non-goals present?
11. For specs and ADRs, if `Outcome` exists, does it add durable engineering context instead of task status?
12. For spikes, are `Result` and `Recommendation` present and meaningful?
13. Are archived documents excluded from current requirements unless referenced through `Replaces` or explicitly needed for history?
14. If an old requirement or ADR changed, was the old document moved to `.worklog/archive/` and the new current document given `Replaces:`?
15. Are accepted ADRs and old requirements left semantically immutable?
16. Are micro-changes and product-neutral small changes kept out of `.worklog/` and left to commit messages?

Legacy migration check:

Flag files matching:

- `.worklog/*.active.md`
- `.worklog/*.retired.md`
- `.worklog/archive/*.active.md`
- `.worklog/archive/*.retired.md`

These should be renamed to the new lifecycle-free format.

Output:

- `OK` if the structure is fine.
- Otherwise list concrete fixes.
- Do not modify files unless the user asks.
