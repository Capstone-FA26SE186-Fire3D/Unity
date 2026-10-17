#Requires -Version 5.1
<#
.SYNOPSIS
  Installs the FET3D Unity package worker on a Windows VM: folders, config, DPAPI-protected worker key, Scheduled Task.

.DESCRIPTION
  Run this **as the Windows user that will run the worker** (the user whose Unity Hub license is activated), in an
  elevated PowerShell only if you want the Scheduled Task registered (-RegisterTask).

  The X-Worker-Key is asked interactively (hidden input) and stored with DPAPI for the current user at -KeyFile.
  It is never written to the config, the repository or a log. Rotate it by running this script again with -KeyOnly.

.EXAMPLE
  .\Install-PackageWorker.ps1 -BaseUrl https://api.fet3d.io.vn -RegisterTask
.EXAMPLE
  .\Install-PackageWorker.ps1 -KeyOnly
#>
[CmdletBinding()]
param(
    [string]$BaseUrl,
    [string]$ToolchainVersion = 'unity-6000.6.6f1-worker-1',
    [string]$BuildTarget = 'Android',
    [string]$UnityExe = 'C:\Program Files\Unity\Hub\Editor\6000.6.6f1\Editor\Unity.exe',
    [string]$ProjectPath = 'C:\fet3d\unity',
    [string]$WorkRoot = 'C:\fet3d\work',
    [string]$KeyFile = 'C:\fet3d\secrets\worker-key.dpapi',
    [string]$ConfigPath,
    [switch]$KeyOnly,
    [switch]$RegisterTask,
    [string]$TaskName = 'FET3D Unity Package Worker',
    [ValidateSet('Interactive', 'Password', 'S4U')] [string]$TaskLogon = 'Interactive'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if (-not $ConfigPath) { $ConfigPath = Join-Path $PSScriptRoot 'worker.config.json' }

function Write-Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

# 1) Worker key (DPAPI, current user)
$secretDir = Split-Path -Parent $KeyFile
New-Item -ItemType Directory -Path $secretDir -Force | Out-Null
Write-Step "Storing the X-Worker-Key for user $env:USERNAME at $KeyFile (DPAPI)"
$secure = Read-Host -Prompt 'Paste the X-Worker-Key issued by the backend team (input hidden)' -AsSecureString
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
if ($plain.Length -lt 32 -or $plain.Length -gt 512) { throw 'The worker key must be 32-512 characters.' }
$plain = $null
$secure | ConvertFrom-SecureString | Set-Content -LiteralPath $KeyFile -Encoding ASCII
# Only this user may read the file.
$acl = Get-Acl -LiteralPath $KeyFile
$acl.SetAccessRuleProtection($true, $false)
$acl.Access | ForEach-Object { $acl.RemoveAccessRule($_) | Out-Null }
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule("$env:USERDOMAIN\$env:USERNAME", 'FullControl', 'Allow')))
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule('BUILTIN\Administrators', 'FullControl', 'Allow')))
Set-Acl -LiteralPath $KeyFile -AclObject $acl
Write-Host 'Key stored.'
if ($KeyOnly) { return }

# 2) Folders and config
if (-not $BaseUrl) { throw '-BaseUrl is required (e.g. https://api.fet3d.io.vn).' }
Write-Step "Creating $WorkRoot"
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
$config = [ordered]@{
    baseUrl             = $BaseUrl.TrimEnd('/')
    toolchainVersion    = $ToolchainVersion
    buildTarget         = $BuildTarget
    unityExe            = $UnityExe
    projectPath         = $ProjectPath
    workRoot            = $WorkRoot
    keyFile             = $KeyFile
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
Write-Step "Writing $ConfigPath"
$config | ConvertTo-Json | Set-Content -LiteralPath $ConfigPath -Encoding UTF8
if (-not (Test-Path -LiteralPath $UnityExe)) { Write-Warning "Unity not found at $UnityExe; install 6000.6.6f1 with Android Build Support." }
if (-not (Test-Path -LiteralPath (Join-Path $ProjectPath 'ProjectSettings\ProjectVersion.txt'))) {
    Write-Warning "Unity project not found at $ProjectPath. Clone it: git clone https://github.com/Capstone-FA26SE186-Fire3D/Unity.git $ProjectPath && git -C $ProjectPath lfs pull"
}

# 3) Scheduled Task (restarts on failure, starts at logon/boot)
if ($RegisterTask) {
    Write-Step "Registering Scheduled Task '$TaskName' for $env:USERDOMAIN\$env:USERNAME ($TaskLogon)"
    $script = Join-Path $PSScriptRoot 'Fire3DPackageWorker.ps1'
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -ConfigPath "{1}"' -f $script, $ConfigPath) -WorkingDirectory $PSScriptRoot
    $settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable -MultipleInstances IgnoreNew -DontStopOnIdleEnd -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $user = "$env:USERDOMAIN\$env:USERNAME"
    switch ($TaskLogon) {
        'Interactive' {
            # Runs while this user is logged on (disconnecting RDP keeps the session; signing out stops the worker).
            $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
            $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
            Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
        }
        'S4U' {
            # Runs without a logged-on session and without storing a password; Unity licensing must be activated for this user.
            $trigger = New-ScheduledTaskTrigger -AtStartup
            $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType S4U -RunLevel Limited
            Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
        }
        'Password' {
            # Windows asks for the account password itself and stores it in the Task Scheduler credential vault.
            $trigger = New-ScheduledTaskTrigger -AtStartup
            $cred = Get-Credential -UserName $user -Message 'Password of the worker account (stored by Task Scheduler, not by FET3D)'
            Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -User $cred.UserName -Password $cred.GetNetworkCredential().Password -RunLevel Limited -Force | Out-Null
        }
    }
    Start-ScheduledTask -TaskName $TaskName
    Write-Host "Task registered and started. Logs: $WorkRoot\logs\worker-<date>.log"
} else {
    Write-Host "Config ready. Start manually with:`n  powershell -NoProfile -ExecutionPolicy Bypass -File `"$PSScriptRoot\Fire3DPackageWorker.ps1`"`nor rerun with -RegisterTask to install the Scheduled Task."
}
