## OverlayHud Node Host

Serve the published `OverlayHud-win-x64.zip` from a simple Express server so that end users can install via PowerShell without touching GitHub Releases.

### 1. Build the HUD release

From the repo root:

```powershell
dotnet publish OverlayHud/OverlayHud.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false

$publishDir = "OverlayHud/OverlayHud/bin/Release/net8.0-windows/win-x64/publish"
Compress-Archive -Path "$publishDir/*" -DestinationPath "OverlayHud-win-x64.zip" -Force
Move-Item OverlayHud-win-x64.zip node-host/public/ -Force
```

The server automatically serves whichever ZIP is present under `node-host/public/` (or `OVERLAYHUD_ZIP` if you rename it).

### 2. Run the host

```powershell
cd node-host
npm install # already done once in repo, keeps package-lock
npm start   # local + automatic localtunnel link

# Need to disable/override the tunnel?
PUBLIC_TUNNEL=false npm start

# Want a specific, best-effort subdomain?
npm run start:public-sub
```

Optional env vars:

| Variable               | Default                  | Description                                      |
| ---------------------- | ------------------------ | ------------------------------------------------ |
| `PORT`                 | `1337`                   | Port to bind locally                             |
| `HOST`                 | `0.0.0.0`                | Interface to bind                                |
| `OVERLAYHUD_ZIP`       | `OverlayHud-win-x64.zip` | Override file name under `public/`               |
| `PUBLIC_TUNNEL`        | `true`                   | Set to `false` to skip the localtunnel link      |
| `PUBLIC_SUBDOMAIN`     | _unset_                  | Optional custom localtunnel subdomain            |
| `PUBLIC_TUNNEL_REGION` | _unset_                  | Force a localtunnel POP region (e.g. `us`, `eu`) |

### 3. PowerShell consumer command

```powershell
irm http://your-host:1337/install.ps1 | iex
```

The generated script downloads the ZIP into a random folder under `%TEMP%`, launches `OverlayHud.exe`, schedules cleanup once the HUD exits, and intentionally closes the calling PowerShell session so nothing is left running. Make sure clients have the .NET 8 Desktop Runtime + WebView2 runtime installed for the app to launch successfully.
## OverlayHud Node Host

Serve the published `OverlayHud-win-x64.zip` from a simple Express server so that end users can install via PowerShell without touching GitHub Releases.

### 1. Build the HUD release

From the repo root:

```powershell
dotnet publish OverlayHud/OverlayHud.csproj `
  -c Release `
  -r win-x64 `
  -p:PublishSingleFile=true `
  --self-contained false

$publishDir = "OverlayHud/OverlayHud/bin/Release/net8.0-windows/win-x64/publish"
Compress-Archive -Path "$publishDir/*" -DestinationPath "OverlayHud-win-x64.zip" -Force
Move-Item OverlayHud-win-x64.zip node-host/public/ -Force
```

The server automatically serves whichever ZIP is present under `node-host/public/` (or `OVERLAYHUD_ZIP` if you rename it).

### 2. Run the host

```powershell
cd node-host
npm install # already done once in repo, keeps package-lock
npm start   # local only

# Need an instant public link? This opens a localtunnel URL:
npm run start:public
# Or pin a subdomain (best-effort, may collide):
npm run start:public-sub
```

Optional env vars:

| Variable              | Default                    | Description                                      |
| --------------------- | -------------------------- | ------------------------------------------------ |
| `PORT`                | `8080`                     | Port to bind locally                             |
| `HOST`                | `0.0.0.0`                  | Interface to bind                                |
| `OVERLAYHUD_ZIP`      | `OverlayHud-win-x64.zip`   | Override file name under `public/`               |
| `PUBLIC_TUNNEL`       | `false`                    | Set to `true` to auto-create a localtunnel link  |
| `PUBLIC_SUBDOMAIN`    | _unset_                    | Optional custom localtunnel subdomain            |
| `PUBLIC_TUNNEL_REGION`| _unset_                    | Force a localtunnel POP region (e.g. `us`, `eu`) |

### 3. PowerShell consumer command

```powershell
$downloadUrl = "http://your-host:8080/OverlayHud-win-x64.zip"
$installRoot = Join-Path $env:LOCALAPPDATA "OverlayHudDeploy"
$zipPath     = Join-Path $env:TEMP          "OverlayHud.zip"

Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath
if (Test-Path $installRoot) { Remove-Item $installRoot -Recurse -Force }
Expand-Archive -Path $zipPath -DestinationPath $installRoot
Remove-Item $zipPath

$exePath = Join-Path $installRoot "OverlayHud.exe"
Start-Process -FilePath $exePath
```

Share that snippet with teammates or automation scripts—no GitHub CLI, no admin rights required. Just keep the ZIP fresh under `node-host/public/`.*** End Patch}"""

