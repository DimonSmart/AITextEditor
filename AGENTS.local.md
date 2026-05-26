# Local Codex instructions

- Keep LLM agent prompts in separate Markdown files when adding or refactoring agent prompts.
- Treat tests that call a local LLM as long/manual checks. Exclude them from the default verification run with `--filter "Category!=Manual"` unless the user explicitly asks to run local LLM tests.
- Character Bible dossier patching must treat current-run evidence as additive input against the existing dossier. Grouping evidence by resolved dossier must not replace incremental updates: the patch agent should propose only new meaningful additions, and return no changes when the new evidence does not add information.
- Character Bible dossier patching must batch grouped evidence by a bounded context size so one resolved dossier can be updated through multiple incremental patch calls instead of sending an unbounded prompt.
- Do not try to recover malformed LLM JSON responses by extracting JSON fragments from raw text. Keep the raw response diagnostic and use ordinary retry attempts instead.
- Route production LLM agent calls through the shared retry mechanism instead of local ad hoc retry loops.
- Display operation log errors with explicit red styling. Keep raw malformed-response copy actions visually quiet, closer to a text link than a primary button.
