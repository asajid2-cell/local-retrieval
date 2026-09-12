# Getting Started

This tutorial builds and opens MUX with sanitized sample data.

## Prerequisites

- Windows 10 or Windows 11
- .NET 8 SDK

## Build

```powershell
dotnet build .\native\CodexLocalRetrieval.Native\CodexLocalRetrieval.Native.csproj -p:Platform=x64
```

Expected result: `Build succeeded`.

## Run

```powershell
.\native\CodexLocalRetrieval.Native\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\CodexLocalRetrieval.Native.exe
```

Expected result: a WinUI window opens with two sanitized sample chats.

## Test

```powershell
dotnet test .\native\CodexLocalRetrieval.Native.Tests\CodexLocalRetrieval.Native.Tests.csproj -p:Platform=x64
```

Expected result: all service tests pass.

## Package

```powershell
.\tools\release\package-win-x64.ps1
```

Expected result: `artifacts/mux-win-x64.zip` is created. The release ZIP should contain a top-level `MUX.exe` launcher and an `app` folder. The runtime executable in that folder is `CodexLocalRetrieval.Native.exe` for internal compatibility. The first release ZIP is unsigned.
