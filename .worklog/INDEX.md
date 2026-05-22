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

## Archived documents

Archived documents live in `archive/` and are not current requirements.

Read archived documents only when a current document references them through `Replaces`, when history is explicitly requested, or when explaining why a decision was made.

## Maintenance

Update this index after creating, archiving, replacing, or substantially reconciling worklog documents.
