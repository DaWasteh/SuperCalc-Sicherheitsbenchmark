@echo off
setlocal EnableExtensions

pushd "%~dp0" >nul || exit /b 1

set "SOLUTION=SuperCalcBenchmark.slnx"
set "CONFIGURATION=Release"
set "APP_EXE=src\SuperCalcBenchmark.App\bin\Release\net10.0-windows\SuperCalcBenchmark.App.exe"

echo SuperCalc Sicherheitsbenchmark - Setup
echo =====================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [Fehler] dotnet wurde nicht gefunden. Bitte .NET 10 SDK installieren und PATH pruefen.
    popd >nul
    exit /b 1
)

if not exist "%SOLUTION%" (
    echo [Fehler] %SOLUTION% wurde nicht gefunden. setup.bat muss aus dem Repository-Root gestartet werden.
    popd >nul
    exit /b 1
)

echo [1/3] Clean (%CONFIGURATION%)...
dotnet clean "%SOLUTION%" --configuration "%CONFIGURATION%"
if errorlevel 1 goto :fail

echo.
echo [2/3] Restore...
dotnet restore "%SOLUTION%"
if errorlevel 1 goto :fail

echo.
echo [3/3] Build (%CONFIGURATION%, clean/no-incremental)...
dotnet build "%SOLUTION%" --configuration "%CONFIGURATION%" --no-restore --no-incremental
if errorlevel 1 goto :fail

if not exist "%APP_EXE%" (
    echo.
    echo [Fehler] Build war erfolgreich, aber die App-Exe wurde nicht gefunden:
    echo         %APP_EXE%
    popd >nul
    exit /b 1
)

echo.
echo Fertig. Die App wurde gebaut:
echo   %APP_EXE%
echo.
echo Starten mit:
echo   start.bat

popd >nul
exit /b 0

:fail
echo.
echo [Fehler] Setup/Build ist fehlgeschlagen.
popd >nul
exit /b 1
