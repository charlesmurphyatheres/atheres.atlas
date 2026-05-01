# =============================================================
# upgrade-azure.ps1 -- Republish application code after development changes.
#
# Unlike deploy-azure.ps1 which provisions infrastructure, this script assumes
# the Azure resources already exist and only redeploys:
#   * Auth Functions  (func-atlas-auth)
#   * Main Functions  (func-atlas-main)
#   * Frontend SPA    -> statlasfe0001 static website
#   * EF migrations   (opt-in: -Migrations)
#
# After publishing it restarts the Function Apps, waits for them to reindex,
# and purges the Front Door cache so the new bundles are served immediately.
#
# Usage:
#   .\upgrade-azure.ps1                           # everything except migrations
#   .\upgrade-azure.ps1 -Backend                  # only Function Apps
#   .\upgrade-azure.ps1 -Frontend                 # only the SPA
#   .\upgrade-azure.ps1 -Auth                     # only Auth Functions
#   .\upgrade-azure.ps1 -Main                     # only Main Functions
#   .\upgrade-azure.ps1 -Migrations               # everything + EF migrate
#   .\upgrade-azure.ps1 -SkipPurge                # skip Front Door cache purge
# =============================================================
[CmdletBinding()]
param(
    [switch]$Backend,     # publish both Function Apps
    [switch]$Auth,        # publish Auth Functions only
    [switch]$Main,        # publish Main Functions only
    [switch]$Frontend,    # build + upload main SPA only
    [switch]$Migrations,  # run `dotnet ef database update` for both contexts
    [switch]$SkipPurge    # don't call `az afd endpoint purge` after uploading SPAs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
trap { Write-Host "`n  Script failed: $($_.Exception.Message)" -ForegroundColor Red; break }

$upgradeStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

# ---- Static configuration (must match deploy-azure.ps1) ----------------------
$ProjectRoot         = $PSScriptRoot
$ResourceGroup       = "rg-atheres-atlas"
$FuncAppMain         = "func-atlas-main"
$FuncAppAuth         = "func-atlas-auth"
$FrontendStorage     = "statlasfe0001"
$FrontDoorName       = "fd-atheres-atlas"
$FrontDoorProfile    = "fdp-atheres-atlas"
$PublicUrl           = "https://www.atlasdeliver.com"
$CustomDomain        = "www.atlasdeliver.com"
$SqlServerName       = "atlas-sql-atheres"
$SqlDbName           = "AtheresAtlas"
$SqlAdminUser        = "atlas-admin"

# ---- Load .env ---------------------------------------------------------------
$envFile = Join-Path $ProjectRoot ".env"
$envVars = @{}
if (Test-Path $envFile) {
    Get-Content $envFile | Where-Object { $_ -match '^\s*[^#]' -and $_ -match '=' } | ForEach-Object {
        $p = $_ -split '=', 2; $envVars[$p[0].Trim()] = $p[1].Trim().Trim('"')
    }
}
function Get-RequiredEnv {
    param([string]$Key, [string]$Hint)
    $v = $envVars[$Key]
    if ([string]::IsNullOrWhiteSpace($v)) { throw "Missing $Key in .env ($Hint). Populate it and rerun." }
    return $v
}

# ---- Resolve target set ------------------------------------------------------
# If no flag is passed, "everything" means both Function Apps + the SPA.
# Migrations are opt-in because they can be destructive on a shared DB.
$noFlags = -not ($Backend -or $Auth -or $Main -or $Frontend -or $Migrations)
if ($noFlags) { $Backend = $true; $Frontend = $true }
if ($Backend) { $Auth = $true; $Main = $true }

# ---- Helpers -----------------------------------------------------------------
function Assert-AzSuccess {
    param([string]$Step)
    if ($LASTEXITCODE -ne 0) { throw "$Step failed (exit $LASTEXITCODE)." }
}

# Poll `az functionapp function list` until the worker registers user-defined
# functions (not just WarmUp). Aborts the upgrade if the app never indexes --
# otherwise the front-end would silently hit a broken API.
function Assert-FunctionAppIndexed {
    param(
        [Parameter(Mandatory)][string]$AppName,
        [string[]]$ExpectedAny = @(),
        [int]$TimeoutSeconds = 240
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $indexed = $null
    while ((Get-Date) -lt $deadline) {
        $raw = az functionapp function list -g $ResourceGroup -n $AppName -o json 2>$null
        if ($LASTEXITCODE -eq 0 -and $raw) {
            try {
                $indexed = $raw | ConvertFrom-Json
                $names = @($indexed | ForEach-Object { $_.name -replace '^.+/', '' })
                $userFns = @($names | Where-Object { $_ -ne 'WarmUp' })
                if ($ExpectedAny.Count -eq 0 -and $userFns.Count -gt 0) {
                    Write-Host "  [ok] $AppName indexed $($userFns.Count) function(s)." -ForegroundColor Green
                    return
                }
                $matched = @($names | Where-Object { $ExpectedAny -contains $_ })
                if ($matched.Count -gt 0) {
                    Write-Host "  [ok] $AppName indexed $($userFns.Count) function(s)." -ForegroundColor Green
                    return
                }
            } catch { }
        }
        Start-Sleep 10
        Write-Host "." -NoNewline -ForegroundColor DarkGray
    }
    Write-Host ""
    $current = if ($indexed) { (@($indexed | ForEach-Object { $_.name -replace '^.+/', '' })) -join ', ' } else { '<list call failed>' }
    throw "$AppName has no user functions indexed after ${TimeoutSeconds}s. Currently indexed: $current"
}

function Assert-FunctionAppExists {
    param([string]$AppName)
    $null = az functionapp show -g $ResourceGroup -n $AppName --query id -o tsv 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Function App '$AppName' not found in '$ResourceGroup'. Run deploy-azure.ps1 first to provision."
    }
}

# =============================================================
# 0. LOGIN
# =============================================================

Write-Host "`n=== Verifying Azure login ===" -ForegroundColor Cyan
$acct = az account show --query name -o tsv 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($acct)) {
    Write-Host "  Not logged in. Launching az login..." -ForegroundColor Yellow
    az login --only-show-errors | Out-Null
    Assert-AzSuccess "az login"
    $acct = az account show --query name -o tsv
}
Write-Host "  Subscription: $acct" -ForegroundColor Gray

# =============================================================
# 1. MIGRATIONS (opt-in)
# =============================================================

if ($Migrations) {
    Write-Host "`n=== Running EF Migrations ===" -ForegroundColor Cyan
    $SqlAdminPassword = Get-RequiredEnv "MSSQL_SA_PASSWORD" "SQL admin password"
    $SqlConnectionString = "Server=tcp:$SqlServerName.database.windows.net,1433;Database=$SqlDbName;User Id=$SqlAdminUser;Password=$SqlAdminPassword;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;"

    Push-Location $ProjectRoot
    try {
        dotnet ef database update `
            --context AtlasDbContext `
            --startup-project Atheres.Atlas.Functions `
            --project Atheres.Atlas.Data `
            --connection $SqlConnectionString
        if ($LASTEXITCODE -ne 0) { throw "AtlasDbContext migration failed." }

        dotnet ef database update `
            --context AtlasIdentityDbContext `
            --startup-project Atheres.Atlas.Auth.Functions `
            --project Atheres.Atlas.Data `
            --connection $SqlConnectionString
        if ($LASTEXITCODE -ne 0) { throw "AtlasIdentityDbContext migration failed." }
    }
    finally { Pop-Location }
}

# =============================================================
# 2. PUBLISH FUNCTION APPS
# =============================================================

if ($Auth) {
    Write-Host "`n=== Publishing Auth Functions ===" -ForegroundColor Cyan
    Assert-FunctionAppExists -AppName $FuncAppAuth
    Push-Location (Join-Path $ProjectRoot "Atheres.Atlas.Auth.Functions")
    try {
        func azure functionapp publish $FuncAppAuth --dotnet-isolated
        if ($LASTEXITCODE -ne 0) { throw "Auth Functions publish failed." }
    } finally { Pop-Location }

    az functionapp restart -g $ResourceGroup -n $FuncAppAuth --output none
    Assert-AzSuccess "$FuncAppAuth restart"
    Assert-FunctionAppIndexed -AppName $FuncAppAuth -ExpectedAny @(
        'auth-login','auth-register','auth-refresh','auth-logout','auth-me'
    )
}

if ($Main) {
    Write-Host "`n=== Publishing Main Functions ===" -ForegroundColor Cyan
    Assert-FunctionAppExists -AppName $FuncAppMain
    Push-Location (Join-Path $ProjectRoot "Atheres.Atlas.Functions")
    try {
        func azure functionapp publish $FuncAppMain --dotnet-isolated
        if ($LASTEXITCODE -ne 0) { throw "Main Functions publish failed." }
    } finally { Pop-Location }

    az functionapp restart -g $ResourceGroup -n $FuncAppMain --output none
    Assert-AzSuccess "$FuncAppMain restart"
    Assert-FunctionAppIndexed -AppName $FuncAppMain -ExpectedAny @(
        'IngestOrders','Negotiate','query-orders-list','OptimizeRoute'
    )
}

# =============================================================
# 3. BUILD + UPLOAD FRONTENDS
# =============================================================

if ($Frontend) {
    Write-Host "`n=== Building + uploading Frontend ===" -ForegroundColor Cyan
    $feDir = Join-Path $ProjectRoot "Atheres.Atlas.FrontEnd"
    if (-not (Test-Path $feDir)) { throw "Frontend directory not found: $feDir" }
    Push-Location $feDir
    try {
        if (-not (Test-Path "node_modules")) {
            npm ci
            if ($LASTEXITCODE -ne 0) { throw "npm ci (frontend) failed." }
        }
        $env:VITE_API_BASE_URL    = ""    # relative /api -> Front Door -> Function Apps
        $env:VITE_GOOGLE_MAPS_KEY = Get-RequiredEnv "GOOGLE_MAPS_API_KEY" "Google Maps JavaScript + Directions API key"
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "Frontend build failed." }
        az storage blob upload-batch `
            --source dist `
            --destination '$web' `
            --account-name $FrontendStorage `
            --overwrite `
            --output none
        Assert-AzSuccess "Frontend blob upload"
    }
    finally {
        Remove-Item Env:\VITE_API_BASE_URL, Env:\VITE_GOOGLE_MAPS_KEY -ErrorAction SilentlyContinue
        Pop-Location
    }
}

# =============================================================
# 4. PURGE FRONT DOOR CACHE
# =============================================================
# Without a purge, the edge can serve the previous bundle for up to the
# static-website cache TTL. A purge is cheap and avoids user-visible staleness.

if (-not $SkipPurge -and $Frontend) {
    Write-Host "`n=== Purging Front Door cache ===" -ForegroundColor Cyan
    $paths = @("/*")
    az afd endpoint purge `
        --resource-group $ResourceGroup `
        --profile-name $FrontDoorProfile `
        --endpoint-name $FrontDoorName `
        --content-paths $paths `
        --domains $CustomDomain `
        --output none
    Assert-AzSuccess "Front Door cache purge"
    Write-Host "  Purged: $($paths -join ', ') on $CustomDomain" -ForegroundColor Green
}

# =============================================================
# 5. SUMMARY
# =============================================================

$upgradeStopwatch.Stop()
$elapsed = "{0:mm}m {0:ss}s" -f $upgradeStopwatch.Elapsed

Write-Host "`n=============================================================" -ForegroundColor Green
Write-Host "  Upgrade complete ($elapsed)" -ForegroundColor Green
Write-Host "=============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Targets:"
if ($Auth)       { Write-Host "    - Auth Functions      -> $FuncAppAuth" }
if ($Main)       { Write-Host "    - Main Functions      -> $FuncAppMain" }
if ($Migrations) { Write-Host "    - EF Migrations       -> both contexts" }
if ($Frontend)   { Write-Host "    - Frontend SPA        -> $FrontendStorage" }
Write-Host ""
Write-Host "  URL:"
Write-Host "    Main:  $PublicUrl"
Write-Host ""
