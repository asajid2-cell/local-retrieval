' Hidden launcher for scheduled task \CodexArchiveRemoteWatchdog.
'
' Task Scheduler starts this through wscript.exe, which is a GUI-subsystem
' binary and therefore has no console. Because the parent is console-less,
' Windows does not allocate a console for the child, and shell.Run style 0
' applies SW_HIDE at process creation, so the child never shows a window.
'
' Do not replace style 0 with 1, and do not rely on -WindowStyle Hidden:
' when the parent is console-less the window is created by the default
' terminal host before that flag is applied, so the window still appears.
' Passing an exit code through keeps Task Scheduler's failure handling and
' restart-on-failure policy working exactly as it did before.
'
' This task repeats every minute, so a visible console here is a once-a-minute
' window flash for as long as the task stays installed. Mirrors watch_muxd.vbs.
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
scriptPath = fso.BuildPath(fso.GetParentFolderName(WScript.ScriptFullName), "watch-remote-server.ps1")
command = """C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe"" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File """ & scriptPath & """"
exitCode = shell.Run(command, 0, True)
WScript.Quit exitCode
