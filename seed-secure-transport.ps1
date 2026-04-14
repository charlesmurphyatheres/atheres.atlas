#Requires -Version 7.0
# =============================================================
# Seed "Secure Transport" company with Admin user, hubs, and vans
# =============================================================

[CmdletBinding()]
param(
    [string]$ConnectionString,
    [string]$IdentityConnectionString
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot

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

# ---- Company ---------------------------------------------------
Write-Header "Creating Secure Transport company"

$companyId = "10000000-0000-0000-0000-000000000001"
$existing = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Companies WHERE Id = '$companyId'" -ConnStr $ConnectionString

if ($existing -gt 0) {
    Write-Warn "Secure Transport already exists. Skipping."
} else {
    $now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff")
    Invoke-Sql -Query @"
INSERT INTO Companies (Id, Name, Slug, ContactEmail, ContactPhone, Timezone, IsActive, CreatedAt, UpdatedAt)
VALUES ('$companyId', 'Secure Transport', 'secure-transport', 'steven@gmail.com', NULL, 'America/Chicago', 1, '$now', '$now')
"@ -ConnStr $ConnectionString
    Write-Ok "Company created: Secure Transport ($companyId)"
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

        # Store the CSV ID in FormattedAddress temporarily for van lookup
        Invoke-Sql -Query @"
INSERT INTO Hubs (Id, CompanyId, Name, Address, City, State, Zip, FormattedAddress, IsActive, CreatedAt, UpdatedAt)
VALUES ('$id', '$companyId', N'$name', N'$address', N'$city', '$state', '$zip', '$csvId', 1, '$now', '$now')
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

# ---- Admin user (Steven) in Identity DB -------------------------
Write-Header "Creating Admin user: Steven"

$userId = Invoke-SqlScalar -Query "SELECT Id FROM AspNetUsers WHERE Email = 'steven@gmail.com'" -ConnStr $IdentityConnectionString

if ($userId) {
    Write-Warn "User steven@gmail.com already exists (Id: $userId). Skipping."
} else {
    # ASP.NET Identity password hashing — we need to use the Auth Functions to create the user
    # since password hashing is done by the Identity framework, not raw SQL.
    # Instead, we'll call the register API endpoint.

    Write-Info "Registering user via Auth API..."

    # Try the API first (requires Auth Functions to be running)
    $registered = $false
    try {
        # First login as SuperAdmin to get a token
        $loginBody = @{ email = "charles.murphy@atheres.com"; password = "Albeniz<18651909>" } | ConvertTo-Json
        $loginResp = Invoke-RestMethod -Uri "http://localhost:7072/api/auth/login" -Method POST -Body $loginBody -ContentType "application/json" -ErrorAction Stop

        $token = $loginResp.accessToken

        # Register Steven as Admin for Secure Transport
        $registerBody = @{
            email     = "steven@gmail.com"
            password  = "Secure@1234567890"
            firstName = "Steven"
            lastName  = "Admin"
            companyId = $companyId
            role      = "Admin"
        } | ConvertTo-Json

        $headers = @{ Authorization = "Bearer $token" }
        Invoke-RestMethod -Uri "http://localhost:7072/api/auth/register" -Method POST -Body $registerBody -ContentType "application/json" -Headers $headers -ErrorAction Stop

        $registered = $true
        Write-Ok "User steven@gmail.com created as Admin for Secure Transport."
    } catch {
        Write-Warn "Could not register via API (Auth Functions may not be running): $_"
        Write-Warn "Start Auth Functions (port 7072), then run this script again to create the user."
    }
}

# ---- Summary ---------------------------------------------------
Write-Header "Secure Transport seed complete"

$hubCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Hubs WHERE CompanyId = '$companyId'" -ConnStr $ConnectionString
$vanCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Trucks WHERE CompanyId = '$companyId'" -ConnStr $ConnectionString
Write-Host "  Company:  Secure Transport ($companyId)"
Write-Host "  Hubs:     $hubCount"
Write-Host "  Vans:     $vanCount"
Write-Host "  Admin:    steven@gmail.com / Secure@1234567890"
Write-Host ""
