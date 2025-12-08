#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NGINX_ROOT="/var/www/html"

echo "[buildall] Ensuring base packages (nginx, node, npm, curl, rsync, zip, unzip, screen)..."
sudo apt update
sudo apt install -y nginx nodejs npm curl rsync zip unzip screen

if [ -d "$ROOT/.git" ]; then
  echo "[buildall] Pulling latest from git..."
  cd "$ROOT"
  git pull --rebase || true
else
  echo "[buildall] WARNING: No .git directory found at $ROOT; skipping git pull."
fi

echo "[buildall] Building admin UI..."
cd "$ROOT/node-host/admin-ui"
ADMIN_OUT="$ROOT/node-host/public/admin"
sudo rm -rf "$ADMIN_OUT"
mkdir -p "$ADMIN_OUT"
export npm_config_engine_strict=false
sudo npm install
sudo npm run build

echo "[buildall] Deploying static assets to nginx root: $NGINX_ROOT"
sudo mkdir -p "$NGINX_ROOT"
sudo find "$NGINX_ROOT" -mindepth 1 -delete
sudo rsync -a "$ROOT/node-host/public/admin/" "$NGINX_ROOT/"
sudo systemctl reload nginx || sudo systemctl restart nginx

echo "[buildall] Installing backend deps and starting server..."
cd "$ROOT/node-host"
sudo npm install --omit=dev
SCREEN_NAME="overlayhud-backend"
screen -S "$SCREEN_NAME" -X quit >/dev/null 2>&1 || true
pkill -f "node server.js" >/dev/null 2>&1 || true
screen -dmS "$SCREEN_NAME" bash -lc "cd \"$ROOT/node-host\" && node server.js"

echo "[buildall] Done."

