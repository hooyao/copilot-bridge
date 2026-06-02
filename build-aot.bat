@echo off
set PATH=%PATH%;C:\Program Files (x86)\Microsoft Visual Studio\Installer\

for /f "usebackq tokens=*" %%i in (`vswhere.exe -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do (
  set InstallDir=%%i
)

if "%InstallDir%"=="" (
  echo Could not find Visual Studio installation with VC++ tools.
  exit /b 1
)

call "%InstallDir%\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64
dotnet publish src/CopilotBridge.Cli -c Release -r win-x64
