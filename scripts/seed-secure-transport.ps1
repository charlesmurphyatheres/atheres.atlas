#Requires -Version 7.0
# =============================================================
# Import hubs + vans for "Secure Transport" from data/*.csv
# (Company and users are seeded in code by Auth Functions startup.)
# =============================================================

[CmdletBinding()]
param(
    [string]$ConnectionString,
    [string]$IdentityConnectionString
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Project root is the parent of scripts/. Functions local.settings.json files
# and the data\ CSVs are looked up relative to it.
$root = Split-Path -Parent $PSScriptRoot

function Write-Header  { param($msg) Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Info    { param($msg) Write-Host "[INFO]  $msg"  -ForegroundColor Cyan }
function Write-Ok      { param($msg) Write-Host "[OK]    $msg"  -ForegroundColor Green }
function Write-Warn    { param($msg) Write-Host "[WARN]  $msg"  -ForegroundColor Yellow }

function Invoke-Sql {
    param([string]$Query, [string]$ConnStr)
    $conn = New-Object System.Data.SqlClient.SqlConnection($ConnStr)
    $conn.Open()
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $Query
    $cmd.CommandTimeout = 120
    $result = $cmd.ExecuteNonQuery()
    $conn.Close()
    return $result
}

function Invoke-SqlScalar {
    param([string]$Query, [string]$ConnStr)
    $conn = New-Object System.Data.SqlClient.SqlConnection($ConnStr)
    $conn.Open()
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $Query
    $result = $cmd.ExecuteScalar()
    $conn.Close()
    return $result
}

# ---- Resolve connection strings --------------------------------
if (-not $ConnectionString) {
    $settingsPath = Join-Path $root "Atheres.Atlas.Functions\local.settings.json"
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $ConnectionString = $settings.Values.SqlConnectionString
}

if (-not $IdentityConnectionString) {
    $authSettingsPath = Join-Path $root "Atheres.Atlas.Auth.Functions\local.settings.json"
    $authSettings = Get-Content $authSettingsPath -Raw | ConvertFrom-Json
    $IdentityConnectionString = $authSettings.Values.SqlConnectionString
}

Write-Info "Business DB: $($ConnectionString.Substring(0, 40))..."
Write-Info "Identity DB: $($IdentityConnectionString.Substring(0, 40))..."

# ---- Resolve company id ---------------------------------------
# Company row is created by Auth Functions startup seed.
$companyId = "10000000-0000-0000-0000-000000000001"
$companyExists = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Companies WHERE Id = '$companyId'" -ConnStr $ConnectionString
if (-not $companyExists -or $companyExists -eq 0) {
    Write-Warn "Secure Transport company row not found. Start Auth Functions once to seed it, then rerun."
    exit 0
}

# ---- Hubs ------------------------------------------------------
Write-Header "Creating hubs for Secure Transport"

$hubsFile = Join-Path $root "data\hubs.csv"
$hubCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Hubs WHERE CompanyId = '$companyId'" -ConnStr $ConnectionString

if ($hubCount -gt 0) {
    Write-Warn "$hubCount hubs already exist for Secure Transport. Skipping."
} else {
    $hubs = Import-Csv $hubsFile
    $now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff")
    $imported = 0

    foreach ($h in $hubs) {
        $id = [Guid]::NewGuid().ToString()
        $name = $h.'Name' -replace "'", "''"
        $address = $h.'Address' -replace "'", "''"
        $city = $h.'City' -replace "'", "''"
        $state = $h.'State' -replace "'", "''"
        $zip = $h.'Zip' -replace "'", "''"
        $csvId = $h.'ID'
        $isTransferSite = if ($h.PSObject.Properties['IsTransferSite'] -and [bool]::Parse($h.IsTransferSite)) { 1 } else { 0 }
        $isChicagoLand  = if ($h.PSObject.Properties['IsChicagoLand']  -and [bool]::Parse($h.IsChicagoLand))  { 1 } else { 0 }

        # Store the CSV ID in FormattedAddress temporarily for van lookup
        Invoke-Sql -Query @"
INSERT INTO Hubs (Id, CompanyId, Name, Address, City, State, Zip, FormattedAddress, IsTransferSite, IsChicagoLand, IsActive, CreatedAt, UpdatedAt)
VALUES ('$id', '$companyId', N'$name', N'$address', N'$city', '$state', '$zip', '$csvId', $isTransferSite, $isChicagoLand, 1, '$now', '$now')
"@ -ConnStr $ConnectionString
        $imported++
    }
    Write-Ok "$imported hubs created."
}

# ---- Vans ------------------------------------------------------
Write-Header "Creating vans for Secure Transport"

$vansFile = Join-Path $root "data\vans.csv"
$vanCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Trucks WHERE CompanyId = '$companyId'" -ConnStr $ConnectionString

if ($vanCount -gt 0) {
    Write-Warn "$vanCount vans already exist for Secure Transport. Skipping."
} else {
    # Build hub CSV ID → DB GUID lookup for this company
    $hubCsvMap = Import-Csv $hubsFile
    $hubLookup = @{}
    foreach ($h in $hubCsvMap) {
        $dbId = Invoke-SqlScalar -Query "SELECT TOP 1 CAST(Id AS NVARCHAR(36)) FROM Hubs WHERE CompanyId = '$companyId' AND FormattedAddress = '$($h.ID)'" -ConnStr $ConnectionString
        if ($dbId) { $hubLookup[$h.ID] = $dbId }
    }

    $vans = Import-Csv $vansFile
    $now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff")
    $imported = 0

    foreach ($v in $vans) {
        $id = [Guid]::NewGuid().ToString()
        $name = $v.'Van' -replace "'", "''"
        $plate = $v.'Licence Plate' -replace "'", "''"
        $hubLocation = $v.'HubLocation'.Trim()
        $hubId = $hubLookup[$hubLocation]
        $hubClause = if ($hubId) { "'$hubId'" } else { "NULL" }

        Invoke-Sql -Query @"
INSERT INTO Trucks (Id, CompanyId, Name, LicensePlate, HubId, IsActive, CreatedAt, UpdatedAt)
VALUES ('$id', '$companyId', N'$name', '$plate', $hubClause, 1, '$now', '$now')
"@ -ConnStr $ConnectionString
        $imported++
    }
    Write-Ok "$imported vans created."

    # Clear the temporary FormattedAddress used for CSV ID lookup
    Invoke-Sql -Query "UPDATE Hubs SET FormattedAddress = NULL WHERE CompanyId = '$companyId'" -ConnStr $ConnectionString
}

# ---- Summary ---------------------------------------------------
Write-Header "Secure Transport data import complete"

$hubCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Hubs WHERE CompanyId = '$companyId'" -ConnStr $ConnectionString
$vanCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Trucks WHERE CompanyId = '$companyId'" -ConnStr $ConnectionString
Write-Host "  Company:  Secure Transport ($companyId)"
Write-Host "  Hubs:     $hubCount"
Write-Host "  Vans:     $vanCount"
Write-Host ""
