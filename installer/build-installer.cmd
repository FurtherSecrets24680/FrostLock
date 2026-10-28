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

for /f "tokens=3" %%V in ('findstr /b /c:"#define MyAppVersion" "FrostLock.iss"') do set "VERSION=%%~V"
set "SETUP=Output\FrostLock-Setup-%VERSION%.exe"
if not exist "%SETUP%" (
    echo Could not find the compiled installer: %SETUP%
    exit /b 1
)

echo Writing SHA-256 sidecar...
powershell.exe -NoProfile -Command "$ErrorActionPreference='Stop'; try { $file='%SETUP%'; $stream=[IO.File]::OpenRead($file); $sha=[Security.Cryptography.SHA256]::Create(); try { $hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant() } finally { $stream.Dispose(); $sha.Dispose() }; [IO.File]::WriteAllText($file+'.sha256','sha256:'+$hash,[Text.Encoding]::ASCII) } catch { Write-Error $_; exit 1 }"
if errorlevel 1 exit /b 1
for %%A in ("%SETUP%.sha256") do if %%~zA NEQ 71 (
    echo The SHA-256 sidecar was not written correctly.
    exit /b 1
)

echo.
echo Installer and SHA-256 written to "%~dp0Output"
