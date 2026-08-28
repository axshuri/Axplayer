@echo off
setlocal enabledelayedexpansion

rem ============================================================================
rem  add-to-path.bat - Add the folder containing Axplayer.exe to the USER PATH
rem  so you can launch it from any Command Prompt / terminal as "axplayer".
rem
rem  Usage:
rem     add-to-path.bat                 add the default publish folder (x64)
rem     add-to-path.bat ^"\path\to\dir^"   add a specific directory
rem ============================================================================

rem --- Determine the directory to add -----------------------------------------
set "AX_DIR=%~1"
if "%AX_DIR%"=="" set "AX_DIR=%~dp0src\Axplayer\bin\Release\net10.0\win-x64\publish"

rem If the argument is a file (e.g. the path to Axplayer.exe), use its folder.
if exist "%AX_DIR%" (
    if not exist "%AX_DIR%\" for %%F in ("%AX_DIR%") do set "AX_DIR=%%~dpF"
)

rem Strip a single trailing backslash if present (reg.exe dislikes trailing \).
if "%AX_DIR:~-1%"=="\" set "AX_DIR=%AX_DIR:~0,-1%"
if "%AX_DIR:~-1%"=="/" set "AX_DIR=%AX_DIR:~0,-1%"

rem --- Validate ---------------------------------------------------------------
if not exist "%AX_DIR%" (
    echo [ERROR] Directory not found: %AX_DIR%
    echo         Run build.bat first to create the exe.
    exit /b 1
)
if not exist "%AX_DIR%\Axplayer.exe" (
    echo [ERROR] Axplayer.exe not found in: %AX_DIR%
    echo         Run build.bat first to create the exe.
    exit /b 1
)

rem --- Read the current USER PATH (HKCU\Environment) --------------------------
set "KEY=HKCU\Environment"
set "AX_CUR="
rem Parses:  PATH    REG_EXPAND_SZ    <value>. The value may contain spaces, so we
rem take everything after the type token.
for /f "skip=2 tokens=2*" %%A in ('reg query "%KEY%" /v Path 2^>nul') do set "AX_CUR=%%B"
echo.
echo  About to add:  %AX_DIR%
echo.

rem --- Prompt for confirmation (unless /y passed) -----------------------------
if "%AX_SKIP_PROMPT%"=="1" goto :proceed
choice /c YN /n /m "Add this folder to your user PATH? [Y/N]: "
if errorlevel 2 (
    echo  Cancelled - PATH unchanged.
    exit /b 0
)

:proceed
rem --- Idempotency: skip if already on the PATH -------------------------------
echo.%AX_CUR% | findstr /c:"%AX_DIR%" >nul
if not errorlevel 1 (
    echo  The folder is already on your PATH. Nothing to do.
    exit /b 0
)

rem --- Build the new PATH value ----------------------------------------------
set "AX_NEW=%AX_CUR%"
if not "%AX_NEW%"=="" set "AX_NEW=%AX_NEW%;"
set "AX_NEW=%AX_NEW%%AX_DIR%"

rem --- Write it back to the user environment -----------------------------------
reg add "%KEY%" /v Path /t REG_EXPAND_SZ /d "%AX_NEW%" /f >nul
if errorlevel 1 (
    echo  [ERROR] Could not update the PATH registry key.
    exit /b 1
)

echo.
echo  Added to your user PATH:  %AX_DIR%
echo.
echo  For the change to take effect in already-open windows, start a new
echo  Command Prompt / terminal window, then run:  axplayer
echo  (Windows may need a fresh logon to broadcast the change on older systems.)
echo.

endlocal
exit /b 0