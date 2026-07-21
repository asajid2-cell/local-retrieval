# Project Reference

## Projects

| path | purpose |
|---|---|
| `native/CodexLocalRetrieval.Core` | models, parsing, search, copy payloads, restore packets, local store access |
| `native/CodexLocalRetrieval.Native` | WinUI 3 desktop application |
| `native/CodexLocalRetrieval.Native.Tests` | service-level tests |
| `data/fixtures` | sanitized JSONL fixtures |
| `data/app-store.json` | sanitized seed store used on first run |

## Local Data

Checked-in seed:

```text
data/app-store.json
```

Runtime metadata:

```text
%LocalAppData%\CodexLocalRetrieval\app-store.json
```

## Commands

```powershell
dotnet build .\CodexLocalRetrieval.sln -p:Platform=x64
dotnet test .\native\CodexLocalRetrieval.Native.Tests\CodexLocalRetrieval.Native.Tests.csproj -p:Platform=x64
.\tools\release\package-win-x64.ps1
```

## Defaults

| setting | default |
|---|---|
| theme | AMOLED |
| accent | Rose |
| shape | Compact |
| source mode | read-only |
| chat source | auto-detected from `%USERPROFILE%\.codex\sessions` when available |
| AI provider | DeepSeek preset |
| AI key storage | Windows credentials |

## Release Artifact

| artifact | status |
|---|---|
| `codex-local-retrieval-win-x64.zip` | portable Windows x64 release with top-level launcher and runtime files under `app/` |
| MSIX | not shipped in the first release |
| signing | not implemented |

## Security Notes

The app reads local files and can display private chat content. Treat screenshots, exports, restore packets, and copied paths as potentially sensitive.
