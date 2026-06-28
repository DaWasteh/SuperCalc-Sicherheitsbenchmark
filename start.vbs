Option Explicit

Dim fso, shell, root, releaseExe, debugExe, appExe, appDir
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

root = fso.GetParentFolderName(WScript.ScriptFullName)
releaseExe = fso.BuildPath(root, "src\SuperCalcBenchmark.App\bin\Release\net10.0-windows\SuperCalcBenchmark.App.exe")
debugExe = fso.BuildPath(root, "src\SuperCalcBenchmark.App\bin\Debug\net10.0-windows\SuperCalcBenchmark.App.exe")

If fso.FileExists(releaseExe) Then
    appExe = releaseExe
ElseIf fso.FileExists(debugExe) Then
    appExe = debugExe
Else
    MsgBox "Keine gebaute SuperCalcBenchmark.App.exe gefunden." & vbCrLf & vbCrLf & _
           "Bitte zuerst setup.bat ausführen.", _
           vbExclamation, "SuperCalc Benchmark"
    WScript.Quit 1
End If

appDir = fso.GetParentFolderName(appExe)
' Keep the repository root as working directory so the GUI writes ./archive next to the repo,
' not into src\SuperCalcBenchmark.App\bin\Release\... where copied benchmark assets also exist.
shell.CurrentDirectory = root
shell.Run """" & appExe & """", 1, False
WScript.Quit 0
