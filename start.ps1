# Starts Agentic SDR in the background (if needed) and opens the local page.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
$url = "http://127.0.0.1:5088/"

function Test-HostUp {
    try {
        $response = Invoke-WebRequest -Uri ($url.TrimEnd("/") + "/api/status") -UseBasicParsing -TimeoutSec 2
        return $response.StatusCode -eq 200
    }
    catch {
        return $false
    }
}

function Show-Error([string]$message) {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show($message, "Agentic SDR", "OK", "Error") | Out-Null
}

if (-not (Test-HostUp)) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Show-Error("dotnet was not found on PATH. Install the .NET SDK, then try again.")
        exit 1
    }

    Start-Process `
        -FilePath $dotnet.Source `
        -ArgumentList @("run", "--project", "Host") `
        -WorkingDirectory $root `
        -WindowStyle Minimized

    $ready = $false
    for ($i = 0; $i -lt 90; $i++) {
        Start-Sleep -Milliseconds 500
        if (Test-HostUp) {
            $ready = $true
            break
        }
    }

    if (-not $ready) {
        Show-Error("Agentic SDR did not become ready at $url")
        exit 1
    }
}

Start-Process $url
