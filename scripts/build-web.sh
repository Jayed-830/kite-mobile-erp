#!/usr/bin/env bash
# Builds the web app and copies it into the server's wwwroot, so the KITE server serves it on the LAN.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT/src/client"
npm ci
npm run build
rm -rf "$ROOT/src/server/Kite.Host/wwwroot"
cp -r apps/web/dist "$ROOT/src/server/Kite.Host/wwwroot"
echo "Web app copied to src/server/Kite.Host/wwwroot"
