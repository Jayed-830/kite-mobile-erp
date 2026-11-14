# Builds the web app and copies it into the server's wwwroot (Windows).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location "$root\src\client"
try {
  npm ci
  npm run build
  $target = "$root\src\server\Kite.Host\wwwroot"
  if (Test-Path $target) { Remove-Item $target -Recurse -Force }
  Copy-Item "apps\web\dist" $target -Recurse
  Write-Host "Web app copied to $target"
} finally { Pop-Location }
