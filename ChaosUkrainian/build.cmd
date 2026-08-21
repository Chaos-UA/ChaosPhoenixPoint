@echo off
setlocal

set MSBUILD="C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"

:: .NET 4.7.2 targeting pack is not installed; 4.8 is a superset and binary-compatible.
set FX_PATH=C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8

set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Debug

echo Building ChaosUkrainian [%CONFIG%]...
%MSBUILD% "%~dp0ChaosUkrainian.sln" /p:Configuration=%CONFIG% /p:FrameworkPathOverride="%FX_PATH%" /v:minimal /nologo

if %ERRORLEVEL% neq 0 (
    echo.
    echo BUILD FAILED.
    exit /b %ERRORLEVEL%
)

echo.
echo Build succeeded.