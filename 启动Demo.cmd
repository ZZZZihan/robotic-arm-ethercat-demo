@echo off
setlocal
set "DEMO_ROOT=%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%DEMO_ROOT%scripts\start-demo.ps1" %*
set "DEMO_EXIT=%ERRORLEVEL%"
if not "%DEMO_EXIT%"=="0" (
  echo.
  echo Demo startup failed. See the message above.
  if "%~1"=="" pause
)
exit /b %DEMO_EXIT%
