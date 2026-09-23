# atg.ps1 - Run the vendored ATG CLI from any repository (Windows).
#
# Kept ASCII-only so Windows PowerShell 5.1 (which reads .ps1 as ANSI) can parse it.
#
# Resolves the interpreter, puts vendor/ on the import path, and forwards every
# argument to `python -m atg`. Does not change directory: ATG resolves its
# state directory relative to the git repository root, not the launcher's
# own location, so the caller's working directory must be preserved.
#
# Usage:
#   .\atg.ps1 --help
#   .\atg.ps1 init
#   .\atg.ps1 contract check atg-contract.json
#
# Environment:
#   ATG_PYTHON  Interpreter to use (default: .venv-atg if present, else python).

[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$VendorDir = Join-Path $ScriptDir "vendor"
$VenvPy    = Join-Path $ScriptDir ".venv-atg\Scripts\python.exe"

if (-not (Test-Path (Join-Path $VendorDir "atg\cli.py"))) {
    Write-Error "vendor/atg is missing. Re-copy the kit with copy_to_repo.py."
    exit 2
}

$Python = if ($env:ATG_PYTHON) { $env:ATG_PYTHON }
          elseif (Test-Path $VenvPy) { $VenvPy }
          else { "python" }

$Arguments = @()
if ($Rest) { $Arguments = @($Rest) }

$env:PYTHONPATH = if ($env:PYTHONPATH) { "$VendorDir;$env:PYTHONPATH" } else { $VendorDir }
& $Python -m atg @Arguments
exit $LASTEXITCODE
