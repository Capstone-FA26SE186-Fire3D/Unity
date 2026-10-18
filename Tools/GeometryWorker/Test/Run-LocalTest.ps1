#Requires -Version 5.1
<#
.SYNOPSIS
  End-to-end test of Fire3DGeometryWorker.ps1 against FakeGeometryBackend.py: lease -> download IFC -> convert -> upload ->
  outputs -> complete. -ExpireFirstUrl also exercises re-signing the input URL; -Broken checks the fail path.

.EXAMPLE
  .\Run-LocalTest.ps1 -Ifc D:\FET3D_Demo\ifc\KIT\KIT_Institute_Arch.ifc -Python ..\.venv\Scripts\python.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Ifc,
    [string]$Python,
    [string]$WorkRoot = (Join-Path $env:TEMP 'fet3d-geometry-test'),
    [int]$Port = 8090,
    [switch]$ExpireFirstUrl,
    [switch]$Broken
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
function Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }
if (-not $Python) { $Python = Join-Path $PSScriptRoot '..\.venv\Scripts\python.exe' }

if (Test-Path -LiteralPath $WorkRoot) { Remove-Item -LiteralPath $WorkRoot -Recurse -Force }
$stateDir = Join-Path $WorkRoot 'backend'
$secretDir = Join-Path $WorkRoot 'secrets'
New-Item -ItemType Directory -Path $stateDir, $secretDir -Force | Out-Null
$source = if ($Broken) { $null } else { (Resolve-Path -LiteralPath $Ifc).Path }
if ($Broken) {
    $source = Join-Path $WorkRoot 'broken.ifc'
    Set-Content -LiteralPath $source -Value "ISO-10303-21;`nHEADER;`nENDSEC;`nDATA;`nENDSEC;`nEND-ISO-10303-21;" -Encoding ASCII
}

$key = -join ((1..48) | ForEach-Object { [char](Get-Random -InputObject ([int[]]([char[]]'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'))) })
$keyFile = Join-Path $secretDir 'worker-key.dpapi'
ConvertTo-SecureString $key -AsPlainText -Force | ConvertFrom-SecureString | Set-Content -LiteralPath $keyFile -Encoding ASCII

Step "Starting fake backend on port $Port"
$backendArgs = @('-I', "`"$PSScriptRoot\FakeGeometryBackend.py`"", '--port', $Port, '--ifc', "`"$source`"", '--state', "`"$stateDir`"", '--key', $key)
if ($ExpireFirstUrl) { $backendArgs += '--expire-first-url' }
$backend = Start-Process -FilePath $Python -ArgumentList $backendArgs -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $WorkRoot 'backend.out.log') -RedirectStandardError (Join-Path $WorkRoot 'backend.err.log')
try {
    $ready = $false
    for ($i = 0; $i -lt 30 -and -not $ready; $i++) {
        Start-Sleep -Milliseconds 300
        if ($backend.HasExited) { Get-Content (Join-Path $WorkRoot 'backend.err.log'); throw 'Fake backend exited.' }
        try { $null = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$Port/storage/in/source.ifc" -Method Head -TimeoutSec 2; $ready = $true } catch { if ($_.Exception.Response) { $ready = $true } }
    }
    if (-not $ready) { throw 'Fake backend did not come up.' }

    $config = [ordered]@{
        baseUrl = "http://127.0.0.1:$Port"; toolchainVersion = 'fet3d-geometry-worker-1'; pythonExe = (Resolve-Path -LiteralPath $Python).Path
        workRoot = (Join-Path $WorkRoot 'work'); keyFile = $keyFile; pollSeconds = 5; renewSeconds = 2; timeoutMinutes = 20; keepJobs = 3; httpTimeoutSeconds = 120
    }
    $configPath = Join-Path $WorkRoot 'geometry-worker.config.json'
    $config | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding UTF8

    Step 'Running the worker once'
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot '..\Fire3DGeometryWorker.ps1') -ConfigPath $configPath -Once
    if ($LASTEXITCODE -ne 0) { throw "Worker exited with $LASTEXITCODE" }
    Write-Host ("Worker finished in {0:n1} s" -f $sw.Elapsed.TotalSeconds)

    $state = Get-Content -LiteralPath (Join-Path $stateDir 'state.json') -Raw | ConvertFrom-Json
    if ($Broken) {
        if ($state.status -ne 'failed' -or -not $state.failReason) { throw "Expected a failed job, got '$($state.status)'." }
        Write-Host "PASS (fail path): $($state.failReason)" -ForegroundColor Green
        return
    }
    if ($state.status -ne 'succeeded') { throw "Job status is '$($state.status)': $($state.failReason)" }
    foreach ($t in 'preview_glb', 'building_metadata') { if (-not $state.outputs.PSObject.Properties[$t]) { throw "Output $t was not registered." } }
    foreach ($name in 'building.glb', 'building_metadata.json', 'preview_metadata.json') { if (-not $state.uploads.PSObject.Properties[$name]) { throw "$name was not uploaded." } }
    if ($ExpireFirstUrl -and $state.resigns -lt 1) { throw 'The expired input URL was not re-signed.' }
    $preview = $state.outputs.preview_glb
    Write-Host ("PASS: outcome {0}, {1} issues, glb {2} bytes, renews {3}, resigns {4}" -f $preview.outcome, @($preview.issues).Count, $preview.sizeBytes, $state.renews, $state.resigns) -ForegroundColor Green
} finally {
    if (-not $backend.HasExited) { Stop-Process -Id $backend.Id -Force }
}
