@echo off
setlocal

if "%NETRATEL_LOCAL_FIRST_WSL_DISTRIBUTION%"=="" (
  echo NETRATEL_LOCAL_FIRST_WSL_DISTRIBUTION is required for the Windows WSL command shim. 1>&2
  exit /b 2
)

wsl.exe --distribution "%NETRATEL_LOCAL_FIRST_WSL_DISTRIBUTION%" --user root -- %*
exit /b %ERRORLEVEL%
