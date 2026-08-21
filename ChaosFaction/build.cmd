@echo off
setlocal

set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Release

:: Locate MSBuild via vswhere
set VSWHERE="%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist %VSWHERE% set VSWHERE="%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"

if exist %VSWHERE% (
    for /f "usebackq tokens=*" %%i in (`%VSWHERE% -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set MSBUILD=%%i
)

if not defined MSBUILD (
    set MSBUILD=msbuild
)

"%MSBUILD%" ChaosFaction.sln /p:Configuration=%CONFIG% /v:minimal /nologo
