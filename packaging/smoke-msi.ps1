# Quiet-install the built MSI, verify the Windows service, then uninstall.
# Run from CI on windows-latest after packaging/build-msi.sh.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$msi = Get-ChildItem -Path (Join-Path $root "packaging\out") -Filter "AgenticSdr-*.msi" |
    Where-Object { $_.Name -ne "AgenticSdr.msi" } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $msi) {
    throw "No AgenticSdr-*.msi under packaging/out"
}

$log = Join-Path $env:TEMP "agenticsdr-msi-smoke.log"
Write-Host "Smoke-installing $($msi.FullName)"
$install = Start-Process -FilePath "msiexec.exe" -ArgumentList @(
    "/i", "`"$($msi.FullName)`"",
    "/qn",
    "/l*v", "`"$log`"",
    "INSTALLDESKTOPSHORTCUT=",
    "LLMKEY="
) -Wait -PassThru
if ($install.ExitCode -ne 0) {
    Write-Host "--- msiexec log (tail) ---"
    Get-Content $log -Tail 80 -ErrorAction SilentlyContinue
    $setup = "C:\ProgramData\AgenticSDR\setup.log"
    if (Test-Path $setup) {
        Write-Host "--- setup.log ---"
        Get-Content $setup -ErrorAction SilentlyContinue
    }
    throw "msiexec install failed with exit $($install.ExitCode); log $log"
}

try {
    $svc = Get-Service -Name "AgenticSdr" -ErrorAction Stop
    if ($svc.Status -ne "Running") {
        throw "AgenticSdr service status is $($svc.Status), expected Running"
    }
    $exe = "C:\Program Files\AgenticSDR\AgenticSdr.Host.exe"
    if (-not (Test-Path $exe)) {
        throw "Missing $exe"
    }
    $rtl = "C:\Program Files\AgenticSDR\rtlsdr.dll"
    if (-not (Test-Path $rtl)) {
        throw "Missing $rtl"
    }
    Write-Host "Smoke install OK (service Running)."
}
finally {
    Write-Host "Uninstalling..."
    $uninstall = Start-Process -FilePath "msiexec.exe" -ArgumentList @(
        "/x", "`"$($msi.FullName)`"",
        "/qn",
        "/l*v", "`"$($env:TEMP)\agenticsdr-msi-uninstall.log`""
    ) -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) {
        Write-Host "Warning: uninstall exit $($uninstall.ExitCode)"
    }
}
