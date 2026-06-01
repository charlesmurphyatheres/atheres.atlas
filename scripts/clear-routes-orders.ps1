#Requires -Version 7.0
# =============================================================
# Atheres Atlas — Clear all orders and routes from the database
#
# Wraps scripts/sql/nuke-route-data.sql in a PowerShell entry
# point so the same wipe deploy-debug.ps1 runs at the end of each
# debug deploy can be invoked on demand — handy for iterative
# end-to-end testing without having to spin up the full deploy
# pipeline.
#
# Scope (destructive, transactional — all-or-nothing):
#   * Orders, RouteStops, Confirmations, Routes, OrderBatches.
#
# Master/reference data (Companies, Stores, Warehouses, Hubs,
# Trucks, Districts, Zones, Users, etc.) is left intact.
#
# Usage:
#   .\clear-routes-orders.ps1                              # uses local.settings.json connection
#   .\clear-routes-orders.ps1 -ConnectionString "Server=..."  # custom DB
#   .\clear-routes-orders.ps1 -Force                       # skip the confirmation prompt
# =============================================================

[CmdletBinding()]
param(
    [string]$ConnectionString,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$sqlPath = Join-Path $PSScriptRoot "sql\nuke-route-data.sql"

function Write-Header { param($msg) Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Info   { param($msg) Write-Host "[INFO]  $msg"  -ForegroundColor Cyan }
function Write-Ok     { param($msg) Write-Host "[OK]    $msg"  -ForegroundColor Green }
function Write-Warn   { param($msg) Write-Host "[WARN]  $msg"  -ForegroundColor Yellow }
function Write-Err    { param($msg) Write-Host "[ERROR] $msg"  -ForegroundColor Red }

# ---- Resolve connection string ----------------------------------------------
# Same fallback chain as import-data.ps1: explicit param wins; otherwise read
# from Functions/local.settings.json. Bail out with a clear message if neither
# is available — the script does too much damage to silently target a default.
if (-not $ConnectionString) {
    $settingsPath = Join-Path $root "Atheres.Atlas.Functions\local.settings.json"
    if (Test-Path $settingsPath) {
        $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
        if ($settings.Values.PSObject.Properties.Name -contains 'SqlConnectionString' `
            -and $settings.Values.SqlConnectionString) {
            $ConnectionString = $settings.Values.SqlConnectionString
            Write-Info "Using connection from local.settings.json"
        }
    }
}
if (-not $ConnectionString) {
    Write-Err "No connection string provided and local.settings.json has no SqlConnectionString."
    Write-Err "Pass -ConnectionString or populate Atheres.Atlas.Functions\local.settings.json first."
    exit 1
}

if (-not (Test-Path $sqlPath)) {
    Write-Err "SQL script not found: $sqlPath"
    exit 1
}

# ---- Helpers --------------------------------------------------------------
function Invoke-SqlScalar {
    param([string]$Query, [string]$ConnStr)
    $conn = New-Object System.Data.SqlClient.SqlConnection($ConnStr)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $Query
        return $cmd.ExecuteScalar()
    } finally { $conn.Close() }
}

function Invoke-SqlNonQuery {
    param([string]$Query, [string]$ConnStr, [int]$Timeout = 120)
    $conn = New-Object System.Data.SqlClient.SqlConnection($ConnStr)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText    = $Query
        $cmd.CommandTimeout = $Timeout
        return $cmd.ExecuteNonQuery()
    } finally { $conn.Close() }
}

function Get-RowCount {
    param([string]$Table, [string]$ConnStr)
    try   { return [int](Invoke-SqlScalar "SELECT COUNT(*) FROM [$Table]" $ConnStr) }
    catch { return $null } # table may not exist on a partially-migrated DB
}

# ---- Pre-wipe snapshot ----------------------------------------------------
Write-Header "Pre-wipe row counts"
$tables = @('Orders','Routes','RouteStops','OrderBatches','Confirmations')
$before = @{}
foreach ($t in $tables) {
    $c = Get-RowCount -Table $t -ConnStr $ConnectionString
    $before[$t] = $c
    if ($null -eq $c) {
        Write-Warn ("  {0,-15} (table not found — skipped)" -f $t)
    } else {
        Write-Host ("  {0,-15} {1,8:n0}" -f $t, $c)
    }
}

$totalBefore = ($before.Values | Where-Object { $_ -ne $null } | Measure-Object -Sum).Sum
if (-not $totalBefore) { $totalBefore = 0 }
if ($totalBefore -eq 0) {
    Write-Info "All target tables are already empty — nothing to do."
    exit 0
}

# ---- Confirm ---------------------------------------------------------------
# Skip the prompt when -Force is set (handy for CI / scripted flows).
if (-not $Force) {
    $serverHint = ($ConnectionString -split ';' | Where-Object { $_ -match '^(Server|Data Source)=' } | Select-Object -First 1)
    $dbHint     = ($ConnectionString -split ';' | Where-Object { $_ -match '^(Initial Catalog|Database)=' } | Select-Object -First 1)
    Write-Host ""
    Write-Host "About to DELETE $totalBefore row(s) from Orders + RouteStops + Confirmations + Routes + OrderBatches" -ForegroundColor Yellow
    Write-Host "Target: $serverHint  $dbHint" -ForegroundColor Yellow
    $answer = Read-Host "Type 'yes' to proceed"
    if ($answer -ne 'yes') {
        Write-Warn "Cancelled — no rows deleted."
        exit 0
    }
}

# ---- Execute the wipe ------------------------------------------------------
Write-Header "Executing nuke-route-data.sql"
$sql = Get-Content $sqlPath -Raw
try {
    [void](Invoke-SqlNonQuery -Query $sql -ConnStr $ConnectionString)
    Write-Ok "Wipe transaction committed."
} catch {
    Write-Err "Wipe failed: $($_.Exception.Message)"
    exit 1
}

# ---- Post-wipe verification -----------------------------------------------
Write-Header "Post-wipe row counts"
$residual = 0
foreach ($t in $tables) {
    $c = Get-RowCount -Table $t -ConnStr $ConnectionString
    if ($null -eq $c) {
        Write-Warn ("  {0,-15} (table not found)" -f $t)
        continue
    }
    Write-Host ("  {0,-15} {1,8:n0}" -f $t, $c)
    $residual += $c
}

if ($residual -gt 0) {
    Write-Warn "Some rows remain — $residual row(s) across the target tables."
} else {
    Write-Ok "All target tables are empty."
}
