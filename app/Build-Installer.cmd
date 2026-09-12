@echo off
rem Build the per-user MUX installer into dist\MUX.msi.
rem Requires the WiX tool: dotnet tool install --global wix
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-msi.ps1" %*
echo.
pause
