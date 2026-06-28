@echo off
rem Double-click to install Codex Local Retrieval into %LOCALAPPDATA%\Programs and
rem create Start Menu + Desktop shortcuts.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1" %*
echo.
pause
