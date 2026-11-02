@echo off
rem Renders the README demo from made-up numbers: docs\demo.gif, docs\demo-compact.gif, docs\widget.png, docs\compact.png.
rem Uses the app's own Widget code, so the pictures match the current build. The GIFs need Python 3 with Pillow.
cd /d "%~dp0.."
if exist dist\demo-frames rmdir /s /q dist\demo-frames
if not exist dist mkdir dist
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" -nologo -optimize -unsafe -platform:x64 -target:exe -main:Demo ^
  -resource:donate.json,donate.json -out:dist\demo.exe -r:System.Windows.Forms.dll -r:System.Drawing.dll ^
  Program.cs Collector.cs Widget.cs Donate.cs Qr.cs Native.cs demo\Demo.cs || exit /b 1
dist\demo.exe dist\demo-frames docs || exit /b 1
py -3 demo\make-gif.py dist\demo-frames docs
