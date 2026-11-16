@echo off
rem Removes WideAwake. You can also uninstall it from Settings > Apps > Installed apps.
set "EXE=%LOCALAPPDATA%\Programs\WideAwake\WideAwake.exe"
if not exist "%EXE%" (
  echo WideAwake is not installed.
  pause
  exit /b 0
)
start "" "%EXE%" /uninstall
