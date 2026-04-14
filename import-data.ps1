#Requires -Version 7.0
# =============================================================
# Atheres Atlas — Import Stores and Warehouses from CSV
#
# Usage:
#   .\import-data.ps1                              # import using local.settings.json connection
#   .\import-data.ps1 -ConnectionString "Server=..." # custom connection string
#   .\import-data.ps1 -CompanyId "guid"             # target a specific company
# =============================================================

[CmdletBinding()]
param(
    [string]$ConnectionString,
    [string]$CompanyId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot

function Write-Header  { param($msg) Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Info    { param($msg) Write-Host "[INFO]  $msg"  -ForegroundColor Cyan }
function Write-Ok      { param($msg) Write-Host "[OK]    $msg"  -ForegroundColor Green }
function Write-Warn    { param($msg) Write-Host "[WARN]  $msg"  -ForegroundColor Yellow }
function Write-Err     { param($msg) Write-Host "[ERROR] $msg"  -ForegroundColor Red }

# ---- Resolve connection string ---------------------------------
if (-not $ConnectionString) {
    $settingsPath = Join-Path $root "Atheres.Atlas.Functions\local.settings.json"
    if (Test-Path $settingsPath) {
        $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
        $ConnectionString = $settings.Values.SqlConnectionString
        Write-Info "Using connection from local.settings.json"
    } else {
        Write-Err "No connection string provided and local.settings.json not found."
        exit 1
    }
}

# ---- Resolve CompanyId -----------------------------------------
if (-not $CompanyId) {
    # Use the default company seeded by the multi-tenancy migration
    $CompanyId = "00000000-0000-0000-0000-000000000001"
    Write-Info "Using default company: $CompanyId"
}

# ---- Helper: Execute SQL ----------------------------------------
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

# ---- Import Stores --------------------------------------------
Write-Header "Importing Stores"

$storesFile = Join-Path $root "data\stores.csv"
if (-not (Test-Path $storesFile)) {
    Write-Warn "stores.csv not found at $storesFile — skipping."
} else {
    $stores = Import-Csv $storesFile
    Write-Info "Found $($stores.Count) stores in CSV"

    $existingCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Stores WHERE CompanyId = '$CompanyId'" -ConnStr $ConnectionString
    if ($existingCount -gt 0) {
        Write-Warn "$existingCount stores already exist for this company. Skipping import (idempotent)."
    } else {
        $imported = 0
        $now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff")

        foreach ($s in $stores) {
            $id = [Guid]::NewGuid().ToString()
            $customer = $s.'ST Customer' -replace "'", "''"
            $name = $s.'Dispensary Name' -replace "'", "''"
            $address = $s.'Street Address' -replace "'", "''"
            $city = $s.'City' -replace "'", "''"
            $zip = $s.'Zip Code' -replace "'", "''"
            $county = $s.'County' -replace "'", "''"
            $region = $s.'BLS Region' -replace "'", "''"
            $license = $s.'License Number' -replace "'", "''"

            $sql = @"
INSERT INTO Stores (Id, CompanyId, Customer, Name, Address, City, State, Zip, County, Region, LicenseNumber, IsActive, CreatedAt, UpdatedAt)
VALUES ('$id', '$CompanyId', N'$customer', N'$name', N'$address', N'$city', 'IL', '$zip', N'$county', N'$region', '$license', 1, '$now', '$now')
"@
            try {
                Invoke-Sql -Query $sql -ConnStr $ConnectionString | Out-Null
                $imported++
            } catch {
                Write-Warn "Failed to import store: $name - $_"
            }
        }
        Write-Ok "$imported stores imported."
    }
}

# ---- Import Warehouses -----------------------------------------
Write-Header "Importing Warehouses"

$warehousesFile = Join-Path $root "data\warehouses.csv"
if (-not (Test-Path $warehousesFile)) {
    Write-Warn "warehouses.csv not found at $warehousesFile — skipping."
} else {
    # CSV has duplicate "Address" column — parse manually
    $lines = Get-Content $warehousesFile
    $dataLines = $lines | Select-Object -Skip 1 | Where-Object { $_.Trim() -ne "" }
    Write-Info "Found $($dataLines.Count) warehouses in CSV"

    $existingCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Warehouses WHERE CompanyId = '$CompanyId'" -ConnStr $ConnectionString
    if ($existingCount -gt 0) {
        Write-Warn "$existingCount warehouses already exist for this company. Skipping import (idempotent)."
    } else {
        $imported = 0
        $now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff")

        # Header: LegacyLicense,License,BusinessName,FullAddr,AKA,Address,City,State,Zip
        foreach ($line in $dataLines) {
            # Parse CSV respecting quoted fields
            $fields = [System.Collections.Generic.List[string]]::new()
            $inQuote = $false; $field = ""
            foreach ($ch in $line.ToCharArray()) {
                if ($ch -eq '"') { $inQuote = -not $inQuote }
                elseif ($ch -eq ',' -and -not $inQuote) { $fields.Add($field); $field = "" }
                else { $field += $ch }
            }
            $fields.Add($field)

            if ($fields.Count -lt 9) { continue }

            $id = [Guid]::NewGuid().ToString()
            $legacyLicense = ($fields[0]).Trim() -replace "'", "''"
            $license       = ($fields[1]).Trim() -replace "'", "''"
            $businessName  = ($fields[2]).Trim() -replace "'", "''"
            $alternateName = ($fields[4]).Trim() -replace "'", "''"
            $addressVal    = ($fields[5]).Trim() -replace "'", "''"
            $city          = ($fields[6]).Trim() -replace "'", "''"
            $state         = ($fields[7]).Trim() -replace "'", "''"
            $zip           = ($fields[8]).Trim() -replace "'", "''"

            $sql = @"
INSERT INTO Warehouses (Id, CompanyId, BusinessName, AlternateName, Address, City, State, Zip, LicenseNumber, LegacyLicenseNumber, IsActive, CreatedAt, UpdatedAt)
VALUES ('$id', '$CompanyId', N'$businessName', N'$alternateName', N'$addressVal', N'$city', '$state', '$zip', '$license', '$legacyLicense', 1, '$now', '$now')
"@
            try {
                Invoke-Sql -Query $sql -ConnStr $ConnectionString | Out-Null
                $imported++
            } catch {
                Write-Warn "Failed to import warehouse: $businessName - $_"
            }
        }
        Write-Ok "$imported warehouses imported."
    }
}

# ---- Import Hubs -----------------------------------------------
Write-Header "Importing Hubs"

$hubsFile = Join-Path $root "data\hubs.csv"
if (-not (Test-Path $hubsFile)) {
    Write-Warn "hubs.csv not found at $hubsFile — skipping."
} else {
    $hubs = Import-Csv $hubsFile
    Write-Info "Found $($hubs.Count) hubs in CSV"

    $existingCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Hubs WHERE CompanyId = '$CompanyId'" -ConnStr $ConnectionString
    if ($existingCount -gt 0) {
        Write-Warn "$existingCount hubs already exist for this company. Skipping import (idempotent)."
    } else {
        $imported = 0
        $now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff")

        foreach ($h in $hubs) {
            $id = [Guid]::NewGuid().ToString()
            $name = $h.'Name' -replace "'", "''"
            $address = $h.'Address' -replace "'", "''"
            $city = $h.'City' -replace "'", "''"
            $state = $h.'State' -replace "'", "''"
            $zip = $h.'Zip' -replace "'", "''"

            $sql = @"
INSERT INTO Hubs (Id, CompanyId, Name, Address, City, State, Zip, IsActive, CreatedAt, UpdatedAt)
VALUES ('$id', '$CompanyId', N'$name', N'$address', N'$city', '$state', '$zip', 1, '$now', '$now')
"@
            try {
                Invoke-Sql -Query $sql -ConnStr $ConnectionString | Out-Null
                $imported++
            } catch {
                Write-Warn "Failed to import hub: $name - $_"
            }
        }
        Write-Ok "$imported hubs imported."
    }
}

# ---- Import Vans (Trucks) --------------------------------------
Write-Header "Importing Vans"

$vansFile = Join-Path $root "data\vans.csv"
if (-not (Test-Path $vansFile)) {
    Write-Warn "vans.csv not found at $vansFile — skipping."
} else {
    $vans = Import-Csv $vansFile
    Write-Info "Found $($vans.Count) vans in CSV"

    $existingCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Trucks WHERE CompanyId = '$CompanyId'" -ConnStr $ConnectionString
    if ($existingCount -gt 0) {
        Write-Warn "$existingCount trucks already exist for this company. Skipping import (idempotent)."
    } else {
        # Build hub ID lookup: CSV HubLocation (e.g., HUB_SPR) → DB Hub Name
        # The hubs.csv ID column maps to the HubLocation column in vans.csv
        $hubsFile = Join-Path $root "data\hubs.csv"
        $hubCsvMap = @{}
        if (Test-Path $hubsFile) {
            Import-Csv $hubsFile | ForEach-Object {
                $hubCsvMap[$_.ID] = $_.Name
            }
        }

        $imported = 0
        $now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff")

        foreach ($v in $vans) {
            $id = [Guid]::NewGuid().ToString()
            $name = $v.'Van' -replace "'", "''"
            $plate = $v.'Licence Plate' -replace "'", "''"
            $hubLocation = $v.'HubLocation'.Trim()

            # Resolve hub name from CSV ID, then look up the DB hub GUID
            $hubName = $hubCsvMap[$hubLocation]
            $hubId = $null
            if ($hubName) {
                $escapedName = $hubName -replace "'", "''"
                $hubId = Invoke-SqlScalar -Query "SELECT TOP 1 CAST(Id AS NVARCHAR(36)) FROM Hubs WHERE CompanyId = '$CompanyId' AND Name = N'$escapedName'" -ConnStr $ConnectionString
            }

            $hubClause = if ($hubId) { "'$hubId'" } else { "NULL" }

            $sql = @"
INSERT INTO Trucks (Id, CompanyId, Name, LicensePlate, HubId, IsActive, CreatedAt, UpdatedAt)
VALUES ('$id', '$CompanyId', N'$name', '$plate', $hubClause, 1, '$now', '$now')
"@
            try {
                Invoke-Sql -Query $sql -ConnStr $ConnectionString | Out-Null
                $imported++
            } catch {
                Write-Warn "Failed to import van: $name ($plate) - $_"
            }
        }
        Write-Ok "$imported vans imported."
    }
}

Write-Header "Import complete"
$storeCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Stores WHERE CompanyId = '$CompanyId'" -ConnStr $ConnectionString
$whCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Warehouses WHERE CompanyId = '$CompanyId'" -ConnStr $ConnectionString
$hubCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Hubs WHERE CompanyId = '$CompanyId'" -ConnStr $ConnectionString
$truckCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Trucks WHERE CompanyId = '$CompanyId'" -ConnStr $ConnectionString
Write-Host "  Stores:     $storeCount"
Write-Host "  Warehouses: $whCount"
Write-Host "  Hubs:       $hubCount"
Write-Host "  Vans:       $truckCount"
Write-Host ""
