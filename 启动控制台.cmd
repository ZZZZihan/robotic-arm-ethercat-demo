@echo off
setlocal
set "CONSOLE_ROOT=%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%CONSOLE_ROOT%scripts\start-console.ps1" %*
set "CONSOLE_EXIT=%ERRORLEVEL%"
if not "%CONSOLE_EXIT%"=="0" (
  echo.
  echo Console startup failed. See the message above.
  if "%~1"=="" pause
)
exit /b %CONSOLE_EXIT%
