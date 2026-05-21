@echo off
setlocal
pushd "%~dp0"

if not exist "%~dp0sdrtrunk-feeds.yml" (
    echo Missing "%~dp0sdrtrunk-feeds.yml"
    echo Copy sdrtrunk-feeds.example.yml to sdrtrunk-feeds.yml and edit it first.
    pause
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-sdrtrunk-feeds.ps1" -Config "%~dp0sdrtrunk-feeds.yml" %*
set "RC=%ERRORLEVEL%"

popd
if not "%RC%"=="0" pause
exit /b %RC%
