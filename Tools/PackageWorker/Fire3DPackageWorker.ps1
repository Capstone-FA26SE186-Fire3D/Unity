#Requires -Version 5.1
<#
.SYNOPSIS
  FET3D Unity package worker agent (BE#110, pull mode). Windows PowerShell 5.1 compatible.

.DESCRIPTION
  Loop: lease a package job from the backend -> download inputs (SHA-256 checked) -> write scenario.json ->
  run Unity Editor in batchmode (Fire3D.Unity.Editor.ContentPipeline.BuildWorker.Run) while renewing the lease ->
  upload every output file through signed PUT URLs (exact checksum headers) -> register the two outputs
  (manifest + unity_package) -> complete. Any build problem fails the job with the last log lines; a lost lease
  (409 ATTEMPT_STALE) stops the build and discards its output without failing the job.

  Contract: BE docs/unity-package-worker.md. Security: the X-Worker-Key is read from a DPAPI-protected file written by
  Install-PackageWorker.ps1 (same Windows user), never from arguments, config, logs or the repository. Signed URLs are
  logged without their query string.

.PARAMETER ConfigPath
  worker.config.json (see worker.config.example.json).
.PARAMETER Once
  Process at most one job (or return when none is waiting) instead of looping. Used for tests.
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [switch]$Once
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if (-not $ConfigPath) { $ConfigPath = Join-Path $PSScriptRoot 'worker.config.json' }
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
[Net.ServicePointManager]::Expect100Continue = $false
Add-Type -AssemblyName System.Net.Http

$script:AgentVersion = 'fet3d-unity-worker/1'
$script:ValidatorVersion = 'fet3d-unity-worker/1'

# ----------------------------------------------------------------------------------------------------------------------
# Configuration
# ----------------------------------------------------------------------------------------------------------------------
function Read-WorkerConfig([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Config not found: $path (copy worker.config.example.json)" }
    $raw = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    $defaults = @{
        baseUrl             = $null
        toolchainVersion    = 'unity-6000.6.6f1-worker-1'
        buildTarget         = 'Android'
        unityExe            = 'C:\Program Files\Unity\Hub\Editor\6000.6.6f1\Editor\Unity.exe'
        projectPath         = 'C:\fet3d\unity'
        workRoot            = 'C:\fet3d\work'
        keyFile             = 'C:\fet3d\secrets\worker-key.dpapi'
        pollSeconds         = 30
        renewSeconds        = 100
        buildTimeoutMinutes = 120
        gpuBake             = $false
        noBake              = $false
        noGraphics          = $false
        gitPull             = $true
        gitBranch           = 'main'
        keepJobs            = 5
        httpTimeoutSeconds  = 300
    }
    $cfg = @{}
    foreach ($k in $defaults.Keys) {
        $cfg[$k] = $defaults[$k]
        if ($raw.PSObject.Properties[$k] -and $null -ne $raw.$k) { $cfg[$k] = $raw.$k }
    }
    if (-not $cfg.baseUrl) { throw 'baseUrl is required in the config.' }
    $cfg.baseUrl = $cfg.baseUrl.TrimEnd('/')
    if ($raw.PSObject.Properties['workerKey'] -or $raw.PSObject.Properties['X-Worker-Key']) {
        throw 'Never put the worker key in the config. Run Install-PackageWorker.ps1 to store it DPAPI-protected.'
    }
    return $cfg
}

function Read-WorkerKey([string]$keyFile) {
    if (-not (Test-Path -LiteralPath $keyFile)) { throw "Worker key file not found: $keyFile (run Install-PackageWorker.ps1 as the worker user)" }
    $secure = Get-Content -LiteralPath $keyFile -Raw | ForEach-Object { $_.Trim() } | ConvertTo-SecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $key = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
    if ($key.Length -lt 32 -or $key.Length -gt 512) { throw 'The stored worker key must be 32-512 characters.' }
    return $key
}

# ----------------------------------------------------------------------------------------------------------------------
# Logging (masks signed URLs and the worker key)
# ----------------------------------------------------------------------------------------------------------------------
$script:LogDir = $null
$script:JobLog = $null
$script:Secrets = @()

function Protect-Text([string]$text) {
    if ([string]::IsNullOrEmpty($text)) { return $text }
    foreach ($s in $script:Secrets) { if ($s) { $text = $text.Replace($s, '***') } }
    # Drop query strings of URLs (presigned S3 URLs carry the signature there).
    return [regex]::Replace($text, '(https?://[^\s"''?]+)\?[^\s"'']*', '$1?[signed]')
}

function Write-Log([string]$message, [string]$level = 'INFO') {
    $line = '{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}' -f (Get-Date), $level, (Protect-Text $message)
    if ($level -eq 'ERROR') { Write-Host $line -ForegroundColor Red }
    elseif ($level -eq 'WARN') { Write-Host $line -ForegroundColor Yellow }
    else { Write-Host $line }
    if ($script:LogDir) {
        $daily = Join-Path $script:LogDir ('worker-{0:yyyyMMdd}.log' -f (Get-Date))
        Add-Content -LiteralPath $daily -Value $line -Encoding UTF8
    }
    if ($script:JobLog) { Add-Content -LiteralPath $script:JobLog -Value $line -Encoding UTF8 }
}

# ----------------------------------------------------------------------------------------------------------------------
# HTTP (HttpClient: streams large files, exposes status + body on errors)
# ----------------------------------------------------------------------------------------------------------------------
$script:Http = $null

function New-Http([int]$timeoutSeconds) {
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds([Math]::Max(60, $timeoutSeconds))
    $client.DefaultRequestHeaders.UserAgent.ParseAdd("$($script:AgentVersion.Replace('/', '_'))/1.0")
    return $client
}

class ApiResult {
    [int]$Status
    [string]$Body
    [object]$Json
    [string]$Code
    [string]$Title
    [bool]$Ok
}

function Invoke-Api {
    param([string]$Method, [string]$Path, [object]$Body, [string]$WorkerKey)
    $url = "$($script:Config.baseUrl)$Path"
    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($Method), $url)
    $req.Headers.TryAddWithoutValidation('X-Worker-Key', $WorkerKey) | Out-Null
    $req.Headers.TryAddWithoutValidation('Accept', 'application/json') | Out-Null
    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Depth 100 -Compress
        $req.Content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json')
    }
    $result = New-Object ApiResult
    try {
        $resp = $script:Http.SendAsync($req).GetAwaiter().GetResult()
        try {
            $result.Status = [int]$resp.StatusCode
            $result.Body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        } finally { $resp.Dispose() }
    } finally { $req.Dispose() }
    $result.Ok = $result.Status -ge 200 -and $result.Status -lt 300
    if ($result.Body -and $result.Body.TrimStart().StartsWith('{')) {
        try { $result.Json = $result.Body | ConvertFrom-Json } catch { $result.Json = $null }
        if ($result.Json) {
            if ($result.Json.PSObject.Properties['code']) { $result.Code = [string]$result.Json.code }
            if ($result.Json.PSObject.Properties['title']) { $result.Title = [string]$result.Json.title }
        }
    }
    if (-not $result.Ok) {
        $detail = ''
        if ($result.Json -and $result.Json.PSObject.Properties['errors'] -and $result.Json.errors) {
            $detail = ' ' + (($result.Json.errors | ConvertTo-Json -Depth 10 -Compress))
        }
        Write-Log "$Method $Path -> $($result.Status) $($result.Code) $($result.Title)$detail" 'WARN'
    }
    return $result
}

function Get-FileFromUrl([string]$url, [string]$destination) {
    $dir = Split-Path -Parent $destination
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $resp = $script:Http.GetAsync($url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    try {
        if (-not $resp.IsSuccessStatusCode) { throw "Download failed with HTTP $([int]$resp.StatusCode)" }
        $stream = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $file = [IO.File]::Create($destination)
        try { $stream.CopyTo($file) } finally { $file.Dispose(); $stream.Dispose() }
    } finally { $resp.Dispose() }
}

function Send-FileToUrl([string]$url, [string]$path, [hashtable]$headers) {
    $file = [IO.File]::OpenRead($path)
    try {
        $content = New-Object System.Net.Http.StreamContent($file)
        $content.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue('application/octet-stream')
        $content.Headers.ContentLength = $file.Length
        $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Put, $url)
        $req.Content = $content
        foreach ($k in $headers.Keys) { $req.Headers.TryAddWithoutValidation($k, [string]$headers[$k]) | Out-Null }
        $resp = $script:Http.SendAsync($req).GetAwaiter().GetResult()
        try {
            if (-not $resp.IsSuccessStatusCode) {
                $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                throw "Upload of $(Split-Path -Leaf $path) failed with HTTP $([int]$resp.StatusCode): $($body.Substring(0, [Math]::Min(300, $body.Length)))"
            }
        } finally { $resp.Dispose(); $req.Dispose() }
    } finally { $file.Dispose() }
}

# ----------------------------------------------------------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------------------------------------------------------
function Get-Sha256Hex([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Prop($obj, [string]$name, $default = $null) {
    if ($null -eq $obj) { return $default }
    $p = $obj.PSObject.Properties[$name]
    if ($p -and $null -ne $p.Value) { return $p.Value }
    return $default
}

function Get-TailLines([string]$path, [int]$count) {
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    return @(Get-Content -LiteralPath $path -Tail $count -ErrorAction SilentlyContinue)
}

function Limit-Text([string]$text, [int]$max) {
    if ($null -eq $text) { return '' }
    $text = [regex]::Replace($text, '[\x00-\x08\x0B\x0C\x0E-\x1F]', '')
    if ($text.Length -le $max) { return $text }
    return $text.Substring(0, $max - 1) + '…'
}

# Input artifact -> file name BuildWorker expects in its input directory.
function Get-InputFileName([string]$artifactType, [string]$artifactId) {
    switch ($artifactType) {
        'preview_glb' { return 'building.glb' }
        'building_metadata' { return 'building_metadata.json' }
        default {
            $safe = [regex]::Replace("$artifactType", '[^A-Za-z0-9._-]', '_')
            $id = [regex]::Replace("$artifactId", '[^A-Za-z0-9._-]', '_')
            return "extra/$safe-$id.bin"
        }
    }
}

function Sync-ProjectSources([string]$projectPath, [string]$branch) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectPath '.git'))) {
        Write-Log "Project $projectPath is not a git checkout; skipping pull." 'WARN'
        return
    }
    try {
        $out = & git -C $projectPath fetch --quiet origin 2>&1
        $out = & git -C $projectPath switch --quiet $branch 2>&1
        $out = & git -C $projectPath pull --quiet --ff-only origin $branch 2>&1
        if ($LASTEXITCODE -ne 0) { throw "git pull: $out" }
        $out = & git -C $projectPath lfs pull 2>&1
        if ($LASTEXITCODE -ne 0) { throw "git lfs pull: $out" }
        $head = (& git -C $projectPath rev-parse --short HEAD 2>&1)
        Write-Log "Project sources at $branch@$head"
    } catch {
        Write-Log "Source sync failed, building with the current checkout: $($_.Exception.Message)" 'WARN'
    }
}

function Remove-StaleUnityLock([string]$projectPath) {
    $lock = Join-Path $projectPath 'Temp\UnityLockfile'
    if (-not (Test-Path -LiteralPath $lock)) { return }
    $running = Get-Process -Name Unity -ErrorAction SilentlyContinue
    if ($running) { throw "Unity is already running (PID $($running.Id -join ',')); only one build per project is allowed." }
    Remove-Item -LiteralPath $lock -Force
    Write-Log 'Removed stale Temp\UnityLockfile left by a crashed Editor.' 'WARN'
}

function Remove-OldJobs([string]$workRoot, [int]$keep) {
    $jobs = Get-ChildItem -LiteralPath (Join-Path $workRoot 'jobs') -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
    if (-not $jobs) { return }
    $jobs | Select-Object -Skip ([Math]::Max(0, $keep)) | ForEach-Object {
        try { Remove-Item -LiteralPath $_.FullName -Recurse -Force } catch { Write-Log "Cannot remove $($_.FullName): $($_.Exception.Message)" 'WARN' }
    }
}

# ----------------------------------------------------------------------------------------------------------------------
# Job processing
# ----------------------------------------------------------------------------------------------------------------------
class LeaseLost : System.Exception { LeaseLost([string]$m) : base($m) {} }
class JobFailed : System.Exception { JobFailed([string]$m) : base($m) {} }

function Invoke-JobCall([string]$Action, [string]$JobId, [hashtable]$Body, [string]$WorkerKey) {
    $r = Invoke-Api -Method 'POST' -Path "/internal/processing/jobs/$JobId/$Action" -Body $Body -WorkerKey $WorkerKey
    if ($r.Status -eq 409 -and $r.Code -eq 'ATTEMPT_STALE') { throw [LeaseLost]::new("Lease lost during $Action") }
    return $r
}

function Invoke-Renew([object]$claim, [string]$WorkerKey) {
    $r = Invoke-JobCall -Action 'renew' -JobId $claim.jobId -Body @{ attemptId = $claim.attemptId; leaseToken = $claim.leaseToken } -WorkerKey $WorkerKey
    if (-not $r.Ok) { Write-Log "Renew answered $($r.Status); keeping the build running." 'WARN' }
    else { Write-Log "Lease renewed until $(Get-Prop $r.Json 'leaseUntil' '?')" }
}

function Save-Inputs([object]$claim, [string]$inputDir, [string]$WorkerKey) {
    $inputs = @(Get-Prop $claim 'inputs' @())
    if ($inputs.Count -eq 0) { throw [JobFailed]::new('The revision has no accepted Geometry attempt (inputs is empty).') }
    $pending = @($inputs)
    for ($round = 0; $round -lt 2 -and $pending.Count -gt 0; $round++) {
        if ($round -gt 0) {
            Write-Log 'Re-signing expired input URLs.'
            $r = Invoke-JobCall -Action 'inputs' -JobId $claim.jobId -Body @{ attemptId = $claim.attemptId; leaseToken = $claim.leaseToken } -WorkerKey $WorkerKey
            if (-not $r.Ok) { throw [JobFailed]::new("Cannot re-sign inputs (HTTP $($r.Status) $($r.Code)).") }
            $fresh = @(Get-Prop $r.Json 'inputs' @())
            $pending = @($fresh | Where-Object { $t = $_.artifactType; ($pending | Where-Object { $_.artifactType -eq $t }) })
        }
        $retry = @()
        foreach ($input in $pending) {
            $type = [string](Get-Prop $input 'artifactType' 'unknown')
            $url = Get-Prop $input 'url'
            if (-not $url) { throw [JobFailed]::new("Input $type is missing in storage or changed size (url is null).") }
            $name = Get-InputFileName $type ([string](Get-Prop $input 'artifactId' ''))
            $dest = Join-Path $inputDir $name
            $expires = Get-Prop $input 'expiresAt'
            if ($expires -and ([DateTimeOffset]::Parse($expires) -lt [DateTimeOffset]::UtcNow.AddSeconds(30))) { $retry += $input; continue }
            Write-Log "Downloading $type -> $name ($(Get-Prop $input 'sizeBytes' '?') bytes)"
            try { Get-FileFromUrl -url $url -destination $dest }
            catch { if ($round -eq 0) { Write-Log "Download of $type failed: $($_.Exception.Message)" 'WARN'; $retry += $input; continue } else { throw [JobFailed]::new("Download of $type failed: $($_.Exception.Message)") } }
            $size = (Get-Item -LiteralPath $dest).Length
            $expectedSize = [int64](Get-Prop $input 'sizeBytes' 0)
            $expectedSha = [string](Get-Prop $input 'sha256' '')
            if ($expectedSize -gt 0 -and $size -ne $expectedSize) { throw [JobFailed]::new("Input $type has $size bytes, expected $expectedSize.") }
            if ($expectedSha -and (Get-Sha256Hex $dest) -ne $expectedSha.ToLowerInvariant()) { throw [JobFailed]::new("Input $type SHA-256 mismatch.") }
        }
        $pending = $retry
    }
    if ($pending.Count -gt 0) { throw [JobFailed]::new('Inputs could not be downloaded before their URLs expired.') }
    if (-not (Test-Path -LiteralPath (Join-Path $inputDir 'building_metadata.json'))) { throw [JobFailed]::new('Input building_metadata is missing.') }
    if (-not (Test-Path -LiteralPath (Join-Path $inputDir 'building.glb'))) { throw [JobFailed]::new('Input preview_glb is missing.') }
}

function Invoke-UnityBuild([object]$claim, [string]$inputDir, [string]$outputDir, [string]$logPath, [string]$resultPath, [string]$WorkerKey) {
    $cfg = $script:Config
    $args = @(
        '-batchmode',
        '-projectPath', ('"{0}"' -f $cfg.projectPath),
        '-logFile', ('"{0}"' -f $logPath),
        '-executeMethod', 'Fire3D.Unity.Editor.ContentPipeline.BuildWorker.Run',
        '-fet3dInput', ('"{0}"' -f $inputDir),
        '-fet3dOutput', ('"{0}"' -f $outputDir),
        '-fet3dJob', [string]$claim.jobId,
        '-fet3dTarget', [string]$cfg.buildTarget,
        '-fet3dResult', ('"{0}"' -f $resultPath)
    )
    if ($cfg.noGraphics) { $args += '-nographics' }
    if ($cfg.gpuBake) { $args += '-fet3dGpu' }
    if ($cfg.noBake) { $args += '-fet3dNoBake' }

    Remove-StaleUnityLock $cfg.projectPath
    Write-Log "Starting Unity: $($cfg.unityExe) $($args -join ' ')"
    $proc = Start-Process -FilePath $cfg.unityExe -ArgumentList $args -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddMinutes([double]$cfg.buildTimeoutMinutes)
    $nextRenew = (Get-Date).AddSeconds([double]$cfg.renewSeconds)
    try {
        while (-not $proc.HasExited) {
            Start-Sleep -Seconds 5
            if ((Get-Date) -ge $nextRenew) {
                try { Invoke-Renew $claim $WorkerKey }
                catch [LeaseLost] { throw }
                catch { Write-Log "Renew error: $($_.Exception.Message)" 'WARN' }
                $nextRenew = (Get-Date).AddSeconds([double]$cfg.renewSeconds)
            }
            if ((Get-Date) -gt $deadline) { throw [JobFailed]::new("Unity build exceeded $($cfg.buildTimeoutMinutes) minutes.") }
        }
    } catch {
        if (-not $proc.HasExited) { try { $proc.Kill() } catch {} }
        throw
    }
    $proc.WaitForExit()
    Write-Log "Unity exited with code $($proc.ExitCode)"
    if (-not (Test-Path -LiteralPath $resultPath)) {
        $tail = (Get-TailLines $logPath 30) -join "`n"
        throw [JobFailed]::new("Unity exited with code $($proc.ExitCode) without a result file.`n$tail")
    }
    $summary = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not (Get-Prop $summary 'ok' $false)) {
        $issues = @(Get-Prop $summary 'issues' @())
        $msg = "Unity build failed: $(Get-Prop $summary 'error' 'unknown error')"
        if ($issues.Count -gt 0) { $msg += "`nIssues: " + ($issues -join '; ') }
        $msg += "`n" + ((Get-TailLines $logPath 30) -join "`n")
        throw [JobFailed]::new($msg)
    }
    foreach ($w in @(Get-Prop $summary 'warnings' @())) { Write-Log "Unity warning: $w" 'WARN' }
    return $summary
}

function Publish-Package([object]$claim, [string]$outputDir, [string]$WorkerKey) {
    $cfg = $script:Config
    $manifestPath = Join-Path $outputDir 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw [JobFailed]::new('manifest.json was not produced.') }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $files = @(Get-Prop $manifest 'files' @())
    if ($files.Count -eq 0) { throw [JobFailed]::new('manifest.json lists no files.') }
    if ([string](Get-Prop $manifest 'buildTarget' '') -ne [string]$cfg.buildTarget) { throw [JobFailed]::new("manifest buildTarget differs from $($cfg.buildTarget).") }

    # Check the package on disk against its manifest before spending bandwidth.
    $uploads = @()
    $total = [int64]0
    foreach ($f in $files) {
        $local = Join-Path $outputDir ($f.path -replace '/', '\')
        if (-not (Test-Path -LiteralPath $local)) { throw [JobFailed]::new("Listed file missing: $($f.path)") }
        $size = (Get-Item -LiteralPath $local).Length
        if ($size -ne [int64]$f.size) { throw [JobFailed]::new("Size of $($f.path) changed after the build.") }
        if ((Get-Sha256Hex $local) -ne $f.sha256) { throw [JobFailed]::new("SHA-256 of $($f.path) changed after the build.") }
        $total += $size
        $uploads += @{ path = $f.path; sizeBytes = $size; sha256 = $f.sha256; local = $local }
    }
    $manifestSha = Get-Sha256Hex $manifestPath
    $manifestSize = (Get-Item -LiteralPath $manifestPath).Length
    $uploads += @{ path = 'manifest.json'; sizeBytes = $manifestSize; sha256 = $manifestSha; local = $manifestPath }

    $attempt = @{ attemptId = $claim.attemptId; leaseToken = $claim.leaseToken }

    $uploadAll = {
        $body = @{ attemptId = $claim.attemptId; leaseToken = $claim.leaseToken; files = @($uploads | ForEach-Object { @{ path = $_.path; sizeBytes = $_.sizeBytes; sha256 = $_.sha256 } }) }
        $r = Invoke-JobCall -Action 'upload-urls' -JobId $claim.jobId -Body $body -WorkerKey $WorkerKey
        if (-not $r.Ok) { throw [JobFailed]::new("upload-urls refused (HTTP $($r.Status) $($r.Code)).") }
        $signed = @(Get-Prop $r.Json 'files' @())
        foreach ($u in $uploads) {
            $entry = $signed | Where-Object { $_.path -eq $u.path } | Select-Object -First 1
            if (-not $entry) { throw [JobFailed]::new("No upload URL for $($u.path).") }
            $headers = @{}
            foreach ($p in (Get-Prop $entry 'headers' @{}).PSObject.Properties) { $headers[$p.Name] = [string]$p.Value }
            Write-Log "Uploading $($u.path) ($($u.sizeBytes) bytes)"
            $ok = $false
            for ($try = 1; $try -le 3 -and -not $ok; $try++) {
                try { Send-FileToUrl -url $entry.url -path $u.local -headers $headers; $ok = $true }
                catch { if ($try -eq 3) { throw [JobFailed]::new($_.Exception.Message) } ; Write-Log "Retry $try for $($u.path): $($_.Exception.Message)" 'WARN'; Start-Sleep -Seconds (5 * $try) }
            }
        }
        return [string](Get-Prop $r.Json 'outputPrefix' $claim.outputPrefix)
    }
    $outputPrefix = & $uploadAll
    $objectKey = $outputPrefix + 'manifest.json'

    $common = @{
        objectKey = $objectKey; sha256Hash = $manifestSha; sizeBytes = $manifestSize
        validatorVersion = $script:ValidatorVersion; outcome = 'Passed'; issues = @()
    }
    $manifestOutput = $common.Clone()
    $manifestOutput.artifactType = 'manifest'
    $manifestOutput.schemaVersion = [string]$manifest.schemaVersion
    $manifestOutput.metadata = $manifest
    $r = Invoke-JobCall -Action 'outputs' -JobId $claim.jobId -Body ($attempt + @{ output = $manifestOutput }) -WorkerKey $WorkerKey
    if (-not $r.Ok) {
        $issues = ''
        if ($r.Json -and $r.Json.PSObject.Properties['issues']) { $issues = ' ' + ($r.Json.issues | ConvertTo-Json -Depth 10 -Compress) }
        throw [JobFailed]::new("Registering the manifest output failed (HTTP $($r.Status) $($r.Code)).$issues")
    }

    $packageOutput = $common.Clone()
    $packageOutput.artifactType = 'unity_package'
    $packageOutput.schemaVersion = [string](Get-Prop $manifest 'manifestSchemaVersion' '1')
    # Same document as the manifest output: the release and publish gates require the two artifacts' metadata to be identical
    # (BE ReleaseAccess.sql / ReleasePublish.sql), so any extra provenance here makes every release fail PACKAGE_METADATA_MISMATCH.
    $packageOutput.metadata = $manifest
    $r = Invoke-JobCall -Action 'outputs' -JobId $claim.jobId -Body ($attempt + @{ output = $packageOutput }) -WorkerKey $WorkerKey
    if (-not $r.Ok) { throw [JobFailed]::new("Registering the unity_package output failed (HTTP $($r.Status) $($r.Code)).") }
    $outputHash = [string](Get-Prop $r.Json 'outputHash' '')
    if (-not $outputHash) { throw [JobFailed]::new('The outputs endpoint returned no outputHash.') }

    $reuploaded = $false
    for ($try = 1; $try -le 6; $try++) {
        $r = Invoke-JobCall -Action 'complete' -JobId $claim.jobId -Body ($attempt + @{ outputHash = $outputHash }) -WorkerKey $WorkerKey
        if ($r.Ok) { Write-Log "Job $($claim.jobId) completed: $objectKey ($($files.Count) files, $total bytes)"; return }
        if ($r.Status -eq 409 -and $r.Code -eq 'PACKAGE_STORAGE_MISMATCH' -and -not $reuploaded) {
            Write-Log 'Storage mismatch: uploading the package again.' 'WARN'
            $null = & $uploadAll
            $reuploaded = $true
            continue
        }
        if ($r.Status -eq 503 -or $r.Status -ge 500) { Start-Sleep -Seconds (10 * $try); continue }
        throw [JobFailed]::new("Complete refused (HTTP $($r.Status) $($r.Code) $($r.Title)).")
    }
    throw [JobFailed]::new('Complete kept failing; the lease will expire and the job can be retried.')
}

function Invoke-Job([object]$claim, [string]$WorkerKey) {
    $cfg = $script:Config
    $jobId = [string]$claim.jobId
    $attemptId = [string]$claim.attemptId
    $jobDir = Join-Path $cfg.workRoot ("jobs\$jobId-" + $attemptId.Substring(0, 8))
    $inputDir = Join-Path $jobDir 'input'
    $outputDir = Join-Path $jobDir 'output'
    foreach ($d in @($inputDir, $outputDir)) {
        if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }
        New-Item -ItemType Directory -Path $d -Force | Out-Null
    }
    $script:JobLog = Join-Path $jobDir 'worker.log'
    $unityLog = Join-Path $jobDir 'unity.log'
    $resultPath = Join-Path $jobDir 'result.json'
    Write-Log "Job $jobId ($(Get-Prop $claim 'kind' '?')) attempt $attemptId, lease until $(Get-Prop $claim 'leaseUntil' '?'), target $($cfg.buildTarget)"
    try {
        Save-Inputs -claim $claim -inputDir $inputDir -WorkerKey $WorkerKey
        $scenario = Get-Prop $claim 'scenarioSnapshot'
        if (-not $scenario) { throw [JobFailed]::new('The claim carries no scenarioSnapshot.') }
        [IO.File]::WriteAllText((Join-Path $inputDir 'scenario.json'), ($scenario | ConvertTo-Json -Depth 100), (New-Object Text.UTF8Encoding($false)))
        Invoke-Renew $claim $WorkerKey
        $null = Invoke-UnityBuild -claim $claim -inputDir $inputDir -outputDir $outputDir -logPath $unityLog -resultPath $resultPath -WorkerKey $WorkerKey
        Invoke-Renew $claim $WorkerKey
        Publish-Package -claim $claim -outputDir $outputDir -WorkerKey $WorkerKey
    } catch [LeaseLost] {
        Write-Log "Lease of job $jobId was lost; output discarded, nothing reported." 'WARN'
        Remove-Item -LiteralPath $outputDir -Recurse -Force -ErrorAction SilentlyContinue
    } catch {
        $reason = Limit-Text $_.Exception.Message 1000
        Write-Log "Job $jobId failed: $reason" 'ERROR'
        try {
            $r = Invoke-JobCall -Action 'fail' -JobId $jobId -Body @{ attemptId = $attemptId; leaseToken = $claim.leaseToken; reason = (Protect-Text $reason) } -WorkerKey $WorkerKey
            if (-not $r.Ok) { Write-Log "Fail call answered HTTP $($r.Status) $($r.Code)" 'WARN' }
        } catch [LeaseLost] { Write-Log 'Lease already lost; fail not reported.' 'WARN' }
        catch { Write-Log "Fail call error: $($_.Exception.Message)" 'WARN' }
    } finally {
        $script:JobLog = $null
        Remove-Item -LiteralPath $inputDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ----------------------------------------------------------------------------------------------------------------------
# Main loop
# ----------------------------------------------------------------------------------------------------------------------
$script:Config = Read-WorkerConfig $ConfigPath
$script:LogDir = Join-Path $script:Config.workRoot 'logs'
New-Item -ItemType Directory -Path $script:LogDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $script:Config.workRoot 'jobs') -Force | Out-Null
$workerKey = Read-WorkerKey $script:Config.keyFile
$script:Secrets = @($workerKey)
$script:Http = New-Http ([int]$script:Config.httpTimeoutSeconds)
if (-not (Test-Path -LiteralPath $script:Config.unityExe)) { throw "Unity not found at $($script:Config.unityExe)" }
if (-not (Test-Path -LiteralPath (Join-Path $script:Config.projectPath 'ProjectSettings\ProjectVersion.txt'))) { throw "Unity project not found at $($script:Config.projectPath)" }

Write-Log "$script:AgentVersion started: $($script:Config.baseUrl), toolchain $($script:Config.toolchainVersion), target $($script:Config.buildTarget), project $($script:Config.projectPath)"
$idleLogged = $false
while ($true) {
    $claim = $null
    try {
        $r = Invoke-Api -Method 'POST' -Path '/internal/processing/package-jobs/lease' -Body @{ toolchainVersion = $script:Config.toolchainVersion; buildTarget = $script:Config.buildTarget } -WorkerKey $workerKey
        if ($r.Status -eq 200 -and $r.Json) { $claim = $r.Json }
        elseif ($r.Status -eq 204) { if (-not $idleLogged) { Write-Log 'No job waiting; polling.'; $idleLogged = $true } }
        elseif ($r.Status -eq 401) { Write-Log 'Worker key rejected (401). Check the key stored by Install-PackageWorker.ps1.' 'ERROR' }
        elseif ($r.Status -eq 409 -and $r.Code -eq 'PACKAGE_PULL_DISABLED') { Write-Log 'Package pull is disabled on the backend; waiting.' 'WARN' }
        else { Write-Log "Lease answered HTTP $($r.Status) $($r.Code)" 'WARN' }
    } catch {
        Write-Log "Lease request error: $($_.Exception.Message)" 'WARN'
    }

    if ($claim) {
        $idleLogged = $false
        if ($script:Config.gitPull) { Sync-ProjectSources $script:Config.projectPath $script:Config.gitBranch }
        Invoke-Job -claim $claim -WorkerKey $workerKey
        Remove-OldJobs $script:Config.workRoot ([int]$script:Config.keepJobs)
    }
    if ($Once) { break }
    if (-not $claim) { Start-Sleep -Seconds ([int]$script:Config.pollSeconds) }
}
