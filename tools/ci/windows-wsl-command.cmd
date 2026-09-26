@echo off
setlocal

pwsh.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0windows-wsl-command.ps1" %*
exit /b %ERRORLEVEL%
