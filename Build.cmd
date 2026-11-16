@echo off
rem Builds WideAwake.exe, then WideAwakeSetup.exe (which contains WideAwake.exe).
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
"%CSC%" /nologo /target:winexe /out:WideAwake.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll WideAwake.cs || goto :fail
"%CSC%" /nologo /target:winexe /out:WideAwakeSetup.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /resource:WideAwake.exe,WideAwake.exe Setup.cs || goto :fail
echo Built WideAwake.exe and WideAwakeSetup.exe
exit /b 0
:fail
echo Build failed.
exit /b 1
