@echo off
rem AOT publish helper. Locates a VS install with the VC++ Tools workload via
rem vswhere, sources vcvars64, then runs `dotnet publish` for the CLI project.

setlocal

set "vswhere=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%vswhere%" (
    echo error: vswhere.exe not found at "%vswhere%".
    echo install/repair the Visual Studio Installer from https://aka.ms/vs/install
    exit /b 1
)

set "vsInstall="
for /f "usebackq tokens=*" %%i in (`"%vswhere%" -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "vsInstall=%%i"

if not defined vsInstall (
    echo error: no Visual Studio install with the C++ Tools workload found.
    echo install "Desktop development with C++" via the Visual Studio Installer.
    exit /b 1
)

set "vcvars=%vsInstall%\VC\Auxiliary\Build\vcvars64.bat"
if not exist "%vcvars%" (
    echo error: vcvars64.bat not found at "%vcvars%".
    echo the C++ Tools workload appears incomplete; repair via the VS Installer.
    exit /b 1
)

rem Put the Installer dir on PATH first so vcvars and the AOT MSBuild target
rem can both find vswhere.exe.
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"

call "%vcvars%" >nul
if errorlevel 1 exit /b %errorlevel%

rem Preserve the encrypted GitHub token across publishes (DPAPI blob lives
rem next to the .exe; nuking publish/ on every build would force re-login).
set "tokenBackup=%TEMP%\copilot-bridge.github_token.bak"
if exist "%~dp0publish\github_token.dat" copy /y "%~dp0publish\github_token.dat" "%tokenBackup%" >nul

dotnet publish "%~dp0src\CopilotBridge.Cli\CopilotBridge.Cli.csproj" -c Release -r win-x64 -o "%~dp0publish"
set "publishExitCode=%errorlevel%"

if exist "%tokenBackup%" (
    copy /y "%tokenBackup%" "%~dp0publish\github_token.dat" >nul
    del "%tokenBackup%"
)

exit /b %publishExitCode%
