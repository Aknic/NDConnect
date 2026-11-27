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
    /resource:assets\glyph_xbox.png,NDConnect.Assets.glyph_xbox.png ^
    /resource:assets\glyph_playstation.png,NDConnect.Assets.glyph_playstation.png ^
    /resource:assets\glyph_nintendo.png,NDConnect.Assets.glyph_nintendo.png ^
    /resource:assets\glyph_bluetooth.png,NDConnect.Assets.glyph_bluetooth.png ^
    /resource:assets\glyph_keyboard.png,NDConnect.Assets.glyph_keyboard.png ^
    /resource:assets\glyph_mouse.png,NDConnect.Assets.glyph_mouse.png ^
    /resource:assets\glyph_headset.png,NDConnect.Assets.glyph_headset.png ^
    /resource:assets\glyph_gamepad.png,NDConnect.Assets.glyph_gamepad.png ^
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
