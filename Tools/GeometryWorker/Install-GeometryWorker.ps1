#Requires -Version 5.1
<#
.SYNOPSIS
  Installs the FET3D IFC geometry worker on the worker VM: Python venv, config, (optional) DPAPI key, Scheduled Task.

.DESCRIPTION
  Run as the Windows user that runs the Unity package worker. The worker key is the same X-Worker-Key: when the package
  worker installer already stored it at -KeyFile, it is reused; pass -SetKey to store/rotate it here (hidden input).
  Needs Python 3.11 x64 on PATH (py -3.11) or -Python.

.EXAMPLE
  .\Install-GeometryWorker.ps1 -BaseUrl https://api.fet3d.io.vn -RegisterTask
#>
[CmdletBinding()]
param(
    [string]$BaseUrl,
    [string]$Python,
    [string]$ToolchainVersion = 'fet3d-geometry-worker-1',
    [string]$WorkRoot = 'C:\fet3d\geometry-work',
    [string]$KeyFile = 'C:\fet3d\secrets\worker-key.dpapi',
    [string]$ConfigPath,
    [switch]$SetKey,
    [switch]$RegisterTask,
    [string]$TaskName = 'FET3D IFC Geometry Worker',
    [ValidateSet('Password', 'Interactive')] [string]$TaskLogon = 'Password'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if (-not $ConfigPath) { $ConfigPath = Join-Path $PSScriptRoot 'geometry-worker.config.json' }
function Write-Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

# 1) Worker key (shared with the package worker)
if ($SetKey -or -not (Test-Path -LiteralPath $KeyFile)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $KeyFile) -Force | Out-Null
    Write-Step "Storing the X-Worker-Key for $env:USERNAME at $KeyFile (DPAPI)"
    $secure = Read-Host -Prompt 'Paste the X-Worker-Key (input hidden)' -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $length = ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)).Length } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
    if ($length -lt 32 -or $length -gt 512) { throw 'The worker key must be 32-512 characters.' }
    $secure | ConvertFrom-SecureString | Set-Content -LiteralPath $KeyFile -Encoding ASCII
    $acl = Get-Acl -LiteralPath $KeyFile
    $acl.SetAccessRuleProtection($true, $false)
    $acl.Access | ForEach-Object { $acl.RemoveAccessRule($_) | Out-Null }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule("$env:USERDOMAIN\$env:USERNAME", 'FullControl', 'Allow')))
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule('BUILTIN\Administrators', 'FullControl', 'Allow')))
    Set-Acl -LiteralPath $KeyFile -AclObject $acl
} else { Write-Step "Reusing the worker key at $KeyFile" }

# 2) Python virtual environment with pinned packages
$venv = Join-Path $PSScriptRoot '.venv'
if (-not (Test-Path -LiteralPath (Join-Path $venv 'Scripts\python.exe'))) {
    Write-Step "Creating $venv"
    if ($Python) { & $Python -m venv $venv } else { & py -3.11 -m venv $venv }
    if ($LASTEXITCODE -ne 0) { throw 'Creating the virtual environment failed (install Python 3.11 x64 or pass -Python).' }
}
$py = Join-Path $venv 'Scripts\python.exe'
Write-Step 'Installing pinned requirements'
& $py -m pip install --disable-pip-version-check -q -r (Join-Path $PSScriptRoot 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'pip install failed.' }
& $py -c "import ifcopenshell, numpy; print('ifcopenshell', ifcopenshell.version)"

# 3) Config
if (-not $BaseUrl) { throw '-BaseUrl is required (e.g. https://api.fet3d.io.vn).' }
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
$config = [ordered]@{
    baseUrl = $BaseUrl.TrimEnd('/'); toolchainVersion = $ToolchainVersion; pythonExe = $py; workRoot = $WorkRoot; keyFile = $KeyFile
    pollSeconds = 20; renewSeconds = 120; timeoutMinutes = 45; keepJobs = 5; httpTimeoutSeconds = 300
}
Write-Step "Writing $ConfigPath"
$config | ConvertTo-Json | Set-Content -LiteralPath $ConfigPath -Encoding UTF8

# 4) Scheduled Task (same logon rules as the package worker: Password = starts at boot without RDP)
if ($RegisterTask) {
    Write-Step "Registering Scheduled Task '$TaskName' ($TaskLogon)"
    $script = Join-Path $PSScriptRoot 'Fire3DGeometryWorker.ps1'
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -ConfigPath "{1}"' -f $script, $ConfigPath) -WorkingDirectory $PSScriptRoot
    $settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable -MultipleInstances IgnoreNew -DontStopOnIdleEnd -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $user = "$env:USERDOMAIN\$env:USERNAME"
    if ($TaskLogon -eq 'Interactive') {
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
    } else {
        $trigger = New-ScheduledTaskTrigger -AtStartup
        $trigger.Delay = 'PT1M'
        $cred = Get-Credential -UserName $user -Message 'Password of the worker account (stored by Task Scheduler, not by FET3D)'
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -User $cred.UserName -Password $cred.GetNetworkCredential().Password -RunLevel Limited -Force | Out-Null
    }
    Start-ScheduledTask -TaskName $TaskName
    Write-Host "Task registered and started. Logs: $WorkRoot\logs\geometry-<date>.log"
} else {
    Write-Host "Config ready. Start manually with:`n  powershell -NoProfile -ExecutionPolicy Bypass -File `"$PSScriptRoot\Fire3DGeometryWorker.ps1`""
}
