' launch_muxd.vbs - run launch_muxd.ps1 fully hidden (window style 0) and WAIT for it, so the
' MuxdSessionHost scheduled task stays "Running" for muxd's lifetime and never flashes a console.
' Mirrors the proven run-muxd-watchdog-hidden.vbs pattern already used by the watchdog task.
Set shell = CreateObject("WScript.Shell")
command = """C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe"" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""C:\Users\Ahmed\muxd\ops\launch_muxd.ps1"""
exitCode = shell.Run(command, 0, True)
WScript.Quit exitCode
