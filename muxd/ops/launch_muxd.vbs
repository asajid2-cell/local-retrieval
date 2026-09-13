' launch_muxd.vbs - run launch_muxd.ps1 fully hidden (window style 0) and WAIT for it, so the
' MuxdSessionHost scheduled task stays "Running" for muxd's lifetime and never flashes a console.
' Mirrors the proven run-muxd-watchdog-hidden.vbs pattern already used by the watchdog task.
' The profile identity travels in the process environment so the hidden launch stays isolated.
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
scriptPath = fso.BuildPath(fso.GetParentFolderName(WScript.ScriptFullName), "launch_muxd.ps1")
profileName = shell.Environment("PROCESS")("MUXD_PROFILE")
If profileName = "" Then profileName = "production"
runtimeRoot = shell.Environment("PROCESS")("MUXD_RUNTIME_ROOT")
If runtimeRoot <> "" Then
  command = """C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe"" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File """ & scriptPath & """ -Profile """ & profileName & """ -RuntimeRoot """ & runtimeRoot & """"
Else
  command = """C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe"" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File """ & scriptPath & """ -Profile """ & profileName & """"
End If
exitCode = shell.Run(command, 0, True)
WScript.Quit exitCode
