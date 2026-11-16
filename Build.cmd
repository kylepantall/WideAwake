@echo off
rem Builds WideAwake.exe, then WideAwakeSetup.exe (which contains WideAwake.exe).
rem Usage: Build.cmd [app^|setup]   - no argument builds both.
rem CI uses the two steps separately so WideAwake.exe can be signed before it is embedded.
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "ICON=/win32icon:assets\WideAwake.ico /resource:assets\WideAwake.ico,WideAwake.ico"
if /i "%~1"=="setup" goto setup
"%CSC%" /nologo /target:winexe /out:WideAwake.exe %ICON% /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll WideAwake.cs || goto fail
echo Built WideAwake.exe
if /i "%~1"=="app" exit /b 0
:setup
"%CSC%" /nologo /target:winexe /out:WideAwakeSetup.exe %ICON% /r:System.Windows.Forms.dll /r:System.Drawing.dll /resource:WideAwake.exe,WideAwake.exe Setup.cs || goto fail
echo Built WideAwakeSetup.exe
exit /b 0
:fail
echo Build failed.
exit /b 1
