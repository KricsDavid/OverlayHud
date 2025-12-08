#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_DIR="$ROOT/OverlayHud/OverlayHud/bin/Release/net8.0-windows/win-x64/publish"
ZIP_PATH="$ROOT/node-host/public/OverlayHud-win-x64.zip"
WEBVIEW_URL="https://go.microsoft.com/fwlink/p/?LinkId=2124701"
NGINX_ROOT="${NGINX_ROOT:-/var/www/overlayhud}"

echo "[startall] Building admin UI..."
cd "$ROOT/node-host/admin-ui"
sudo npm ci
sudo npm run build

echo "[startall] Publishing .NET app (self-contained)..."
cd "$ROOT"
dotnet publish OverlayHud/OverlayHud.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false

echo "[startall] Fetching WebView2 runtime bootstrapper..."
mkdir -p "$PUBLISH_DIR"
curl -fL "$WEBVIEW_URL" -o "$PUBLISH_DIR/MicrosoftEdgeWebView2RuntimeInstaller.exe"

echo "[startall] Creating zip payload..."
rm -f "$ZIP_PATH"
(cd "$PUBLISH_DIR" && zip -r9 "$ZIP_PATH" .)

echo "[startall] Deploying static assets to nginx root: $NGINX_ROOT"
sudo mkdir -p "$NGINX_ROOT"
sudo rsync -a "$ZIP_PATH" "$NGINX_ROOT/"
sudo rsync -a "$ROOT/node-host/public/admin/" "$NGINX_ROOT/admin/"

echo "[startall] Installing backend deps and starting server..."
cd "$ROOT/node-host"
sudo npm ci --omit=dev
pkill -f "node server.js" >/dev/null 2>&1 || true
nohup node server.js > "$ROOT/node-host/server.log" 2>&1 &

echo "[startall] Done. Backend running; static files deployed."

