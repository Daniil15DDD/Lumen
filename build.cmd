@echo off
setlocal
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set CSC=%FW%\csc.exe
set REFS=/r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" /r:"%FW%\System.Xaml.dll" /r:System.Windows.Forms.dll /r:System.Drawing.dll
set OPTS=/nologo /target:winexe /platform:x64 /optimize+ /unsafe /codepage:65001 /resource:src\ui.xaml,Lumen.ui.xaml

if not exist build mkdir build
"%CSC%" %OPTS% %REFS% /out:build\icon-tool.exe src\*.cs || exit /b 1
build\icon-tool.exe --make-icon build\lumen.ico
"%CSC%" %OPTS% %REFS% /win32icon:build\lumen.ico /out:Lumen.exe src\*.cs || exit /b 1
del build\icon-tool.exe
echo Built %CD%\Lumen.exe
