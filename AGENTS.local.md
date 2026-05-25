---
id: agents-local
scope: [codex, claude, gemini]
category: user-overrides
---
# Local AI agent overrides

This file holds project-specific rules and is **not managed by the template**.
It is safe to edit — template updates will not overwrite it.

Read `AGENTS.md` first, then apply the rules here with higher priority.

## Repo checks

<!-- Override or extend the defaults from docs/AI_RULES.md.
     Default: `dotnet build` then `dotnet test`
     Examples of project-specific additions:
       - dotnet build -c Release
       - dotnet test --configuration Release --no-build
       - dotnet format --verify-no-changes
-->

## Project-specific rules

<!-- Add durable corrections, preferences, and workflow rules below.
     Record rules when you give a correction you expect to stick permanently,
     or when a repeated mistake signals that a rule was missing. -->

- Do not add fallback paths in tests or product flows unless the user explicitly asks for compatibility behavior. Prefer fail-fast diagnostics in this learning project.
- All model calls must be typed through tool contracts, response-format schemas, or equivalent structured contracts. Do not ask a model to return ad hoc JSON text and then scrape JSON from prose.
- The character bible companion file path is always derived from the current book file path; do not require users or callers to set a separate bible path.
- The application shell owns left-side navigation. Keep the left menu fixed relative to the viewport and let page content manage its own vertical or horizontal scrolling when needed.
- Character bible extraction prompt must tell the LLM that aliases are name forms, nicknames, titles, or spelling/pronunciation variants, not pronouns. Do not add deterministic pronoun filtering in storage or post-processing unless explicitly requested.
- Character bible extraction prompt must require empty descriptions when personality cannot be inferred. Do not let the LLM write absence-of-detail notes or scene retellings as character descriptions.
- Do not add alias-based full-text character importance recalculation to ordinary Character Bible generation. Treat it as a separate explicit command with its own ambiguity rules.
