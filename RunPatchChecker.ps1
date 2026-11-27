# Checks every FtdModApi patch against the installed game, without running it. Run after a game update.
# Usage (from the src folder):  .\RunPatchChecker.ps1   [--managed "<game>\From_The_Depths_Data\Managed"] [--all] [--verbose]
# Exit code: 0 = all required patches fit, 1 = some feature broken, 2 = the check couldn't run, 3 = build failed.
Push-Location $PSScriptRoot
try {
    dotnet build "Tools\PatchChecker" -c Release -v quiet -nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host "The patch checker did not build."
        exit 3
    }
    dotnet run -c Release --no-build --project "Tools\PatchChecker" -- @args
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
