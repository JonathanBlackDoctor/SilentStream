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

export async function authorizeUpdate(storage, proof, now = Date.now()) {
  if (!/^[a-f0-9]{64}$/.test(proof.keyId || "") || !/^\d{10}$/.test(proof.timestamp || "")
      || !/^[a-f0-9]{32}$/.test(proof.nonce || "") || !/^[A-Za-z0-9_-]{86}$/.test(proof.signature || "")
      || !["GET", "HEAD"].includes(proof.method) || typeof proof.target !== "string"
      || !proof.target.startsWith("/api/updates/") || proof.target.length > 2048
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
    if (!current || current.publicKey !== key.publicKey || !active || active.revokedAtUtc) return denied();
    const nonceKey = `update-nonces:${proof.keyId}`;
    const nonces = (await store.get(nonceKey) || []).filter(x => x.time > now - 600000);
    if (nonces.some(x => x.value === proof.nonce)) return denied();
    if (nonces.length >= 256) return new Response(null, { status: 429 });
    nonces.push({ value: proof.nonce, time: now });
    await store.put(nonceKey, nonces);
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
