@echo off
setlocal EnableExtensions

pushd "%~dp0" >nul || exit /b 1

set "RELEASE_EXE=src\SuperCalcBenchmark.App\bin\Release\net10.0-windows\SuperCalcBenchmark.App.exe"
set "DEBUG_EXE=src\SuperCalcBenchmark.App\bin\Debug\net10.0-windows\SuperCalcBenchmark.App.exe"

if exist "%RELEASE_EXE%" (
    set "APP_EXE=%RELEASE_EXE%"
) else if exist "%DEBUG_EXE%" (
    set "APP_EXE=%DEBUG_EXE%"
) else (
    echo [Fehler] Keine gebaute SuperCalcBenchmark.App.exe gefunden.
    echo Bitte zuerst setup.bat ausfuehren.
    popd >nul
    exit /b 1
)

for %%I in ("%APP_EXE%") do (
    set "APP_EXE=%%~fI"
    set "APP_DIR=%%~dpI"
    set "APP_FILE=%%~nxI"
)

echo Starte %APP_EXE% ...
start "SuperCalc Benchmark" /D "%APP_DIR%" "%APP_FILE%"
set "EXITCODE=%ERRORLEVEL%"

popd >nul
exit /b %EXITCODE%
