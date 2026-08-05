@echo off
setlocal EnableExtensions
title Archon
color 0A
pushd "%~dp0"

set "PS=powershell -NoProfile -ExecutionPolicy Bypass"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

REM fltmc, not net session - net session needs the Server service, often disabled
fltmc >nul 2>&1
if errorlevel 1 (set "ADMIN=no") else (set "ADMIN=yes")

:menu
cls
echo.
echo   ==========================================
echo    ARCHON
echo   ==========================================
echo.
if "%ADMIN%"=="yes" (
    echo    Administrator: YES
) else (
    echo    Administrator: no  -  option 5 restarts elevated
)
echo.
echo    [1] Full launcher  ^(recommended^)
echo        Frees the singleton handles from any running Roblox,
echo        then holds both names. Works even if Roblox is open.
echo.
echo    [2] Names only
echo        Holds both names, never touches a running Roblox.
echo        Start this BEFORE opening Roblox.
echo.
echo    [3] Run the GUI app
echo        Window, tray icon, and saved-account launching.
echo.
echo    [4] Build the GUI app
echo        Compiles Archon.exe from source.
echo.
echo    [5] Restart as Administrator
echo    [6] Exit
echo.

set "choice="
set /p "choice=   Choice: "
if not defined choice goto menu
set "choice=%choice:"=%"

if "%choice%"=="1" goto full
if "%choice%"=="2" goto namesonly
if "%choice%"=="3" goto rungui
if "%choice%"=="4" goto buildgui
if "%choice%"=="5" goto elevate
if "%choice%"=="6" goto done
echo.
echo    "%choice%" is not one of the options.
ping -n 3 127.0.0.1 >nul 2>&1
goto menu

:full
echo.
if "%ADMIN%"=="no" echo    Note: without Administrator, freeing handles from a running Roblox may fail.
echo.
%PS% -File "%~dp0MultiInstance.ps1"
goto after

:namesonly
echo.
%PS% -File "%~dp0MultiInstance.ps1" -SkipHandleClose
goto after

:rungui
if not exist "%~dp0Archon.exe" (
    echo.
    echo    Archon.exe not found - build it first ^(option 4^).
    echo.
    pause
    goto menu
)
start "" "%~dp0Archon.exe"
goto done

:buildgui
echo.
if not exist "%CSC%" (
    echo    Could not find the .NET Framework C# compiler:
    echo      %CSC%
    echo    Install .NET Framework 4.x, or build with Visual Studio.
    echo.
    pause
    goto menu
)
echo    Building with:
echo      %CSC%
echo.
"%CSC%" /nologo /target:winexe /out:"%~dp0Archon.exe" "%~dp0*.cs" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Security.dll
if errorlevel 1 (
    echo.
    echo    Build FAILED - see the errors above.
) else (
    echo.
    echo    Build OK  -^>  Archon.exe
)
echo.
pause
goto menu

:elevate
if "%ADMIN%"=="yes" (
    echo.
    echo    Already running as Administrator.
    ping -n 3 127.0.0.1 >nul 2>&1
    goto menu
)
%PS% -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
goto done

:after
echo.
pause
goto menu

:done
popd
endlocal
