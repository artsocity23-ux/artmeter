@echo off
rem Builds ArtMeter.exe with the C# compiler that ships with .NET Framework 4 (no SDK needed).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" -nologo -optimize -unsafe -platform:x64 -target:winexe ^
  -win32icon:icon.ico -out:ArtMeter.exe -r:System.Windows.Forms.dll -r:System.Drawing.dll ^
  Program.cs Collector.cs Widget.cs Native.cs
