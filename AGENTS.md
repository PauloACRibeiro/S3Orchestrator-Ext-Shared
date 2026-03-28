# AGENTS.md

This repository is the shared slice of the main `S3Orchestrator_ExternalLogic` project. The main repo is canonical for releases and broader architectural changes.

## Cloud Knowledge Routing

- Use the hosted `workspace-knowledge` MCP for shared OutSystems public/internal guidance and support assets.
- Do not assume the umbrella workspace root is mounted in cloud tasks.
- `knowledge/private/personal` is local-only and must not be used in cloud workflows.

## Validation

- Build:
  - `dotnet build S3Orchestrator_ExternalLogic.csproj -c Release`
- Package only when explicitly asked:
  - `../workspace-agent-tools/scripts/publish_and_package.sh S3Orchestrator_ExternalLogic.csproj --version-file S3Orchestrator_ExternalLogic.cs --no-bump`

## Change Boundaries

- Keep this repo aligned with the canonical main repo.
- Route larger behavioral changes and release work through `projects/S3Orchestrator_ExternalLogic` unless the task is intentionally scoped to this shared slice.
