#requires -version 5
<#
  DEV ONLY: rebuild the MUX Debug build and launch it from bin\Debug for fast iteration.
  This is NOT the installed MUX app - for a real, pinnable install run scripts\install.ps1.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'native\CodexLocalRetrieval.Native\CodexLocalRetrieval.Native.csproj'
$exe  = Join-Path $repo 'native\CodexLocalRetrieval.Native\bin\Debug\net8.0-windows10.0.26100.0\win-x64\CodexLocalRetrieval.Native.exe'

Get-Process -Name CodexLocalRetrieval.Native -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
& dotnet build $proj -c Debug --nologo -v m
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
Start-Process $exe
Write-Host "Launched the MUX DEV build from bin\Debug. (Use scripts\install.ps1 for the real install.)" -ForegroundColor Yellow
