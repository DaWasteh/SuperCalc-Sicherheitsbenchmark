@echo off
setlocal EnableExtensions

set "LAUNCHER=%~dp0start.vbs"
if not exist "%LAUNCHER%" (
    echo [Fehler] start.vbs wurde nicht gefunden.
    exit /b 1
)

rem Keep this batch file only as a compatibility wrapper.  Windows 11 may keep
rem Terminal/cmd windows alive for several seconds after any .bat exits, so the
rem no-console launcher is start.vbs.  Prefer double-clicking start.vbs directly.
wscript.exe //nologo "%LAUNCHER%"
exit /b %ERRORLEVEL%
