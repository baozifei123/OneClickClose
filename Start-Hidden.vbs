' Launch OneClickClose.ps1 without a console window.
' Double-click this file for daily use; the UI is available via the tray icon
' or the global hotkey (Ctrl+Alt+X by default).

Option Explicit

Dim fso, shell, baseDir, scriptPath, command
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

baseDir = fso.GetParentFolderName(WScript.ScriptFullName)
scriptPath = fso.BuildPath(baseDir, "OneClickClose.ps1")

command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & scriptPath & """ -Hidden"
shell.Run command, 0, False
