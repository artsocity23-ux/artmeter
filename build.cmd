@echo off
rem Builds ArtMeter.exe with the C# compiler that ships with .NET Framework 4 (no SDK needed).
rem donate.json is embedded as a resource: the exe stays a single file.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" -nologo -optimize -unsafe -platform:x64 -target:winexe ^
  -win32icon:icon.ico -resource:donate.json,donate.json -out:ArtMeter.exe -r:System.Windows.Forms.dll -r:System.Drawing.dll ^
  Program.cs Collector.cs Widget.cs Donate.cs Qr.cs Native.cs
