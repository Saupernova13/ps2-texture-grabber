@echo off
REM ===========================================================================
REM  dlps2tex - friendly launcher for ps2tex.exe
REM
REM  Put this file and ps2tex.exe in the same folder, then add that folder to
REM  PATH. `dlps2tex "God of War"` is then all you need - no --query, no
REM  remembering where the exe lives.
REM
REM  Usage: dlps2tex "Game Name" [--json] [--interactive]
REM         dlps2tex "SLUS-21569"        (a serial works too)
REM         dlps2tex --list [--json]
REM         dlps2tex --status <jobId> [--json]
REM         dlps2tex --clean [--all] [--dry-run] [--json]
REM
REM  The download always runs in the BACKGROUND: ps2tex resolves the game and
REM  its links, spawns a detached worker and returns a job id straight away.
REM  Follow it with --status.
REM ===========================================================================

setlocal

REM --- locate ps2tex.exe ------------------------------------------------------
REM Release layout first (exe sits beside this shim), then a source checkout,
REM then PATH. No absolute path is baked in, so the same file works whether it
REM was unzipped from a release or copied out of the repo.
set "PS2TEX=%~dp0ps2tex.exe"
if exist "%PS2TEX%" goto :found

set "PS2TEX=%~dp0..\bin\publish\ps2tex.exe"
if exist "%PS2TEX%" goto :found

for %%I in (ps2tex.exe) do set "PS2TEX=%%~$PATH:I"
if defined PS2TEX if exist "%PS2TEX%" goto :found

echo [ERROR] ps2tex.exe not found.
echo.
echo Looked in:
echo   %~dp0ps2tex.exe                  ^(release layout: exe beside this shim^)
echo   %~dp0..\bin\publish\ps2tex.exe   ^(source checkout^)
echo   every directory on PATH
echo.
echo Fix it by either:
echo   * downloading the latest release zip and unpacking it so this shim and
echo     ps2tex.exe end up in the same folder, or
echo   * building from source:
echo       dotnet publish Ps2TextureGrabber.csproj -c Release -r win-x64 --self-contained -o bin\publish
exit /b 1

:found
if "%~1"==""        goto :usage
if /i "%~1"=="--help" goto :usage
if /i "%~1"=="-h"     goto :usage
if /i "%~1"=="/?"     goto :usage

REM --- subcommands carry no game name: hand the command line over untouched so
REM     trailing flags like --json and --all survive ---
if /i "%~1"=="--list"    goto :passthru
if /i "%~1"=="-list"     goto :passthru
if /i "%~1"=="--status"  goto :passthru
if /i "%~1"=="-status"   goto :passthru
if /i "%~1"=="--clean"   goto :passthru
if /i "%~1"=="-clean"    goto :passthru
if /i "%~1"=="--clear"   goto :passthru
if /i "%~1"=="--version" goto :passthru

REM --- otherwise arg 1 is the game name (or serial) and the rest are flags ---
set "QUERY=%~1"
set "REST="

:collect
shift
if "%~1"=="" goto :run_query
REM %1 (not %~1) keeps the caller's own quoting intact
set "REST=%REST% %1"
goto :collect

:run_query
"%PS2TEX%" --query "%QUERY%"%REST%
exit /b %ERRORLEVEL%

:passthru
"%PS2TEX%" %*
exit /b %ERRORLEVEL%

:usage
echo Usage: dlps2tex "Game Name" [--json] [--interactive]
echo        dlps2tex "SLUS-21569"
echo        dlps2tex --list [--json]
echo        dlps2tex --status ^<jobId^> [--json]
echo        dlps2tex --clean [--all] [--dry-run] [--json]
echo.
echo The download runs in the background and returns a job id immediately.
echo Poll it with:  dlps2tex --status ^<jobId^>
echo.
echo --clean deletes abandoned job working dirs and rebuildable caches.
echo         --all also clears finished job records, --dry-run only previews.
echo.
echo Using: %PS2TEX%
exit /b 1
