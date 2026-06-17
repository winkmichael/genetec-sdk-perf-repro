@echo off
REM ============================================================================
REM Genetec SDK Performance Test - build + run
REM ============================================================================

setlocal

set MSBUILD="C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
if not exist %MSBUILD% (
    echo MSBuild not found at %MSBUILD%
    echo Edit run.bat and point MSBUILD at your MSBuild.exe
    exit /b 1
)

echo === Restoring NuGet packages ===
%MSBUILD% GenetecPerformanceTest.csproj /t:Restore /p:Configuration=Release /v:minimal /nologo
if errorlevel 1 (
    echo Restore failed.
    exit /b 1
)

echo === Building ===
%MSBUILD% GenetecPerformanceTest.csproj /p:Configuration=Release /v:minimal /nologo
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)

echo.
echo === Running ===
echo.
"%~dp0bin\Release\net48\GenetecPerformanceTest.exe"

endlocal
