import { useEffect, useMemo, useState } from "react";
import { ShieldCheck, Link2, Moon, Sun, KeyRound, RefreshCw, Copy, LogIn } from "lucide-react";
import { applyTheme, getInitialTheme, Theme } from "./theme";
import { createItem, createKey, fetchItems, fetchKeys, fetchDefaultItemId, login, DownloadItem, DownloadKey } from "./api";

type Mode = "items" | "keys";

function App() {
  const [theme, setTheme] = useState<Theme>(getInitialTheme());
  const [token, setToken] = useState<string>(() => localStorage.getItem("admin_jwt") || "");
  const [baseUrl, setBaseUrl] = useState<string>(() => localStorage.getItem("api_base") || window.location.origin);
  const [email, setEmail] = useState<string>(() => localStorage.getItem("admin_email") || "tomi0928@overlayhud.local");
  const [password, setPassword] = useState<string>("");
  const [mode, setMode] = useState<Mode>("items");
  const [items, setItems] = useState<DownloadItem[]>([]);
  const [keys, setKeys] = useState<DownloadKey[]>([]);
  const [defaultItemId, setDefaultItemId] = useState<number | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [newItem, setNewItem] = useState({ name: "", filePath: "", priceCents: 0 });
  const [newKey, setNewKey] = useState({ downloadItemId: 0, maxUses: 1, expiresAt: "" });

  useEffect(() => {
    applyTheme(theme);
  }, [theme]);

  const authSet = useMemo(() => token.trim().length > 0, [token]);

  const savePrefs = () => {
    localStorage.setItem("admin_jwt", token);
    localStorage.setItem("api_base", baseUrl);
    localStorage.setItem("admin_email", email);
  };

  const handleLogin = async () => {
    setError(null);
    if (!email.trim() || !password.trim()) {
      setError("Email and password required");
      return;
    }
    setLoading(true);
    const res = await login(baseUrl, email, password);
    setLoading(false);
    if (!res.ok) {
      setError(res.error);
    } else {
      setToken(res.data.token);
      localStorage.setItem("admin_jwt", res.data.token);
      localStorage.setItem("admin_email", email);
      setPassword("");
      loadItems();
      loadDefaultItem();
      loadKeys();
    }
  };

  const loadItems = async () => {
    if (!authSet) {
      setError("Set admin JWT first");
      return;
    }
    setLoading(true);
    setError(null);
    const res = await fetchItems(baseUrl, token);
    setLoading(false);
    if (!res.ok) {
      setError(res.error);
    } else {
      setItems(res.data);
    }
  };

  const loadDefaultItem = async () => {
    if (!authSet) return;
    const res = await fetchDefaultItemId(baseUrl, token);
    if (res.ok) {
      setDefaultItemId(res.data);
      if (!newKey.downloadItemId) {
        setNewKey((prev) => ({ ...prev, downloadItemId: res.data }));
      }
    }
  };

  const loadKeys = async () => {
    if (!authSet) {
      setError("Set admin JWT first");
      return;
    }
    setLoading(true);
    setError(null);
    const res = await fetchKeys(baseUrl, token);
    setLoading(false);
    if (!res.ok) {
      setError(res.error);
    } else {
      setKeys(res.data);
    }
  };

  const handleCreateItem = async () => {
    if (!newItem.name.trim() || !newItem.filePath.trim()) {
      setError("Name and file path required");
      return;
    }
    setLoading(true);
    setError(null);
    const res = await createItem(baseUrl, token, newItem);
    setLoading(false);
    if (!res.ok) {
      setError(res.error);
    } else {
      setNewItem({ name: "", filePath: "", priceCents: 0 });
      loadItems();
      loadDefaultItem();
    }
  };

  const handleCreateKey = async () => {
    if (!newKey.downloadItemId) {
      setError("Download item ID required");
      return;
    }
    setLoading(true);
    setError(null);
    const targetId = Number(newKey.downloadItemId || defaultItemId);
    if (!targetId) {
      setError("Default item not available; create an item first");
      return;
    }
    const res = await createKey(baseUrl, token, {
      downloadItemId: targetId,
      maxUses: Number(newKey.maxUses) || 1,
      expiresAt: newKey.expiresAt || null,
    });
    setLoading(false);
    if (!res.ok) {
      setError(res.error);
    } else {
      setNewKey({ downloadItemId: defaultItemId ?? 0, maxUses: 1, expiresAt: "" });
      loadKeys();
      navigator.clipboard?.writeText(res.data.key).catch(() => {});
    }
  };

  const copy = (text: string) => {
    navigator.clipboard?.writeText(text).catch(() => {});
  };

  return (
    <div className="page">
      <header className="header">
        <div className="flex">
          <ShieldCheck size={22} />
          <div>
            <div style={{ fontWeight: 700 }}>OverlayHud Admin</div>
            <div className="muted">Manage download items and keys</div>
          </div>
        </div>
        <div className="flex">
          <button
            className="btn secondary"
            onClick={() => {
              const next = theme === "dark" ? "light" : "dark";
              setTheme(next);
            }}
            title="Toggle theme"
          >
            {theme === "dark" ? <Sun size={16} /> : <Moon size={16} />}
            {theme === "dark" ? "Light" : "Dark"}
          </button>
        </div>
      </header>

      <div className="card" style={{ marginBottom: 16 }}>
        <div className="section-title">
          <KeyRound size={16} />
          Authentication
        </div>
        <div className="grid">
          <div className="stack">
            <label className="muted">API Base URL</label>
            <input
              className="input"
              value={baseUrl}
              onChange={(e) => setBaseUrl(e.target.value)}
              placeholder="http://localhost:8443"
            />
          </div>
          <div className="stack">
            <label className="muted">Admin JWT</label>
            <input
              className="input"
              value={token}
              onChange={(e) => setToken(e.target.value)}
              placeholder="Paste admin JWT"
            />
          </div>
          <div className="stack">
            <label className="muted">Email</label>
            <input
              className="input"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="tomi0928@overlayhud.local"
            />
            <label className="muted">Password</label>
            <input
              className="input"
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              placeholder="Enter password"
            />
          </div>
          <div className="stack" style={{ justifyContent: "flex-end" }}>
            <div className="flex wrap">
              <button className="btn secondary" onClick={savePrefs}>
                Save
              </button>
              <button className="btn" onClick={handleLogin} disabled={loading}>
                <LogIn size={16} />
                Login
              </button>
              <button className="btn" onClick={mode === "items" ? loadItems : () => { loadDefaultItem(); loadKeys(); }}>
                <RefreshCw size={16} />
                Refresh {mode === "items" ? "Items" : "Keys"}
              </button>
            </div>
            {error && <div className="muted" style={{ color: "#f97316" }}>{error}</div>}
          </div>
        </div>
      </div>

      <div className="flex" style={{ gap: 12, marginBottom: 12 }}>
        <button className={`btn ${mode === "items" ? "" : "secondary"}`} onClick={() => setMode("items")}>
          Download Items
        </button>
        <button className={`btn ${mode === "keys" ? "" : "secondary"}`} onClick={() => setMode("keys")}>
          Download Keys
        </button>
      </div>

      {mode === "items" ? (
        <div className="grid">
          <div className="card">
            <div className="section-title">
              <Link2 size={16} />
              Create Item
            </div>
            <div className="stack">
              <input
                className="input"
                placeholder="Name"
                value={newItem.name}
                onChange={(e) => setNewItem({ ...newItem, name: e.target.value })}
              />
              <input
                className="input"
                placeholder="File path (e.g. /OverlayHud-win-x64.zip)"
                value={newItem.filePath}
                onChange={(e) => setNewItem({ ...newItem, filePath: e.target.value })}
              />
              <input
                className="input"
                type="number"
                min={0}
                placeholder="Price cents (optional)"
                value={newItem.priceCents}
                onChange={(e) => setNewItem({ ...newItem, priceCents: Number(e.target.value) || 0 })}
              />
              <button className="btn" onClick={handleCreateItem} disabled={loading}>
                Create Item
              </button>
            </div>
          </div>
          <div className="card">
            <div className="section-title">
              <Link2 size={16} />
              Items
            </div>
            <div className="list">
              {items.map((it) => (
                <div className="row" key={it.id}>
                  <div className="stack" style={{ flex: 1 }}>
                    <strong>{it.name}</strong>
                    <div className="muted">{it.file_path}</div>
                    <div className="flex wrap">
                      <span className="pill">ID {it.id}</span>
                      <span className="pill">{it.active ? "Active" : "Inactive"}</span>
                      <span className="pill">{(it.price_cents / 100).toFixed(2)} USD</span>
                    </div>
                  </div>
                  <button className="btn secondary" onClick={() => copy(`${baseUrl}${it.file_path}`)}>
                    <Copy size={14} />
                    Copy Link
                  </button>
                </div>
              ))}
              {items.length === 0 && <div className="muted">No items loaded yet.</div>}
            </div>
          </div>
        </div>
      ) : (
        <div className="grid">
          <div className="card">
            <div className="section-title">
              <KeyRound size={16} />
              Create Key
            </div>
            <div className="stack">
              <input
                className="input"
                type="number"
                placeholder="Download Item ID"
                value={newKey.downloadItemId || ""}
                onChange={(e) => setNewKey({ ...newKey, downloadItemId: Number(e.target.value) })}
              />
              <input
                className="input"
                type="number"
                min={1}
                placeholder="Max uses"
                value={newKey.maxUses}
                onChange={(e) => setNewKey({ ...newKey, maxUses: Number(e.target.value) || 1 })}
              />
              <input
                className="input"
                type="text"
                placeholder="Expires at (ISO, optional)"
                value={newKey.expiresAt}
                onChange={(e) => setNewKey({ ...newKey, expiresAt: e.target.value })}
              />
              <div className="flex wrap">
                <button className="btn" onClick={handleCreateKey} disabled={loading}>
                  Create Key
                </button>
                <button
                  className="btn secondary"
                  onClick={() => {
                    if (defaultItemId) {
                      setNewKey((prev) => ({ ...prev, downloadItemId: defaultItemId }));
                    }
                  }}
                  disabled={!defaultItemId}
                >
                  Use Default Item {defaultItemId ?? ""}
                </button>
              </div>
            </div>
          </div>
          <div className="card">
            <div className="section-title">
              <KeyRound size={16} />
              Keys
            </div>
            <div className="list">
              {keys.map((k) => (
                <div className="row" key={k.id}>
                  <div className="stack" style={{ flex: 1 }}>
                    <strong>{k.key}</strong>
                    <div className="muted">
                      Item {k.download_item_id} · Uses {k.uses}/{k.max_uses} {k.expires_at ? `· Expires ${k.expires_at}` : ""}
                    </div>
                    <div className="flex wrap">
                      <span className="pill">ID {k.id}</span>
                      {k.item_name && <span className="pill">{k.item_name}</span>}
                    </div>
                  </div>
                  <button className="btn secondary" onClick={() => copy(k.key)}>
                    <Copy size={14} />
                    Copy
                  </button>
                </div>
              ))}
              {keys.length === 0 && <div className="muted">No keys loaded yet.</div>}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

export default App;

