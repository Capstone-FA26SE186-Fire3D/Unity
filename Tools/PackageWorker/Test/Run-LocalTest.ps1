#Requires -Version 5.1
<#
.SYNOPSIS
  End-to-end test of the package worker loop on a developer machine: fake backend + real Unity build.

.DESCRIPTION
  1. Exports the graybox building as worker input (building.glb, building_metadata.json, scenario.json) unless -InputDir is given.
  2. Starts Tools/PackageWorker/Test/FakeBackend.py (python 3) with a throwaway worker key.
  3. Writes a temporary worker config + DPAPI key file and runs Fire3DPackageWorker.ps1 -Once.
  4. Asserts the fake backend saw renew, two outputs and complete, and that the uploaded files match manifest.json.

.EXAMPLE
  .\Run-LocalTest.ps1                      # StandaloneWindows64, no lightmap bake (fast)
  .\Run-LocalTest.ps1 -Bake -BuildTarget Android
#>
[CmdletBinding()]
param(
    [string]$UnityExe = 'D:\Unity\Hub\Editor\6000.6.6f1\Editor\Unity.exe',
    [string]$ProjectPath,
    [string]$InputDir,
    [string]$WorkRoot = (Join-Path $env:TEMP 'fet3d-worker-test'),
    [string]$BuildTarget = 'StandaloneWindows64',
    [switch]$Bake,
    [switch]$NoGraphics,
    [int]$Port = 8089,
    [string]$Python = 'python'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if (-not $ProjectPath) { $ProjectPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path }
function Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

if (-not (Test-Path -LiteralPath $UnityExe)) { throw "Unity not found: $UnityExe" }
if (Test-Path -LiteralPath $WorkRoot) { Remove-Item -LiteralPath $WorkRoot -Recurse -Force }
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
$stateDir = Join-Path $WorkRoot 'backend'
$secretDir = Join-Path $WorkRoot 'secrets'
New-Item -ItemType Directory -Path $stateDir, $secretDir -Force | Out-Null

if (-not $InputDir) {
    $InputDir = Join-Path $WorkRoot 'input'
    Step "Exporting graybox worker input to $InputDir"
    $log = Join-Path $WorkRoot 'export.log'
    $p = Start-Process -FilePath $UnityExe -ArgumentList @('-batchmode', '-projectPath', "`"$ProjectPath`"", '-logFile', "`"$log`"",
        '-executeMethod', 'Fire3D.Unity.Editor.Development.DevBuildingExporter.Export', '-fet3dOut', "`"$InputDir`"") -PassThru -Wait -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { Get-Content $log -Tail 40; throw "Export failed (exit $($p.ExitCode)); see $log" }
}
foreach ($f in 'building.glb', 'building_metadata.json', 'scenario.json') {
    if (-not (Test-Path -LiteralPath (Join-Path $InputDir $f))) { throw "Input is missing $f" }
}

# Throwaway key, stored the same way Install-PackageWorker.ps1 stores it (DPAPI, current user).
$key = -join ((1..48) | ForEach-Object { [char](Get-Random -InputObject ([int[]]([char[]]'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'))) })
$keyFile = Join-Path $secretDir 'worker-key.dpapi'
ConvertTo-SecureString $key -AsPlainText -Force | ConvertFrom-SecureString | Set-Content -LiteralPath $keyFile -Encoding ASCII

Step "Starting fake backend on port $Port"
$backend = Start-Process -FilePath $Python -ArgumentList @('-I', "`"$PSScriptRoot\FakeBackend.py`"", '--port', $Port, '--input', "`"$InputDir`"", '--state', "`"$stateDir`"",
    '--key', $key, '--target', $BuildTarget) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $WorkRoot 'backend.out.log') -RedirectStandardError (Join-Path $WorkRoot 'backend.err.log')
try {
    $ready = $false
    for ($i = 0; $i -lt 20 -and -not $ready; $i++) {
        Start-Sleep -Milliseconds 500
        if ($backend.HasExited) { Get-Content (Join-Path $WorkRoot 'backend.err.log'); throw 'Fake backend exited.' }
        try { $null = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$Port/storage/in/scenario.json" -TimeoutSec 2; $ready = $true } catch {}
    }
    if (-not $ready) { throw 'Fake backend did not come up.' }

    $config = [ordered]@{
        baseUrl = "http://127.0.0.1:$Port"; toolchainVersion = 'unity-6000.6.6f1-worker-1'; buildTarget = $BuildTarget
        unityExe = $UnityExe; projectPath = $ProjectPath; workRoot = (Join-Path $WorkRoot 'work'); keyFile = $keyFile
        pollSeconds = 5; renewSeconds = 20; buildTimeoutMinutes = 90; gpuBake = $false; noBake = (-not $Bake); noGraphics = [bool]$NoGraphics
        gitPull = $false; gitBranch = 'main'; keepJobs = 3; httpTimeoutSeconds = 300
    }
    $configPath = Join-Path $WorkRoot 'worker.config.json'
    $config | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding UTF8

    Step 'Running the worker once'
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot '..\Fire3DPackageWorker.ps1') -ConfigPath $configPath -Once
    if ($LASTEXITCODE -ne 0) { throw "Worker exited with $LASTEXITCODE" }
    Write-Host ("Worker finished in {0:n0} s" -f $sw.Elapsed.TotalSeconds)

    Step 'Checking the fake backend state'
    $state = Get-Content -LiteralPath (Join-Path $stateDir 'state.json') -Raw | ConvertFrom-Json
    if ($state.status -ne 'succeeded') { throw "Job status is '$($state.status)': $($state.failReason)" }
    if ($state.renews -lt 1) { throw 'The worker never renewed the lease.' }
    $types = @($state.outputs | ForEach-Object { $_.artifactType })
    if (-not ($types -contains 'manifest' -and $types -contains 'unity_package')) { throw "Outputs registered: $($types -join ',')" }
    $manifest = ($state.outputs | Where-Object { $_.artifactType -eq 'manifest' }).metadata
    foreach ($f in $manifest.files) {
        $stored = Join-Path (Join-Path $stateDir "out\$($state.attemptId)") ($f.path -replace '/', '\')
        if (-not (Test-Path -LiteralPath $stored)) { throw "Uploaded file missing: $($f.path)" }
        $sha = (Get-FileHash -LiteralPath $stored -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($sha -ne $f.sha256) { throw "Uploaded $($f.path) differs from the manifest." }
    }
    Write-Host ("PASS: {0} floors, {1} files, {2} renews, requiredCapabilities = {3}" -f $manifest.floors.Count, $manifest.files.Count, $state.renews, ($manifest.requiredCapabilities -join ', ')) -ForegroundColor Green
    Write-Host "Package: $stateDir\out\$($state.attemptId)"
} finally {
    if (-not $backend.HasExited) { $backend.Kill() }
}
