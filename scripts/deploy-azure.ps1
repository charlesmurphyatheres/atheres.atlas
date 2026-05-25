# =============================================================
# deploy-azure.ps1 -- Provision the full Atlas Deliver stack
#                     in Azure with managed SSL on www.atlasdeliver.com
#
# Prerequisites:
#   - Azure CLI installed (az)
#   - .NET 8 SDK (for EF migrations)
#   - Node 20+ / npm (for frontend build)
#   - Azure Functions Core Tools v4 (func)
#
# Usage:
#   .\deploy-azure.ps1              # incremental; creates anything missing
#   .\deploy-azure.ps1 -Clean       # delete the resource group first, then redeploy
#   .\deploy-azure.ps1 -WipeOnly    # delete the resource group and exit (no redeploy).
#                                   # Use when you want to nuke the entire stack —
#                                   # site, SQL database, Functions, Front Door,
#                                   # storage, Service Bus, SignalR, App Insights —
#                                   # without provisioning anything new.
#   .\deploy-azure.ps1 -Force       # skip the interactive "type WIPE to confirm"
#                                   # prompt for -Clean / -WipeOnly. Use with care.
#   .\deploy-azure.ps1 -SkipDnsWait # skip the interactive DNS-validation pause
#   .\deploy-azure.ps1 -Diagnose    # run health checks against an existing deploy;
#                                   # print status of Function Apps, runtimes, and
#                                   # the current function list. No changes.
# =============================================================
[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$WipeOnly,
    [switch]$Force,
    [switch]$SkipDnsWait,
    [switch]$Diagnose
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---- Deployment timer ---------------------------------------
$deployStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

function Format-Duration {
    param([System.Diagnostics.Stopwatch]$sw)
    $ts = $sw.Elapsed
    if ($ts.TotalHours -ge 1) { return ("{0:D}h {1:D2}m {2:D2}s" -f [int]$ts.TotalHours, $ts.Minutes, $ts.Seconds) }
    if ($ts.TotalMinutes -ge 1) { return ("{0:D}m {1:D2}s" -f $ts.Minutes, $ts.Seconds) }
    return ("{0:D}.{1:D3}s" -f $ts.Seconds, $ts.Milliseconds)
}

# `trap { continue }` previously swallowed failures and resumed the next
# statement, producing cascading errors downstream. Stop the stopwatch and
# rethrow so the first real failure aborts the deploy.
trap {
    if ($deployStopwatch.IsRunning) {
        $deployStopwatch.Stop()
        Write-Host "`n  Script failed after " -NoNewline -ForegroundColor Red
        Write-Host (Format-Duration $deployStopwatch) -ForegroundColor Cyan
    }
    break
}

# ---- Helpers ------------------------------------------------

# Cryptographically secure 32-byte key, base64-encoded.
function New-CryptoKey {
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return [Convert]::ToBase64String($bytes)
}

# Persist a key=value pair into the repo .env file so the next run can reuse it.
# Idempotent: existing entries for the key are replaced.
function Save-EnvValue {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Key,
        [Parameter(Mandatory)][string]$Value
    )
    $line = "$Key=$Value"
    if (-not (Test-Path $Path)) {
        Set-Content -Path $Path -Value $line -Encoding UTF8
        return
    }
    $lines = Get-Content $Path
    if ($lines -match "^\s*$Key\s*=") {
        ($lines | ForEach-Object { if ($_ -match "^\s*$Key\s*=") { $line } else { $_ } }) |
            Set-Content -Path $Path -Encoding UTF8
    } else {
        Add-Content -Path $Path -Value $line -Encoding UTF8
    }
}

# Exit with a useful message if `az` returned non-zero on the previous call.
function Assert-AzSuccess {
    param([string]$Step)
    if ($LASTEXITCODE -ne 0) { throw "$Step failed (exit $LASTEXITCODE)." }
}

# Returns $true if the given `az ... show` command succeeds (resource exists).
# Used to make every provisioning step idempotent: we only call `create` when
# the target doesn't already exist, so a failed deploy can be rerun without
# the "resource already exists" cascade.
function Test-AzResource {
    param([Parameter(Mandatory)][string[]]$ShowArgs)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    try {
        az @ShowArgs --output none 2>$null
        return ($LASTEXITCODE -eq 0)
    }
    finally {
        $ErrorActionPreference = $prev
        $global:LASTEXITCODE = 0   # don't leak the probe's exit code
    }
}

# Wraps a create step so "already exists" skips, anything else creates. The
# $Label is only used for the console message. $CreateBlock is a scriptblock
# that does the actual az create call.
function Invoke-EnsureResource {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string[]]$ShowArgs,
        [Parameter(Mandatory)][scriptblock]$CreateBlock
    )
    if (Test-AzResource -ShowArgs $ShowArgs) {
        Write-Host "  [skip] $Label already exists" -ForegroundColor DarkGray
        return
    }
    Write-Host "  [create] $Label" -ForegroundColor Gray
    & $CreateBlock
    Assert-AzSuccess $Label
}

# Upload function-app settings via a JSON file (@file syntax) rather than
# positional "Key=Value" args. The positional form silently drops the whole
# settings batch when a value contains semicolons or equals signs (e.g. a SQL
# connection string) -- the CLI exits 0 and the Function App comes up with
# zero of the settings applied, which manifests as the worker crashing at
# startup ("only WarmUp indexed"). @file syntax bypasses shell parsing
# entirely.
function Set-FunctionAppAppSettings {
    param(
        [Parameter(Mandatory)][string]$AppName,
        [Parameter(Mandatory)][hashtable]$Settings
    )
    $payload = foreach ($kv in $Settings.GetEnumerator()) {
        @{ name = $kv.Key; value = [string]$kv.Value; slotSetting = $false }
    }
    $jsonPath = Join-Path $env:TEMP ("appsettings-" + [guid]::NewGuid().ToString("N") + ".json")
    try {
        $payload | ConvertTo-Json | Set-Content -Path $jsonPath -Encoding utf8
        az functionapp config appsettings set `
            --resource-group $ResourceGroup `
            --name $AppName `
            --settings "@$jsonPath" `
            --output none
        Assert-AzSuccess "app settings on $AppName"
    }
    finally {
        if (Test-Path $jsonPath) { Remove-Item $jsonPath -Force -ErrorAction SilentlyContinue }
    }
}

# Pin the Linux-consumption Function App to the isolated .NET 8 worker and the
# Functions v4 runtime explicitly. `az functionapp create` usually sets these,
# but the later `appsettings set` on some CLI versions or plan types has been
# observed to drop FUNCTIONS_WORKER_RUNTIME / leave linuxFxVersion empty,
# which manifests as the app booting with no functions indexed (the "only
# WarmUp shows up" symptom).
function Set-FunctionAppIsolatedRuntime {
    param([Parameter(Mandatory)][string]$AppName)
    az functionapp config appsettings set `
        --resource-group $ResourceGroup `
        --name $AppName `
        --settings `
            "FUNCTIONS_WORKER_RUNTIME=dotnet-isolated" `
            "FUNCTIONS_EXTENSION_VERSION=~4" `
        --output none
    Assert-AzSuccess "Runtime settings on $AppName"

    # The `|` in "DOTNET-ISOLATED|8.0" is re-parsed by cmd.exe (via the az.cmd
    # shim) as a pipe regardless of PowerShell-side quoting. Route the call
    # through `cmd /c` with the whole command as one single-quoted string so
    # cmd.exe's own parser sees the `|` inside the inner `"…"` (literal, not
    # a pipe separator).
    $cmdline = 'az functionapp config set --resource-group "{0}" --name "{1}" --linux-fx-version "DOTNET-ISOLATED|8.0" --output none' -f $ResourceGroup, $AppName
    cmd /c $cmdline
    Assert-AzSuccess "linuxFxVersion on $AppName"
}

# After `func azure functionapp publish`, verify the runtime indexed the
# expected functions. If it didn't (publish succeeded but loaded nothing),
# throw with guidance instead of letting the deploy proceed to import steps
# that will fail against a non-functional Auth host.
function Assert-FunctionAppIndexed {
    param(
        [Parameter(Mandatory)][string]$AppName,
        [string[]]$ExpectedAny = @(),
        [int]$TimeoutSeconds = 240
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $attempt = 0
    $indexed = $null
    $lastListErr = ''
    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        while ((Get-Date) -lt $deadline) {
            $attempt++
            # Capture stderr so we can surface *why* the list call is failing
            # (auth/network/Azure-side) instead of the opaque "<list call failed>".
            $raw = az functionapp function list `
                --resource-group $ResourceGroup `
                --name $AppName `
                -o json 2>$errFile
            if ($LASTEXITCODE -eq 0 -and $raw) {
                try {
                    $indexed = $raw | ConvertFrom-Json
                    $names = @($indexed | ForEach-Object { $_.name -replace '^.+/', '' })
                    if ($ExpectedAny.Count -eq 0 -and $names.Count -gt 0) {
                        Write-Host "  [ok] $AppName indexed $($names.Count) function(s): $($names -join ', ')" -ForegroundColor Green
                        return
                    }
                    $matched = @($names | Where-Object { $ExpectedAny -contains $_ })
                    if ($matched.Count -gt 0) {
                        Write-Host "  [ok] $AppName indexed: $($names -join ', ')" -ForegroundColor Green
                        return
                    }
                } catch { }
            } else {
                $lastListErr = (Get-Content $errFile -Raw -ErrorAction SilentlyContinue) -as [string]
            }
            Start-Sleep 10
            Write-Host "." -NoNewline -ForegroundColor DarkGray
        }
    } finally {
        Remove-Item $errFile -ErrorAction SilentlyContinue
    }
    Write-Host ""

    # On timeout, run the same diagnostic checks inline so the error message
    # includes the actual runtime config / last list-call stderr instead of
    # telling the user to go run three more commands by hand.
    $current = if ($indexed) { ($indexed | ForEach-Object { $_.name -replace '^.+/', '' }) -join ', ' } else { '<list call failed>' }
    $linuxFx       = (az functionapp config show              -g $ResourceGroup -n $AppName --query linuxFxVersion -o tsv 2>$null)
    $workerRuntime = (az functionapp config appsettings list  -g $ResourceGroup -n $AppName --query "[?name=='FUNCTIONS_WORKER_RUNTIME'].value" -o tsv 2>$null)
    $extVersion    = (az functionapp config appsettings list  -g $ResourceGroup -n $AppName --query "[?name=='FUNCTIONS_EXTENSION_VERSION'].value" -o tsv 2>$null)
    $listErrText   = if ([string]::IsNullOrWhiteSpace($lastListErr)) { '(no stderr captured)' } else { $lastListErr.Trim() }

    throw @"
$AppName has no user functions indexed after $TimeoutSeconds s.
Currently indexed: $current
Expected at least one of: $($ExpectedAny -join ', ')

Runtime config (observed now):
  linuxFxVersion              = $linuxFx
  FUNCTIONS_WORKER_RUNTIME    = $workerRuntime
  FUNCTIONS_EXTENSION_VERSION = $extVersion

Last 'az functionapp function list' stderr:
  $listErrText

Common causes:
  - linuxFxVersion isn't DOTNET-ISOLATED|8.0 (see above).
  - FUNCTIONS_WORKER_RUNTIME isn't dotnet-isolated (see above).
  - Worker is crashing on startup. Tail logs to see the exception:
      az webapp log tail -g $ResourceGroup -n $AppName
  - Publish uploaded an empty package. Re-run publish with --verbose and watch
    the 'Functions in <app>:' list it prints after upload — if that list is
    empty too, the host indexed zero functions from the package.

Or: .\deploy-azure.ps1 -Diagnose
"@
}

# Health-check only mode. Prints runtime config + function list for both apps.
function Invoke-Diagnostics {
    Write-Host "`n=== Diagnostic: Function App runtime + indexed functions ===" -ForegroundColor Cyan
    foreach ($app in @($FuncAppMain, $FuncAppAuth)) {
        Write-Host "`n--- $app ---" -ForegroundColor Yellow
        if (-not (Test-AzResource -ShowArgs @('functionapp','show','--resource-group',$ResourceGroup,'--name',$app))) {
            Write-Host "  Function App does not exist in $ResourceGroup." -ForegroundColor Red
            continue
        }
        $linuxFx = (az functionapp config show -g $ResourceGroup -n $app --query linuxFxVersion -o tsv 2>$null)
        $workerRuntime = (az functionapp config appsettings list -g $ResourceGroup -n $app --query "[?name=='FUNCTIONS_WORKER_RUNTIME'].value" -o tsv 2>$null)
        $extVersion    = (az functionapp config appsettings list -g $ResourceGroup -n $app --query "[?name=='FUNCTIONS_EXTENSION_VERSION'].value" -o tsv 2>$null)
        $runPackage    = (az functionapp config appsettings list -g $ResourceGroup -n $app --query "[?name=='WEBSITE_RUN_FROM_PACKAGE'].value" -o tsv 2>$null)
        Write-Host ("  linuxFxVersion:             {0}" -f $linuxFx)
        Write-Host ("  FUNCTIONS_WORKER_RUNTIME:   {0}" -f $workerRuntime)
        Write-Host ("  FUNCTIONS_EXTENSION_VERSION:{0}" -f $extVersion)
        Write-Host ("  WEBSITE_RUN_FROM_PACKAGE:   {0}" -f $runPackage)
        $raw = az functionapp function list -g $ResourceGroup -n $app -o json 2>$null
        if ($LASTEXITCODE -eq 0 -and $raw) {
            # Wrap in @() so strict-mode doesn't choke when the pipeline yields
            # nothing (ConvertFrom-Json | ForEach-Object -> $null in that case).
            $fns = @($raw | ConvertFrom-Json | ForEach-Object { $_.name -replace '^.+/', '' })
            if ($fns.Count -eq 0) {
                Write-Host "  Functions:                  (none)" -ForegroundColor Red
            } else {
                Write-Host "  Functions:                  $($fns -join ', ')"
            }
        } else {
            Write-Host "  Functions:                  <list call failed>" -ForegroundColor Red
        }
    }
    Write-Host ""
}

# =============================================================
# 1. CONFIGURATION -- fill in before running
# =============================================================

# Project root is the parent of scripts/. .env, project bin folders, etc.
# all anchor on it.
$_repoRoot = Split-Path -Parent $PSScriptRoot

# Load secrets from .env (git-ignored)
$_envFile = Join-Path $_repoRoot ".env"
$_envVars = @{}
if (Test-Path $_envFile) {
    Get-Content $_envFile | Where-Object { $_ -match '^\s*[^#]' -and $_ -match '=' } | ForEach-Object {
        $p = $_ -split '=', 2; $_envVars[$p[0].Trim()] = $p[1].Trim().Trim('"')
    }
}

$Location           = "centralus"
$ResourceGroup      = "rg-atheres-atlas"
$CustomDomain       = "www.atlasdeliver.com"
$PublicUrl           = "https://www.atlasdeliver.com"

# SQL
$SqlServerName      = "atlas-sql-atheres"
$SqlDbName          = "AtheresAtlas"
$SqlAdminUser       = "atlas-admin"
$SqlAdminPasswordPlain = if ($_envVars["MSSQL_SA_PASSWORD"]) { $_envVars["MSSQL_SA_PASSWORD"] } else {
    $sp = Read-Host -Prompt "SQL admin password" -AsSecureString
    [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sp))
}

# Storage
$StorageAccount     = "statheresatlasprod"

# Service Bus
$ServiceBusNs       = "sb-atheres-atlas"

# SignalR
$SignalRName        = "atlas-signalr"

# Function Apps
$FuncPlanName       = "plan-atlas-functions"
$FuncAppMain        = "func-atlas-main"
$FuncAppAuth        = "func-atlas-auth"

# Frontend
$FrontendStorage    = "statlasfe0001"

# Front Door
$FrontDoorName      = "fd-atheres-atlas"
$FrontDoorProfile   = "fdp-atheres-atlas"

# Monitoring
$LogWorkspace       = "log-atheres-atlas"
$AppInsights        = "ai-atheres-atlas"

# Third-party keys -- required; the deploy fails fast if they're missing so we
# don't accidentally configure Function Apps with placeholder strings and then
# discover the breakage at runtime.
function Get-RequiredEnv {
    param([string]$Key, [string]$FriendlyHint)
    $val = $_envVars[$Key]
    if ([string]::IsNullOrWhiteSpace($val) -or $val -match '^<.*>$') {
        throw "Missing $Key in .env ($FriendlyHint). Populate it and rerun."
    }
    return $val
}
$GoogleMapsApiKey    = Get-RequiredEnv "GOOGLE_MAPS_API_KEY" "Google Maps JavaScript + Directions API key"
$SendGridApiKey      = Get-RequiredEnv "SENDGRID_API_KEY"    "SendGrid account API key"
$SendGridFromEmail   = "noreply@atlasdeliver.com"
$SendGridFromName    = "Atlas Deliver"

# JWT -- persist the signing key in .env so redeploys don't invalidate every
# issued token. First run generates a cryptographically secure 256-bit key.
if ([string]::IsNullOrWhiteSpace($_envVars["JWT_SECRET_KEY"])) {
    $JwtSecretKey = New-CryptoKey
    Save-EnvValue -Path $_envFile -Key "JWT_SECRET_KEY" -Value $JwtSecretKey
    Write-Host "  Generated a new JWT_SECRET_KEY and saved it to .env" -ForegroundColor Gray
} else {
    $JwtSecretKey = $_envVars["JWT_SECRET_KEY"]
}
$JwtIssuer          = $PublicUrl
$JwtAudience        = $PublicUrl
$JwtExpiryMinutes   = "15"
$JwtRefreshDays     = "7"

# Seed admin. Persist the password so re-deploys don't lock out the bootstrap
# account; first run generates one. Clear SEED_ADMIN_PASSWORD from the Function
# App settings after you've proven you can log in (follow-up task printed at
# the end of the run).
$SeedAdminEmail     = "charles.murphy@atheres.com"
if ([string]::IsNullOrWhiteSpace($_envVars["SEED_ADMIN_PASSWORD"])) {
    $SeedAdminPassword = New-CryptoKey
    Save-EnvValue -Path $_envFile -Key "SEED_ADMIN_PASSWORD" -Value $SeedAdminPassword
    Write-Host "  Generated a new SEED_ADMIN_PASSWORD and saved it to .env" -ForegroundColor Gray
} else {
    $SeedAdminPassword = $_envVars["SEED_ADMIN_PASSWORD"]
}

# =============================================================
# 2. LOGIN
# =============================================================

Write-Host "`n=== Logging in to Azure ===" -ForegroundColor Cyan
az login
az account set --subscription "171bf58a-ae31-415c-ad50-85ec2b520edb"
az account show --output table

# Health-check mode -- run diagnostics and exit without touching anything.
if ($Diagnose) {
    Invoke-Diagnostics
    $deployStopwatch.Stop()
    Write-Host "Diagnostics complete in $(Format-Duration $deployStopwatch)" -ForegroundColor Cyan
    exit 0
}

# =============================================================
# 3. CLEAN UP PREVIOUS DEPLOYMENT
# =============================================================
# -Clean wipes the resource group and continues with a fresh deploy.
# -WipeOnly wipes the resource group and exits — for when you want a
#   clean slate without immediately rebuilding.
# Both prompt for explicit "WIPE" confirmation unless -Force is passed.

$rgExists = az group exists --name $ResourceGroup 2>$null
$wantWipe = $Clean -or $WipeOnly

if ($wantWipe -and $rgExists -eq "true") {
    if (-not $Force) {
        $action = if ($WipeOnly) { "wipe ALL Atlas Deliver Azure resources (no redeploy)" }
                  else           { "wipe ALL Atlas Deliver Azure resources, then redeploy" }
        $sub = az account show --query name -o tsv 2>$null
        Write-Host ""
        Write-Host "  ==================== DESTRUCTIVE ACTION ====================" -ForegroundColor Red
        Write-Host "  About to $action." -ForegroundColor Yellow
        Write-Host "    Resource group: $ResourceGroup" -ForegroundColor Yellow
        Write-Host "    Subscription:   $sub" -ForegroundColor Yellow
        Write-Host "    Includes:       SQL server + AtheresAtlas database, both Function Apps," -ForegroundColor Yellow
        Write-Host "                    Front Door, Storage, Service Bus, SignalR, App Insights." -ForegroundColor Yellow
        Write-Host "    All data will be deleted. This cannot be undone." -ForegroundColor Yellow
        Write-Host "  ============================================================" -ForegroundColor Red
        Write-Host ""
        $answer = Read-Host "  Type 'WIPE' to continue (anything else aborts)"
        if ($answer -ne 'WIPE') {
            Write-Host "  Cancelled — nothing was deleted." -ForegroundColor Green
            exit 0
        }
    }

    Write-Host "`n=== Deleting resource group: $ResourceGroup ===" -ForegroundColor Yellow
    az group delete --name $ResourceGroup --yes --no-wait --output none
    Assert-AzSuccess "Resource group delete"

    # Wait for deletion. A full stack (SQL + Front Door + Functions) can take
    # 20+ minutes to tear down; fail hard rather than racing the create step.
    Write-Host "  Waiting for deletion..." -ForegroundColor Gray
    $retries = 240  # 240 x 10s = 40 minutes
    while ($retries -gt 0) {
        $still = az group exists --name $ResourceGroup 2>$null
        if ($still -ne "true") { break }
        Start-Sleep 10
        Write-Host "." -NoNewline
        $retries--
    }
    Write-Host ""
    if ($retries -eq 0) {
        throw "Resource group $ResourceGroup did not finish deleting within 40 minutes. Re-run when it's gone."
    }
    Write-Host "  [OK] Resource group deleted." -ForegroundColor Green

    # Purge any soft-deleted Log Analytics workspace with our well-known
    # name. Azure retains deleted workspaces for 14 days by default; if we
    # leave the tombstone in place, recreating with the same name later
    # fails with "name not available." Best-effort: log + continue if the
    # cleanup itself errors, since the next deploy will surface a clear
    # message either way.
    Write-Host "  Purging soft-deleted Log Analytics workspace if present..." -ForegroundColor Gray
    $deletedJson = az monitor log-analytics workspace list-deleted-workspaces --query "[?name=='$LogWorkspace']" -o json 2>$null
    if ($deletedJson -and $deletedJson -ne '[]') {
        try {
            # Recover into the (now-empty) RG, then delete with --force so the
            # workspace name is reusable immediately.
            az group create --name $ResourceGroup --location $Location --output none 2>$null | Out-Null
            az monitor log-analytics workspace recover --resource-group $ResourceGroup --workspace-name $LogWorkspace --output none 2>$null
            az monitor log-analytics workspace delete --resource-group $ResourceGroup --workspace-name $LogWorkspace --force true --yes --output none 2>$null
            # Then drop the placeholder RG so the rest of the deploy starts truly clean.
            az group delete --name $ResourceGroup --yes --output none 2>$null
            Write-Host "  [OK] Soft-deleted workspace purged." -ForegroundColor Green
        } catch {
            Write-Host "  [WARN] Workspace purge failed: $($_.Exception.Message). The next deploy may need to use a different workspace name if it errors." -ForegroundColor Yellow
        }
    } else {
        Write-Host "  No soft-deleted workspace to purge." -ForegroundColor Gray
    }

    if ($WipeOnly) {
        $deployStopwatch.Stop()
        Write-Host ""
        Write-Host "=============================================================" -ForegroundColor Green
        Write-Host "  WIPE COMPLETE in $(Format-Duration $deployStopwatch)" -ForegroundColor Green
        Write-Host "  Resource group + soft-deleted dependencies removed." -ForegroundColor Green
        Write-Host "  Re-run without -WipeOnly to provision a fresh stack." -ForegroundColor Green
        Write-Host "=============================================================" -ForegroundColor Green
        exit 0
    }
} elseif ($WipeOnly -and $rgExists -ne "true") {
    Write-Host "`n=== -WipeOnly specified but resource group $ResourceGroup doesn't exist. Nothing to wipe. ===" -ForegroundColor Gray
    exit 0
} elseif ($rgExists -eq "true") {
    Write-Host "`n=== Resource group $ResourceGroup exists -- incremental deploy (pass -Clean to wipe + redeploy, or -WipeOnly to wipe + exit) ===" -ForegroundColor Gray
}

# =============================================================
# 4. RESOURCE GROUP
# =============================================================

Write-Host "`n=== Creating resource group: $ResourceGroup ===" -ForegroundColor Cyan
az group create --name $ResourceGroup --location $Location --output none

# =============================================================
# 4a. SQL SERVER + DATABASE
# =============================================================

Write-Host "`n=== SQL Server: $SqlServerName ===" -ForegroundColor Cyan
Invoke-EnsureResource -Label "SQL Server $SqlServerName" `
    -ShowArgs @('sql','server','show','--resource-group',$ResourceGroup,'--name',$SqlServerName) `
    -CreateBlock {
        az sql server create `
            --resource-group $ResourceGroup `
            --name $SqlServerName `
            --location $Location `
            --admin-user $SqlAdminUser `
            --admin-password $SqlAdminPasswordPlain `
            --output none
    }

# Allow Azure services to connect
Invoke-EnsureResource -Label "Firewall rule AllowAzureServices" `
    -ShowArgs @('sql','server','firewall-rule','show','--resource-group',$ResourceGroup,'--server',$SqlServerName,'--name','AllowAzureServices') `
    -CreateBlock {
        az sql server firewall-rule create `
            --resource-group $ResourceGroup `
            --server $SqlServerName `
            --name "AllowAzureServices" `
            --start-ip-address 0.0.0.0 `
            --end-ip-address 0.0.0.0 `
            --output none
    }

# Allow current machine (for running migrations). The LocalDev rule's IP may
# drift between runs if the operator is on a different network, so refresh it
# on every deploy with `firewall-rule update` after ensure-create.
$myIp = (Invoke-RestMethod -Uri "https://api.ipify.org")
Invoke-EnsureResource -Label "Firewall rule LocalDev ($myIp)" `
    -ShowArgs @('sql','server','firewall-rule','show','--resource-group',$ResourceGroup,'--server',$SqlServerName,'--name','LocalDev') `
    -CreateBlock {
        az sql server firewall-rule create `
            --resource-group $ResourceGroup `
            --server $SqlServerName `
            --name "LocalDev" `
            --start-ip-address $myIp `
            --end-ip-address $myIp `
            --output none
    }
az sql server firewall-rule update `
    --resource-group $ResourceGroup `
    --server $SqlServerName `
    --name "LocalDev" `
    --start-ip-address $myIp `
    --end-ip-address $myIp `
    --output none
Assert-AzSuccess "Firewall rule LocalDev refresh"

Invoke-EnsureResource -Label "SQL database $SqlDbName" `
    -ShowArgs @('sql','db','show','--resource-group',$ResourceGroup,'--server',$SqlServerName,'--name',$SqlDbName) `
    -CreateBlock {
        az sql db create `
            --resource-group $ResourceGroup `
            --server $SqlServerName `
            --name $SqlDbName `
            --service-objective S1 `
            --backup-storage-redundancy Local `
            --output none
    }

$SqlConnectionString = "Server=tcp:$SqlServerName.database.windows.net,1433;Database=$SqlDbName;User Id=$SqlAdminUser;Password=$SqlAdminPasswordPlain;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;"

# =============================================================
# 5. STORAGE ACCOUNT (Functions runtime)
# =============================================================

Write-Host "`n=== Storage Account: $StorageAccount ===" -ForegroundColor Cyan
Invoke-EnsureResource -Label "Storage account $StorageAccount" `
    -ShowArgs @('storage','account','show','--resource-group',$ResourceGroup,'--name',$StorageAccount) `
    -CreateBlock {
        az storage account create `
            --resource-group $ResourceGroup `
            --name $StorageAccount `
            --location $Location `
            --sku Standard_LRS `
            --kind StorageV2 `
            --min-tls-version TLS1_2 `
            --allow-blob-public-access false `
            --output none
    }

$StorageConnStr = (az storage account show-connection-string `
    --resource-group $ResourceGroup `
    --name $StorageAccount `
    --query connectionString -o tsv)

# =============================================================
# 6. SERVICE BUS NAMESPACE + QUEUES
# =============================================================

Write-Host "`n=== Service Bus: $ServiceBusNs ===" -ForegroundColor Cyan
Invoke-EnsureResource -Label "Service Bus namespace $ServiceBusNs" `
    -ShowArgs @('servicebus','namespace','show','--resource-group',$ResourceGroup,'--name',$ServiceBusNs) `
    -CreateBlock {
        az servicebus namespace create `
            --resource-group $ResourceGroup `
            --name $ServiceBusNs `
            --location $Location `
            --sku Basic `
            --output none
    }

# Queue definitions
$queues = @(
    "atlas-orders-ingest",
    "atlas-routes-optimize",
    "atlas-confirmations-send",
    "atlas-confirmations-received",
    "atlas-reschedule",
    "atlas-notifications",
    "atlas-audit"
)

foreach ($q in $queues) {
    Invoke-EnsureResource -Label "Queue $q" `
        -ShowArgs @('servicebus','queue','show','--resource-group',$ResourceGroup,'--namespace-name',$ServiceBusNs,'--name',$q) `
        -CreateBlock {
            az servicebus queue create `
                --resource-group $ResourceGroup `
                --namespace-name $ServiceBusNs `
                --name $q `
                --output none
        }
}

$ServiceBusConnStr = (az servicebus namespace authorization-rule keys list `
    --resource-group $ResourceGroup `
    --namespace-name $ServiceBusNs `
    --name RootManageSharedAccessKey `
    --query primaryConnectionString -o tsv)

# =============================================================
# 7. AZURE SIGNALR SERVICE
# =============================================================

Write-Host "`n=== SignalR Service: $SignalRName ===" -ForegroundColor Cyan
Invoke-EnsureResource -Label "SignalR $SignalRName" `
    -ShowArgs @('signalr','show','--resource-group',$ResourceGroup,'--name',$SignalRName) `
    -CreateBlock {
        az signalr create `
            --resource-group $ResourceGroup `
            --name $SignalRName `
            --location $Location `
            --sku Free_F1 `
            --service-mode Serverless `
            --output none
    }

$SignalRConnStr = (az signalr key list `
    --resource-group $ResourceGroup `
    --name $SignalRName `
    --query primaryConnectionString -o tsv)

# =============================================================
# 8. LOG ANALYTICS + APPLICATION INSIGHTS
# =============================================================

Write-Host "`n=== Application Insights ===" -ForegroundColor Cyan

# Log Analytics workspaces enter a 14-day soft-delete window; hitting `create`
# on a soft-deleted workspace with the same name fails. Check for the live
# workspace first; if create fails mention the recover path.
$workspaceExists = Test-AzResource -ShowArgs @('monitor','log-analytics','workspace','show','--resource-group',$ResourceGroup,'--workspace-name',$LogWorkspace)
if (-not $workspaceExists) {
    az monitor log-analytics workspace create `
        --resource-group $ResourceGroup `
        --workspace-name $LogWorkspace `
        --location $Location `
        --output none
    if ($LASTEXITCODE -ne 0) {
        throw "Log Analytics workspace create failed. If this workspace was recently deleted, run: az monitor log-analytics workspace recover -g $ResourceGroup -n $LogWorkspace"
    }
    Write-Host "  [create] Log Analytics workspace $LogWorkspace" -ForegroundColor Gray
} else {
    Write-Host "  [skip] Log Analytics workspace $LogWorkspace already exists" -ForegroundColor DarkGray
}

$LogWorkspaceId = (az monitor log-analytics workspace show `
    --resource-group $ResourceGroup `
    --workspace-name $LogWorkspace `
    --query id -o tsv)

Invoke-EnsureResource -Label "Application Insights $AppInsights" `
    -ShowArgs @('monitor','app-insights','component','show','--resource-group',$ResourceGroup,'--app',$AppInsights) `
    -CreateBlock {
        az monitor app-insights component create `
            --resource-group $ResourceGroup `
            --app $AppInsights `
            --location $Location `
            --workspace $LogWorkspaceId `
            --output none
    }

$AppInsightsConnStr = (az monitor app-insights component show `
    --resource-group $ResourceGroup `
    --app $AppInsights `
    --query connectionString -o tsv)

# =============================================================
# 9. FUNCTION APP -- MAIN (6 agents)
# =============================================================

Write-Host "`n=== Function App: $FuncAppMain ===" -ForegroundColor Cyan
Invoke-EnsureResource -Label "Function App $FuncAppMain" `
    -ShowArgs @('functionapp','show','--resource-group',$ResourceGroup,'--name',$FuncAppMain) `
    -CreateBlock {
        az functionapp create `
            --resource-group $ResourceGroup `
            --name $FuncAppMain `
            --consumption-plan-location $Location `
            --storage-account $StorageAccount `
            --runtime dotnet-isolated `
            --runtime-version 8 `
            --functions-version 4 `
            --os-type Linux `
            --app-insights $AppInsights `
            --output none
    }

Write-Host "  Configuring app settings..." -ForegroundColor Gray
Set-FunctionAppAppSettings -AppName $FuncAppMain -Settings @{
    SqlConnectionString                   = $SqlConnectionString
    ServiceBusConnection                  = $ServiceBusConnStr
    AzureSignalRConnectionString          = $SignalRConnStr
    GoogleMapsApiKey                      = $GoogleMapsApiKey
    SendGridApiKey                        = $SendGridApiKey
    SendGridFromEmail                     = $SendGridFromEmail
    SendGridFromName                      = $SendGridFromName
    JwtSecretKey                          = $JwtSecretKey
    JwtIssuer                             = $JwtIssuer
    JwtAudience                           = $JwtAudience
    FunctionHostUrl                       = $PublicUrl
    APPLICATIONINSIGHTS_CONNECTION_STRING = $AppInsightsConnStr
    ASPNETCORE_ENVIRONMENT                = "Production"
}

# Explicitly re-pin the isolated runtime after appsettings set; some Azure CLI
# versions drop FUNCTIONS_WORKER_RUNTIME when you bulk-upsert settings.
Set-FunctionAppIsolatedRuntime -AppName $FuncAppMain

# Enable CORS for the frontend (idempotent -- `cors add` silently dedups
# anyway, but we guard explicitly so reruns don't grow the list).
function Add-FunctionAppCorsOrigin {
    param([string]$AppName, [string]$Origin)
    $existing = (az functionapp cors show --resource-group $ResourceGroup --name $AppName --query "allowedOrigins" -o tsv 2>$null) -split "`t"
    if ($existing -contains $Origin) {
        Write-Host "  [skip] CORS $Origin already allowed on $AppName" -ForegroundColor DarkGray
        return
    }
    az functionapp cors add --resource-group $ResourceGroup --name $AppName --allowed-origins $Origin --output none
    Assert-AzSuccess "CORS add $Origin on $AppName"
}
Add-FunctionAppCorsOrigin -AppName $FuncAppMain -Origin $PublicUrl

# =============================================================
# 10. FUNCTION APP -- AUTH (Identity / JWT)
# =============================================================

Write-Host "`n=== Function App: $FuncAppAuth ===" -ForegroundColor Cyan
Invoke-EnsureResource -Label "Function App $FuncAppAuth" `
    -ShowArgs @('functionapp','show','--resource-group',$ResourceGroup,'--name',$FuncAppAuth) `
    -CreateBlock {
        az functionapp create `
            --resource-group $ResourceGroup `
            --name $FuncAppAuth `
            --consumption-plan-location $Location `
            --storage-account $StorageAccount `
            --runtime dotnet-isolated `
            --runtime-version 8 `
            --functions-version 4 `
            --os-type Linux `
            --app-insights $AppInsights `
            --output none
    }

Write-Host "  Configuring app settings..." -ForegroundColor Gray
Set-FunctionAppAppSettings -AppName $FuncAppAuth -Settings @{
    SqlConnectionString                   = $SqlConnectionString
    JwtSecretKey                          = $JwtSecretKey
    JwtIssuer                             = $JwtIssuer
    JwtAudience                           = $JwtAudience
    JwtExpiryMinutes                      = $JwtExpiryMinutes
    JwtRefreshExpiryDays                  = $JwtRefreshDays
    SeedAdminEmail                        = $SeedAdminEmail
    SeedAdminPassword                     = $SeedAdminPassword
    SendGridApiKey                        = $SendGridApiKey
    SendGridFromEmail                     = $SendGridFromEmail
    SendGridFromName                      = $SendGridFromName
    APPLICATIONINSIGHTS_CONNECTION_STRING = $AppInsightsConnStr
    ASPNETCORE_ENVIRONMENT                = "Production"
}

Set-FunctionAppIsolatedRuntime -AppName $FuncAppAuth

Add-FunctionAppCorsOrigin -AppName $FuncAppAuth -Origin $PublicUrl

# =============================================================
# 11. FRONTEND STORAGE (Static Website)
# =============================================================

Write-Host "`n=== Frontend Storage: $FrontendStorage ===" -ForegroundColor Cyan
Invoke-EnsureResource -Label "Frontend storage $FrontendStorage" `
    -ShowArgs @('storage','account','show','--resource-group',$ResourceGroup,'--name',$FrontendStorage) `
    -CreateBlock {
        az storage account create `
            --resource-group $ResourceGroup `
            --name $FrontendStorage `
            --location $Location `
            --sku Standard_LRS `
            --kind StorageV2 `
            --min-tls-version TLS1_2 `
            --output none
    }

# Static website config is an upsert -- safe to call every run.
az storage blob service-properties update `
    --account-name $FrontendStorage `
    --static-website `
    --index-document "index.html" `
    --404-document "index.html" `
    --output none

$FrontendOrigin = (az storage account show `
    --resource-group $ResourceGroup `
    --name $FrontendStorage `
    --query "primaryEndpoints.web" -o tsv).TrimEnd('/')

# =============================================================
# 12. AZURE FRONT DOOR (SSL + Custom Domain + Routing)
# =============================================================

Write-Host "`n=== Front Door: $FrontDoorName ===" -ForegroundColor Cyan

# Create Front Door profile (Standard tier for managed SSL)
Invoke-EnsureResource -Label "Front Door profile $FrontDoorProfile" `
    -ShowArgs @('afd','profile','show','--resource-group',$ResourceGroup,'--profile-name',$FrontDoorProfile) `
    -CreateBlock {
        az afd profile create `
            --resource-group $ResourceGroup `
            --profile-name $FrontDoorProfile `
            --sku Standard_AzureFrontDoor `
            --output none
    }

# ----- Endpoint -----
Invoke-EnsureResource -Label "Front Door endpoint $FrontDoorName" `
    -ShowArgs @('afd','endpoint','show','--resource-group',$ResourceGroup,'--profile-name',$FrontDoorProfile,'--endpoint-name',$FrontDoorName) `
    -CreateBlock {
        az afd endpoint create `
            --resource-group $ResourceGroup `
            --profile-name $FrontDoorProfile `
            --endpoint-name $FrontDoorName `
            --enabled-state Enabled `
            --output none
    }

$FrontDoorHostname = (az afd endpoint show `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --endpoint-name $FrontDoorName `
    --query hostName -o tsv)

# ----- Origin Groups + Origins -----

$FrontendHost = ($FrontendOrigin -replace "https://", "")

# Declarative definition -- every origin group uses the same probe config.
$originGroups = @(
    @{ Name = "og-frontend";       Host = $FrontendHost;                       OriginName = "origin-frontend" },
    @{ Name = "og-functions-main"; Host = "$FuncAppMain.azurewebsites.net";    OriginName = "origin-func-main" },
    @{ Name = "og-functions-auth"; Host = "$FuncAppAuth.azurewebsites.net";    OriginName = "origin-func-auth" }
)

foreach ($og in $originGroups) {
    # Capture loop variable by value for the closure -- PowerShell scriptblocks
    # referenced from Invoke-EnsureResource otherwise close over the iterator
    # and every create call would use the last group's values.
    $ogName   = $og.Name
    $ogHost   = $og.Host
    $ogOrigin = $og.OriginName

    Invoke-EnsureResource -Label "AFD origin group $ogName" `
        -ShowArgs @('afd','origin-group','show','--resource-group',$ResourceGroup,'--profile-name',$FrontDoorProfile,'--origin-group-name',$ogName) `
        -CreateBlock {
            az afd origin-group create `
                --resource-group $ResourceGroup `
                --profile-name $FrontDoorProfile `
                --origin-group-name $ogName `
                --probe-request-type GET `
                --probe-protocol Https `
                --probe-path "/" `
                --probe-interval-in-seconds 120 `
                --sample-size 4 `
                --successful-samples-required 3 `
                --output none
        }.GetNewClosure()

    Invoke-EnsureResource -Label "AFD origin $ogOrigin -> $ogHost" `
        -ShowArgs @('afd','origin','show','--resource-group',$ResourceGroup,'--profile-name',$FrontDoorProfile,'--origin-group-name',$ogName,'--origin-name',$ogOrigin) `
        -CreateBlock {
            az afd origin create `
                --resource-group $ResourceGroup `
                --profile-name $FrontDoorProfile `
                --origin-group-name $ogName `
                --origin-name $ogOrigin `
                --host-name $ogHost `
                --origin-host-header $ogHost `
                --http-port 80 `
                --https-port 443 `
                --priority 1 `
                --weight 1000 `
                --enabled-state Enabled `
                --output none
        }.GetNewClosure()
}

# ----- Routes (most specific first, all linked to default domain) -----
#
# Azure Front Door Standard only accepts patterns ending with `/*` (or a bare
# `/*` catch-all). Exact paths ("/api/warehouses") and segment-stars
# ("/api/warehouses*") both return BadRequest. This means a collection
# endpoint has to be called with a trailing slash ("/api/warehouses/") to
# match /api/warehouses/*; the frontend does this explicitly in apiService.ts
# for /api/{companies,hubs,warehouses,users} which live in the auth app.
# Without the trailing slash the request falls through to /api/* -> the main
# Function App, which doesn't own those endpoints, and returns 404.

$routeDefs = @(
    @{ Name="route-signalr";     Group="og-functions-main"; Patterns=@("/api/negotiate/*") },
    @{ Name="route-auth";        Group="og-functions-auth"; Patterns=@("/api/auth/*") },
    @{ Name="route-users";       Group="og-functions-auth"; Patterns=@("/api/users/*") },
    @{ Name="route-companies";   Group="og-functions-auth"; Patterns=@("/api/companies/*") },
    @{ Name="route-warehouses";  Group="og-functions-auth"; Patterns=@("/api/warehouses/*") },
    @{ Name="route-hubs";        Group="og-functions-auth"; Patterns=@("/api/hubs/*") },
    @{ Name="route-api";         Group="og-functions-main"; Patterns=@("/api/*") },
    @{ Name="route-frontend";    Group="og-frontend";       Patterns=@("/*") }
)

foreach ($r in $routeDefs) {
    $rName     = $r.Name
    $rGroup    = $r.Group
    $rPatterns = $r.Patterns

    Invoke-EnsureResource -Label "AFD route $rName -> $($rPatterns -join ', ')" `
        -ShowArgs @('afd','route','show','--resource-group',$ResourceGroup,'--profile-name',$FrontDoorProfile,'--endpoint-name',$FrontDoorName,'--route-name',$rName) `
        -CreateBlock {
            $createArgs = @(
                'afd','route','create',
                '--resource-group', $ResourceGroup,
                '--profile-name',   $FrontDoorProfile,
                '--endpoint-name',  $FrontDoorName,
                '--route-name',     $rName,
                '--origin-group',   $rGroup,
                '--patterns-to-match'
            ) + $rPatterns + @(
                '--supported-protocols',  'Https','Http',
                '--forwarding-protocol',  'HttpsOnly',
                '--link-to-default-domain','Enabled',
                '--output','none'
            )
            & az @createArgs
        }.GetNewClosure()
}

# ----- Custom Domain + Managed SSL Certificate -----

Write-Host "`n=== Custom domain: $CustomDomain ===" -ForegroundColor Cyan
Invoke-EnsureResource -Label "Custom domain www-atlasdeliver" `
    -ShowArgs @('afd','custom-domain','show','--resource-group',$ResourceGroup,'--profile-name',$FrontDoorProfile,'--custom-domain-name','www-atlasdeliver') `
    -CreateBlock {
        az afd custom-domain create `
            --resource-group $ResourceGroup `
            --profile-name $FrontDoorProfile `
            --custom-domain-name "www-atlasdeliver" `
            --host-name $CustomDomain `
            --certificate-type ManagedCertificate `
            --minimum-tls-version TLS12 `
            --output none
    }

# Attach the custom domain to EVERY route by delete+recreate.
#
# Why not just `az afd route update --custom-domains`? Because that call is a
# control-plane-only change: it updates the Cosmos record but never triggers
# the data-plane republication, so the edge POPs keep serving only the route
# that HAD the custom domain attached. Symptom: every www.atlasdeliver.com
# request falls through to the /* catch-all (the frontend) regardless of
# path, and /api/auth/login returns Azure Storage's 405 UnsupportedHttpVerb.
#
# Recreating with --custom-domains at create time triggers a full data-plane
# rebuild of the custom domain's routing table, which makes path specificity
# (/api/auth/* beats /*) actually take effect.
foreach ($r in $routeDefs) {
    $rName     = $r.Name
    $rGroup    = $r.Group
    $rPatterns = $r.Patterns

    # Check (a) custom domain is attached and (b) the patterns on the live
    # route match our definition. If a pattern was added/changed in the
    # $routeDefs table since the original deploy, recreate so Azure picks it
    # up -- `az afd route update --patterns-to-match` has the same
    # control-plane-only caveat as --custom-domains (no data-plane rebuild).
    $existing = az afd route show `
        --resource-group $ResourceGroup `
        --profile-name $FrontDoorProfile `
        --endpoint-name $FrontDoorName `
        --route-name $rName `
        --query "{domains:length(customDomains), patterns:patternsToMatch}" -o json 2>$null | ConvertFrom-Json
    $attached = ($existing -and $existing.domains -gt 0)
    $patternsMatch = $false
    if ($existing -and $existing.patterns) {
        $live = @($existing.patterns | Sort-Object)
        $want = @($rPatterns | Sort-Object)
        $patternsMatch = (($live -join '|') -eq ($want -join '|'))
    }
    if ($attached -and $patternsMatch) {
        Write-Host "  [skip] $rName already attached + patterns match" -ForegroundColor DarkGray
        continue
    }

    Write-Host "  [recreate] $rName -> $($rPatterns -join ', ') (with custom domain)" -ForegroundColor Gray
    az afd route delete `
        --resource-group $ResourceGroup `
        --profile-name $FrontDoorProfile `
        --endpoint-name $FrontDoorName `
        --route-name $rName `
        --yes --output none 2>$null

    $recreateArgs = @(
        'afd','route','create',
        '--resource-group', $ResourceGroup,
        '--profile-name',   $FrontDoorProfile,
        '--endpoint-name',  $FrontDoorName,
        '--route-name',     $rName,
        '--origin-group',   $rGroup,
        '--patterns-to-match'
    ) + $rPatterns + @(
        '--supported-protocols',  'Https','Http',
        '--forwarding-protocol',  'HttpsOnly',
        '--link-to-default-domain','Enabled',
        '--custom-domains',       'www-atlasdeliver',
        '--output','none'
    )
    & az @recreateArgs
    Assert-AzSuccess "Route $rName recreate with custom-domain"
}

# Get the validation token for DNS
$DomainValidation = (az afd custom-domain show `
    --resource-group $ResourceGroup `
    --profile-name $FrontDoorProfile `
    --custom-domain-name "www-atlasdeliver" `
    --query "validationProperties.validationToken" -o tsv)

# ----- Pause: wait for DNS records to be set -----
Write-Host ""
Write-Host "=============================================================" -ForegroundColor Yellow
Write-Host "  ACTION REQUIRED - Add DNS records in GoDaddy" -ForegroundColor Yellow
Write-Host "=============================================================" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Go to GoDaddy DNS Management for atlasdeliver.com and add:" -ForegroundColor White
Write-Host ""
Write-Host "  Record 1:" -ForegroundColor Cyan
Write-Host "    Type:  CNAME"
Write-Host "    Name:  www"
Write-Host "    Value: $FrontDoorHostname"
Write-Host ""
Write-Host "  Record 2:" -ForegroundColor Cyan
Write-Host "    Type:  TXT"
Write-Host "    Name:  _dnsauth.www"
Write-Host "    Value: $DomainValidation"
Write-Host ""
Write-Host "  After adding both records, wait 1-2 minutes for propagation." -ForegroundColor Gray
Write-Host ""

if ($SkipDnsWait) {
    Write-Host "  -SkipDnsWait passed; continuing without waiting for DNS." -ForegroundColor Gray
} else {
    Read-Host "  Press ENTER when DNS records are set (or Ctrl+C to abort)"
}

# Verify DNS propagation
Write-Host "`n  Checking DNS propagation..." -ForegroundColor Gray
$retries = 12
$validated = $false
while ($retries -gt 0 -and -not $validated) {
    $status = (az afd custom-domain show `
        --resource-group $ResourceGroup `
        --profile-name $FrontDoorProfile `
        --custom-domain-name "www-atlasdeliver" `
        --query "domainValidationState" -o tsv 2>$null)
    if ($status -eq "Approved") {
        $validated = $true
    } else {
        Write-Host "  Validation state: $status - waiting 30s..." -ForegroundColor Gray
        Start-Sleep 30
        $retries--
    }
}

if ($validated) {
    Write-Host "  DNS validated! SSL certificate is provisioning." -ForegroundColor Green
} else {
    Write-Host "  DNS not yet validated. SSL will provision automatically once records propagate." -ForegroundColor Yellow
    Write-Host "  Continuing with deployment..." -ForegroundColor Gray
}

# =============================================================
# 13a. RUN EF MIGRATIONS
# =============================================================

Write-Host "`n=== Running EF Migrations ===" -ForegroundColor Cyan
# ProjectRoot is the repo root (parent of scripts/) — everything beyond
# this point assumes paths like "Atheres.Atlas.Functions" are siblings of it.
$ProjectRoot = $_repoRoot

# Both startup projects' Program.cs validate required env vars during host
# build. `dotnet ef` spins the host up just far enough to resolve the
# DbContext, so without these set the migration command throws
# `InvalidOperationException: ServiceBusConnection is required.` before any
# SQL runs. Set them explicitly; the Azure SQL connection is passed per
# command via `--connection`.
$env:SqlConnectionString           = $SqlConnectionString
$env:ServiceBusConnection          = $ServiceBusConnStr
$env:AzureSignalRConnectionString  = $SignalRConnStr
$env:GoogleMapsApiKey              = $GoogleMapsApiKey
$env:SendGridApiKey                = $SendGridApiKey
$env:SendGridFromEmail             = $SendGridFromEmail
$env:SendGridFromName              = $SendGridFromName
$env:JwtSecretKey                  = $JwtSecretKey
$env:JwtIssuer                     = $JwtIssuer
$env:JwtAudience                   = $JwtAudience

Push-Location $ProjectRoot
try {
    dotnet ef database update `
        --context AtlasDbContext `
        --startup-project Atheres.Atlas.Functions `
        --project Atheres.Atlas.Data `
        --connection $SqlConnectionString
    if ($LASTEXITCODE -ne 0) { throw "AtlasDbContext migration failed." }

    # Identity context lives in Atheres.Atlas.Auth.Functions -- using the main
    # Functions project as the startup would fail DI resolution.
    dotnet ef database update `
        --context AtlasIdentityDbContext `
        --startup-project Atheres.Atlas.Auth.Functions `
        --project Atheres.Atlas.Data `
        --connection $SqlConnectionString
    if ($LASTEXITCODE -ne 0) { throw "AtlasIdentityDbContext migration failed." }
}
finally {
    Pop-Location
}

# =============================================================
# 13. DEPLOY CODE (Functions + Frontends)
# =============================================================
# Auth Functions must be published + warm BEFORE any reference-data import,
# because its startup seed creates the Companies rows that every imported
# hub/warehouse/store/truck FKs into.
# =============================================================

Write-Host "`n=== Publishing Auth Functions ===" -ForegroundColor Cyan
Push-Location (Join-Path $ProjectRoot "Atheres.Atlas.Auth.Functions")
func azure functionapp publish $FuncAppAuth --dotnet-isolated
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "Auth Functions publish failed." }
Pop-Location
# Restart nudges the isolated worker to reload the newly-uploaded package.
# Without it we've seen the site keep serving the previous (sometimes empty)
# build until something else triggers a cold start.
az functionapp restart --resource-group $ResourceGroup --name $FuncAppAuth --output none
Assert-AzSuccess "$FuncAppAuth restart"
Assert-FunctionAppIndexed -AppName $FuncAppAuth -ExpectedAny @(
    'auth-login','auth-register','auth-refresh','auth-logout','auth-me'
)

Write-Host "`n=== Publishing Main Functions ===" -ForegroundColor Cyan
Push-Location (Join-Path $ProjectRoot "Atheres.Atlas.Functions")
func azure functionapp publish $FuncAppMain --dotnet-isolated
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "Main Functions publish failed." }
Pop-Location
az functionapp restart --resource-group $ResourceGroup --name $FuncAppMain --output none
Assert-AzSuccess "$FuncAppMain restart"
Assert-FunctionAppIndexed -AppName $FuncAppMain -ExpectedAny @(
    'IngestOrders','Negotiate','query-orders-list','query-routes-optimize'
)

# Wait for Auth Functions to cold-start and run its startup seed. Poll /api/auth/me
# -- the middleware returns 401 as soon as the host is up, which is our signal.
Write-Host "`n=== Waiting for Auth Functions to be ready (startup seed) ===" -ForegroundColor Cyan
$authHealth = "https://$FuncAppAuth.azurewebsites.net/api/auth/me"
$authReady = $false
$retries = 60   # 60 x 10s = 10 minutes (cold start + seed can be slow)
while ($retries -gt 0 -and -not $authReady) {
    $code = 0
    try {
        $resp = Invoke-WebRequest -Uri $authHealth -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
        $code = [int]$resp.StatusCode
    } catch {
        # Socket errors (DNS not up, cold start) have no Response; HTTP errors
        # (401/404) do. Probe defensively rather than chaining properties --
        # Set-StrictMode throws on missing members.
        $resp = $null
        try { $resp = $_.Exception.Response } catch { }
        if ($resp) {
            try { $code = [int]$resp.StatusCode } catch { $code = 0 }
        }
    }
    if ($code -in 200, 401, 403, 404) {
        $authReady = $true
    } else {
        Start-Sleep 10
        Write-Host "." -NoNewline
        $retries--
    }
}
Write-Host ""
if (-not $authReady) {
    throw "Auth Functions did not respond at $authHealth within 10 minutes. Check Function App logs and rerun."
}
Write-Host "  Auth Functions is responding; startup seed has populated Companies + Users." -ForegroundColor Green

# ---- Nuke order + route data ------------------------------------------------
# Every deploy starts with zero orders, routes, batches, and confirmations so
# iterative end-to-end testing (CSV import → schedule → optimize) doesn't
# accumulate carry-over rows between runs. Master data (stores, warehouses,
# hubs, trucks, districts, zones, users) is preserved — only the
# transactional shell is wiped (see scripts/sql/nuke-route-data.sql). The
# LocalDev firewall rule provisioned earlier in this script grants this
# host's IP access to the Azure SQL server.
Write-Host "`n=== Nuking order + route data ===" -ForegroundColor Cyan
$nukeSqlPath = Join-Path $PSScriptRoot "sql/nuke-route-data.sql"
if (Test-Path $nukeSqlPath) {
    Add-Type -AssemblyName System.Data -ErrorAction SilentlyContinue
    $nukeConn = New-Object System.Data.SqlClient.SqlConnection $SqlConnectionString
    try {
        $nukeConn.Open()
        $nukeCmd = $nukeConn.CreateCommand()
        $nukeCmd.CommandText    = Get-Content $nukeSqlPath -Raw
        $nukeCmd.CommandTimeout = 60
        $nukeCmd.ExecuteNonQuery() | Out-Null
        Write-Host "  [OK] Orders, Routes, RouteStops, OrderBatches, Confirmations wiped." -ForegroundColor Green
    }
    catch {
        # Don't fail the deploy on a wipe error -- a fresh DB has nothing to wipe.
        Write-Host "  [warn] Order + route data wipe failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }
    finally {
        $nukeConn.Close()
    }
} else {
    Write-Host "  [warn] nuke-route-data.sql not found at $nukeSqlPath -- skipping wipe." -ForegroundColor Yellow
}

# Import reference data (Stores, Warehouses, Hubs, Vans) -- safe now that the
# Secure Transport company row exists.
Write-Host "`n=== Importing Reference Data ===" -ForegroundColor Cyan
$importScript = Join-Path $PSScriptRoot "import-data.ps1"
if (Test-Path $importScript) {
    pwsh -File $importScript -ConnectionString $SqlConnectionString
    if ($LASTEXITCODE -ne 0) { throw "import-data.ps1 failed (exit $LASTEXITCODE)." }
}

# ---- Re-run the Auth seed now that warehouses exist ----------------------
# SeedWarehouseImportersAsync (Atheres.Atlas.Auth.Functions/Program.cs) creates
# one OrderImporter account per warehouse for Secure Transport. It runs from
# the Auth Functions startup seed, but on the first deploy the seed runs
# BEFORE import-data.ps1 has loaded any warehouses, so it logs
# "no warehouses for company ... skipping OrderImporter seed" and exits. The
# bootstrap users (ken/secure/demo/...) get created on that first pass; the
# data importers do not. Restart the Auth Functions so the seed runs again
# with warehouses present, then poll AspNetUsers to confirm the importers
# actually landed.
Write-Host "`n=== Re-running Auth seed for warehouse OrderImporter users ===" -ForegroundColor Cyan
az functionapp restart --resource-group $ResourceGroup --name $FuncAppAuth --output none
Assert-AzSuccess "$FuncAppAuth restart for importer seed"

# System.Data.SqlClient ships with .NET but isn't auto-loaded under pwsh.
# Force-load it once before the polling loop instantiates SqlConnection.
Add-Type -AssemblyName System.Data -ErrorAction SilentlyContinue

# Expected importer count = min(active Secure Transport warehouses, 5). Read
# the actual warehouse count from the DB so we don't have to hard-code the
# slugified emails -- those depend on whatever import-data.ps1 just loaded.
function Get-ImporterSeedStatus {
    param([Parameter(Mandatory)][string]$ConnectionString)
    $conn = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    try {
        $conn.Open()

        # Warehouses.CompanyId was dropped in 20260524000002_StoresWarehousesMultiCompany;
        # the company link now lives in the WarehouseCompanies join table.
        $whCmd = $conn.CreateCommand()
        $whCmd.CommandText = @"
SELECT COUNT(*) FROM (
    SELECT TOP (5) w.Id
    FROM   Warehouses w
    INNER JOIN WarehouseCompanies wc ON wc.WarehouseId = w.Id
    WHERE  wc.CompanyId = '10000000-0000-0000-0000-000000000001'
      AND  w.IsActive = 1
    ORDER BY w.BusinessName
) x
"@
        $expected = [int]$whCmd.ExecuteScalar()

        # SQL LIKE: underscore is a single-char wildcard. Bracket-escape it
        # ('[_]') so we match the literal "data_" prefix and not "dataX".
        $userCmd = $conn.CreateCommand()
        $userCmd.CommandText = "SELECT COUNT(*) FROM AspNetUsers WHERE NormalizedEmail LIKE 'DATA[_]%@SECURETRANSPORT.COM'"
        $actual = [int]$userCmd.ExecuteScalar()

        return [pscustomobject]@{ Expected = $expected; Actual = $actual }
    } finally {
        $conn.Close()
    }
}

$importerAttempts = 3
$importerOk       = $false
for ($attempt = 1; $attempt -le $importerAttempts -and -not $importerOk; $attempt++) {
    $deadline = (Get-Date).AddMinutes(5)
    $status   = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $status = Get-ImporterSeedStatus -ConnectionString $SqlConnectionString
        } catch {
            Write-Host "`n  [warn] SQL probe failed: $($_.Exception.Message)" -ForegroundColor Yellow
            Start-Sleep 10
            continue
        }
        if ($status.Expected -eq 0) {
            # No warehouses to seed against -- import-data.ps1 didn't load any.
            # Surface this loudly rather than waiting silently for nothing.
            throw "No active Secure Transport warehouses in DB after import-data.ps1; importer seed cannot run. Check the import script's output."
        }
        if ($status.Actual -ge $status.Expected) { $importerOk = $true; break }
        Start-Sleep 10
        Write-Host "." -NoNewline -ForegroundColor DarkGray
    }
    Write-Host ""
    if ($importerOk) {
        Write-Host "  [OK] $($status.Actual)/$($status.Expected) OrderImporter users present." -ForegroundColor Green
        break
    }
    # $status stays $null when every probe in the window threw (e.g. schema
    # drift like a renamed/dropped column). Render "?/?" instead of crashing
    # on a missing property so the underlying SQL warning above stays visible.
    $actualText   = if ($status) { $status.Actual }   else { '?' }
    $expectedText = if ($status) { $status.Expected } else { '?' }
    if ($attempt -lt $importerAttempts) {
        Write-Host "  [retry $attempt/$importerAttempts] Only $actualText/$expectedText importer users present. Restarting $FuncAppAuth..." -ForegroundColor Yellow
        az functionapp restart --resource-group $ResourceGroup --name $FuncAppAuth --output none
        Assert-AzSuccess "$FuncAppAuth restart for importer seed retry"
        Start-Sleep 20
    } else {
        throw @"
OrderImporter users still missing after $importerAttempts restart attempts ($actualText/$expectedText).
The Auth Functions startup seed is not creating data_*@securetransport.com accounts.
Inspect logs:
  az webapp log tail -g $ResourceGroup -n $FuncAppAuth
Then rerun .\deploy-azure.ps1 -- the seed is idempotent and will only add what's missing.
"@
    }
}

# Build + upload the main Frontend. Front Door proxies /api/* on $PublicUrl to
# the Function Apps, so the build can use a relative API base.
Write-Host "`n=== Building + uploading Frontend ===" -ForegroundColor Cyan
$feDir = Join-Path $ProjectRoot "Atheres.Atlas.FrontEnd"
Push-Location $feDir
try {
    if (-not (Test-Path "node_modules")) { npm ci; if ($LASTEXITCODE -ne 0) { throw "npm ci (frontend) failed." } }
    $env:VITE_API_BASE_URL    = ""            # relative /api hits Front Door
    $env:VITE_GOOGLE_MAPS_KEY = $GoogleMapsApiKey
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "Frontend build failed." }
    az storage blob upload-batch --source dist --destination '$web' --account-name $FrontendStorage --overwrite --output none
    Assert-AzSuccess "Frontend blob upload"
}
finally {
    Remove-Item Env:\VITE_API_BASE_URL, Env:\VITE_GOOGLE_MAPS_KEY -ErrorAction SilentlyContinue
    Pop-Location
}

# =============================================================
# 14. SUMMARY
# =============================================================

$deployStopwatch.Stop()

# If the custom domain hasn't validated yet, fall back to the Front Door
# endpoint so the URLs we print + open are actually reachable now.
$mainAppUrl = $PublicUrl
try {
    Invoke-WebRequest -Uri $mainAppUrl -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop | Out-Null
} catch {
    $mainAppUrl = "https://$FrontDoorHostname"
}
# ---- Append the Azure URLs + users to CREDENTIALS.txt --------
$credsFile = Join-Path $PSScriptRoot "CREDENTIALS.txt"
$bootstrapLine = if ([string]::IsNullOrWhiteSpace($SeedAdminPassword)) {
    "  SuperAdmin      charles.murphy@atheres.com     (not seeded)           (all companies)"
} else {
    "  SuperAdmin      charles.murphy@atheres.com     $SeedAdminPassword    (all companies)"
}
$azureBlock = @"

================================================================================
  ATLAS DELIVER -- AZURE DEPLOYMENT ($(Get-Date -Format "yyyy-MM-dd HH:mm"))
================================================================================
  NOTE: This file contains cleartext passwords for the seeded dev accounts.
  Ensure it stays in the git-ignored paths and is not shared.
================================================================================

  Main Application:  $mainAppUrl
  Swagger UI:        https://$FuncAppMain.azurewebsites.net/api/swagger

--------------------------------------------------------------------------------
  USERS (seeded by Auth Functions startup)
--------------------------------------------------------------------------------

  Role            Email                          Password                Company
  --------------- ------------------------------ ---------------------- ------------------
$bootstrapLine
  SuperAdmin      ken@atheres.com                Phone@3313059708       (all companies)
  Admin           secure@gmail.com               Secure@1234567890      Secure Transport
  Driver          secureuser@gmail.com           Secure@1234567890      Secure Transport

--------------------------------------------------------------------------------
  API ENDPOINTS
--------------------------------------------------------------------------------

  Main Functions API: https://$FuncAppMain.azurewebsites.net/api
  Auth Functions API: https://$FuncAppAuth.azurewebsites.net/api

--------------------------------------------------------------------------------
  INFRASTRUCTURE
--------------------------------------------------------------------------------

  Resource Group:  $ResourceGroup
  SQL Server:      $SqlServerName.database.windows.net  (DB: $SqlDbName)
  Storage:         $StorageAccount
  Service Bus:     $ServiceBusNs.servicebus.windows.net
  SignalR:         $SignalRName
  Front Door:      $FrontDoorHostname

================================================================================
"@
# Replace any previous Azure-deployment block so CREDENTIALS.txt doesn't
# accumulate a new copy every run. The marker "ATLAS DELIVER -- AZURE
# DEPLOYMENT" is written on every run, so we just strip anything from that
# header to EOF before appending the fresh block.
if (Test-Path $credsFile) {
    $current = Get-Content $credsFile -Raw
    $marker = 'ATLAS DELIVER -- AZURE DEPLOYMENT'
    $idx = $current.IndexOf($marker)
    if ($idx -ge 0) {
        # Walk back to the "=====" separator above the marker to preserve the
        # border lines belonging to the block.
        $borderStart = $current.LastIndexOf("================================================================================", $idx)
        if ($borderStart -lt 0) { $borderStart = $idx }
        $current = $current.Substring(0, $borderStart).TrimEnd() + "`r`n"
        Set-Content -Path $credsFile -Value $current -Encoding UTF8 -NoNewline
    }
}
Add-Content -Path $credsFile -Value $azureBlock -Encoding UTF8
Write-Host "`nRefreshed Azure deployment block in $credsFile" -ForegroundColor Gray

# ---- Console summary -----------------------------------------
Write-Host "`n" -NoNewline
Write-Host "=============================================================" -ForegroundColor Green
Write-Host "  DEPLOYMENT COMPLETE in " -NoNewline -ForegroundColor Green
Write-Host (Format-Duration $deployStopwatch) -ForegroundColor Cyan
Write-Host "=============================================================" -ForegroundColor Green

Write-Host "`nMain Application:    $mainAppUrl"
Write-Host "Swagger:             https://$FuncAppMain.azurewebsites.net/api/swagger"
Write-Host "Function App (Main): $FuncAppMain.azurewebsites.net"
Write-Host "Function App (Auth): $FuncAppAuth.azurewebsites.net"
Write-Host "Front Door:          $FrontDoorHostname"

if ($mainAppUrl -ne $PublicUrl) {
    Write-Host "`n(Custom domain $PublicUrl hasn't finished validating -- falling back to Front Door URL.)" -ForegroundColor Yellow
}

Write-Host "`nFollow-up task after the first successful deploy:" -ForegroundColor Gray
Write-Host "  az functionapp config appsettings set -g $ResourceGroup -n $FuncAppAuth --settings SeedAdminPassword=" -ForegroundColor Gray

# ---- Open the app in the default browser ---------------------
Write-Host "`nOpening application in browser..." -ForegroundColor Cyan
Start-Process $mainAppUrl
