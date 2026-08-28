@echo off
setlocal enabledelayedexpansion

rem ============================================================================
rem  build.bat - Build Axplayer as a single-file self-contained .exe
rem
rem  Usage:   build.bat
rem           build.bat -r          rebuild from scratch (clean)
rem           build.bat -x64        force 64-bit build
rem           build.bat -arm64      force ARM64 build
rem ============================================================================

rem --- Figure out the CPU architecture (used for the publish RID) -------------
if "%~1"=="-x64"   set "AX_ARCH=win-x64"
if "%~1"=="-arm64" set "AX_ARCH=win-arm64"

if not defined AX_ARCH (
    where %SystemRoot%\System32\reg.exe >nul 2>nul
    if not errorlevel 1 (
        rem Query PROCESSOR_ARCHITECTURE for win32-on-x64 / arm variants.
        set "AX_PROC=%PROCESSOR_ARCHITECTURE%"
        if /i "!AX_PROC!"=="ARM64" (
            set "AX_ARCH=win-arm64"
        ) else if /i "!AX_PROC!"=="x86" (
            if defined PROCESSOR_ARCHITEW6432 (
                set "AX_PROC=!PROCESSOR_ARCHITEW6432!"
            )
        )
    )
    if not defined AX_PROC set "AX_PROC=%PROCESSOR_ARCHITECTURE%"
    if /i "!AX_PROC!"=="AMD64" set "AX_ARCH=win-x64"
    if /i "!AX_PROC!"=="x86"   set "AX_ARCH=win-x86"
    if /i "!AX_PROC!"=="ARM64" set "AX_ARCH=win-arm64"
)

if not defined AX_ARCH set "AX_ARCH=win-x64"

rem --- Locate dotnet ----------------------------------------------------------
where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet not found on PATH. Install the .NET SDK and retry.
    exit /b 1
)

set "SLN=%~dp0Axplayer.slnx"
if not exist "%SLN%" (
    echo [ERROR] Solution not found: %SLN%
    exit /b 1
)

rem --- Clean build if requested ----------------------------------------------
if /i "%~1"=="-r" (
    echo [build] Cleaning previous output...
    rmdir /s /q "%~dp0src\Axplayer\bin\Release" 2>nul
    rmdir /s /q "%~dp0src\Axplayer\obj\Release" 2>nul
)

echo.
echo [build] Architecture: %AX_ARCH%
echo [build] dotnet:        %~f0 - using dotnet from PATH
echo [build] Building and publishing...
echo.

set "OUT=%~dp0src\Axplayer\bin\Release\net10.0\%AX_ARCH%\publish\Axplayer.exe"

dotnet publish "%~dp0src\Axplayer\Axplayer.csproj" ^
    -c Release ^
    -r %AX_ARCH% ^
    --self-contained true ^
    -p:PublishSingleFile=true
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed.
    exit /b 1
)

if not exist "%OUT%" (
    echo.
    echo [ERROR] Publish finished but exe not found at:
    echo         %OUT%
    exit /b 1
)

rem Report the exe size in MB.
set "AX_MB=0"
for %%F in ("%OUT%") do set "AX_BYTES=%%~zF"
if defined AX_BYTES set /a "AX_MB=!AX_BYTES! / 1048576"
if not defined AX_MB set "AX_MB=0"

echo.
echo   Build OK:
	echo       %OUT%
echo   Size:      !AX_MB! MB
echo   Run now:   "%OUT%"
echo.
echo   To call it as "axplayer" from any Command Prompt, use:
echo       add-to-path.bat
echo ============================================================
echo.
choice /c YN /n /m "Add this exe to your system PATH now? [Y/N]: "
if not errorlevel 2 (
    set "AX_SKIP_PROMPT=1"
    call "%~dp0add-to-path.bat" "%~dp0src\Axplayer\bin\Release\net10.0\%AX_ARCH%\publish"
)

endlocal
exit /b 0