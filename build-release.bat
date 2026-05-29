@echo off
setlocal EnableExtensions

set "VERSION=1.0.1"
set "ROOT=%~dp0"
set "ROOT=%ROOT:~0,-1%"
set "APP_CSPROJ=%ROOT%\app\SpoofGUI\SpoofGUI.csproj"
set "DIST_DIR=%ROOT%\dist"
set "PUBLISH_DIR=%DIST_DIR%\publish"
set "ENGINE_SOURCE=%ROOT%\app\SpoofGUI\EngineSource"
if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" (
    set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
) else if exist "C:\Program Files\Inno Setup 6\ISCC.exe" (
    set "ISCC=C:\Program Files\Inno Setup 6\ISCC.exe"
) else (
    set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
)
:FoundIscc

echo.
echo SpoofGUI release build %VERSION%
echo Root: %ROOT%
echo.

tasklist /FI "IMAGENAME eq SpoofGUI.exe" 2>nul | find /I "SpoofGUI.exe" >nul
if not errorlevel 1 (
    echo ERROR: SpoofGUI.exe is running. Close the app before building release packages.
    exit /b 1
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo ERROR: dotnet SDK was not found in PATH.
    exit /b 1
)

where python >nul 2>nul
if errorlevel 1 (
    echo ERROR: python was not found in PATH. Needed to build SNI-Spoofing engine.
    exit /b 1
)

if exist "%ISCC%" goto IsccOk
echo ERROR: Inno Setup compiler not found.
echo Expected: C:\Program Files (x86)\Inno Setup 6\ISCC.exe
exit /b 1
:IsccOk

echo [1/4] Cleaning dist...
if exist "%DIST_DIR%" rmdir /s /q "%DIST_DIR%"
mkdir "%PUBLISH_DIR%" || exit /b 1

echo [2/4] Building SNI-Spoofing python backend (PyInstaller)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\build-python-engine.ps1"
if errorlevel 1 exit /b 1
if not exist "%ROOT%\app\SpoofGUI\Engine\SpoofGUI.SniSpoofEngine.exe" (
    echo ERROR: PyInstaller did not produce SpoofGUI.SniSpoofEngine.exe.
    exit /b 1
)

if not exist "%ROOT%\app\SpoofGUI\Engine\WinDivert.dll" (
    echo ERROR: engine\WinDivert.dll missing for SNI-Spoofing engine.
    exit /b 1
)
if not exist "%ROOT%\app\SpoofGUI\Engine\WinDivert64.dll" (
    echo ERROR: engine\WinDivert64.dll missing for SNI-Spoofing engine.
    exit /b 1
)
if not exist "%ROOT%\app\SpoofGUI\Engine\WinDivert64.sys" (
    echo ERROR: engine\WinDivert64.sys missing for SNI-Spoofing engine.
    exit /b 1
)

copy /Y "%ROOT%\app\SpoofGUI\Xray\wintun.dll" "%ROOT%\app\SpoofGUI\Engine\wintun.dll" >nul
if not exist "%ROOT%\app\SpoofGUI\Engine\wintun.dll" (
    echo ERROR: wintun.dll missing for sing-box tunnel mode.
    exit /b 1
)

set "XRAY_EXE=%ROOT%\app\SpoofGUI\Xray\xray.exe"
if not exist "%XRAY_EXE%" call :FetchXray
if errorlevel 1 exit /b 1
if not exist "%XRAY_EXE%" (
    echo ERROR: xray.exe missing after fetch.
    exit /b 1
)

set "SINGBOX_VERSION=1.13.12"
set "SINGBOX_EXE=%ROOT%\app\SpoofGUI\Engine\sing-box.exe"
if not exist "%SINGBOX_EXE%" call :FetchSingBox
if errorlevel 1 exit /b 1
if not exist "%SINGBOX_EXE%" (
    echo ERROR: sing-box.exe missing after fetch.
    exit /b 1
)

echo [3/4] Publishing SpoofGUI frontend self-contained...
dotnet publish "%APP_CSPROJ%" -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o "%PUBLISH_DIR%"
if errorlevel 1 exit /b 1

if not exist "%PUBLISH_DIR%\SpoofGUI.exe" (
    echo ERROR: Publish did not produce SpoofGUI.exe.
    exit /b 1
)

if not exist "%PUBLISH_DIR%\Xray\xray.exe" (
    echo ERROR: Publish did not include Xray\xray.exe.
    exit /b 1
)
if not exist "%PUBLISH_DIR%\engine\sing-box.exe" (
    echo ERROR: Publish did not include engine\sing-box.exe.
    exit /b 1
)

if not exist "%PUBLISH_DIR%\engine\SpoofGUI.SniSpoofEngine.exe" (
    echo ERROR: Publish did not include engine\SpoofGUI.SniSpoofEngine.exe.
    exit /b 1
)
if not exist "%PUBLISH_DIR%\engine\WinDivert.dll" (
    echo ERROR: Publish did not include engine\WinDivert.dll.
    exit /b 1
)
if not exist "%PUBLISH_DIR%\engine\WinDivert64.dll" (
    echo ERROR: Publish did not include engine\WinDivert64.dll.
    exit /b 1
)
if not exist "%PUBLISH_DIR%\engine\WinDivert64.sys" (
    echo ERROR: Publish did not include engine\WinDivert64.sys.
    exit /b 1
)
if not exist "%PUBLISH_DIR%\engine\wintun.dll" (
    echo ERROR: Publish did not include engine\wintun.dll.
    exit /b 1
)

robocopy "%ENGINE_SOURCE%" "%PUBLISH_DIR%\source\SNI-Spoofing" /E /XD .git __pycache__ /XF *.pyc >nul
if %ERRORLEVEL% GEQ 8 (
    echo ERROR: Failed to copy SNI-Spoofing source.
    exit /b 1
)

echo [4/4] Building portable and setup EXEs with Inno Setup...
set "SPOOFGUI_VERSION=%VERSION%"
set "SPOOFGUI_ROOT=%ROOT%"
set "SPOOFGUI_PUBLISH_DIR=%PUBLISH_DIR%"
set "SPOOFGUI_DIST_DIR=%DIST_DIR%"
del /f /q "%DIST_DIR%\SpoofGUI-Portable.exe" "%DIST_DIR%\SpoofGUI-Setup.exe" 2>nul

"%ISCC%" "%ROOT%\installer\SpoofGUI.Portable.iss"
if errorlevel 1 exit /b 1

"%ISCC%" "%ROOT%\installer\SpoofGUI.iss"
if errorlevel 1 exit /b 1

echo.
echo Done.
echo Portable: %DIST_DIR%\SpoofGUI-Portable.exe
echo Setup:    %DIST_DIR%\SpoofGUI-Setup.exe
echo.

endlocal
exit /b 0

:LoadMsvcEnv
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
    for /f "usebackq tokens=*" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do (
        set "VSINSTALL=%%I"
    )
)

if defined VSINSTALL (
    if exist "%VSINSTALL%\VC\Auxiliary\Build\vcvars64.bat" (
        call "%VSINSTALL%\VC\Auxiliary\Build\vcvars64.bat" >nul
        exit /b 0
    )
)

for %%D in (
    "%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools"
    "%ProgramFiles%\Microsoft Visual Studio\2022\Community"
    "%ProgramFiles%\Microsoft Visual Studio\2022\Professional"
    "%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise"
    "%ProgramFiles%\Microsoft Visual Studio\18\Community"
    "%ProgramFiles%\Microsoft Visual Studio\18\BuildTools"
) do (
    if exist "%%~D\VC\Auxiliary\Build\vcvars64.bat" (
        call "%%~D\VC\Auxiliary\Build\vcvars64.bat" >nul
        exit /b 0
    )
)

exit /b 0

:FetchXray
echo Fetching Xray-core (latest)...
set "XRAY_DIR=%ROOT%\app\SpoofGUI\Xray"
set "XRAY_ZIP=%TEMP%\xray.zip"
set "XRAY_URL=https://github.com/XTLS/Xray-core/releases/latest/download/Xray-windows-64.zip"
set "PS_CMD=$ErrorActionPreference='Stop'; Invoke-WebRequest -Uri '%XRAY_URL%' -OutFile '%XRAY_ZIP%' -UseBasicParsing; $tmp=Join-Path $env:TEMP 'xray_x'; if(Test-Path $tmp){Remove-Item -Recurse -Force $tmp}; Expand-Archive -Path '%XRAY_ZIP%' -DestinationPath $tmp -Force; Copy-Item -Force (Join-Path $tmp 'xray.exe') (Join-Path '%XRAY_DIR%' 'xray.exe'); Remove-Item -Force '%XRAY_ZIP%'; Remove-Item -Recurse -Force $tmp"
powershell -NoProfile -ExecutionPolicy Bypass -Command "%PS_CMD%"
exit /b %ERRORLEVEL%

:FetchSingBox
echo Fetching sing-box %SINGBOX_VERSION%...
set "SINGBOX_DIR=%ROOT%\app\SpoofGUI\Engine"
set "SINGBOX_ZIP=%TEMP%\singbox.zip"
set "SINGBOX_URL=https://github.com/SagerNet/sing-box/releases/download/v%SINGBOX_VERSION%/sing-box-%SINGBOX_VERSION%-windows-amd64.zip"
set "PS_CMD=$ErrorActionPreference='Stop'; Invoke-WebRequest -Uri '%SINGBOX_URL%' -OutFile '%SINGBOX_ZIP%' -UseBasicParsing; $tmp=Join-Path $env:TEMP 'singbox_x'; if(Test-Path $tmp){Remove-Item -Recurse -Force $tmp}; Expand-Archive -Path '%SINGBOX_ZIP%' -DestinationPath $tmp -Force; $exe=Get-ChildItem -Recurse -Path $tmp -Filter 'sing-box.exe' | Select-Object -First 1; Copy-Item -Force $exe.FullName (Join-Path '%SINGBOX_DIR%' 'sing-box.exe'); Remove-Item -Force '%SINGBOX_ZIP%'; Remove-Item -Recurse -Force $tmp"
powershell -NoProfile -ExecutionPolicy Bypass -Command "%PS_CMD%"
exit /b %ERRORLEVEL%
