# Contributing

## Setup

Install the .NET 8 SDK on Windows, then build:

```powershell
dotnet build .\CodexLocalRetrieval.sln -p:Platform=x64
```

Run tests:

```powershell
dotnet test .\native\CodexLocalRetrieval.Native.Tests\CodexLocalRetrieval.Native.Tests.csproj -p:Platform=x64
```

## Pull Requests

- Keep source-session handling read-only unless the change is explicitly about export/write behavior.
- Use sanitized fixtures only. Do not commit real `app-store.json`, `.codex` sessions, local indexes, screenshots with private paths, or build output.
- Update docs when behavior or commands change.
- Include the validation command you ran in the PR description.

## Release Work

Packaging and signing should follow `docs/how-to/signing.md`.
