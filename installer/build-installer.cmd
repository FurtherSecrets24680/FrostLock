@echo off
setlocal
cd /d "%~dp0"

rem Inno Setup 6 is installed per-user by winget; fall back to the machine-wide paths.
set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
    echo Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php
    exit /b 1
)

rem Start from a clean folder so a previous self-contained publish cannot leak its
rem runtime DLLs into the installer.
if exist "publish" rd /s /q "publish"

rem Framework-dependent publish: the app itself is only a few MB and the installer
rem asks Setup to pull in the .NET 10 Desktop Runtime when the machine lacks it.
rem For a fully offline, self-contained installer instead, swap the publish line for:
rem   dotnet publish "..\FrostLock.csproj" -c Release -r win-x64 --self-contained true ^
rem       -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
rem       -p:EnableCompressionInSingleFile=true -o "publish"
echo Publishing FrostLock (framework-dependent win-x64)...
dotnet publish "..\FrostLock.csproj" -c Release -r win-x64 --self-contained false ^
    -o "publish"
if errorlevel 1 exit /b 1

echo Compiling the installer...
"%ISCC%" "FrostLock.iss"
if errorlevel 1 exit /b 1

echo.
echo Installer written to "%~dp0Output"
