const encoder = new TextEncoder();
const repository = "JonathanBlackDoctor/SilentStream";
const releaseCache = new Map();
const decode = value => Uint8Array.from(atob(value.replace(/-/g, "+").replace(/_/g, "/")), c => c.charCodeAt(0));
const hex = value => [...new Uint8Array(value)].map(x => x.toString(16).padStart(2, "0")).join("");
const denied = () => new Response(null, { status: 401 });
const validVersion = value => /^0\.\d+\.\d+$/.test(value);

// Only the already-authenticated device heartbeat may register a PC's public update key.
// Its private key stays DPAPI-encrypted on that PC; no GitHub credential is sent to clients.
export async function registerUpdateIdentity(storage, device, identity) {
  if (!identity || device.revokedAtUtc || !/^[a-f0-9]{64}$/.test(identity.keyId || "")
      || typeof identity.publicKey !== "string" || identity.publicKey.length > 300) return false;
  try {
    const bytes = decode(identity.publicKey);
    if (hex(await crypto.subtle.digest("SHA-256", bytes)) !== identity.keyId) return false;
    await crypto.subtle.importKey("spki", bytes, { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
    const previous = await storage.get(`update-device:${device.deviceId}`);
    if (previous && previous !== identity.keyId) await storage.delete(`update-key:${previous}`);
    await storage.put(`update-key:${identity.keyId}`, { deviceId: device.deviceId, publicKey: identity.publicKey });
    await storage.put(`update-device:${device.deviceId}`, identity.keyId);
    return true;
  } catch { return false; }
}

// The device binding comes from its verified key, never a client-supplied room/version.
// A default hold is materialized per device and survives later configuration changes.
export async function readUpdatePolicy(storage, device, defaultHoldRooms = "", now = Date.now()) {
  const name = `update-policy:${device.deviceId}`;
  const stored = await storage.get(name);
  if (stored !== undefined) {
    if (stored?.deviceId !== device.deviceId || typeof stored.held !== "boolean" || !Number.isSafeInteger(stored.revision) || stored.revision < 1)
      return { deviceId: device.deviceId, held: true, revision: 0, reason: "policy-unreadable" };
    return stored;
  }
  const held = String(defaultHoldRooms).split(",").map(x => x.trim()).includes(device.roomId);
  const policy = { deviceId: device.deviceId, held, revision: held ? 1 : 0,
    reason: held ? "original-preservation-required" : "", updatedAtUtc: new Date(now).toISOString(), actor: "deployment-default" };
  if (held) await storage.put(name, policy);
  return policy;
}

export async function changeUpdatePolicy(storage, device, change, actor, defaultHoldRooms = "", now = Date.now()) {
  if (typeof change.held !== "boolean" || !Number.isSafeInteger(change.expectedRevision) || change.expectedRevision < 0
      || typeof change.reason !== "string" || !change.reason.trim() || change.reason.length > 500
      || (!change.held && !/^[a-f0-9]{64}$/.test(change.preservationManifestSha256 || "")))
    return Response.json({ error: "보존 기록과 정책 버전을 확인해 주세요." }, { status: 400 });
  return storage.transaction(async store => {
    const active = await store.get(`device:${device.deviceId}`);
    if (!active || active.revokedAtUtc) return new Response(null, { status: 404 });
    const previous = await readUpdatePolicy(store, active, defaultHoldRooms, now);
    if (previous.revision !== change.expectedRevision) return Response.json({ error: "정책이 변경되었습니다. 다시 조회해 주세요." }, { status: 409 });
    const next = { deviceId: active.deviceId, held: change.held, revision: previous.revision + 1,
      reason: change.reason.trim(), preservationManifestSha256: change.preservationManifestSha256 || null,
      actor, updatedAtUtc: new Date(now).toISOString() };
    await store.put(`update-policy:${active.deviceId}`, next);
    await store.put(`update-policy-audit:${active.deviceId}:${next.revision}`, { previous, next });
    return Response.json(next);
  });
}

export async function authorizeUpdate(storage, proof, now = Date.now(), scope = "updates", defaultHoldRooms = "") {
  const validTarget = scope === "upload-budget"
    ? proof.method === "POST" && typeof proof.target === "string" && proof.target.startsWith("/api/upload-budget?")
    : ["GET", "HEAD"].includes(proof.method) && typeof proof.target === "string" && proof.target.startsWith("/api/updates/");
  if (!/^[a-f0-9]{64}$/.test(proof.keyId || "") || !/^\d{10}$/.test(proof.timestamp || "")
      || !/^[a-f0-9]{32}$/.test(proof.nonce || "") || !/^[A-Za-z0-9_-]{86}$/.test(proof.signature || "")
      || !validTarget || proof.target.length > 2048
      || Math.abs(now - Number(proof.timestamp) * 1000) > 300000) return denied();
  const key = await storage.get(`update-key:${proof.keyId}`);
  if (!key) return denied();
  const device = await storage.get(`device:${key.deviceId}`);
  if (!device || device.revokedAtUtc) return denied();
  try {
    const publicKey = await crypto.subtle.importKey("spki", decode(key.publicKey), { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
    const message = [proof.method, proof.target, proof.timestamp, proof.nonce].join("\n");
    if (!await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, publicKey, decode(proof.signature), encoder.encode(message))) return denied();
  } catch { return denied(); }
  return storage.transaction(async store => {
    const current = await store.get(`update-key:${proof.keyId}`);
    const active = await store.get(`device:${key.deviceId}`);
    if (!current || current.deviceId !== key.deviceId || current.publicKey !== key.publicKey || !active || active.revokedAtUtc) return denied();
    const nonceKey = `update-nonces:${proof.keyId}`;
    const nonces = (await store.get(nonceKey) || []).filter(x => x.time > now - 600000);
    if (nonces.some(x => x.value === proof.nonce)) return denied();
    if (nonces.length >= 256) return new Response(null, { status: 429 });
    nonces.push({ value: proof.nonce, time: now });
    await store.put(nonceKey, nonces);
    if (scope === "updates" && (await readUpdatePolicy(store, active, defaultHoldRooms, now)).held)
      return new Response(null, { status: 503, headers: { "cache-control": "no-store", "retry-after": "21600", "x-update-policy": "preservation-hold" } });
    return new Response(null, { status: 200 });
  });
}

export function updateProof(request) {
  const url = new URL(request.url);
  return { method: request.method, target: url.pathname + url.search,
    keyId: request.headers.get("x-update-key"), timestamp: request.headers.get("x-update-time"),
    nonce: request.headers.get("x-update-nonce"), signature: request.headers.get("x-update-signature") };
}

export async function githubRelease(env, version = "latest") {
  if (version !== "latest" && !validVersion(version)) throw new Error("Invalid release version");
  if (!env.GITHUB_RELEASE_READ_TOKEN) throw new Error("Private release access is not configured");
  const cached = releaseCache.get(version);
  if (cached && cached.expires > Date.now()) return cached.value;
  const route = version === "latest" ? "latest" : `tags/v${version}`;
  const response = await fetch(`https://api.github.com/repos/${repository}/releases/${route}`, {
    headers: { authorization: `Bearer ${env.GITHUB_RELEASE_READ_TOKEN}`, accept: "application/vnd.github+json",
      "user-agent": "SilentStream-Private-Distribution", "x-github-api-version": "2022-11-28" }, redirect: "manual"
  });
  if (!response.ok) throw new Error(`Private release is unavailable (${response.status})`);
  const value = await response.json();
  if (!/^v0\.\d+\.\d+$/.test(value.tag_name || "") || value.draft || value.prerelease || !Array.isArray(value.assets))
    throw new Error("Invalid production release");
  releaseCache.set(version, { value, expires: Date.now() + 60000 });
  return value;
}

export async function releaseAsset(env, externalUrl) {
  const url = new URL(externalUrl);
  const match = url.pathname.match(/^\/JonathanBlackDoctor\/SilentStream\/releases\/download\/v(0\.\d+\.\d+)\/([A-Za-z0-9_.-]+)$/);
  if (url.origin !== "https://github.com" || !match || url.search || url.hash) throw new Error("Invalid artifact source");
  const release = await githubRelease(env, match[1]);
  const asset = release.assets.find(x => x.name === match[2]);
  if (!asset || !Number.isSafeInteger(asset.id) || !Number.isSafeInteger(asset.size) || !/^sha256:[a-f0-9]{64}$/.test(asset.digest || ""))
    throw new Error("Invalid artifact metadata");
  return asset;
}

export async function downloadReleaseAsset(env, asset, range) {
  const headers = { authorization: `Bearer ${env.GITHUB_RELEASE_READ_TOKEN}`, accept: "application/octet-stream",
    "user-agent": "SilentStream-Private-Distribution", "x-github-api-version": "2022-11-28" };
  if (range) headers.range = range;
  let response = await fetch(`https://api.github.com/repos/${repository}/releases/assets/${asset.id}`, { headers, redirect: "manual" });
  if (response.status === 302) {
    const target = new URL(response.headers.get("location"));
    if (target.protocol !== "https:" || target.hostname !== "release-assets.githubusercontent.com") throw new Error("Invalid artifact redirect");
    response = await fetch(target, { headers: range ? { range } : {}, redirect: "manual" });
  }
  if (!response.ok && response.status !== 206) throw new Error("Private artifact is unavailable");
  return response;
}

export async function privateUpdateResponse(request, env) {
  const path = new URL(request.url).pathname.slice("/api/updates/".length);
  const manifest = path.match(/^v(0\.\d+\.\d+)\/manifest\.json$/);
  if (manifest) {
    const version = manifest[1], release = await githubRelease(env, version);
    const assets = ["SilentStream-Central-Installer-v" + version + ".exe", "SilentStream-prepared-package-v" + version + ".zip"]
      .map(name => { const asset = release.assets.find(a => a.name === name);
        if (!asset || !/^sha256:[a-f0-9]{64}$/.test(asset.digest || "")) throw new Error("Missing installer metadata");
        return { name, length: asset.size, sha256: asset.digest.slice(7), url: `${new URL(env.DEVICE_BASE_URI).origin}/api/updates/${name}` }; });
    return Response.json({ version, assets });
  }
  if (!/^(?:releases\.win\.json|assets\.win\.json|MediaCaptureHelper-0\.\d+\.\d+-full\.nupkg|SilentStream-(?:Central-Installer-v0\.\d+\.\d+\.exe|prepared-package-v0\.\d+\.\d+\.zip))$/.test(path))
    return new Response(null, { status: 404 });
  const version = path.match(/(?:Helper-|Installer-v|package-v)(0\.\d+\.\d+)/)?.[1] || "latest";
  const release = await githubRelease(env, version);
  const asset = release.assets.find(a => a.name === path);
  if (!asset) return new Response(null, { status: 404 });
  if (request.method === "HEAD") return new Response(null, { headers: { "content-length": String(asset.size) } });
  const response = await downloadReleaseAsset(env, asset, request.headers.get("range"));
  const headers = new Headers(response.headers);
  headers.delete("set-cookie"); headers.delete("location");
  headers.set("cache-control", "private, no-store");
  return new Response(response.body, { status: response.status, headers });
}
