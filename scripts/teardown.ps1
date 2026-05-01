# ──────────────────────────────────────────────────────────────
# teardown.ps1 — Completely remove the Atheres Atlas project
#                from Docker (containers, images, volumes, network)
# ──────────────────────────────────────────────────────────────

$Project = "atheres-atlas"

# docker compose reads docker-compose.yml from cwd. Anchor to the repo root
# (parent of scripts/) so the script works no matter where it's invoked.
Set-Location (Split-Path -Parent $PSScriptRoot)

Write-Host "=== Stopping and removing containers, networks, and volumes ===" -ForegroundColor Cyan
docker compose -p $Project down --volumes --remove-orphans 2>$null

Write-Host "=== Removing project images ===" -ForegroundColor Cyan
$images = docker compose -p $Project config --images 2>$null
foreach ($img in $images) {
    if ($img) {
        Write-Host "  Removing image: $img"
        docker rmi -f $img 2>$null
    }
}

Write-Host "=== Removing any dangling images from the build ===" -ForegroundColor Cyan
docker image prune -f --filter "label=com.docker.compose.project=$Project" 2>$null

Write-Host "=== Done. All $Project containers, images, volumes, and networks have been removed. ===" -ForegroundColor Green
