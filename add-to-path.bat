@echo off
setlocal enabledelayedexpansion

rem ============================================================================
rem  add-to-path.bat - Add the folder containing Axplayer.exe to the USER PATH
rem  so you can launch it from any Command Prompt / terminal as "axplayer".
rem
rem  Usage:
rem     add-to-path.bat                 add the default publish folder (x64)
rem     add-to-path.bat ^"\path\to\dir^"   add a specific directory
rem
rem  The value is written via PowerShell, which broadcasts WM_SETTINGCHANGE
rem  right after the write. That is what makes the new PATH visible to new
rem  terminal windows immediately - reg.exe alone never notifies Windows, so
rem  the change would otherwise only appear after a logoff/reboot.
rem ============================================================================

rem --- Determine the directory to add -----------------------------------------
set "AX_DIR=%~1"
if "%AX_DIR%"=="" set "AX_DIR=%~dp0src\Axplayer\bin\Release\net10.0\win-x64\publish"

rem If the argument is a file (e.g. the path to Axplayer.exe), use its folder.
if exist "%AX_DIR%" (
    if not exist "%AX_DIR%\" for %%F in ("%AX_DIR%") do set "AX_DIR=%%~dpF"
)

rem Strip a single trailing backslash if present (registry writes dislike it).
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

rem --- Prompt for confirmation (unless /y passed) -----------------------------
echo.
echo  About to add:  %AX_DIR%
echo.
if "%AX_SKIP_PROMPT%"=="1" goto :proceed
choice /c YN /n /m "Add this folder to your user PATH? [Y/N]: "
if errorlevel 2 (
    echo  Cancelled - PATH unchanged.
    exit /b 0
)

:proceed
rem --- Add to the USER PATH and broadcast the change --------------------------
rem PowerShell reads the raw REG_EXPAND_SZ value (so %VAR% entries survive),
rem appends the folder only if it is not already there, writes it back with the
rem same expandable type, then broadcasts WM_SETTINGCHANGE so the new PATH is
rem live in new terminal windows immediately. The folder is passed through the
rem AX_DIR environment variable to avoid any command-line quoting issues.
set "AX_RESULT="
for /f "usebackq delims=" %%R in (`powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; try { $dir=$env:AX_DIR.TrimEnd('\'); $cur=(Get-ItemProperty -Path 'HKCU:\Environment' -Name 'Path' -ErrorAction SilentlyContinue).Path; if($null -eq $cur){$cur=''}; $has=$false; foreach($p in ($cur -split ';')){ if($p -and $p.Trim().TrimEnd('\').Equals($dir,[StringComparison]::OrdinalIgnoreCase)){ $has=$true } }; $status='ALREADY'; if(-not $has){ $new=if([string]::IsNullOrWhiteSpace($cur)){ $dir } else { $cur.TrimEnd(';')+';'+$dir }; Set-ItemProperty -Path 'HKCU:\Environment' -Name 'Path' -Value $new -Type ExpandString; $status='OK' }; Add-Type -Namespace Win32 -Name NativeMethods -MemberDefinition '[DllImport(\"user32.dll\", SetLastError=true, CharSet=CharSet.Auto)] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);'; $r=[UIntPtr]::Zero; [void][Win32.NativeMethods]::SendMessageTimeout([IntPtr]::Zero,0x1A,[UIntPtr]::Zero,'Environment',0x0002,5000,[ref]$r); Write-Output $status } catch { Write-Output 'FAIL'; exit 1 }"`) do set "AX_RESULT=%%R"

if "%AX_RESULT%"=="ALREADY" (
    echo  The folder is already on your user PATH.
    echo  Environment refreshed - it is live in new terminal windows now.
    exit /b 0
)
if "%AX_RESULT%"=="OK" (
    echo.
    echo  Added to your user PATH:  %AX_DIR%
    echo.
    echo  The change is live - open a new Command Prompt / terminal window
    echo  and run:  axplayer
    echo.
    exit /b 0
)

echo  [ERROR] Could not update the PATH registry key.
exit /b 1
