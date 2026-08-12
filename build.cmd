@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe

"%CSC%" /nologo /target:winexe /out:"%~dp0TarkovColorToggle.exe" ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Management.dll ^
  /reference:System.Runtime.Serialization.dll ^
  "%~dp0src\*.cs"

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo Built %~dp0TarkovColorToggle.exe
