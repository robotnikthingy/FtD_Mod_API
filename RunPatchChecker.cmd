@echo off
rem Checks every FtdModApi patch against the installed game, without running it. Run after a game update.
rem Options: --managed "<game>\From_The_Depths_Data\Managed"  --all  --verbose
rem Exit code: 0 = all required patches fit, 1 = some feature broken, 2 = the check couldn't run, 3 = build failed.
setlocal
pushd "%~dp0"

rem Use the dotnet that has an SDK: a runtime-only copy (often in Program Files (x86)) can come first on PATH.
set "DOTNET=dotnet"
if exist "%ProgramFiles%\dotnet\sdk\" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"

"%DOTNET%" build "Tools\PatchChecker" -c Release -v quiet -nologo
if %ERRORLEVEL% NEQ 0 (
  echo.
  echo The patch checker did not build. If it says no .NET SDK was found, run RunPatchChecker.ps1 from the
  echo PowerShell window you build in, or install the .NET SDK from https://aka.ms/dotnet/download
  popd
  exit /b 3
)
"%DOTNET%" run -c Release --no-build --project "Tools\PatchChecker" -- %*
set CODE=%ERRORLEVEL%
popd
exit /b %CODE%
