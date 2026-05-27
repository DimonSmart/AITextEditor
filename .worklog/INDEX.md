# Worklog Index

This file is a generated or manually maintained summary of current worklog documents.

The numbered worklog documents are the source of truth.
If this index conflicts with a numbered document, trust the numbered document.

## Current documents

| ID | Type | Title | Summary | Related |
|---|---|---|---|---|
| 0001 | spike | Character bible roadmap | Investigate the current project state and propose a staged path toward an automatic character bible pipeline for long Markdown manuscripts. | none |
| 0002 | spec | MAF character bible workflow | Add a Microsoft Agent Framework graph workflow for character bible generation while reusing current deterministic services. | 0001 |
| 0003 | spec | Character bible workflow stages | Split the MAF character bible workflow into typed traversal, extraction, matching, and commit stages. | 0001, 0002 |
| 0004 | spec | Character bible ambiguity bucket | Report ambiguous character matches without committing them as new or merged dossiers. | 0001, 0003 |
| 0006 | spec | Agentic Framework generic model calls | Replace project-owned OpenAI HTTP/JSON model-call clients with Microsoft Agent Framework generic typed calls. | 0002, 0003 |
| 0007 | spec | Blazor editor UI | Add a Blazor UI for editing Markdown, inspecting linear items, viewing character dossiers, and running character bible operations. | 0001, 0002, 0003, 0004, 0006 |
| 0008 | spec | Book file and bible loading | Add UI actions to open and save Markdown book files, load/save a derived companion character bible file, and use the native MudBlazor splitter. | 0007 |
| 0009 | spec | Program settings tab | Add a persisted Settings tab for program and LLM server connection values used by the Web UI. | 0007, 0008 |
| 0010 | spec | AI server profiles | Add multiple OpenAI-compatible AI server profiles with separate persisted current server and model selections. | 0006, 0009 |
| 0011 | spec | Character bible progress events | Show detailed progress events for character bible operations across document loading, traversal, extraction, resolution, and bible update stages. | 0003, 0007 |
| 0012 | spec | JSON character bible UI | Make Character Bible storage canonical JSON and replace the Markdown/YAML page with searchable MudBlazor character cards. | 0007, 0008 |
| 0013 | spec | Character Bible catalog polish | Improve the Character Bible catalog card hierarchy, toolbar filters, incomplete state, and edit dialog readability. | 0012 |
| 0014 | spec | Remembered book folder loading | Remember the last loaded book path in program settings and make Load use a selected or remembered disk path with derived companion bibles. | 0008, 0012 |
| 0015 | spec | Fixed MudBlazor shell navigation | Keep left navigation fixed in the MudBlazor app shell and use MudBlazor's default overlay layers for dialogs. | 0007, 0013 |
| 0016 | spec | Background character bible generation | Let character bible generation continue across page navigation, keep manual editing read-only while it runs, and make extraction batching configurable with overlap. | 0003, 0007, 0009, 0011, 0012 |
| 0017 | spec | Character importance level | Add a stable nullable Character Bible importance level derived from transient resolved-character activity during generation. | 0003, 0004, 0012, 0016 |
| 0019 | spec | Simplified character dossier card | Remove generic description and role bonds from character dossiers, and rename the status field to `statusAndCompetence`. | 0012, 0013, 0016, 0017 |
| 0020 | spec | Character card expanded readonly view | Add one-at-a-time expanded read-only character cards inside the Character Bible grid while keeping editing in the existing dialog. | 0012, 0013, 0019 |
| 0021 | spec | Character Bible agent structure | Separate Character Bible workflow, extraction, prompt, and resolution code without changing current behavior. | 0002, 0003, 0006, 0011 |
| 0023 | spec | Character bible overlap byte limit | Limit character bible extraction overlap by both paragraph count and optional byte size while keeping main batch sizing separate. | 0003, 0009, 0016 |
| 0025 | spec | Character candidate extraction contract | Narrow Character Bible extraction to pointer-backed local candidates and move profile authoring out of the extractor. | 0003, 0004, 0021 |
| 0026 | spec | Character dossier patch agent | Restore profile generation through a post-resolution dossier patch proposal agent. | 0003, 0021, 0025 |
| 0027 | spec | Character Bible resolver split | Split deterministic resolver responsibilities into archive search, identity resolution, and commit plan building. | 0003, 0004, 0021, 0025, 0026 |
| 0028 | spec | Character Bible full agent pipeline | Complete the remaining multi-agent pipeline contracts, post-processing, operation commit, and support collections. | 0025, 0026, 0027 |
| 0029 | spec | Character dossier evidence context | Expand pointer-backed candidate evidence into nearby document context before dossier patch synthesis. | 0025, 0026, 0027, 0028 |
| 0030 | spec | Malformed model response retry | Treat malformed model responses as parse errors with copyable diagnostics and rely on retries instead of JSON fragment recovery. | 0003, 0006, 0011 |
| 0031 | spec | LLM retry and log errors | Make operation-log errors visually explicit and keep all production LLM agent calls behind one shared retry mechanism. | 0006, 0011, 0030 |
| 0032 | spec | Character vector search | Add a pure semantic vector search tool over current Character Bible dossiers using an automatically synchronized in-memory index. | 0012, 0019, 0027, 0028, 0029 |

## Archived documents

Archived documents live in `archive/` and are not current requirements.

Read archived documents only when a current document references them through `Replaces`, when history is explicitly requested, or when explaining why a decision was made.

| ID | Type | Title | Summary | Replaced by |
|---|---|---|---|---|
| 0005 | spec | Remove Semantic Kernel from agent path | Move model calls from Semantic Kernel to typed Microsoft Agent Framework/OpenAI-compatible contracts. | 0006 |
| 0018 | spec | Character dossier structured profile | Added the earlier five-section profile shape that is superseded by the simplified four-field profile. | 0019 |
| 0022 | spec | Malformed JSON response recovery | Earlier JsonExtractor-based recovery requirement for malformed structured responses. | 0030 |
| 0024 | spec | Model response diagnostics UI | Earlier diagnostics UI requirement that included JsonExtractor recovery outcomes. | 0030 |

## Maintenance

Update this index after creating, archiving, replacing, or substantially reconciling worklog documents.
