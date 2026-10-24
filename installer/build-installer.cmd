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

echo Publishing FrostLock (self-contained win-x64, single file)...
dotnet publish "..\FrostLock.csproj" -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o "publish"
if errorlevel 1 exit /b 1

echo Compiling the installer...
"%ISCC%" "FrostLock.iss"
if errorlevel 1 exit /b 1

echo.
echo Installer written to "%~dp0Output"
