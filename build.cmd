@echo off
rem Builds dist\VolkovExile.exe and tools\wm_test.exe with the C# compiler that ships with Windows.
setlocal
set F=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set M=%WINDIR%\System32\WinMetadata
set REFS=-r:System.Drawing.dll -r:System.Windows.Forms.dll -r:"%F%\System.Runtime.dll" -r:"%F%\System.Runtime.WindowsRuntime.dll" ^
 -r:"%M%\Windows.Foundation.winmd" -r:"%M%\Windows.Media.winmd" -r:"%M%\Windows.Graphics.winmd" ^
 -r:"%M%\Windows.Globalization.winmd" -r:"%M%\Windows.Storage.winmd" -r:"%M%\Windows.Security.winmd"
set RES=-resource:ws_tpl.png,ws_tpl.png -resource:volkov.ico,volkov.ico -resource:divine.wav,divine.wav
set SRC=Program.cs Analyzer.cs SlotDetector.cs CraftLock.cs ScreenGrab.cs Log.cs WaystoneOverlay.cs PriceReader.cs AntiLandmine.cs Config.cs

if not exist "%~dp0dist" mkdir "%~dp0dist"
pushd "%~dp0src"
"%F%\csc.exe" -nologo -optimize -platform:x64 %REFS% %RES% -target:winexe -win32icon:volkov.ico -out:..\dist\VolkovExile.exe %SRC% || goto :fail
"%F%\csc.exe" -nologo -optimize -platform:x64 %REFS% %RES% -out:..\tools\wm_test.exe %SRC% || goto :fail
popd
echo Built dist\VolkovExile.exe
exit /b 0

:fail
popd
exit /b 1
