#Requires -Version 7.0
# =============================================================
# Atheres Atlas — Import reference data from CSV
#
# Reads:
#   data\store_zone.csv  — Stores + their District + Zone (one CSV; replaces
#                          the legacy stores.csv/zones.csv split now stored
#                          under data\deprecated\).
#   data\warehouses.csv  — Warehouses including IsChicagoLand.
#   data\hubs.csv        — Hubs including IsTransferSite + IsChicagoLand.
#   data\vans.csv        — Trucks/vans, linked to a hub by HubLocation.
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

# ---- Import Districts, Zones, and Stores from store_zone.csv ---
# store_zone.csv supersedes the legacy stores.csv + zones.csv split: every
# row carries the store row + its delivery zone + its district in one place,
# so we build the District/Zone hierarchy and the Store row from the same
# pass. The companion zones.csv is still consulted for the per-district
# IsChicagoLand flag (District 5 = Chicago-Naperville-Elgin).
#
# Each row in store_zone.csv:
#   Disp, Address, City, Zip, County, Del. zone, License #, District #,
#   District, Opened
#
# Disp is the legacy combined "Customer - Name" string (e.g.
# "Dutchess - Oak Park"). We split on the first " - " so the Customer column
# keeps the supplier brand and Name keeps the location.
Write-Header "Importing Districts, Zones, and Stores"

$storeZoneFile = Join-Path $root "data\store_zone.csv"
if (-not (Test-Path $storeZoneFile)) {
    Write-Warn "store_zone.csv not found at $storeZoneFile — skipping."
} else {
    $rows = Import-Csv $storeZoneFile
    Write-Info "Found $($rows.Count) store-zone rows in CSV"

    # Stores are many-to-many with Companies via StoreCompanies. Deactivate
    # blank/legacy rows in scope for this company before we add anything new.
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

    $now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff")

    # ---- Districts -------------------------------------------------
    $districtIdsByNumber = @{}
    $districtsInserted   = 0
    $districts = $rows |
        Where-Object { $_.'District #' -and $_.'District' } |
        ForEach-Object {
            [pscustomobject]@{
                Number = [int][double]$_.'District #'.Trim()
                Name   = $_.'District'.Trim()
            }
        } |
        Sort-Object Number, Name -Unique
    foreach ($d in $districts) {
        $existingId = Invoke-SqlScalar `
            -Query   "SELECT TOP 1 CAST(Id AS NVARCHAR(36)) FROM Districts WHERE CompanyId='$CompanyId' AND Number=$($d.Number)" `
            -ConnStr $ConnectionString
        if ($existingId) {
            $districtIdsByNumber[$d.Number] = $existingId
            continue
        }
        $id   = [Guid]::NewGuid().ToString()
        $name = $d.Name -replace "'", "''"
        # District 5 (Chicago-Naperville-Elgin) is ChicagoLand; everything
        # else stays false unless an admin flips it via the API later.
        $isChicagoLand = if ($d.Number -eq 5) { 1 } else { 0 }
        $sql  = @"
INSERT INTO Districts (Id, CompanyId, Number, Name, IsChicagoLand, IsActive, CreatedAt, UpdatedAt)
VALUES ('$id', '$CompanyId', $($d.Number), N'$name', $isChicagoLand, 1, '$now', '$now')
"@
        try {
            Invoke-Sql -Query $sql -ConnStr $ConnectionString | Out-Null
            $districtIdsByNumber[$d.Number] = $id
            $districtsInserted++
        } catch {
            Write-Warn "Failed to insert district $($d.Number) ($name): $_"
        }
    }
    # Sync IsChicagoLand on existing District rows so re-runs converge on the
    # seed truth (District 5 = ChicagoLand) without overwriting unrelated
    # fields. Admins can still flip individual districts via the API after.
    Invoke-Sql -Query @"
UPDATE Districts
SET    IsChicagoLand = 1,
       UpdatedAt     = SYSUTCDATETIME()
WHERE  CompanyId = '$CompanyId' AND Number = 5 AND IsChicagoLand <> 1
"@ -ConnStr $ConnectionString | Out-Null
    Write-Ok "$districtsInserted districts inserted ($($districtIdsByNumber.Count) tracked)."

    # ---- Zones -----------------------------------------------------
    # "Del. zone" arrives as "Zone 5.51"; strip the prefix so the stored code
    # stays comparable to the bare "5.51" used in zones.csv historically.
    $zoneIdsByKey  = @{}
    $zonesInserted = 0
    $zones = $rows |
        Where-Object { $_.'Del. zone' -and $_.'District #' } |
        ForEach-Object {
            [pscustomobject]@{
                Code           = ($_.'Del. zone' -replace '^\s*Zone\s+', '').Trim()
                DistrictNumber = [int][double]$_.'District #'.Trim()
            }
        } |
        Sort-Object DistrictNumber, Code -Unique
    foreach ($z in $zones) {
        $districtId = $districtIdsByNumber[$z.DistrictNumber]
        if (-not $districtId) {
            Write-Warn "No district for $($z.DistrictNumber); skipping zone $($z.Code)"
            continue
        }
        $code = $z.Code -replace "'", "''"
        $key  = "$districtId|$($z.Code)"
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
            Write-Warn "Failed to insert zone $($z.Code) under district $($z.DistrictNumber): $_"
        }
    }
    Write-Ok "$zonesInserted zones inserted ($($zoneIdsByKey.Count) tracked)."

    # ---- Stores ----------------------------------------------------
    # Two-step insert per row (store row, then StoreCompanies link) and a
    # ZoneId UPDATE pass for rows already present. The license number is the
    # natural key — re-runs link the store to its zone without re-inserting.
    $existingCount = Invoke-SqlScalar -Query @"
SELECT COUNT(*) FROM Stores s
WHERE  s.IsActive = 1
  AND  EXISTS (SELECT 1 FROM StoreCompanies sc WHERE sc.StoreId = s.Id AND sc.CompanyId = '$CompanyId')
"@ -ConnStr $ConnectionString
    $skipInserts = $existingCount -gt 0
    if ($skipInserts) {
        Write-Warn "$existingCount stores already exist for this company. Skipping store inserts; still syncing ZoneId."
    }

    $storesInserted = 0
    $storesLinked   = 0
    $skippedRows    = 0
    foreach ($r in $rows) {
        $rawDisp    = if ($r.'Disp')      { $r.'Disp'.Trim() }      else { '' }
        $rawLicense = if ($r.'License #') { $r.'License #'.Trim() } else { '' }
        if (-not $rawDisp -or -not $rawLicense) { $skippedRows++; continue }

        # "Customer - Name" — split on the first " - " so location names that
        # also contain hyphens (e.g. "Sunnyside - N. Aurora") stay intact.
        $dashIdx = $rawDisp.IndexOf(' - ')
        if ($dashIdx -ge 0) {
            $customer = $rawDisp.Substring(0, $dashIdx).Trim()
            $name     = $rawDisp.Substring($dashIdx + 3).Trim()
        } else {
            $customer = ''
            $name     = $rawDisp
        }

        $address = if ($r.'Address') { $r.'Address'.Trim() } else { '' }
        $city    = if ($r.'City')    { $r.'City'.Trim() }    else { '' }
        $zip     = if ($r.'Zip')     { $r.'Zip'.Trim() }     else { '' }
        $county  = if ($r.'County')  { $r.'County'.Trim() }  else { '' }

        $districtNumber = if ($r.'District #') { [int][double]$r.'District #'.Trim() } else { 0 }
        $zoneCode       = if ($r.'Del. zone')  { ($r.'Del. zone' -replace '^\s*Zone\s+', '').Trim() } else { '' }
        $districtId     = $districtIdsByNumber[$districtNumber]
        $zoneId         = if ($districtId -and $zoneCode) { $zoneIdsByKey["$districtId|$zoneCode"] } else { $null }
        $zoneIdSql      = if ($zoneId) { "'$zoneId'" } else { 'NULL' }

        $customerSql = $customer -replace "'", "''"
        $nameSql     = $name -replace "'", "''"
        $addressSql  = $address -replace "'", "''"
        $citySql     = $city -replace "'", "''"
        $countySql   = $county -replace "'", "''"
        $licenseSql  = $rawLicense -replace "'", "''"

        if (-not $skipInserts) {
            $id  = [Guid]::NewGuid().ToString()
            $sql = @"
INSERT INTO Stores (Id, Customer, Name, Address, City, State, Zip, County, LicenseNumber, ZoneId, IsActive, CreatedAt, UpdatedAt)
VALUES ('$id', N'$customerSql', N'$nameSql', N'$addressSql', N'$citySql', 'IL', '$zip', N'$countySql', '$licenseSql', $zoneIdSql, 1, '$now', '$now');
INSERT INTO StoreCompanies (StoreId, CompanyId) VALUES ('$id', '$CompanyId');
"@
            try {
                Invoke-Sql -Query $sql -ConnStr $ConnectionString | Out-Null
                $storesInserted++
            } catch {
                Write-Warn "Failed to import store: $nameSql - $_"
            }
        } elseif ($zoneId) {
            $sql = @"
UPDATE s
SET    s.ZoneId    = '$zoneId',
       s.UpdatedAt = SYSUTCDATETIME()
FROM   Stores s
WHERE  s.LicenseNumber = N'$licenseSql'
  AND  EXISTS (SELECT 1 FROM StoreCompanies sc WHERE sc.StoreId = s.Id AND sc.CompanyId = '$CompanyId')
  AND (s.ZoneId IS NULL OR s.ZoneId <> '$zoneId')
"@
            try {
                $updated = Invoke-Sql -Query $sql -ConnStr $ConnectionString
                if ($updated -gt 0) { $storesLinked += $updated }
            } catch {
                Write-Warn "Failed to link store license $licenseSql to zone ${zoneCode}: $_"
            }
        }
    }
    if ($skippedRows -gt 0) { Write-Warn "Skipped $skippedRows row(s) with blank name or license." }
    if ($storesInserted -gt 0) { Write-Ok "$storesInserted stores imported." }
    if ($storesLinked  -gt 0) { Write-Ok "Linked $storesLinked existing store(s) to their zone." }
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
        Write-Warn "$existingCount warehouses already exist for this company. Skipping inserts; syncing IsChicagoLand from CSV..."
        $synced = 0
        foreach ($line in $dataLines) {
            $fields = [System.Collections.Generic.List[string]]::new()
            $inQuote = $false; $field = ""
            foreach ($ch in $line.ToCharArray()) {
                if ($ch -eq '"') { $inQuote = -not $inQuote }
                elseif ($ch -eq ',' -and -not $inQuote) { $fields.Add($field); $field = "" }
                else { $field += $ch }
            }
            $fields.Add($field)
            if ($fields.Count -lt 10) { continue }

            $license = ($fields[1]).Trim() -replace "'", "''"
            if (-not $license) { continue }
            $raw = $fields[9].Trim()
            $isChicagoLand = if ($raw -eq '1' -or $raw -ieq 'true') { 1 } else { 0 }

            $sql = @"
UPDATE w
SET    w.IsChicagoLand = $isChicagoLand,
       w.UpdatedAt     = SYSUTCDATETIME()
FROM   Warehouses w
WHERE  w.LicenseNumber = N'$license'
  AND  EXISTS (SELECT 1 FROM WarehouseCompanies wc WHERE wc.WarehouseId = w.Id AND wc.CompanyId = '$CompanyId')
  AND  w.IsChicagoLand <> $isChicagoLand
"@
            try {
                $rows = Invoke-Sql -Query $sql -ConnStr $ConnectionString
                if ($rows -gt 0) { $synced += $rows }
            } catch {
                Write-Warn "Failed to sync IsChicagoLand for warehouse license ${license}: $_"
            }
        }
        if ($synced -gt 0) { Write-Ok "Synced IsChicagoLand on $synced existing warehouse row(s)." }
    } else {
        $imported = 0
        $now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff")

        # Header: LegacyLicense,License,BusinessName,FullAddr,AKA,Address,City,State,Zip,IsChicagoLand
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
            # IsChicagoLand lives in column index 9 (added 2026-05-25); fall
            # back to 0 for older CSVs that don't carry it.
            $isChicagoLand = 0
            if ($fields.Count -ge 10 -and -not [string]::IsNullOrWhiteSpace($fields[9])) {
                $raw = $fields[9].Trim()
                if ($raw -eq '1' -or $raw -ieq 'true') { $isChicagoLand = 1 }
            }

            $sql = @"
INSERT INTO Warehouses (Id, BusinessName, AlternateName, Address, City, State, Zip, LicenseNumber, LegacyLicenseNumber, IsChicagoLand, IsActive, CreatedAt, UpdatedAt)
VALUES ('$id', N'$businessName', N'$alternateName', N'$addressVal', N'$city', '$state', '$zip', '$license', '$legacyLicense', $isChicagoLand, 1, '$now', '$now');
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
        Write-Warn "$existingCount hubs already exist for this company. Backfilling lat/lng + IsTransferSite from CSV..."
        $backfilled = 0
        foreach ($h in $hubs) {
            $name = $h.'Name' -replace "'", "''"

            if (-not ([string]::IsNullOrWhiteSpace($h.Latitude) -or [string]::IsNullOrWhiteSpace($h.Longitude))) {
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
                    Write-Warn "Failed to backfill hub coordinates '$name': $_"
                }
            }

            # Sync IsTransferSite + IsChicagoLand flags from CSV onto existing
            # rows so designations can be flipped without a redeploy.
            if ($h.PSObject.Properties['IsTransferSite']) {
                $isTransferSite = if ([bool]::Parse($h.IsTransferSite)) { 1 } else { 0 }
                $sql = @"
UPDATE Hubs
SET    IsTransferSite = $isTransferSite,
       UpdatedAt      = SYSUTCDATETIME()
WHERE  CompanyId      = '$CompanyId'
  AND  Name           = N'$name'
  AND  IsTransferSite <> $isTransferSite;
"@
                try {
                    Invoke-Sql -Query $sql -ConnStr $ConnectionString | Out-Null
                } catch {
                    Write-Warn "Failed to sync IsTransferSite for hub '$name': $_"
                }
            }
            if ($h.PSObject.Properties['IsChicagoLand']) {
                $isChicagoLand = if ([bool]::Parse($h.IsChicagoLand)) { 1 } else { 0 }
                $sql = @"
UPDATE Hubs
SET    IsChicagoLand = $isChicagoLand,
       UpdatedAt     = SYSUTCDATETIME()
WHERE  CompanyId     = '$CompanyId'
  AND  Name          = N'$name'
  AND  IsChicagoLand <> $isChicagoLand;
"@
                try {
                    Invoke-Sql -Query $sql -ConnStr $ConnectionString | Out-Null
                } catch {
                    Write-Warn "Failed to sync IsChicagoLand for hub '$name': $_"
                }
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
            $isTransferSite = if ($h.PSObject.Properties['IsTransferSite'] -and [bool]::Parse($h.IsTransferSite)) { 1 } else { 0 }
            $isChicagoLand  = if ($h.PSObject.Properties['IsChicagoLand']  -and [bool]::Parse($h.IsChicagoLand))  { 1 } else { 0 }

            $sql = @"
INSERT INTO Hubs (Id, CompanyId, Name, Address, City, State, Zip, Latitude, Longitude, IsTransferSite, IsChicagoLand, IsActive, CreatedAt, UpdatedAt)
VALUES ('$id', '$CompanyId', N'$name', N'$address', N'$city', '$state', '$zip', $latSql, $lngSql, $isTransferSite, $isChicagoLand, 1, '$now', '$now')
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
