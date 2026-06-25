@echo off
rem Kill any running copy, rebuild the desktop app, then launch the fresh build.
cd /d "%~dp0"
set EXE=native\CodexLocalRetrieval.Native\bin\Debug\net8.0-windows10.0.26100.0\win-x64\CodexLocalRetrieval.Native.exe
taskkill /IM CodexLocalRetrieval.Native.exe /F >nul 2>&1
echo Building (this can take ~30s)...
dotnet build native\CodexLocalRetrieval.Native\CodexLocalRetrieval.Native.csproj -c Debug --nologo -v m
if errorlevel 1 (
  echo.
  echo Build FAILED - see errors above.
  pause
  exit /b 1
)
echo Launching...
start "" "%EXE%"
