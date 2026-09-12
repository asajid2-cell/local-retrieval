@echo off
rem Double-click to install MUX into %LOCALAPPDATA%\Programs\MUX and
rem create MUX Start Menu + Desktop shortcuts.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1" %*
echo.
pause
