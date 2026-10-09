@echo off
rem Baut PerfTray.exe mit dem C#-Compiler, der in Windows (.NET Framework 4) schon enthalten ist.
rem Die Windows-Medienschnittstelle kommt aus den WinMetadata-Dateien, die jedes Windows 10/11 mitbringt.
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WM=%WINDIR%\System32\WinMetadata
"%FW%\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /out:PerfTray.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"%FW%\System.Runtime.dll" ^
  /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Media.winmd" /r:"%WM%\Windows.Storage.winmd" ^
  PerfTray.cs Lucide.cs Media.cs AppVolume.cs
if errorlevel 1 (echo Build fehlgeschlagen & pause & exit /b 1)
echo Fertig: %~dp0PerfTray.exe
