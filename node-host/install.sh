#!/usr/bin/env bash
set -euo pipefail

REPO_URL="https://github.com/KricsDavid/OverlayHud.git"
TARGET_DIR="${TARGET_DIR:-/opt/overlayhud}"
NGINX_ROOT="${NGINX_ROOT:-/var/www/overlayhud}"

echo "[install] Installing prerequisites (nginx, git, node, npm, zip tools)..."
sudo apt update
sudo apt install -y nginx git curl unzip rsync zip nodejs npm

if ! command -v dotnet >/dev/null 2>&1; then
  echo "[install] Warning: dotnet SDK not found. Install .NET 8 SDK before running startall."
fi

echo "[install] Cloning or updating repository..."
if [ ! -d "$TARGET_DIR/.git" ]; then
  sudo mkdir -p "$TARGET_DIR"
  sudo chown "$(whoami)":"$(whoami)" "$TARGET_DIR"
  git clone "$REPO_URL" "$TARGET_DIR"
else
  cd "$TARGET_DIR"
  git pull --rebase
fi

cd "$TARGET_DIR/node-host"
chmod +x startall.sh

echo "[install] Running startall.sh (build, deploy, start backend)..."
NGINX_ROOT="$NGINX_ROOT" bash ./startall.sh

echo "[install] Completed. Static assets in $NGINX_ROOT and backend running."

