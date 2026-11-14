# PoC (blueprint 24.1 / 26.2): silent setup of PostgreSQL as a Windows service for a KITE server,
# then the KITE bootstrap and a migration check. Runs on GitHub's windows-latest runner, which
# ships PostgreSQL binaries; the real installer (Phase 14) bundles the binaries itself.
# Usage: pwsh -File install-service-poc.ps1 [-PgBin "C:\Program Files\PostgreSQL\17\bin"]
param(
  [string]$PgBin = "",
  [string]$DataDir = "$env:RUNNER_TEMP\kite-pgdata",
  [int]$Port = 55432,
  [string]$ServiceName = "KitePostgresPoc"
)
$ErrorActionPreference = "Stop"

if (-not $PgBin) {
  $PgBin = (Get-ChildItem "C:\Program Files\PostgreSQL" -Directory | Sort-Object { [int]$_.Name } -Descending | Select-Object -First 1).FullName + "\bin"
}
Write-Host "Using PostgreSQL binaries in $PgBin"
$superPw = [Convert]::ToBase64String((1..24 | ForEach-Object { Get-Random -Maximum 256 }))
$pwFile = Join-Path $env:RUNNER_TEMP "kite-pw.txt"
Set-Content -Path $pwFile -Value $superPw -NoNewline -Encoding ascii

# 1. Initialise a cluster: UTF-8, scram-sha-256, listening on localhost only.
if (Test-Path $DataDir) { Remove-Item $DataDir -Recurse -Force }
& "$PgBin\initdb.exe" -D $DataDir -U postgres --pwfile=$pwFile -E UTF8 --locale=C --auth=scram-sha-256 | Out-Host
Add-Content "$DataDir\postgresql.conf" "`nlisten_addresses = 'localhost'`nport = $Port`n"

# 2. Register and start it as a Windows service (automatic start in production).
& "$PgBin\pg_ctl.exe" register -N $ServiceName -D $DataDir -S auto | Out-Host
Start-Service $ServiceName
$svc = Get-Service $ServiceName
if ($svc.Status -ne "Running") { throw "Service did not start" }
Write-Host "Service $ServiceName is $($svc.Status)"

try {
  # 3. Create the KITE database and run the bootstrap (roles, extensions) as superuser.
  $env:PGPASSWORD = $superPw
  & "$PgBin\psql.exe" -h localhost -p $Port -U postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE kite" | Out-Host
  & "$PgBin\psql.exe" -h localhost -p $Port -U postgres -d kite -v ON_ERROR_STOP=1 -f "$PSScriptRoot\..\..\db\bootstrap\00_bootstrap.sql" | Out-Host

  # 4. Apply every migration as kite_migrator and confirm the schema is complete.
  & "$PgBin\psql.exe" -h localhost -p $Port -U postgres -d kite -v ON_ERROR_STOP=1 -c "ALTER ROLE kite_migrator PASSWORD 'poc-migrator'" | Out-Host
  $env:PGPASSWORD = "poc-migrator"
  Get-ChildItem "$PSScriptRoot\..\..\db\migrations\V*.sql" | Sort-Object Name | ForEach-Object {
    Write-Host "  applying $($_.Name)"
    & "$PgBin\psql.exe" -h localhost -p $Port -U kite_migrator -d kite -v ON_ERROR_STOP=1 -1 -q -f $_.FullName | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Migration $($_.Name) failed" }
  }
  $tables = & "$PgBin\psql.exe" -h localhost -p $Port -U kite_migrator -d kite -tAc "SELECT count(*) FROM information_schema.tables WHERE table_schema IN ('core','iam','tax','acct','audit','sync')"
  $perms = & "$PgBin\psql.exe" -h localhost -p $Port -U kite_migrator -d kite -tAc "SELECT count(*) FROM iam.permission"
  Write-Host "Tables: $tables, permissions: $perms"
  if ([int]$tables -lt 20 -or [int]$perms -lt 35) { throw "Unexpected schema: $tables tables, $perms permissions" }

  # 5. A restart of the service keeps the data (durability across reboots).
  Restart-Service $ServiceName
  $env:PGPASSWORD = "poc-migrator"
  $again = & "$PgBin\psql.exe" -h localhost -p $Port -U kite_migrator -d kite -tAc "SELECT count(*) FROM iam.permission"
  if ([int]$again -ne [int]$perms) { throw "Data missing after restart" }
  Write-Host "PostgreSQL Windows service PoC: ALL CHECKS PASSED"
}
finally {
  Stop-Service $ServiceName -ErrorAction SilentlyContinue
  & "$PgBin\pg_ctl.exe" unregister -N $ServiceName | Out-Host
  Remove-Item $pwFile -Force -ErrorAction SilentlyContinue
}
