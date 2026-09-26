@echo off
setlocal

pwsh.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0windows-wsl-docker.ps1" %*
exit /b %ERRORLEVEL%
