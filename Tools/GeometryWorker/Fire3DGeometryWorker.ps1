#Requires -Version 5.1
<#
.SYNOPSIS
  FET3D IFC geometry worker agent (pull mode). Windows PowerShell 5.1 compatible; runs beside the Unity package worker.

.DESCRIPTION
  Loop: lease a Geometry job -> download the IFC through its signed URL (SHA-256 checked) -> run the Python converter
  (IfcOpenShell, no LLM) while renewing the lease -> upload building.glb + building_metadata.json + preview_metadata.json
  through signed PUT URLs (exact checksum headers) -> register preview_glb (fet3d.editor/1) and building_metadata
  (fet3d.building/1) with the QA issues -> complete. A conversion error fails the job with its code and message; a lost
  lease (409 ATTEMPT_STALE) discards the output without failing the job.

  Contract: BE docs/geometry-worker.md. The X-Worker-Key is read from the DPAPI file written by Install-GeometryWorker.ps1
  (or the package worker's installer, same key), never from arguments, config, logs or the repository.

.PARAMETER ConfigPath
  geometry-worker.config.json (see geometry-worker.config.example.json).
.PARAMETER Once
  Process at most one job (or return when none is waiting). Used for tests.
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [switch]$Once
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if (-not $ConfigPath) { $ConfigPath = Join-Path $PSScriptRoot 'geometry-worker.config.json' }
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
[Net.ServicePointManager]::Expect100Continue = $false
Add-Type -AssemblyName System.Net.Http

$script:AgentVersion = 'fet3d-geometry-worker/1'
$script:ValidatorVersion = 'fet3d-geometry-worker/1'

# ----------------------------------------------------------------------------------------------------------------------
# Configuration
# ----------------------------------------------------------------------------------------------------------------------
function Read-WorkerConfig([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Config not found: $path (copy geometry-worker.config.example.json)" }
    $raw = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    $defaults = @{
        baseUrl            = $null
        toolchainVersion   = 'fet3d-geometry-worker-1'
        pythonExe          = (Join-Path $PSScriptRoot '.venv\Scripts\python.exe')
        workRoot           = 'C:\fet3d\geometry-work'
        keyFile            = 'C:\fet3d\secrets\worker-key.dpapi'
        pollSeconds        = 20
        renewSeconds       = 120
        timeoutMinutes     = 45
        keepJobs           = 5
        httpTimeoutSeconds = 300
    }
    $cfg = @{}
    foreach ($k in $defaults.Keys) {
        $cfg[$k] = $defaults[$k]
        if ($raw.PSObject.Properties[$k] -and $null -ne $raw.$k) { $cfg[$k] = $raw.$k }
    }
    if (-not $cfg.baseUrl) { throw 'baseUrl is required in the config.' }
    $cfg.baseUrl = $cfg.baseUrl.TrimEnd('/')
    if ($raw.PSObject.Properties['workerKey'] -or $raw.PSObject.Properties['X-Worker-Key']) {
        throw 'Never put the worker key in the config. Run Install-GeometryWorker.ps1 to store it DPAPI-protected.'
    }
    return $cfg
}

function Read-WorkerKey([string]$keyFile) {
    if (-not (Test-Path -LiteralPath $keyFile)) { throw "Worker key file not found: $keyFile (run Install-GeometryWorker.ps1 as the worker user)" }
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
    return [regex]::Replace($text, '(https?://[^\s"''?]+)\?[^\s"'']*', '$1?[signed]')
}

function Write-Log([string]$message, [string]$level = 'INFO') {
    $line = '{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}' -f (Get-Date), $level, (Protect-Text $message)
    if ($level -eq 'ERROR') { Write-Host $line -ForegroundColor Red }
    elseif ($level -eq 'WARN') { Write-Host $line -ForegroundColor Yellow }
    else { Write-Host $line }
    if ($script:LogDir) {
        $daily = Join-Path $script:LogDir ('geometry-{0:yyyyMMdd}.log' -f (Get-Date))
        Add-Content -LiteralPath $daily -Value $line -Encoding UTF8
    }
    if ($script:JobLog) { Add-Content -LiteralPath $script:JobLog -Value $line -Encoding UTF8 }
}

# ----------------------------------------------------------------------------------------------------------------------
# HTTP
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
    param([string]$Method, [string]$Path, [object]$Body, [string]$RawBody, [string]$WorkerKey)
    $url = "$($script:Config.baseUrl)$Path"
    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($Method), $url)
    $req.Headers.TryAddWithoutValidation('X-Worker-Key', $WorkerKey) | Out-Null
    $req.Headers.TryAddWithoutValidation('Accept', 'application/json') | Out-Null
    $json = $RawBody
    if (-not $json -and $null -ne $Body) { $json = $Body | ConvertTo-Json -Depth 100 -Compress }
    if ($json) { $req.Content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json') }
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
        foreach ($field in @('errors', 'issues')) {
            if ($result.Json -and $result.Json.PSObject.Properties[$field] -and $result.Json.$field) {
                $detail += ' ' + (($result.Json.$field | ConvertTo-Json -Depth 10 -Compress))
            }
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
function Get-Sha256Hex([string]$path) { return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Get-Prop($obj, [string]$name, $default = $null) {
    if ($null -eq $obj) { return $default }
    $p = $obj.PSObject.Properties[$name]
    if ($p -and $null -ne $p.Value) { return $p.Value }
    return $default
}

function Limit-Text([string]$text, [int]$max) {
    if ($null -eq $text) { return '' }
    $text = [regex]::Replace($text, '[\x00-\x08\x0B\x0C\x0E-\x1F]', '')
    if ($text.Length -le $max) { return $text }
    return $text.Substring(0, $max - 1) + '…'
}

function ConvertTo-JsonString([string]$value) { return ($value | ConvertTo-Json -Compress) }

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

function Invoke-JobCall([string]$Action, [string]$JobId, [hashtable]$Body, [string]$RawBody, [string]$WorkerKey) {
    $r = Invoke-Api -Method 'POST' -Path "/internal/processing/jobs/$JobId/$Action" -Body $Body -RawBody $RawBody -WorkerKey $WorkerKey
    if ($r.Status -eq 409 -and $r.Code -eq 'ATTEMPT_STALE') { throw [LeaseLost]::new("Lease lost during $Action") }
    return $r
}

function Invoke-Renew([object]$claim, [string]$WorkerKey) {
    $r = Invoke-JobCall -Action 'renew' -JobId $claim.jobId -Body @{ attemptId = $claim.attemptId; leaseToken = $claim.leaseToken } -WorkerKey $WorkerKey
    if (-not $r.Ok) { Write-Log "Renew answered $($r.Status); keeping the conversion running." 'WARN' }
    else { Write-Log "Lease renewed until $(Get-Prop $r.Json 'leaseUntil' '?')" }
}

function Save-Source([object]$claim, [string]$ifcPath, [string]$WorkerKey) {
    $inputs = @(Get-Prop $claim 'inputs' @())
    for ($round = 0; $round -lt 2; $round++) {
        $source = $inputs | Where-Object { $_.artifactType -eq 'ifc_source' } | Select-Object -First 1
        if (-not $source) { throw [JobFailed]::new('The claim carries no ifc_source input.') }
        $url = Get-Prop $source 'url'
        $expires = Get-Prop $source 'expiresAt'
        $stale = (-not $url) -or ($expires -and ([DateTimeOffset]::Parse($expires) -lt [DateTimeOffset]::UtcNow.AddSeconds(30)))
        if (-not $stale) {
            Write-Log "Downloading IFC ($(Get-Prop $source 'sizeBytes' '?') bytes)"
            try {
                Get-FileFromUrl -url $url -destination $ifcPath
                $expectedSize = [int64](Get-Prop $source 'sizeBytes' 0)
                $expectedSha = ([string](Get-Prop $source 'sha256' '')).ToLowerInvariant()
                $size = (Get-Item -LiteralPath $ifcPath).Length
                if ($expectedSize -gt 0 -and $size -ne $expectedSize) { throw [JobFailed]::new("IFC has $size bytes, expected $expectedSize.") }
                if ($expectedSha -and (Get-Sha256Hex $ifcPath) -ne $expectedSha) { throw [JobFailed]::new('IFC SHA-256 differs from the verified source.') }
                return
            } catch [JobFailed] { throw }
            catch { if ($round -gt 0) { throw [JobFailed]::new("IFC download failed: $($_.Exception.Message)") }; Write-Log "IFC download failed: $($_.Exception.Message)" 'WARN' }
        }
        Write-Log 'Re-signing the IFC input URL.'
        $r = Invoke-JobCall -Action 'inputs' -JobId $claim.jobId -Body @{ attemptId = $claim.attemptId; leaseToken = $claim.leaseToken } -WorkerKey $WorkerKey
        if (-not $r.Ok) { throw [JobFailed]::new("Cannot re-sign inputs (HTTP $($r.Status) $($r.Code)).") }
        $inputs = @(Get-Prop $r.Json 'inputs' @())
    }
    throw [JobFailed]::new('The IFC could not be downloaded before its URL expired.')
}

function Invoke-Converter([object]$claim, [string]$ifcPath, [string]$outputDir, [string]$logPath, [string]$WorkerKey) {
    $cfg = $script:Config
    $runner = Join-Path $PSScriptRoot 'run_converter.py'
    $argList = @('-X', 'utf8', ('"{0}"' -f $runner), 'convert', '--ifc', ('"{0}"' -f $ifcPath), '--out', ('"{0}"' -f $outputDir))
    $revision = [string](Get-Prop $claim 'revisionId' '')
    if ($revision) { $argList += @('--revision-id', $revision) }
    Write-Log "Starting converter: $($cfg.pythonExe) $($argList -join ' ')"
    $stdout = "$logPath.out"
    $proc = Start-Process -FilePath $cfg.pythonExe -ArgumentList $argList -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $logPath -WorkingDirectory $PSScriptRoot
    $null = $proc.Handle # keeps the handle so ExitCode is available after exit (Start-Process quirk)
    $deadline = (Get-Date).AddMinutes([double]$cfg.timeoutMinutes)
    $nextRenew = (Get-Date).AddSeconds([double]$cfg.renewSeconds)
    try {
        while (-not $proc.HasExited) {
            Start-Sleep -Seconds 2
            if ((Get-Date) -ge $nextRenew) {
                try { Invoke-Renew $claim $WorkerKey } catch [LeaseLost] { throw } catch { Write-Log "Renew error: $($_.Exception.Message)" 'WARN' }
                $nextRenew = (Get-Date).AddSeconds([double]$cfg.renewSeconds)
            }
            if ((Get-Date) -gt $deadline) { throw [JobFailed]::new("Conversion exceeded $($cfg.timeoutMinutes) minutes.") }
        }
    } catch {
        if (-not $proc.HasExited) { try { $proc.Kill() } catch {} }
        throw
    }
    $proc.WaitForExit()
    $line = @(Get-Content -LiteralPath $stdout -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_.Trim().StartsWith('{') }) | Select-Object -Last 1
    $status = if ($line) { $line | ConvertFrom-Json } else { $null }
    if ($proc.ExitCode -ne 0 -or -not $status -or -not (Get-Prop $status 'ok' $false)) {
        $code = Get-Prop $status 'code' 'WORKER_EXCEPTION'
        $err = Get-Prop $status 'error' ((Get-Content -LiteralPath $logPath -Tail 20 -ErrorAction SilentlyContinue) -join "`n")
        throw [JobFailed]::new("$code`: $err")
    }
    Write-Log "Converted: outcome $($status.outcome), $($status.floors) floors, $($status.elements) elements, $($status.triangles) triangles"
    return (Get-Content -LiteralPath (Join-Path $outputDir 'result.json') -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Publish-Geometry([object]$claim, [string]$outputDir, [object]$result, [string]$WorkerKey) {
    $files = @(
        @{ path = 'building.glb'; local = (Join-Path $outputDir 'building.glb') },
        @{ path = 'building_metadata.json'; local = (Join-Path $outputDir 'building_metadata.json') },
        @{ path = 'preview_metadata.json'; local = (Join-Path $outputDir 'preview_metadata.json') }
    )
    foreach ($f in $files) {
        if (-not (Test-Path -LiteralPath $f.local)) { throw [JobFailed]::new("Converter did not produce $($f.path).") }
        $f.sizeBytes = (Get-Item -LiteralPath $f.local).Length
        $f.sha256 = Get-Sha256Hex $f.local
    }
    $uploadAll = {
        $body = @{ attemptId = $claim.attemptId; leaseToken = $claim.leaseToken; files = @($files | ForEach-Object { @{ path = $_.path; sizeBytes = $_.sizeBytes; sha256 = $_.sha256 } }) }
        $r = Invoke-JobCall -Action 'upload-urls' -JobId $claim.jobId -Body $body -WorkerKey $WorkerKey
        if (-not $r.Ok) { throw [JobFailed]::new("upload-urls refused (HTTP $($r.Status) $($r.Code)).") }
        $signed = @(Get-Prop $r.Json 'files' @())
        foreach ($f in $files) {
            $entry = $signed | Where-Object { $_.path -eq $f.path } | Select-Object -First 1
            if (-not $entry) { throw [JobFailed]::new("No upload URL for $($f.path).") }
            $headers = @{}
            foreach ($p in (Get-Prop $entry 'headers' @{}).PSObject.Properties) { $headers[$p.Name] = [string]$p.Value }
            Write-Log "Uploading $($f.path) ($($f.sizeBytes) bytes)"
            $ok = $false
            for ($try = 1; $try -le 3 -and -not $ok; $try++) {
                try { Send-FileToUrl -url $entry.url -path $f.local -headers $headers; $ok = $true }
                catch { if ($try -eq 3) { throw [JobFailed]::new($_.Exception.Message) }; Write-Log "Retry $try for $($f.path): $($_.Exception.Message)" 'WARN'; Start-Sleep -Seconds (5 * $try) }
            }
        }
        return [string](Get-Prop $r.Json 'outputPrefix' (Get-Prop $claim 'outputPrefix' ''))
    }
    $prefix = & $uploadAll
    if (-not $prefix) { throw [JobFailed]::new('The backend returned no outputPrefix.') }

    $outcome = [string](Get-Prop $result 'outcome' 'Failed')
    $issuesJson = (ConvertTo-Json -InputObject @(Get-Prop $result 'issues' @()) -Depth 10 -Compress)
    if (-not $issuesJson -or $issuesJson -eq 'null') { $issuesJson = '[]' }
    $head = '{"attemptId":' + (ConvertTo-JsonString $claim.attemptId) + ',"leaseToken":' + (ConvertTo-JsonString $claim.leaseToken) + ',"output":{'
    # Metadata files are spliced in verbatim: the converter already wrote valid JSON and a PowerShell round trip of a large
    # semantic mapping is slow and can reorder or retype numbers.
    $outputs = @(
        @{ type = 'preview_glb'; file = $files[0]; schema = 'fet3d.editor/1'; metadata = $files[2].local; issues = $issuesJson },
        @{ type = 'building_metadata'; file = $files[1]; schema = 'fet3d.building/1'; metadata = $files[1].local; issues = '[]' }
    )
    $outputHash = ''
    foreach ($o in $outputs) {
        $metadata = [IO.File]::ReadAllText($o.metadata, [Text.Encoding]::UTF8)
        $raw = $head + '"artifactType":' + (ConvertTo-JsonString $o.type) +
            ',"objectKey":' + (ConvertTo-JsonString ($prefix + $o.file.path)) +
            ',"sha256Hash":' + (ConvertTo-JsonString $o.file.sha256) +
            ',"sizeBytes":' + $o.file.sizeBytes +
            ',"schemaVersion":' + (ConvertTo-JsonString $o.schema) +
            ',"validatorVersion":' + (ConvertTo-JsonString $script:ValidatorVersion) +
            ',"outcome":' + (ConvertTo-JsonString $outcome) +
            ',"issues":' + $o.issues +
            ',"metadata":' + $metadata + '}}'
        $r = Invoke-JobCall -Action 'outputs' -JobId $claim.jobId -RawBody $raw -WorkerKey $WorkerKey
        if (-not $r.Ok) { throw [JobFailed]::new("Registering $($o.type) failed (HTTP $($r.Status) $($r.Code)).") }
        $outputHash = [string](Get-Prop $r.Json 'outputHash' '')
    }
    if (-not $outputHash) { throw [JobFailed]::new('The outputs endpoint returned no outputHash.') }

    $reuploaded = $false
    for ($try = 1; $try -le 6; $try++) {
        $r = Invoke-JobCall -Action 'complete' -JobId $claim.jobId -Body @{ attemptId = $claim.attemptId; leaseToken = $claim.leaseToken; outputHash = $outputHash } -WorkerKey $WorkerKey
        if ($r.Ok) { Write-Log "Job $($claim.jobId) completed ($outcome): $($prefix)building.glb"; return }
        if ($r.Status -eq 409 -and $r.Code -like '*STORAGE_MISMATCH' -and -not $reuploaded) {
            Write-Log 'Storage mismatch: uploading the outputs again.' 'WARN'
            $null = & $uploadAll
            $reuploaded = $true
            continue
        }
        if ($r.Status -ge 500) { Start-Sleep -Seconds (10 * $try); continue }
        throw [JobFailed]::new("Complete refused (HTTP $($r.Status) $($r.Code) $($r.Title)).")
    }
    throw [JobFailed]::new('Complete kept failing; the lease will expire and the job can be retried.')
}

function Invoke-Job([object]$claim, [string]$WorkerKey) {
    $cfg = $script:Config
    $jobId = [string]$claim.jobId
    $attemptId = [string]$claim.attemptId
    $jobDir = Join-Path $cfg.workRoot ("jobs\$jobId-" + $attemptId.Substring(0, 8))
    $outputDir = Join-Path $jobDir 'output'
    if (Test-Path -LiteralPath $jobDir) { Remove-Item -LiteralPath $jobDir -Recurse -Force }
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    $script:JobLog = Join-Path $jobDir 'worker.log'
    $ifcPath = Join-Path $jobDir 'source.ifc'
    Write-Log "Geometry job $jobId attempt $attemptId (revision $(Get-Prop $claim 'revisionId' '?')), lease until $(Get-Prop $claim 'leaseUntil' '?')"
    try {
        Save-Source -claim $claim -ifcPath $ifcPath -WorkerKey $WorkerKey
        $result = Invoke-Converter -claim $claim -ifcPath $ifcPath -outputDir $outputDir -logPath (Join-Path $jobDir 'converter.log') -WorkerKey $WorkerKey
        Invoke-Renew $claim $WorkerKey
        Publish-Geometry -claim $claim -outputDir $outputDir -result $result -WorkerKey $WorkerKey
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
        Remove-Item -LiteralPath $ifcPath -Force -ErrorAction SilentlyContinue
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
if (-not (Test-Path -LiteralPath $script:Config.pythonExe)) { throw "Python not found at $($script:Config.pythonExe) (run Install-GeometryWorker.ps1)" }

Write-Log "$script:AgentVersion started: $($script:Config.baseUrl), toolchain $($script:Config.toolchainVersion)"
$idleLogged = $false
while ($true) {
    $claim = $null
    try {
        $r = Invoke-Api -Method 'POST' -Path '/internal/processing/geometry-jobs/lease' -Body @{ toolchainVersion = $script:Config.toolchainVersion } -WorkerKey $workerKey
        if ($r.Status -eq 200 -and $r.Json) { $claim = $r.Json }
        elseif ($r.Status -eq 204) { if (-not $idleLogged) { Write-Log 'No geometry job waiting; polling.'; $idleLogged = $true } }
        elseif ($r.Status -eq 401) { Write-Log 'Worker key rejected (401). Check the stored key.' 'ERROR' }
        elseif ($r.Status -eq 409 -and $r.Code -eq 'GEOMETRY_PULL_DISABLED') { Write-Log 'Geometry pull is disabled on the backend; waiting.' 'WARN' }
        else { Write-Log "Lease answered HTTP $($r.Status) $($r.Code)" 'WARN' }
    } catch {
        Write-Log "Lease request error: $($_.Exception.Message)" 'WARN'
    }
    if ($claim) {
        $idleLogged = $false
        Invoke-Job -claim $claim -WorkerKey $workerKey
        Remove-OldJobs $script:Config.workRoot ([int]$script:Config.keepJobs)
    }
    if ($Once) { break }
    if (-not $claim) { Start-Sleep -Seconds ([int]$script:Config.pollSeconds) }
}
