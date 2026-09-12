@echo off
rem Double-click to remove the installed MUX app + shortcuts. Your data is kept
rem (pass -PurgeData to delete it too).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\uninstall.ps1" %*
echo.
pause
