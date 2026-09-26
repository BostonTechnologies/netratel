@echo off
setlocal

if "%NETRATEL_LOCAL_FIRST_WSL_DISTRIBUTION%"=="" (
  echo NETRATEL_LOCAL_FIRST_WSL_DISTRIBUTION is required for the Windows WSL Docker shim. 1>&2
  exit /b 2
)
if "%NETRATEL_LOCAL_FIRST_WSL_WORKSPACE%"=="" (
  echo NETRATEL_LOCAL_FIRST_WSL_WORKSPACE is required for the Windows WSL Docker shim. 1>&2
  exit /b 2
)

wsl.exe --distribution "%NETRATEL_LOCAL_FIRST_WSL_DISTRIBUTION%" --user root --cd "%NETRATEL_LOCAL_FIRST_WSL_WORKSPACE%" -- docker %*
exit /b %ERRORLEVEL%
