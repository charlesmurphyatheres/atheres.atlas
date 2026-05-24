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

# Project root is the parent of scripts/. Functions local.settings.json + the
# data\warehouses.csv etc. are looked up relative to it.
$root = Split-Path -Parent $PSScriptRoot

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
    # Secure Transport is the first onboarded tenant
    $CompanyId = "10000000-0000-0000-0000-000000000001"
    Write-Info "Using Secure Transport: $CompanyId"
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

    # Stores are many-to-many with Companies now (StoreCompanies join table).
    # Deactivate junk rows that this company can see — same scope as before,
    # just expressed via the join.
    $deactivated = Invoke-Sql -Query @"
UPDATE s
SET    s.IsActive  = 0,
       s.UpdatedAt = SYSUTCDATETIME()
FROM   Stores s
WHERE  s.IsActive = 1
  AND  EXISTS (SELECT 1 FROM StoreCompanies sc WHERE sc.StoreId = s.Id AND sc.CompanyId = '$CompanyId')
  AND (s.Name IS NULL OR LTRIM(RTRIM(s.Name)) = ''
       OR s.LicenseNumber IS NULL OR LTRIM(RTRIM(s.LicenseNumber)) = '')
"@ -ConnStr $ConnectionString
    if ($deactivated -gt 0) { Write-Warn "Deactivated $deactivated blank/legacy store row(s)." }

    $existingCount = Invoke-SqlScalar -Query @"
SELECT COUNT(*) FROM Stores s
WHERE  s.IsActive = 1
  AND  EXISTS (SELECT 1 FROM StoreCompanies sc WHERE sc.StoreId = s.Id AND sc.CompanyId = '$CompanyId')
"@ -ConnStr $ConnectionString
    if ($existingCount -gt 0) {
        Write-Warn "$existingCount stores already exist for this company. Skipping import (idempotent)."
    } else {
        $imported = 0
        $skipped  = 0
        $now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff")

        foreach ($s in $stores) {
            # Skip rows that would land in the table as blank — they'd never
            # zone, never have a license to import orders against, and just
            # clutter the dashboard.
            $rawName    = if ($s.'Dispensary Name')  { $s.'Dispensary Name'.Trim() }  else { '' }
            $rawLicense = if ($s.'License Number') { $s.'License Number'.Trim() } else { '' }
            if (-not $rawName -or -not $rawLicense) {
                $skipped++
                continue
            }

            $id = [Guid]::NewGuid().ToString()
            $customer = $s.'ST Customer' -replace "'", "''"
            $name = $rawName -replace "'", "''"
            $address = $s.'Street Address' -replace "'", "''"
            $city = $s.'City' -replace "'", "''"
            $zip = $s.'Zip Code' -replace "'", "''"
            $county = $s.'County' -replace "'", "''"
            $region = $s.'BLS Region' -replace "'", "''"
            $license = $rawLicense -replace "'", "''"

            # Two-step insert: the row, then the membership link. Both run
            # inside one connection so a failure on link insert can be
            # observed (the store row exists but the catch reports it).
            $sql = @"
INSERT INTO Stores (Id, Customer, Name, Address, City, State, Zip, County, Region, LicenseNumber, IsActive, CreatedAt, UpdatedAt)
VALUES ('$id', N'$customer', N'$name', N'$address', N'$city', 'IL', '$zip', N'$county', N'$region', '$license', 1, '$now', '$now');
INSERT INTO StoreCompanies (StoreId, CompanyId) VALUES ('$id', '$CompanyId');
"@
            try {
                Invoke-Sql -Query $sql -ConnStr $ConnectionString | Out-Null
                $imported++
            } catch {
                Write-Warn "Failed to import store: $name - $_"
            }
        }
        if ($skipped -gt 0) { Write-Warn "Skipped $skipped CSV row(s) with blank name or license." }
        Write-Ok "$imported stores imported."
    }
}

# ---- Import Districts, Zones, and link Stores ------------------
# zones.csv carries one row per (zone, store license) assignment. We flatten
# it into:
#   1. Districts  — distinct (Number, Name) pairs (cols 3 + 4).
#   2. Zones      — distinct (DistrictId, Code)   pairs (cols 1 + 3).
#   3. Stores.ZoneId  — UPDATE by LicenseNumber match (col 2).
# The block is idempotent: re-running skips districts/zones that already exist
# and only updates Stores rows whose ZoneId is missing or out of date.
# zones.csv has TWO columns both named "District", which would collide under
# Import-Csv — split each line by comma manually instead.
Write-Header "Importing Districts and Zones"

$zonesFile = Join-Path $root "data\zones.csv"
if (-not (Test-Path $zonesFile)) {
    Write-Warn "zones.csv not found at $zonesFile — skipping."
} else {
    $zoneLines = Get-Content $zonesFile | Select-Object -Skip 1 | Where-Object { $_.Trim() -ne "" }
    Write-Info "Found $($zoneLines.Count) zone rows in CSV"

    $zoneRows = foreach ($line in $zoneLines) {
        $f = $line.Split(',')
        if ($f.Count -lt 4) { continue }
        # District number arrives as plain int ("1","10") but coerce via double
        # so a stray "1.0" in future data is still tolerated.
        [pscustomobject]@{
            ZoneCode       = $f[0].Trim()
            License        = $f[1].Trim()
            DistrictNumber = [int][double]$f[2].Trim()
            DistrictName   = $f[3].Trim()
        }
    }

    $now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff")

    # ---- Districts -------------------------------------------------
    $districtIdsByNumber = @{}
    $districtsInserted   = 0
    $districts = $zoneRows | Sort-Object DistrictNumber, DistrictName -Unique
    foreach ($d in $districts) {
        $existingId = Invoke-SqlScalar `
            -Query   "SELECT TOP 1 CAST(Id AS NVARCHAR(36)) FROM Districts WHERE CompanyId='$CompanyId' AND Number=$($d.DistrictNumber)" `
            -ConnStr $ConnectionString
        if ($existingId) {
            $districtIdsByNumber[$d.DistrictNumber] = $existingId
            continue
        }
        $id   = [Guid]::NewGuid().ToString()
        $name = $d.DistrictName -replace "'", "''"
        $sql  = @"
INSERT INTO Districts (Id, CompanyId, Number, Name, IsActive, CreatedAt, UpdatedAt)
VALUES ('$id', '$CompanyId', $($d.DistrictNumber), N'$name', 1, '$now', '$now')
"@
        try {
            Invoke-Sql -Query $sql -ConnStr $ConnectionString | Out-Null
            $districtIdsByNumber[$d.DistrictNumber] = $id
            $districtsInserted++
        } catch {
            Write-Warn "Failed to insert district $($d.DistrictNumber) ($name): $_"
        }
    }
    Write-Ok "$districtsInserted districts inserted ($($districtIdsByNumber.Count) tracked)."

    # ---- Zones -----------------------------------------------------
    $zoneIdsByKey   = @{}
    $zonesInserted  = 0
    $zones = $zoneRows | Sort-Object DistrictNumber, ZoneCode -Unique
    foreach ($z in $zones) {
        $districtId = $districtIdsByNumber[$z.DistrictNumber]
        if (-not $districtId) { Write-Warn "No district for $($z.DistrictNumber); skipping zone $($z.ZoneCode)"; continue }
        $code = $z.ZoneCode -replace "'", "''"
        $key  = "$districtId|$($z.ZoneCode)"
        $existingId = Invoke-SqlScalar `
            -Query   "SELECT TOP 1 CAST(Id AS NVARCHAR(36)) FROM Zones WHERE CompanyId='$CompanyId' AND DistrictId='$districtId' AND Code=N'$code'" `
            -ConnStr $ConnectionString
        if ($existingId) {
            $zoneIdsByKey[$key] = $existingId
            continue
        }
        $id  = [Guid]::NewGuid().ToString()
        $sql = @"
INSERT INTO Zones (Id, CompanyId, DistrictId, Code, IsActive, CreatedAt, UpdatedAt)
VALUES ('$id', '$CompanyId', '$districtId', N'$code', 1, '$now', '$now')
"@
        try {
            Invoke-Sql -Query $sql -ConnStr $ConnectionString | Out-Null
            $zoneIdsByKey[$key] = $id
            $zonesInserted++
        } catch {
            Write-Warn "Failed to insert zone $($z.ZoneCode) under district $($z.DistrictNumber): $_"
        }
    }
    Write-Ok "$zonesInserted zones inserted ($($zoneIdsByKey.Count) tracked)."

    # ---- Link Stores.ZoneId by LicenseNumber -----------------------
    # The UPDATE is filtered to skip rows that already point at the correct
    # zone, so the row count we sum represents real reassignments rather than
    # no-op writes.
    $storesLinked = 0
    foreach ($r in $zoneRows) {
        $districtId = $districtIdsByNumber[$r.DistrictNumber]
        if (-not $districtId) { continue }
        $zoneId = $zoneIdsByKey["$districtId|$($r.ZoneCode)"]
        if (-not $zoneId) { continue }
        $license = $r.License -replace "'", "''"
        $sql = @"
UPDATE s
SET    s.ZoneId    = '$zoneId',
       s.UpdatedAt = SYSUTCDATETIME()
FROM   Stores s
WHERE  s.LicenseNumber = N'$license'
  AND  EXISTS (SELECT 1 FROM StoreCompanies sc WHERE sc.StoreId = s.Id AND sc.CompanyId = '$CompanyId')
  AND (s.ZoneId IS NULL OR s.ZoneId <> '$zoneId')
"@
        try {
            $updated = Invoke-Sql -Query $sql -ConnStr $ConnectionString
            if ($updated -gt 0) { $storesLinked += $updated }
        } catch {
            Write-Warn "Failed to link store license $license to zone $($r.ZoneCode): $_"
        }
    }
    Write-Ok "Linked $storesLinked store(s) to zones."
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

    # Warehouses are many-to-many with Companies (WarehouseCompanies join).
    $existingCount = Invoke-SqlScalar -Query @"
SELECT COUNT(*) FROM Warehouses w
WHERE  EXISTS (SELECT 1 FROM WarehouseCompanies wc WHERE wc.WarehouseId = w.Id AND wc.CompanyId = '$CompanyId')
"@ -ConnStr $ConnectionString
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
INSERT INTO Warehouses (Id, BusinessName, AlternateName, Address, City, State, Zip, LicenseNumber, LegacyLicenseNumber, IsActive, CreatedAt, UpdatedAt)
VALUES ('$id', N'$businessName', N'$alternateName', N'$addressVal', N'$city', '$state', '$zip', '$license', '$legacyLicense', 1, '$now', '$now');
INSERT INTO WarehouseCompanies (WarehouseId, CompanyId) VALUES ('$id', '$CompanyId');
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
# Hubs.csv carries hard-coded Latitude/Longitude for the four real
# Secure Transport depots; persisting them here means RouteOptimizationAgent
# never has to call Google to geocode the depot — it just reads the cached
# coords. The script handles two states:
#   1. Fresh DB (no hubs)         -> INSERT every row with lat/lng baked in.
#   2. Existing DB with coords    -> no-op (idempotent).
#   3. Existing DB missing coords -> UPDATE rows whose Latitude is NULL
#      using the CSV's coords, matched by Name.
Write-Header "Importing Hubs"

$hubsFile = Join-Path $root "data\hubs.csv"
if (-not (Test-Path $hubsFile)) {
    Write-Warn "hubs.csv not found at $hubsFile — skipping."
} else {
    $hubs = Import-Csv $hubsFile
    Write-Info "Found $($hubs.Count) hubs in CSV"

    $existingCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Hubs WHERE CompanyId = '$CompanyId'" -ConnStr $ConnectionString
    if ($existingCount -gt 0) {
        Write-Warn "$existingCount hubs already exist for this company. Backfilling lat/lng on rows missing coordinates..."
        $backfilled = 0
        foreach ($h in $hubs) {
            if ([string]::IsNullOrWhiteSpace($h.Latitude) -or [string]::IsNullOrWhiteSpace($h.Longitude)) {
                continue
            }
            $name = $h.'Name' -replace "'", "''"
            $sql = @"
UPDATE Hubs
SET    Latitude  = $($h.Latitude),
       Longitude = $($h.Longitude),
       UpdatedAt = SYSUTCDATETIME()
WHERE  CompanyId = '$CompanyId'
  AND  Name      = N'$name'
  AND (Latitude IS NULL OR Longitude IS NULL);
"@
            try {
                $rows = Invoke-Sql -Query $sql -ConnStr $ConnectionString
                if ($rows -gt 0) { $backfilled += $rows }
            } catch {
                Write-Warn "Failed to backfill hub '$name': $_"
            }
        }
        if ($backfilled -gt 0) {
            Write-Ok "Backfilled coordinates on $backfilled hub row(s)."
        } else {
            Write-Info "All existing hubs already have coordinates — nothing to backfill."
        }
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
            # Latitude / Longitude are optional in the CSV; emit a SQL NULL
            # when blank so the EF query filter still treats the row as
            # ungeocoded (and the optimizer self-heal can fill it).
            $latSql = if ([string]::IsNullOrWhiteSpace($h.Latitude))  { 'NULL' } else { $h.Latitude }
            $lngSql = if ([string]::IsNullOrWhiteSpace($h.Longitude)) { 'NULL' } else { $h.Longitude }

            $sql = @"
INSERT INTO Hubs (Id, CompanyId, Name, Address, City, State, Zip, Latitude, Longitude, IsActive, CreatedAt, UpdatedAt)
VALUES ('$id', '$CompanyId', N'$name', N'$address', N'$city', '$state', '$zip', $latSql, $lngSql, 1, '$now', '$now')
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
$storeCount = Invoke-SqlScalar -Query @"
SELECT COUNT(*) FROM Stores s
WHERE EXISTS (SELECT 1 FROM StoreCompanies sc WHERE sc.StoreId = s.Id AND sc.CompanyId = '$CompanyId')
"@ -ConnStr $ConnectionString
$whCount = Invoke-SqlScalar -Query @"
SELECT COUNT(*) FROM Warehouses w
WHERE EXISTS (SELECT 1 FROM WarehouseCompanies wc WHERE wc.WarehouseId = w.Id AND wc.CompanyId = '$CompanyId')
"@ -ConnStr $ConnectionString
$hubCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Hubs WHERE CompanyId = '$CompanyId'" -ConnStr $ConnectionString
$truckCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Trucks WHERE CompanyId = '$CompanyId'" -ConnStr $ConnectionString
$districtCount = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Districts WHERE CompanyId = '$CompanyId'" -ConnStr $ConnectionString
$zoneCount     = Invoke-SqlScalar -Query "SELECT COUNT(*) FROM Zones WHERE CompanyId = '$CompanyId'" -ConnStr $ConnectionString
$zonedStores   = Invoke-SqlScalar -Query @"
SELECT COUNT(*) FROM Stores s
WHERE s.ZoneId IS NOT NULL
  AND EXISTS (SELECT 1 FROM StoreCompanies sc WHERE sc.StoreId = s.Id AND sc.CompanyId = '$CompanyId')
"@ -ConnStr $ConnectionString
Write-Host "  Stores:           $storeCount  ($zonedStores zoned)"
Write-Host "  Warehouses:       $whCount"
Write-Host "  Hubs:             $hubCount"
Write-Host "  Vans:             $truckCount"
Write-Host "  Districts:        $districtCount"
Write-Host "  Zones:            $zoneCount"
Write-Host ""
