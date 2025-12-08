export type DownloadItem = {
  id: number;
  name: string;
  file_path: string;
  price_cents: number;
  active: number;
  created_at: string;
};

export type DownloadKey = {
  id: number;
  key: string;
  download_item_id: number;
  user_id: number | null;
  max_uses: number;
  uses: number;
  expires_at: string | null;
  created_at: string;
  item_name?: string;
};

export type ApiResult<T> = { ok: true; data: T } | { ok: false; error: string };

function authHeaders(token: string) {
  return {
    Authorization: `Bearer ${token}`,
    "Content-Type": "application/json",
  };
}

export async function fetchItems(baseUrl: string, token: string): Promise<ApiResult<DownloadItem[]>> {
  try {
    const res = await fetch(`${baseUrl}/api/admin/download-items`, {
      headers: authHeaders(token),
    });
    if (!res.ok) return { ok: false, error: await res.text() };
    const json = await res.json();
    return { ok: true, data: json.items as DownloadItem[] };
  } catch (err: any) {
    return { ok: false, error: err?.message || "Network error" };
  }
}

export async function createItem(
  baseUrl: string,
  token: string,
  input: { name: string; filePath: string; priceCents?: number; active?: boolean },
): Promise<ApiResult<DownloadItem>> {
  try {
    const res = await fetch(`${baseUrl}/api/admin/download-items`, {
      method: "POST",
      headers: authHeaders(token),
      body: JSON.stringify({
        name: input.name,
        filePath: input.filePath,
        priceCents: input.priceCents ?? 0,
        active: input.active ?? true,
      }),
    });
    if (!res.ok) return { ok: false, error: await res.text() };
    const json = await res.json();
    return { ok: true, data: json as DownloadItem };
  } catch (err: any) {
    return { ok: false, error: err?.message || "Network error" };
  }
}

export async function fetchKeys(baseUrl: string, token: string): Promise<ApiResult<DownloadKey[]>> {
  try {
    const res = await fetch(`${baseUrl}/api/admin/download-keys`, {
      headers: authHeaders(token),
    });
    if (!res.ok) return { ok: false, error: await res.text() };
    const json = await res.json();
    return { ok: true, data: json.keys as DownloadKey[] };
  } catch (err: any) {
    return { ok: false, error: err?.message || "Network error" };
  }
}

export async function fetchDefaultItemId(baseUrl: string, token: string): Promise<ApiResult<number>> {
  try {
    const res = await fetch(`${baseUrl}/api/admin/default-item`, {
      headers: authHeaders(token),
    });
    if (!res.ok) return { ok: false, error: await res.text() };
    const json = await res.json();
    return { ok: true, data: json.id as number };
  } catch (err: any) {
    return { ok: false, error: err?.message || "Network error" };
  }
}

export async function createKey(
  baseUrl: string,
  token: string,
  input: { downloadItemId: number; maxUses?: number; expiresAt?: string | null; userId?: number | null },
): Promise<ApiResult<{ key: string }>> {
  try {
    const res = await fetch(`${baseUrl}/api/admin/download-keys`, {
      method: "POST",
      headers: authHeaders(token),
      body: JSON.stringify({
        downloadItemId: input.downloadItemId,
        maxUses: input.maxUses ?? 1,
        expiresAt: input.expiresAt ?? null,
        userId: input.userId ?? null,
      }),
    });
    if (!res.ok) return { ok: false, error: await res.text() };
    const json = await res.json();
    return { ok: true, data: json as { key: string } };
  } catch (err: any) {
    return { ok: false, error: err?.message || "Network error" };
  }
}

