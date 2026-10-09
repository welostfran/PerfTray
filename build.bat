@echo off
rem Baut PerfTray.exe mit dem C#-Compiler, der in Windows (.NET Framework 4) schon enthalten ist.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /out:PerfTray.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll PerfTray.cs Lucide.cs
if errorlevel 1 (echo Build fehlgeschlagen & pause & exit /b 1)
echo Fertig: %~dp0PerfTray.exe
