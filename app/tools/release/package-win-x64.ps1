Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..\..")
$artifactsRoot = Join-Path $repoRoot "artifacts"
$packageRoot = Join-Path $artifactsRoot "codex-local-retrieval-package"
$appRoot = Join-Path $packageRoot "app"
$zipPath = Join-Path $artifactsRoot "codex-local-retrieval-win-x64.zip"
$projectPath = Join-Path $repoRoot "native\CodexLocalRetrieval.Native\CodexLocalRetrieval.Native.csproj"
$buildOutput = Join-Path $repoRoot "native\CodexLocalRetrieval.Native\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64"
$launcherSource = Join-Path $scriptRoot "StartCodexLocalRetrieval.c"
$launcherPath = Join-Path $packageRoot "Codex Local Retrieval.exe"
$iconPath = Join-Path $repoRoot "native\CodexLocalRetrieval.Native\Assets\AppIcon.ico"
$dataSource = Join-Path $repoRoot "data"
$dataTarget = Join-Path $appRoot "data"
$objPath = Join-Path $artifactsRoot "StartCodexLocalRetrieval.obj"
$rcPath = Join-Path $artifactsRoot "launcher-icon.rc"
$resPath = Join-Path $artifactsRoot "launcher-icon.res"

function Find-VsDevCmd {
    $defaultPath = Join-Path ${env:ProgramFiles} "Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat"
    if (Test-Path $defaultPath) {
        return $defaultPath
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $installPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($installPath) {
            $candidate = Join-Path $installPath "Common7\Tools\VsDevCmd.bat"
            if (Test-Path $candidate) {
                return $candidate
            }
        }
    }

    throw "Could not find Visual Studio C++ build tools. Install Desktop development with C++ or update Find-VsDevCmd in this script."
}

if (Test-Path $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $appRoot -Force | Out-Null

dotnet build $projectPath -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) {
    throw "App build failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path (Join-Path $buildOutput "CodexLocalRetrieval.Native.exe"))) {
    throw "Expected build output was not found at $buildOutput."
}

Copy-Item -Path (Join-Path $buildOutput "*") -Destination $appRoot -Recurse -Force

Get-ChildItem -Path $appRoot -Recurse -Filter *.pdb | Remove-Item -Force

New-Item -ItemType Directory -Path $dataTarget -Force | Out-Null
Copy-Item -Path (Join-Path $dataSource "app-store.json") -Destination $dataTarget -Force
Copy-Item -Path (Join-Path $dataSource "fixtures") -Destination $dataTarget -Recurse -Force

$readmeFirst = @"
Codex Local Retrieval

Run "Codex Local Retrieval.exe".

The app files are in app\. Keep the app folder next to the launcher.
This build is unsigned, so Windows SmartScreen may warn on first launch.
"@
Set-Content -Path (Join-Path $packageRoot "README-FIRST.txt") -Value $readmeFirst -Encoding UTF8

$rcIconPath = $iconPath.Replace("\", "/")
Set-Content -Path $rcPath -Value "1 ICON `"$rcIconPath`"" -Encoding ASCII

$vsDevCmd = Find-VsDevCmd
$compileCommand = "`"$vsDevCmd`" -arch=x64 -host_arch=x64 >nul && rc.exe /nologo /fo `"$resPath`" `"$rcPath`" && cl.exe /nologo /O2 /MT /Fo:`"$objPath`" /Fe:`"$launcherPath`" `"$launcherSource`" `"$resPath`" user32.lib"
cmd.exe /c $compileCommand
if ($LASTEXITCODE -ne 0) {
    throw "Launcher build failed with exit code $LASTEXITCODE."
}

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -Force

Write-Host "Created $zipPath"
