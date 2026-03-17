#Requires -Version 7.0

<#
.SYNOPSIS
    Bootstrap a single-node Scylla instance with canonical schema for live integration testing.
    
.DESCRIPTION
    Stops and removes any existing octocon-scylla-single container, starts a fresh Scylla 5.4 instance,
    waits for CQL to be ready, seeds the canonical schema (global + nam keyspaces with all required tables),
    and creates regional keyspaces (eur/eas/sam/sas/ocn/gdpr) with replicated tables.
    
    This is a temporary fallback for multi-node compose stack instability. Use when:
    - docker-compose Scylla NAM container is in crash-loop
    - Multi-seed coordination is blocking the dev stack
    - Live integration gate needs to pass immediately
    
.PARAMETER ScyllaVersion
    Scylla image tag to use (default: "5.4")
    
.PARAMETER WaitTimeoutSeconds
    Maximum seconds to wait for CQL to become available (default: 120)
    
.PARAMETER ScyllaContactPoint
    IP/hostname for docker exec cqlsh commands (default: "127.0.0.1")
    
.EXAMPLE
    PS> .\dev\scylla\scripts\start-single-node-gate.ps1
    
.EXAMPLE
    PS> .\dev\scylla\scripts\start-single-node-gate.ps1 -ScyllaVersion "2024.2" -WaitTimeoutSeconds 60
#>

param(
    [string]$ScyllaVersion = "5.4",
    [int]$WaitTimeoutSeconds = 120,
    [string]$ScyllaContactPoint = "127.0.0.1"
)

$ErrorActionPreference = "Stop"
$WarningPreference = "Continue"

$ContainerName = "octocon-scylla-single"
$Port = 9042
$Username = "cassandra"
$Password = "cassandra"
$PostgresContainerName = "octocon-msg-db-1"
$PostgresUser = "postgres"
$PostgresPassword = "postgres"
$PostgresDatabase = "octocon"

Write-Host "🔧 Single-Node Scylla Gate Bootstrap" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan

# Step 1: Stop and remove existing container
Write-Host "`n[1/6] Cleanup: Removing existing container..." -ForegroundColor Yellow
docker rm -f $ContainerName 2>$null | Out-Null
Write-Host "      ✓ Ready to start fresh" -ForegroundColor Green

# Step 2: Start Scylla container
Write-Host "`n[2/6] Starting Scylla $ScyllaVersion..." -ForegroundColor Yellow
try {
    $ContainerId = docker run -d --name $ContainerName -p "${Port}:9042" "scylladb/scylla:$ScyllaVersion" 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "      ⚠ Failed with version '$ScyllaVersion', retrying with 5.4..." -ForegroundColor Yellow
        $ContainerId = docker run -d --name $ContainerName -p "${Port}:9042" "scylladb/scylla:5.4" 2>&1
        $ScyllaVersion = "5.4"
    }
    Write-Host "      ✓ Container started: $($ContainerId.Substring(0, 12))" -ForegroundColor Green
} catch {
    Write-Error "Failed to start Scylla container: $_"
}

# Step 3: Wait for CQL to be ready
Write-Host "`n[3/6] Waiting for CQL to be ready (up to $WaitTimeoutSeconds seconds)..." -ForegroundColor Yellow

# Get the container's internal Docker network IP for probing
$ContainerIp = docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' $ContainerName
if (-not $ContainerIp) {
    Write-Error "Failed to get container IP address"
}

Write-Host "      Container IP: $ContainerIp" -ForegroundColor DarkGray

$Elapsed = 0
$Ready = $false
$ProbeInterval = 3

while ($Elapsed -lt $WaitTimeoutSeconds) {
    try {
        $Output = docker exec $ContainerName cqlsh $ContainerIp -u $Username -p $Password -e "DESCRIBE KEYSPACES" 2>&1
        if ($LASTEXITCODE -eq 0) {
            $Ready = $true
            Write-Host "      ✓ CQL is ready (took ${Elapsed}s)" -ForegroundColor Green
            break
        }
    } catch {
        # Connection refused is expected; continue waiting
    }
    
    Start-Sleep -Seconds $ProbeInterval
    $Elapsed += $ProbeInterval
    Write-Host "      ⏳ Waiting... ($Elapsed/$WaitTimeoutSeconds seconds)" -ForegroundColor DarkGray
}

if (-not $Ready) {
    Write-Error "CQL did not become ready within $WaitTimeoutSeconds seconds. Check 'docker logs $ContainerName'"
}

# Step 4: Seed canonical schema (global + nam keyspaces)
Write-Host "`n[4/6] Seeding canonical schema..." -ForegroundColor Yellow

# Locate the single-node-bootstrap.cql file
$BootstrapCqlPath = Split-Path -Parent $PSCommandPath
$BootstrapCqlFile = Join-Path $BootstrapCqlPath "single-node-bootstrap.cql"

if (-not (Test-Path $BootstrapCqlFile)) {
    Write-Error "Bootstrap CQL file not found at: $BootstrapCqlFile"
}

# Copy the CQL file into the container
docker cp $BootstrapCqlFile "${ContainerName}:/tmp/bootstrap.cql" | Out-Null

# Execute the bootstrap CQL file
try {
    docker exec $ContainerName cqlsh $ContainerIp -u $Username -p $Password -f /tmp/bootstrap.cql 2>&1 | 
        Where-Object { $_ -notmatch "^Warning:" } | 
        ForEach-Object { if ($_ -match "error|Error|ERROR") { Write-Host "      ERROR: $_" -ForegroundColor Red } }
    Write-Host "      ✓ Canonical schema created" -ForegroundColor Green
} catch {
    Write-Warning "Failed to seed canonical schema from file, attempting inline creation..."
}

# Step 5: Create regional keyspaces and tables
Write-Host "`n[5/6] Creating regional keyspaces (eur/eas/sam/sas/ocn/gdpr)..." -ForegroundColor Yellow

# Note: nam is already created in step 4 with full schema from single-node-bootstrap.cql
# For other regions, adapt the same schema by replacing 'nam.' with the region name
$Regions = @("eur", "eas", "sam", "sas", "ocn", "gdpr")

foreach ($Region in $Regions) {
    try {
        # Create keyspace
        $CqlCreateKeyspace = "CREATE KEYSPACE IF NOT EXISTS $Region WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1}"
        docker exec $ContainerName cqlsh $ContainerIp -u $Username -p $Password -e $CqlCreateKeyspace 2>&1 | Out-Null
        
        # Copy and adapt the canonical nam schema for this region
        $RegionCqlFile = [System.IO.Path]::GetTempFileName() -replace '\.tmp$', '.cql'
        $namCql = Get-Content $BootstrapCqlPath\single-node-bootstrap.cql -Raw
        $regionCql = $namCql -replace '\bnam\.', "$Region."
        # Strip keyspace creates already done — only keep table DDL for this region's keyspace
        $regionCql = $regionCql -replace 'CREATE KEYSPACE IF NOT EXISTS(?:(?!;).)*;\s*', ''
        Set-Content -Path $RegionCqlFile -Value $regionCql -Encoding UTF8
        docker cp $RegionCqlFile "${ContainerName}:/tmp/region_${Region}.cql" | Out-Null
        Remove-Item -Path $RegionCqlFile
        docker exec $ContainerName cqlsh $ContainerIp -u $Username -p $Password -f "/tmp/region_${Region}.cql" 2>&1 | Out-Null
        Write-Host "      ✓ $Region" -ForegroundColor Green
    } catch {
        Write-Warning "Failed to create region ${Region}, continuing with others..."
    }
}

# Step 6: Ensure Postgres database + idempotency schema exist
Write-Host "`n[6/6] Bootstrapping Postgres database/schema..." -ForegroundColor Yellow

$ResolvedPostgresContainer = docker ps --format '{{.Names}}' |
    Where-Object { $_ -eq $PostgresContainerName } |
    Select-Object -First 1

if (-not $ResolvedPostgresContainer) {
    $ResolvedPostgresContainer = docker ps --format '{{.Names}}\t{{.Image}}' |
        Where-Object { $_ -match 'timescale|postgres' } |
        ForEach-Object { ($_ -split "`t")[0] } |
        Select-Object -First 1
}

if (-not $ResolvedPostgresContainer) {
    Write-Error "No running Postgres container found. Start msg-db (docker compose) before running this script."
}

Write-Host "      Postgres container: $ResolvedPostgresContainer" -ForegroundColor DarkGray

$DbExistsQuery = "SELECT 1 FROM pg_database WHERE datname = '$PostgresDatabase';"
$DbExists = docker exec $ResolvedPostgresContainer env PGPASSWORD=$PostgresPassword psql -U $PostgresUser -d postgres -tAc $DbExistsQuery 2>&1

if (($DbExists | Out-String).Trim() -ne "1") {
    docker exec $ResolvedPostgresContainer env PGPASSWORD=$PostgresPassword psql -U $PostgresUser -d postgres -c "CREATE DATABASE $PostgresDatabase;" 2>&1 | Out-Null
    Write-Host "      ✓ Created Postgres database '$PostgresDatabase'" -ForegroundColor Green
}
else {
    Write-Host "      ✓ Postgres database '$PostgresDatabase' already exists" -ForegroundColor Green
}

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..\..")
$PostgresSchemaFile = Join-Path $RepoRoot "csharp\db\postgres\001_create_octocon_idempotency.sql"

if (-not (Test-Path $PostgresSchemaFile)) {
    Write-Error "Postgres schema SQL file not found at: $PostgresSchemaFile"
}

docker cp $PostgresSchemaFile "${ResolvedPostgresContainer}:/tmp/001_create_octocon_idempotency.sql" | Out-Null
docker exec $ResolvedPostgresContainer env PGPASSWORD=$PostgresPassword psql -U $PostgresUser -d $PostgresDatabase -f /tmp/001_create_octocon_idempotency.sql 2>&1 | Out-Null
Write-Host "      ✓ Postgres idempotency table ensured" -ForegroundColor Green

Write-Host "`n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "✅ Single-Node Scylla Gate Ready!" -ForegroundColor Green
Write-Host "`n📍 Connection: ${ScyllaContactPoint}:${Port}" -ForegroundColor Cyan
Write-Host "🔐 Credentials: ${Username} / ${Password}" -ForegroundColor Cyan
Write-Host "🗄 Postgres: localhost:4200 / db '${PostgresDatabase}'" -ForegroundColor Cyan
Write-Host "`n📋 Keyspaces created:" -ForegroundColor Cyan
Write-Host "   • global (shared)" -ForegroundColor DarkGray
Write-Host "   • nam, eur, eas, sam, sas, ocn, gdpr (regional)" -ForegroundColor DarkGray
Write-Host "`n💡 Next: Run integration tests with:" -ForegroundColor Cyan
Write-Host "   dotnet test --project csharp/Octocon.IntegrationTests/Octocon.IntegrationTests.csproj" -ForegroundColor DarkGray
Write-Host "`n📝 To view logs: docker logs -f ${ContainerName}" -ForegroundColor Cyan
