@echo off
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /warn:4 /win32manifest:app.manifest /out:mouser.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll mouser.cs
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)
echo Built mouser.exe
