@echo off
setlocal EnableExtensions

pushd "%~dp0" >nul || exit /b 1

set "PROJECT=src\SuperCalcBenchmark.App\SuperCalcBenchmark.App.csproj"
set "OUTPUT=artifacts\standalone\SuperCalcBenchmark-win-x64"

echo SuperCalc Sicherheitsbenchmark - Standalone Publish
echo ===================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [Fehler] dotnet wurde nicht gefunden. Bitte .NET 10 SDK installieren und PATH pruefen.
    popd >nul
    exit /b 1
)

if not exist "%PROJECT%" (
    echo [Fehler] %PROJECT% wurde nicht gefunden. publish.bat muss aus dem Repository-Root gestartet werden.
    popd >nul
    exit /b 1
)

echo [1/2] Publish (Release, win-x64, self-contained, single-file)...
dotnet publish "%PROJECT%" ^
    --configuration Release ^
    --runtime win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    --output "%OUTPUT%"
if errorlevel 1 goto :fail

if not exist "%OUTPUT%\SuperCalcBenchmark.App.exe" (
    echo.
    echo [Fehler] Publish war erfolgreich, aber die EXE wurde nicht gefunden:
    echo         %OUTPUT%\SuperCalcBenchmark.App.exe
    popd >nul
    exit /b 1
)

echo.
echo [2/2] Fertig. Standalone-App liegt unter:
echo   %OUTPUT%\SuperCalcBenchmark.App.exe
echo.
echo Der Ordner ist komplett portabel: EXE plus benchmarks\ und enhanced_calc.cpp
echo koennen zusammen an einen beliebigen Ort kopiert werden. Kein .NET noetig.

popd >nul
exit /b 0

:fail
echo.
echo [Fehler] Publish ist fehlgeschlagen.
popd >nul
exit /b 1
