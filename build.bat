@echo off
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found at %CSC%
    exit /b 1
)

if not exist bin mkdir bin

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /utf8output ^
    /win32icon:assets\app.ico ^
    /win32manifest:app.manifest ^
    /out:bin\NDConnect.exe ^
    /reference:System.dll ^
    /reference:System.Core.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Runtime.Serialization.dll ^
    /recurse:src\*.cs

if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 1
)

echo [OK] Built bin\NDConnect.exe
endlocal
