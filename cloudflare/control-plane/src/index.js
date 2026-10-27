import {mergeUploads} from "./offline-history.js";
import {YouTubeReplayJobs, YOUTUBE_SCOPE} from "./youtube-replay-jobs.js";
const ROOM_DEFINITIONS = Object.freeze({
  m111: Object.freeze({ displayName: "M111" }),
  m112: Object.freeze({ displayName: "M112" }),
  m113: Object.freeze({ displayName: "M113" }),
  baek202: Object.freeze({ displayName: "백202" })
});
const ROOMS = Object.freeze(Object.keys(ROOM_DEFINITIONS));
const ACTIONS = Object.freeze({
  "live.start": { method: "POST", path: "/api/live/start", role: "manager" },
  "live.retry": { method: "POST", path: "/api/live/retry", role: "manager" },
  "audio.select": { method: "POST", path: "/api/audio/select", role: "manager" },
  "audio.refresh": { method: "POST", path: "/api/audio/refresh", role: "manager" },
  "audio.restore": { method: "POST", path: "/api/audio/restore", role: "manager" },
  "live.stopBroadcast": { method: "POST", path: "/api/live/stop-broadcast", role: "manager" },
  "live.stopAll": { method: "POST", path: "/api/live/stop", role: "admin" },
  "app.restart": { method: "POST", path: "/api/app/restart", role: "manager" },
  "inspection.rerun": { method: "POST", path: "/inspect/internal/v1/startup/rerun", role: "manager" },
  "system.shutdown": { method: "POST", path: "/api/system/shutdown", role: "admin" },
  "system.shutdown.schedule": { method: "POST", path: "/api/system/shutdown/schedule", role: "admin" },
  "system.shutdown.schedule.cancel": { method: "POST", path: "/api/system/shutdown/schedule/cancel", role: "admin" }
});

const APP_STATUS_FIELDS = Object.freeze([
  "appVersion", "updatedAtUtc", "shutdownScheduleControl", "quietShutdownControl", "shutdownSchedule",
  "windowsShutdownPending", "remoteShutdownScheduleCommands", "state", "badge", "live",
  "liveReplay", "liveReplays", "room", "dailyPlaylist", "playlists", "recovery", "automation", "settingsLock",
  "quality", "audio", "metrics", "recording", "today", "diagnostics", "queue"
]);

export function normalizeAppStatus(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  return Object.fromEntries(APP_STATUS_FIELDS.filter(key => Object.hasOwn(value, key)).map(key => [key, value[key]]));
}

const textEncoder = new TextEncoder();
let jwksCache = { domain: "", expires: 0, keys: [] };
let googleJwksCache = { expires: 0, keys: [] };

export function normalizeRoom(value) {
  const room = String(value || "").trim().toLowerCase();
  return ROOMS.includes(room) ? room : "";
}

export function roomDisplayName(value) {
  const room = normalizeRoom(value);
  return room ? ROOM_DEFINITIONS[room].displayName : "";
}

function publicZone(env) {
  const labels = new URL(env.PUBLIC_BASE_URI).hostname.split(".");
  return labels.length > 2 ? labels.slice(1).join(".") : labels.join(".");
}

export function roomForHostname(hostname, env) {
  const normalizedHost = String(hostname || "").trim().toLowerCase();
  const zone = publicZone(env);
  return ROOMS.find(room => normalizedHost === `${room}.${zone}`) || "";
}

function roomOrigin(roomId, env) {
  const room = normalizeRoom(roomId);
  if (!room) throw new HttpFailure(404, "호실을 찾을 수 없습니다.");
  const origin = new URL(env.PUBLIC_BASE_URI);
  origin.hostname = `${room}.${publicZone(env)}`;
  origin.pathname = "/";
  origin.search = "";
  origin.hash = "";
  return origin;
}

export function normalizePlaylistHistory(value, roomId) {
  if (!value || typeof value !== "object" || !Array.isArray(value.items)) return null;
  const room = normalizeRoom(roomId);
  if (!room || (value.room != null && normalizeRoom(value.room) !== room)) return null;
  const statuses = new Set(["ready", "creating", "auth-required", "failed"]);
  const byDate = new Map();
  for (const candidate of value.items.slice(0, 1000)) {
    const date = String(candidate?.date || "");
    if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) continue;
    const parsed = new Date(`${date}T00:00:00Z`);
    if (Number.isNaN(parsed.valueOf()) || parsed.toISOString().slice(0, 10) !== date) continue;
    const status = statuses.has(candidate?.status) ? candidate.status : "failed";
    const playlistId = safeId(candidate?.playlistId, 160) ? candidate.playlistId : "";
    byDate.set(date, {
      date,
      title: String(candidate?.title || `${room} | ${date}`).slice(0, 200),
      status,
      playlistId: playlistId || null,
      url: status === "ready" && playlistId
        ? `https://www.youtube.com/playlist?list=${encodeURIComponent(playlistId)}` : null,
      error: candidate?.error ? String(candidate.error).slice(0, 300) : null
    });
  }
  const items = [...byDate.values()].sort((left, right) => right.date.localeCompare(left.date)).slice(0, 400);
  return { room, from: items.at(-1)?.date || null, to: items[0]?.date || null, items };
}

export function recentPublicPlaylists(device, now = new Date()) {
  const history = normalizePlaylistHistory(device?.playlists, "m112");
  const today = new Date(now.valueOf() + 9 * 3600000).toISOString().slice(0, 10);
  return {
    room: "m112",
    updatedAtUtc: device?.lastSeenAtUtc || null,
    items: (history?.items || []).filter(item => item.date <= today && item.status === "ready" && item.url)
      .slice(0, 3).map(({ date, url }) => ({ date, status: "ready", url }))
  };
}

export function parseDeviceAuthorization(value) {
  if (!value || !/^Device /i.test(value)) return null;
  const parts = value.slice(7).split(":");
  if (parts.length !== 3) return null;
  const roomId = normalizeRoom(parts[0]);
  if (!roomId || !safeId(parts[1], 128) || !safeSecret(parts[2])) return null;
  return { roomId, deviceId: parts[1], deviceSecret: parts[2] };
}

export function isSafeLoopbackCallback(value) {
  try {
    const url = new URL(value);
    return url.protocol === "http:" && (url.hostname === "127.0.0.1" || url.hostname === "[::1]" || url.hostname === "localhost")
      && Number(url.port) > 1024 && Number(url.port) <= 65535 && !url.username && !url.password && !url.hash;
  } catch {
    return false;
  }
}

function safeId(value, maximum = 64) {
  return typeof value === "string" && value.length > 0 && value.length <= maximum && /^[A-Za-z0-9_.-]+$/.test(value);
}

function safeSecret(value) {
  return typeof value === "string" && value.length >= 20 && value.length <= 4096 && !/[\x00-\x1f\x7f]/.test(value);
}

function normalizeEmail(value) {
  const email = String(value || "").trim().toLowerCase();
  return email.length <= 254 && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) ? email : "";
}

function json(data, status = 200, headers = {}) {
  return new Response(JSON.stringify(data), {
    status,
    headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store", ...headers }
  });
}

function error(message, status) {
  return json({ error: message }, status);
}

function security(response) {
  const secured = new Response(response.body, response);
  secured.headers.set("cache-control", "no-store");
  secured.headers.set("x-content-type-options", "nosniff");
  secured.headers.set("x-frame-options", "DENY");
  secured.headers.set("referrer-policy", "no-referrer");
  secured.headers.set("permissions-policy", "camera=(), geolocation=(), payment=(), usb=()");
  secured.headers.set("content-security-policy", "default-src 'self'; base-uri 'none'; frame-ancestors 'none'; object-src 'none'; img-src 'self' data:; connect-src 'self' wss:; style-src 'self' 'unsafe-inline'; script-src 'self'");
  return secured;
}

function redirect(location, status = 302, headers = {}) {
  return security(new Response(null, {
    status,
    headers: { location: String(location), ...headers }
  }));
}

function base64UrlBytes(value) {
  const padded = value.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((value.length + 3) % 4);
  const binary = atob(padded);
  return Uint8Array.from(binary, character => character.charCodeAt(0));
}

function decodeJwtPart(value) {
  return JSON.parse(new TextDecoder().decode(base64UrlBytes(value)));
}

async function accessKeys(domain, force = false) {
  const now = Date.now();
  if (!force && jwksCache.domain === domain && jwksCache.expires > now) return jwksCache.keys;
  const response = await fetch(`https://${domain}/cdn-cgi/access/certs`, { cf: { cacheTtl: 3600 } });
  if (!response.ok) throw new Error("Cloudflare Access signing keys are unavailable.");
  const body = await response.json();
  const keys = Array.isArray(body.keys) ? body.keys : [];
  if (!keys.length) throw new Error("Cloudflare Access signing keys are empty.");
  jwksCache = { domain, expires: now + 60 * 60 * 1000, keys };
  return keys;
}

async function verifyAccess(request, env) {
  if (String(env.ENABLE_DEVELOPMENT_IDENTITY) === "true") {
    const developmentEmail = normalizeEmail(request.headers.get("x-silentstream-dev-email"));
    if (developmentEmail) return developmentEmail;
  }
  const domain = String(env.ACCESS_TEAM_DOMAIN || "").replace(/^https?:\/\//, "").replace(/\/$/, "");
  const audiences = String(env.ACCESS_AUD || "").split(",").map(value => value.trim()).filter(Boolean);
  if (!domain || audiences.length === 0) throw new HttpFailure(503, "Google 로그인 보호가 아직 구성되지 않았습니다.");
  const token = request.headers.get("cf-access-jwt-assertion") || "";
  const parts = token.split(".");
  if (parts.length !== 3) throw new HttpFailure(401, "Cloudflare Access 로그인이 필요합니다.");
  let header;
  let payload;
  try {
    header = decodeJwtPart(parts[0]);
    payload = decodeJwtPart(parts[1]);
  } catch {
    throw new HttpFailure(401, "로그인 토큰이 올바르지 않습니다.");
  }
  if (header.alg !== "RS256" || !safeId(header.kid, 256)) throw new HttpFailure(401, "로그인 서명 형식이 올바르지 않습니다.");
  let keys = await accessKeys(domain);
  let jwk = keys.find(key => key.kid === header.kid);
  if (!jwk) {
    keys = await accessKeys(domain, true);
    jwk = keys.find(key => key.kid === header.kid);
  }
  if (!jwk) throw new HttpFailure(401, "로그인 서명 키를 찾지 못했습니다.");
  const key = await crypto.subtle.importKey("jwk", jwk, { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["verify"]);
  const valid = await crypto.subtle.verify("RSASSA-PKCS1-v1_5", key, base64UrlBytes(parts[2]), textEncoder.encode(`${parts[0]}.${parts[1]}`));
  const now = Math.floor(Date.now() / 1000);
  const tokenAudiences = Array.isArray(payload.aud) ? payload.aud : [payload.aud];
  if (!valid || payload.iss !== `https://${domain}` || !Number.isFinite(payload.exp) || payload.exp <= now
      || Number.isFinite(payload.nbf) && payload.nbf > now + 30
      || !audiences.some(audience => tokenAudiences.includes(audience))) {
    throw new HttpFailure(401, "로그인 토큰 검증에 실패했습니다.");
  }
  const email = normalizeEmail(payload.email);
  if (!email) throw new HttpFailure(401, "로그인 이메일을 확인하지 못했습니다.");
  return email;
}

async function googleKeys(force = false) {
  const now = Date.now();
  if (!force && googleJwksCache.expires > now) return googleJwksCache.keys;
  const response = await fetch("https://www.googleapis.com/oauth2/v3/certs", { cf: { cacheTtl: 3600 } });
  if (!response.ok) throw new Error("Google signing keys are unavailable.");
  const body = await response.json();
  const keys = Array.isArray(body.keys) ? body.keys : [];
  if (!keys.length) throw new Error("Google signing keys are empty.");
  googleJwksCache = { expires: now + 60 * 60 * 1000, keys };
  return keys;
}

async function verifyGoogleIdToken(token, clientId) {
  const parts = String(token || "").split(".");
  if (parts.length !== 3) throw new HttpFailure(401, "Google 로그인 토큰이 올바르지 않습니다.");
  let header;
  let payload;
  try {
    header = decodeJwtPart(parts[0]);
    payload = decodeJwtPart(parts[1]);
  } catch {
    throw new HttpFailure(401, "Google 로그인 토큰을 읽지 못했습니다.");
  }
  if (header.alg !== "RS256" || !safeId(header.kid, 256)) throw new HttpFailure(401, "Google 로그인 서명 형식이 올바르지 않습니다.");
  let keys = await googleKeys();
  let jwk = keys.find(key => key.kid === header.kid);
  if (!jwk) {
    keys = await googleKeys(true);
    jwk = keys.find(key => key.kid === header.kid);
  }
  if (!jwk) throw new HttpFailure(401, "Google 로그인 서명 키를 찾지 못했습니다.");
  const key = await crypto.subtle.importKey("jwk", jwk, { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["verify"]);
  const valid = await crypto.subtle.verify("RSASSA-PKCS1-v1_5", key, base64UrlBytes(parts[2]), textEncoder.encode(`${parts[0]}.${parts[1]}`));
  const now = Math.floor(Date.now() / 1000);
  if (!valid || !["accounts.google.com", "https://accounts.google.com"].includes(payload.iss)
      || payload.aud !== clientId || !Number.isFinite(payload.exp) || payload.exp <= now || payload.email_verified !== true)
    throw new HttpFailure(401, "Google 로그인 검증에 실패했습니다.");
  const email = normalizeEmail(payload.email);
  if (!email) throw new HttpFailure(401, "Google 로그인 이메일을 확인하지 못했습니다.");
  return email;
}

function cookieValue(request, name) {
  const cookie = request.headers.get("cookie") || "";
  for (const part of cookie.split(";")) {
    const separator = part.indexOf("=");
    if (separator > 0 && part.slice(0, separator).trim() === name) return part.slice(separator + 1).trim();
  }
  return "";
}

function sessionCookie(token, maximumAge = 12 * 60 * 60) {
  return `__Host-SilentStreamSession=${token}; Path=/; Secure; HttpOnly; SameSite=Lax; Max-Age=${maximumAge}`;
}

function safeReturnPath(value) {
  const path = String(value || "");
  return path.startsWith("/") && !path.startsWith("//") && path.length <= 2048 ? path : "/control/";
}

function safeRoomReturnPath(value) {
  const path = String(value || "");
  return path.startsWith("/") && !path.startsWith("//") && path.length <= 2048 ? path : "/";
}

async function humanEmail(request, env, requiredRoom = "") {
  if (String(env.ENABLE_DEVELOPMENT_IDENTITY) === "true") {
    const developmentEmail = normalizeEmail(request.headers.get("x-silentstream-dev-email"));
    if (developmentEmail) return developmentEmail;
  }
  if (env.GOOGLE_CLIENT_ID && env.GOOGLE_CLIENT_SECRET) {
    const token = cookieValue(request, "__Host-SilentStreamSession");
    if (!safeSecret(token)) throw new HttpFailure(401, "Google 로그인이 필요합니다.");
    const response = await callControl(env, "/oauth/session", { token });
    if (!response.ok) throw new HttpFailure(401, "Google 로그인 세션이 만료되었습니다.");
    const session = await response.json();
    const boundRoom = normalizeRoom(session.roomId);
    if (requiredRoom ? boundRoom !== requiredRoom : Boolean(boundRoom))
      throw new HttpFailure(401, "로그인 세션의 호실이 일치하지 않습니다.");
    return normalizeEmail(session.email);
  }
  return verifyAccess(request, env);
}

function controlStub(env) {
  return env.CONTROL.get(env.CONTROL.idFromName("global"));
}

async function callControl(env, path, value, init = {}) {
  const request = new Request(`https://control.internal${path}`, {
    method: init.method || "POST",
    headers: { "content-type": "application/json", ...(init.headers || {}) },
    body: value === undefined ? undefined : JSON.stringify(value)
  });
  return controlStub(env).fetch(request);
}

function isAgentHost(url, env) {
  try { return url.hostname === new URL(env.DEVICE_BASE_URI).hostname; }
  catch { return url.hostname === "agent.silentstream.win"; }
}

function sameOrigin(request) {
  const origin = request.headers.get("origin");
  if (!origin) return false;
  try { return new URL(origin).origin === new URL(request.url).origin; }
  catch { return false; }
}

async function limitedJson(request, maximum = 1024 * 1024) {
  const declared = Number(request.headers.get("content-length") || "0");
  if (declared > maximum) throw new HttpFailure(413, "요청이 너무 큽니다.");
  const text = await request.text();
  if (text.length > maximum) throw new HttpFailure(413, "요청이 너무 큽니다.");
  try { return text ? JSON.parse(text) : {}; }
  catch { throw new HttpFailure(400, "JSON 요청이 올바르지 않습니다."); }
}

async function artifactIdentity(env, key, expectedLength, expectedHash, externalUrl) {
  const configuredLength = Number(expectedLength || "0");
  if (configuredLength <= 0 || !/^[a-f0-9]{64}$/i.test(expectedHash || "")) return null;
  if (env.ARTIFACTS) {
    const head = await env.ARTIFACTS.head(key);
    if (!head || head.size !== configuredLength) return null;
  } else {
    let url;
    try { url = new URL(externalUrl); } catch { return null; }
    if (url.protocol !== "https:") return null;
    const head = await fetch(url, { method: "HEAD", redirect: "follow" });
    if (!head.ok || Number(head.headers.get("content-length") || "0") !== configuredLength) return null;
  }
  return { key, length: configuredLength, hash: String(expectedHash).toLowerCase() };
}

async function readinessResponse(env) {
  const [packageInfo, installerInfo, durable] = await Promise.all([
    artifactIdentity(env, env.PACKAGE_KEY, env.PACKAGE_LENGTH, env.PACKAGE_SHA256, env.PACKAGE_URL),
    artifactIdentity(env, env.INSTALLER_KEY, env.INSTALLER_LENGTH, env.INSTALLER_SHA256, env.INSTALLER_URL),
    callControl(env, "/ready", undefined, { method: "GET" })
  ]);
  const missing = [];
  const googleReady = Boolean(env.GOOGLE_CLIENT_ID && env.GOOGLE_CLIENT_SECRET);
  const accessReady = Boolean(env.ACCESS_TEAM_DOMAIN && env.ACCESS_AUD);
  if (!googleReady && !accessReady) missing.push("Google OAuth or Cloudflare Access");
  if (!packageInfo) missing.push("prepared package");
  if (!installerInfo) missing.push("bootstrap installer");
  if (!durable.ok) missing.push("durable object");
  return security(json({ status: missing.length ? "not-ready" : "ready", missing }, missing.length ? 503 : 200));
}

async function serveArtifact(request, env, key, externalUrl, fileName, contentType) {
  const requestedRange = request.headers.has("range");
  if (!env.ARTIFACTS) {
    let source;
    try { source = new URL(externalUrl); } catch { return error("배포 파일 주소가 올바르지 않습니다.", 503); }
    if (source.protocol !== "https:") return error("배포 파일 주소가 안전하지 않습니다.", 503);
    const upstreamHeaders = new Headers();
    if (requestedRange) upstreamHeaders.set("range", request.headers.get("range"));
    const upstream = await fetch(source, { headers: upstreamHeaders, redirect: "follow" });
    if (!upstream.ok && upstream.status !== 206) return error("배포 파일을 찾지 못했습니다.", 503);
    const headers = new Headers(upstream.headers);
    headers.set("content-type", contentType);
    headers.set("content-disposition", `attachment; filename="${fileName}"`);
    headers.set("cache-control", "private, no-store");
    headers.delete("set-cookie");
    return new Response(upstream.body, { status: upstream.status, headers });
  }
  const object = await env.ARTIFACTS.get(key, requestedRange ? { range: request.headers } : undefined);
  if (!object) return error("배포 파일을 찾지 못했습니다.", 503);
  const headers = new Headers();
  object.writeHttpMetadata(headers);
  headers.set("content-type", contentType);
  headers.set("content-disposition", `attachment; filename="${fileName}"`);
  headers.set("etag", object.httpEtag);
  headers.set("accept-ranges", "bytes");
  headers.set("cache-control", "private, no-store");
  let status = 200;
  if (requestedRange && object.range && "offset" in object.range) {
    status = 206;
    headers.set("content-range", `bytes ${object.range.offset}-${object.range.offset + object.range.length - 1}/${object.size}`);
    headers.set("content-length", String(object.range.length));
  } else {
    headers.set("content-length", String(object.size));
  }
  return new Response(object.body, { status, headers });
}

function installPage(room) {
  const display = roomDisplayName(room);
  return new Response(`<!doctype html><html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${display} 자동 설치</title><style>body{font-family:system-ui;margin:0;background:#f3f6fb;color:#172033}.card{max-width:620px;margin:10vh auto;background:#fff;border:1px solid #dfe6f1;border-radius:20px;padding:32px;box-shadow:0 12px 40px #17203318}h1{margin-top:0}a{display:inline-block;padding:14px 20px;background:#1769e0;color:#fff;border-radius:10px;text-decoration:none;font-weight:800}.sub{color:#68758a;line-height:1.65}.plain{display:inline;padding:0;background:none;color:#1769e0;font-weight:600}.card{box-sizing:border-box}@media(max-width:680px){.card{margin:24px 12px;padding:24px}}</style></head><body><main class="card"><h1>${display} 자동 설치</h1><p class="sub">파일 실행 후 Windows 권한 요청에 <b>예</b>를 누르세요. 기존 설정·YouTube 인증은 유지됩니다.</p><p><a href="/install/${room}/download">설치파일 다운로드</a></p><p class="sub">자동 방송·교시 영상·재생목록·마이크 제어를 제공합니다. PC 종료·예약은 관리자 전용입니다.</p><p class="sub"><a class="plain" href="https://${room}.silentstream.win/">${display} 원격 제어</a> · <a class="plain" href="https://silentstream.win/guide.html#remote">사용 안내</a></p></main></body></html>`, { headers: { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" } });
}

function pwaAsset(response, maximumAge = 86400) {
  const secured = security(response);
  secured.headers.set("cache-control", `public, max-age=${maximumAge}`);
  return secured;
}

export function roomManifest(roomId) {
  const room = normalizeRoom(roomId);
  if (!room) return error("호실을 찾을 수 없습니다.", 404);
  const displayName = roomDisplayName(room);
  return new Response(JSON.stringify({
    id: "/",
    name: `SilentStream ${displayName}`,
    short_name: displayName,
    description: `${displayName} 강의실 송출 상태 확인 및 원격 제어`,
    lang: "ko",
    start_url: "/?source=pwa",
    scope: "/",
    display: "standalone",
    orientation: "portrait-primary",
    background_color: "#0b1522",
    theme_color: "#0e2942",
    prefer_related_applications: false,
    icons: [
      { src: "/icons/icon-192.png?v=1", sizes: "192x192", type: "image/png", purpose: "any" },
      { src: "/icons/icon-512.png?v=1", sizes: "512x512", type: "image/png", purpose: "any" },
      { src: "/icons/icon-maskable.png?v=1", sizes: "512x512", type: "image/png", purpose: "maskable" }
    ]
  }), { headers: { "content-type": "application/manifest+json; charset=utf-8" } });
}

async function roomStaticAsset(request, env, url, assetPath, maximumAge = 86400) {
  const assetUrl = new URL(assetPath, url);
  const response = await env.ASSETS.fetch(new Request(assetUrl, request));
  return pwaAsset(response, maximumAge);
}

async function handleRoomHost(request, env, url, room) {
  const staticRead = request.method === "GET" || request.method === "HEAD";
  if (staticRead && url.pathname === "/manifest.webmanifest")
    return pwaAsset(roomManifest(room), 300);
  if (staticRead && url.pathname === "/service-worker.js") {
    const response = await roomStaticAsset(request, env, url, "/room/service-worker.js", 300);
    response.headers.set("service-worker-allowed", "/");
    return response;
  }
  if (staticRead && url.pathname === "/app.js")
    return roomStaticAsset(request, env, url, "/room/app.js", 300);
  if (staticRead && url.pathname === "/upload-view.js")
    return roomStaticAsset(request, env, url, "/room/upload-view.js", 300);
  if (staticRead && url.pathname === "/icons/apple-touch-icon.png")
    return roomStaticAsset(request, env, url, `/room/icons/${room}/apple-touch-icon.png`, 31536000);
  const icon = url.pathname.match(/^\/icons\/(icon-(?:192|512|maskable)\.png)$/);
  if (staticRead && icon)
    return roomStaticAsset(request, env, url, `/room/icons/${room}/${icon[1]}`, 31536000);

  if (url.pathname === "/auth/handoff" && request.method === "GET") {
    const code = url.searchParams.get("code") || "";
    if (!safeSecret(code)) return security(error("호실 로그인 인계 요청이 올바르지 않습니다.", 400));
    const consumed = await callControl(env, "/room-handoff/consume", { code, roomId: room });
    if (!consumed.ok) return security(consumed);
    const handoff = await consumed.json();
    const sessionResponse = await callControl(env, "/oauth/session/create", { email: handoff.email, roomId: room });
    if (!sessionResponse.ok) return security(sessionResponse);
    const session = await sessionResponse.json();
    return redirect(new URL(safeRoomReturnPath(handoff.returnTo), url), 302, {
      "set-cookie": sessionCookie(session.token)
    });
  }
  if (url.pathname === "/auth/logout") {
    const token = cookieValue(request, "__Host-SilentStreamSession");
    if (token) await callControl(env, "/oauth/logout", { token });
    return redirect(new URL("/auth/logout", env.PUBLIC_BASE_URI), 302, {
      "set-cookie": sessionCookie("deleted", 0),
      "clear-site-data": '"cache", "cookies", "storage"'
    });
  }

  let email;
  try { email = await humanEmail(request, env, room); }
  catch (failure) {
    if (failure instanceof HttpFailure && failure.status === 401) {
      if (url.pathname.startsWith("/api/")) return security(new Response(null, { status: 401 }));
      const login = new URL("/auth/room", env.PUBLIC_BASE_URI);
      login.searchParams.set("room", room);
      login.searchParams.set("returnTo", safeRoomReturnPath(url.pathname + url.search));
      return redirect(login);
    }
    throw failure;
  }

  const allowed = await callControl(env, "/authorize-room", { email, roomId: room });
  if (!allowed.ok) return security(allowed);

  if (url.pathname === "/api/room/playlist-links" && request.method === "GET") {
    if (room !== "m112") return security(new Response(null,{status:404}));
    return security(await callControl(env,"/room-playlists",{email,roomId:room}));
  }
  if (staticRead && url.pathname.startsWith("/archive")) {
    if (room !== "m112") return security(new Response(null,{status:404}));
    const path={"/archive/":"index.html","/archive/index.html":"index.html","/archive/style.css":"style.css",
      "/archive/app.js":"app.js","/archive/data.mjs":"data.mjs","/archive/icon.svg":"icon.svg","/archive/playlists.json":"playlists.json"}[url.pathname];
    if(url.pathname==="/archive")return redirect(new URL("/archive/",url));
    if(!path)return security(new Response(null,{status:404}));
    const response=security(await env.ASSETS.fetch(new Request(new URL("/archive/"+path,url),request)));
    response.headers.set("content-security-policy","default-src 'self'; base-uri 'none'; frame-ancestors 'none'; object-src 'none'; img-src 'self' data:; connect-src 'self'; style-src 'self' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; script-src 'self'");
    return response;
  }

  if (request.method === "GET" && ["/", "/index.html", "/control", "/control/"].includes(url.pathname))
    return security(await env.ASSETS.fetch(new Request(new URL("/room/index.html", url), request)));
  if (url.pathname === "/api/control/session" && request.method === "GET") {
    const response = await callControl(env, "/session", { email });
    if (!response.ok) return security(response);
    const session = await response.json();
    return security(json({
      ...session,
      roomId: room,
      roomName: roomDisplayName(room),
      rooms: [room],
      centralUrl: new URL("/control/", env.PUBLIC_BASE_URI).toString()
    }));
  }
  if (url.pathname === "/api/control/rooms" && request.method === "GET") {
    const response = await callControl(env, "/rooms", { email });
    if (!response.ok) return security(response);
    const rooms = await response.json();
    const selected = rooms.find(candidate => candidate.roomId === room);
    return security(selected ? json([selected]) : error("이 호실에 대한 권한이 없습니다.", 403));
  }
  const action = url.pathname.match(/^\/api\/control\/rooms\/([^/]+)\/actions\/([^/]+)$/);
  if (action && request.method === "POST") {
    if (!sameOrigin(request)) return security(error("요청 출처를 확인할 수 없습니다.", 403));
    const requestedRoom = normalizeRoom(decodeURIComponent(action[1]));
    const actionName = decodeURIComponent(action[2]);
    if (requestedRoom !== room || !ACTIONS[actionName]) return security(new Response("Not Found", { status: 404 }));
    const response = await callControl(env, "/command", { email, roomId: room, action: actionName, body: await limitedJson(request) });
    return security(response);
  }
  return security(new Response("Not Found", { status: 404 }));
}

class HttpFailure extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

export default {
  async fetch(request, env) {
    try {
      const url = new URL(request.url);
      const agentHost = isAgentHost(url, env);
      const devicePath = url.pathname.startsWith("/api/device/");
      const localDevelopment = String(env.ENABLE_DEVELOPMENT_IDENTITY) === "true";
      if (!localDevelopment && ((agentHost && !devicePath && !["/health", "/ready"].includes(url.pathname)) || (!agentHost && devicePath)))
        return security(new Response("Not Found", { status: 404 }));

      // Machine probes remain available only on the device-only hostname. On every human-facing
      // hostname these endpoints pass through the same login gate as the control portal.
      if (agentHost && url.pathname === "/health") return security(json({ status: "ok", runtime: "cloudflare-workers" }));
      if (agentHost && url.pathname === "/ready") return readinessResponse(env);

      if (url.pathname === "/api/device/enroll" && request.method === "POST") {
        const packageInfo = await artifactIdentity(env, env.PACKAGE_KEY, env.PACKAGE_LENGTH, env.PACKAGE_SHA256, env.PACKAGE_URL);
        if (!packageInfo) return security(error("설치 패키지가 아직 게시되지 않았습니다.", 503));
        const body = await limitedJson(request);
        const response = await callControl(env, "/enroll", {
          request: body,
          packageInfo,
          publicBaseUri: String(env.PUBLIC_BASE_URI).replace(/\/$/, ""),
          deviceBaseUri: String(env.DEVICE_BASE_URI).replace(/\/$/, "")
        });
        return security(response);
      }
      if (url.pathname === "/api/device/package" && request.method === "GET") {
        const authorization = request.headers.get("authorization") || "";
        const authenticated = await callControl(env, "/auth", { authorization });
        if (!authenticated.ok) return security(new Response(null, { status: 401 }));
        return security(await serveArtifact(request, env, env.PACKAGE_KEY, env.PACKAGE_URL, "SilentStream-prepared-package.zip", "application/zip"));
      }
      if (url.pathname === "/api/device/self" && request.method === "GET") {
        const response = await callControl(env, "/self", { authorization: request.headers.get("authorization") || "" });
        return security(response);
      }
      if (url.pathname === "/api/device/connect") {
        if (request.headers.get("upgrade")?.toLowerCase() !== "websocket") return security(new Response(null, { status: 401 }));
        return controlStub(env).fetch(new Request("https://control.internal/connect", request));
      }

      if (agentHost) return security(new Response("Not Found", { status: 404 }));

      const publicBase = new URL(env.PUBLIC_BASE_URI);
      const zone = publicZone(env);
      if ([zone, `www.${zone}`].includes(url.hostname) && url.pathname.startsWith("/api/public/")) {
        if (url.pathname !== "/api/public/m112/playlists" || env.PUBLIC_M112_PLAYLISTS !== "true")
          return security(new Response("Not Found", { status: 404 }));
        if (!["GET", "HEAD"].includes(request.method))
          return security(new Response(null, { status: 405, headers: { allow: "GET, HEAD" } }));
        const source = await callControl(env, "/public-playlists", {});
        const response = security(request.method === "HEAD" ? new Response(null, source) : source);
        response.headers.set("access-control-allow-origin", "*");
        return response;
      }
      const publicSitePath = {
        "/": "/site/index.html",
        "/index.html": "/site/index.html",
        "/guide.html": "/site/guide.html",
        "/assets/site.css": "/site/assets/site.css",
        "/assets/site.js": "/site/assets/site.js"
      }[url.pathname];
      if ([zone, `www.${zone}`].includes(url.hostname) && publicSitePath && ["GET", "HEAD"].includes(request.method)) {
        const response = security(await env.ASSETS.fetch(new Request(new URL(publicSitePath, url), request)));
        response.headers.set("cache-control", "public, max-age=0, must-revalidate");
        return response;
      }
      const aliasRoom = roomForHostname(url.hostname, env);
      if (url.hostname !== publicBase.hostname) {
        if (aliasRoom) return handleRoomHost(request, env, url, aliasRoom);
        const destination = new URL(env.PUBLIC_BASE_URI);
        destination.pathname = url.pathname === "/" ? "/control/" : url.pathname;
        destination.search = url.search;
        return redirect(destination);
      }

      if (url.pathname === "/auth/login" && request.method === "GET") {
        if (!env.GOOGLE_CLIENT_ID || !env.GOOGLE_CLIENT_SECRET) return security(error("Google 로그인이 아직 구성되지 않았습니다.", 503));
        const state = randomSecret();
        const verifier = randomSecret() + randomSecret();
        const challenge = await sha256Base64Url(verifier);
        const returnTo = safeReturnPath(url.searchParams.get("returnTo"));
        const saved = await callControl(env, "/oauth/begin", { state, verifier, returnTo });
        if (!saved.ok) return security(saved);
        const authorization = new URL("https://accounts.google.com/o/oauth2/v2/auth");
        authorization.searchParams.set("client_id", env.GOOGLE_CLIENT_ID);
        authorization.searchParams.set("redirect_uri", `${String(env.PUBLIC_BASE_URI).replace(/\/$/, "")}/auth/callback`);
        authorization.searchParams.set("response_type", "code");
        authorization.searchParams.set("scope", "openid email profile");
        authorization.searchParams.set("state", state);
        authorization.searchParams.set("code_challenge", challenge);
        authorization.searchParams.set("code_challenge_method", "S256");
        authorization.searchParams.set("prompt", "select_account");
        return redirect(authorization);
      }
      if (url.pathname === "/auth/callback" && request.method === "GET") {
        const state = url.searchParams.get("state") || "";
        const code = url.searchParams.get("code") || "";
        if (!safeSecret(state) || !safeSecret(code)) return security(error("Google 로그인 응답이 올바르지 않습니다.", 400));
        const consumed = await callControl(env, "/oauth/consume", { state });
        if (!consumed.ok) return security(error("Google 로그인 요청이 만료되었습니다.", 400));
        const authorization = await consumed.json();
        if (authorization.purpose === "youtube") {
          const email=await humanEmail(request,env);
          if(email!==authorization.email)return security(error("연결을 시작한 관리자로 다시 로그인해 주세요.",403));
        }
        const tokenResponse = await fetch("https://oauth2.googleapis.com/token", {
          method: "POST",
          headers: { "content-type": "application/x-www-form-urlencoded" },
          body: new URLSearchParams({
            code, client_id: env.GOOGLE_CLIENT_ID, client_secret: env.GOOGLE_CLIENT_SECRET,
            redirect_uri: `${String(env.PUBLIC_BASE_URI).replace(/\/$/, "")}/auth/callback`,
            grant_type: "authorization_code", code_verifier: authorization.verifier
          })
        });
        if (!tokenResponse.ok) return security(error("Google 로그인 코드를 교환하지 못했습니다.", 401));
        const tokens = await tokenResponse.json();
        if (authorization.purpose === "youtube") {
          const saved=await callControl(env,"/youtube/register",{email:authorization.email,roomId:authorization.roomId,tokens});
          if(!saved.ok)return security(saved);
          return redirect(new URL(`/control/youtube?room=${authorization.roomId}`,publicBase));
        }
        const email = await verifyGoogleIdToken(tokens.id_token, env.GOOGLE_CLIENT_ID);
        const sessionResponse = await callControl(env, "/oauth/session/create", { email });
        if (!sessionResponse.ok) return security(sessionResponse);
        const session = await sessionResponse.json();
        return redirect(safeReturnPath(authorization.returnTo), 302, { "set-cookie": sessionCookie(session.token) });
      }
      if (url.pathname === "/auth/logout") {
        const token = cookieValue(request, "__Host-SilentStreamSession");
        if (token) await callControl(env, "/oauth/logout", { token });
        return redirect("/auth/login", 302, {
          "set-cookie": sessionCookie("deleted", 0),
          "clear-site-data": '"cache", "cookies", "storage"'
        });
      }

      let email;
      try { email = await humanEmail(request, env); }
      catch (failure) {
        if (failure instanceof HttpFailure && failure.status === 401) {
          // The installer opens this endpoint as a browser navigation. Resume the exact
          // authorization request after login; other APIs must continue returning 401.
          const installNavigation = url.pathname === "/api/install/authorize" && request.method === "GET";
          if (url.pathname.startsWith("/api/") && !installNavigation) return security(new Response(null, { status: 401 }));
          return redirect(new URL(`/auth/login?returnTo=${encodeURIComponent(url.pathname + url.search)}`, publicBase));
        }
        throw failure;
      }

      if (url.pathname === "/auth/room" && request.method === "GET") {
        const room = normalizeRoom(url.searchParams.get("room"));
        if (!room) return security(error("호실 로그인 요청이 올바르지 않습니다.", 400));
        const returnTo = safeRoomReturnPath(url.searchParams.get("returnTo"));
        const issued = await callControl(env, "/room-handoff/create", { email, roomId: room, returnTo });
        if (!issued.ok) return security(issued);
        const handoff = await issued.json();
        const target = roomOrigin(room, env);
        target.pathname = "/auth/handoff";
        target.searchParams.set("code", handoff.code);
        return redirect(target);
      }

      if (url.pathname === "/health") return security(json({ status: "ok", runtime: "cloudflare-workers" }));

      if (url.pathname === "/control/youtube" && request.method === "GET") {
        const roomId=normalizeRoom(url.searchParams.get("room")||"m112");
        if(!roomId)return security(error("호실을 확인해 주세요.",400));
        const result=await callControl(env,"/youtube/status",{email,roomId});
        if(!result.ok)return security(result);
        const value=await result.json();
        const esc=x=>String(x??"").replace(/[&<>"']/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));
        const labels={pending:"예약됨",waiting:"방송 종료·처리 대기",running:"확인 중",completed:"편입 완료","auth-required":"YouTube 연결 필요",failed:"확인 필요"};
        const rows=value.jobs.map(j=>`<li>${esc(j.date)} · ${esc(labels[j.status]||j.status)}</li>`).join("");
        return security(new Response(`<!doctype html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>서버 재생목록 · SilentStream</title><style>body{font:15px system-ui;color:#243a32;background:#f5f4ef;margin:0}main{max-width:620px;margin:10vh auto;padding:28px}h1{font-size:25px}p,li{line-height:1.8}button,a{color:inherit}button{font:inherit;background:#243a32;color:#fff;border:0;border-radius:8px;padding:13px 18px;cursor:pointer}nav{display:flex;gap:15px;flex-wrap:wrap;margin:24px 0}small{color:#697366}</style><main><a href="/control/">중앙 관리</a><h1>${roomDisplayName(roomId)} 서버 재생목록</h1><p>${value.connected?"YouTube 채널 연결됨":"YouTube 채널을 연결해 주세요."}</p><p>수업 종료 이후 라이브를 날짜별 재생목록에 추가합니다. PC가 꺼져 있어도 서버가 처리합니다.</p><form method="post" action="/api/control/youtube/connect"><input type="hidden" name="room" value="${roomId}"><button>${value.connected?"YouTube 연결 갱신":"YouTube 계정 연결"}</button></form><small>강의가 올라가는 YouTube 채널을 선택하세요. 기존 관리자 로그인은 유지됩니다.</small><nav>${ROOMS.map(room=>`<a href="?room=${room}">${roomDisplayName(room)}</a>`).join("")}</nav><ul>${rows||"<li>예약된 라이브 없음</li>"}</ul><a href="https://m112.silentstream.win/archive/">M112 재생목록 보기</a></main></html>`,{headers:{"content-type":"text/html; charset=utf-8"}}));
      }
      if (url.pathname === "/api/control/youtube/connect" && request.method === "POST") {
        if(!sameOrigin(request))return security(error("요청 출처를 확인할 수 없습니다.",403));
        if(Number(request.headers.get("content-length")||0)>2048)return security(error("요청이 너무 큽니다.",413));
        const formText=await request.text();
        if(formText.length>2048)return security(error("요청이 너무 큽니다.",413));
        const form=new URLSearchParams(formText),roomId=normalizeRoom(form.get("room"));
        if(!roomId)return security(error("호실을 확인해 주세요.",400));
        if(!env.YOUTUBE_TOKEN_KEY)return security(error("서버 연결 준비가 필요합니다.",503));
        const state=randomSecret(),verifier=randomSecret()+randomSecret();
        const saved=await callControl(env,"/oauth/begin",{state,verifier,purpose:"youtube",email,roomId});
        if(!saved.ok)return security(saved);
        const authorization=new URL("https://accounts.google.com/o/oauth2/v2/auth");
        for(const [key,value] of Object.entries({client_id:env.GOOGLE_CLIENT_ID,redirect_uri:new URL("/auth/callback",publicBase).toString(),
          response_type:"code",scope:YOUTUBE_SCOPE,access_type:"offline",prompt:"consent select_account",state,
          code_challenge:await sha256Base64Url(verifier),code_challenge_method:"S256"}))authorization.searchParams.set(key,value);
        return redirect(authorization);
      }
      if (url.pathname === "/ready") return readinessResponse(env);

      if (url.pathname === "/") {
        const room = normalizeRoom(url.hostname.split(".")[0]);
        return redirect(new URL(room ? `/control/?room=${room}` : "/control/", url));
      }
      if (url.pathname === "/control") return redirect(new URL("/control/", url));
      if (url.pathname === "/control/" && request.method === "GET") {
        return security(await env.ASSETS.fetch(new Request(new URL("/control/index.html", url), request)));
      }
      if (url.pathname === "/control/app.js" && request.method === "GET") {
        return security(await env.ASSETS.fetch(request));
      }
      if (url.pathname === "/api/control/session" && request.method === "GET") {
        const response = await callControl(env, "/session", { email });
        if (!response.ok) return security(response);
        const session = await response.json();
        return security(json({
          ...session,
          roomApps: Object.fromEntries(session.rooms.map(room => [room, roomOrigin(room, env).origin])),
          roomNames: Object.fromEntries(session.rooms.map(room => [room, roomDisplayName(room)]))
        }));
      }
      if (url.pathname === "/api/control/rooms" && request.method === "GET") {
        return security(await callControl(env, "/rooms", { email }));
      }
      if (url.pathname === "/api/control/members" && request.method === "GET") {
        return security(await callControl(env, "/members", { email }));
      }
      if (url.pathname === "/api/control/members" && request.method === "POST") {
        if (!sameOrigin(request)) return security(error("요청 출처를 확인할 수 없습니다.", 403));
        return security(await callControl(env, "/members", { email, member: await limitedJson(request) }));
      }
      const memberDelete = url.pathname.match(/^\/api\/control\/members\/([^/]+)$/);
      if (memberDelete && request.method === "DELETE") {
        if (!sameOrigin(request)) return security(error("요청 출처를 확인할 수 없습니다.", 403));
        return security(await callControl(env, "/members/delete", { email, target: decodeURIComponent(memberDelete[1]) }));
      }
      const action = url.pathname.match(/^\/api\/control\/rooms\/([^/]+)\/actions\/([^/]+)$/);
      if (action && request.method === "POST") {
        if (!sameOrigin(request)) return security(error("요청 출처를 확인할 수 없습니다.", 403));
        const roomId = normalizeRoom(decodeURIComponent(action[1]));
        const actionName = decodeURIComponent(action[2]);
        if (!roomId || !ACTIONS[actionName]) return security(new Response("Not Found", { status: 404 }));
        const body = await limitedJson(request);
        const response = await callControl(env, "/command", { email, roomId, action: actionName, body });
        return security(response);
      }
      const installDownload = url.pathname.match(/^\/install\/([^/]+)\/download$/);
      if (installDownload && request.method === "GET") {
        const room = normalizeRoom(installDownload[1]);
        if (!room) return security(new Response("Not Found", { status: 404 }));
        const allowed = await callControl(env, "/authorize-room", { email, roomId: room });
        if (!allowed.ok) return security(allowed);
        return security(await serveArtifact(request, env, env.INSTALLER_KEY, env.INSTALLER_URL, `SilentStream-Install-${room}.exe`, "application/vnd.microsoft.portable-executable"));
      }
      const install = url.pathname.match(/^\/install\/([^/]+)$/);
      if (install && request.method === "GET") {
        const room = normalizeRoom(install[1]);
        if (!room) return security(new Response("Not Found", { status: 404 }));
        const allowed = await callControl(env, "/authorize-room", { email, roomId: room });
        return security(allowed.ok ? installPage(room) : allowed);
      }
      if (url.pathname === "/api/install/authorize" && request.method === "GET") {
        const roomId = normalizeRoom(url.searchParams.get("room"));
        const state = url.searchParams.get("state") || "";
        const callback = url.searchParams.get("callback") || "";
        if (!roomId || state.length < 20 || state.length > 200 || !isSafeLoopbackCallback(callback))
          return security(error("설치 승인 요청이 올바르지 않습니다.", 400));
        const issued = await callControl(env, "/authorize-enrollment", { email, roomId });
        if (!issued.ok) return security(issued);
        const ticket = await issued.json();
        const target = new URL(callback);
        target.searchParams.set("code", ticket.code);
        target.searchParams.set("state", state);
        target.searchParams.set("room", roomId);
        target.searchParams.set("enroll", `${String(env.DEVICE_BASE_URI).replace(/\/$/, "")}/api/device/enroll`);
        return redirect(target);
      }
      return security(new Response("Not Found", { status: 404 }));
    } catch (failure) {
      if (failure instanceof HttpFailure) return security(error(failure.message, failure.status));
      console.error(failure instanceof Error ? failure.message : String(failure));
      return security(error("중앙 서비스 요청을 처리하지 못했습니다.", 500));
    }
  }
};

export class ControlPlane {
  constructor(state, env) {
    this.state = state;
    this.env = env;
    this.pending = new Map();
    this.replayJobs = new YouTubeReplayJobs(state,env);
  }

  async fetch(request) {
    const url = new URL(request.url);
    if (url.pathname === "/ready") return json({ status: "ok" });
    if (url.pathname === "/connect") return this.connect(request);
    const body = request.method === "GET" ? {} : await request.json();
    switch (url.pathname) {
      case "/session": return this.session(body.email);
      case "/rooms": return this.rooms(body.email);
      case "/public-playlists": return this.publicPlaylists();
      case "/room-playlists": {
        if(body.roomId!=="m112"||await this.role(body.email,"m112")==="none")return error("권한이 없습니다.",403);
        return this.publicPlaylists(true);
      }
      case "/youtube/status": {
        if(!normalizeRoom(body.roomId)||await this.role(body.email,body.roomId)!=="admin")return error("관리자 권한이 필요합니다.",403);
        await this.replayJobs.seedDevices();return json(await this.replayJobs.status(body.roomId));
      }
      case "/youtube/register": {
        if(!normalizeRoom(body.roomId)||await this.role(body.email,body.roomId)!=="admin")return error("관리자 권한이 필요합니다.",403);
        try{return json(await this.replayJobs.register(body.tokens));}
        catch{return error("YouTube 편집 권한과 오프라인 연결을 확인해 주세요.",400);}
      }
      case "/members": return request.method === "GET" ? error("Method Not Allowed", 405) : this.members(body);
      case "/members/delete": return this.deleteMember(body);
      case "/authorize-room": return this.authorizeRoom(body.email, body.roomId);
      case "/authorize-enrollment": return this.authorizeEnrollment(body.email, body.roomId);
      case "/enroll": return this.enroll(body);
      case "/auth": return (await this.authenticate(body.authorization)) ? json({ ok: true }) : new Response(null, { status: 401 });
      case "/self": return this.self(body.authorization);
      case "/command": return this.command(body);
      case "/oauth/begin": return this.oauthBegin(body);
      case "/oauth/consume": return this.oauthConsume(body);
      case "/oauth/session/create": return this.oauthSessionCreate(body);
      case "/oauth/session": return this.oauthSession(body);
      case "/oauth/logout": return this.oauthLogout(body);
      case "/room-handoff/create": return this.roomHandoffCreate(body);
      case "/room-handoff/consume": return this.roomHandoffConsume(body);
      default: return new Response("Not Found", { status: 404 });
    }
  }

  async owner() {
    return await this.state.storage.get("ownerEmail") || "";
  }

  async publicPlaylists(authenticated=false) {
    if (!authenticated && this.env.PUBLIC_M112_PLAYLISTS !== "true") return new Response(null, { status: 404 });
    const ids = await this.state.storage.get("roomDevices:m112") || [];
    const devices = (await Promise.all(ids.map(id => this.state.storage.get(`device:${id}`))))
      .filter(device => device && device.roomId === "m112" && !device.revokedAtUtc)
      .sort((a, b) => String(b.lastSeenAtUtc || b.registeredAtUtc || "")
        .localeCompare(String(a.lastSeenAtUtc || a.registeredAtUtc || "")));
    const device = devices[0];
    return normalizePlaylistHistory(device?.playlists, "m112") ? json(recentPublicPlaylists(device))
      : json({ error: "재생목록이 아직 동기화되지 않았습니다." }, 503);
  }

  async ensureOwner(email) {
    const normalized = normalizeEmail(email);
    if (!normalized) return "";
    let owner = await this.owner();
    if (!owner) {
      const expected = normalizeEmail(this.env.BOOTSTRAP_OWNER_EMAIL);
      if (expected && expected !== normalized) return "";
      owner = normalized;
      await this.state.storage.put("ownerEmail", owner);
      await this.state.storage.put(`member:${owner}`, { email: owner, role: "admin", rooms: ROOMS, createdAtUtc: new Date().toISOString() });
    }
    return owner;
  }

  async role(email, roomId = "") {
    const normalized = normalizeEmail(email);
    if (!normalized) return "none";
    const owner = await this.ensureOwner(normalized);
    if (owner === normalized) return "admin";
    const member = await this.state.storage.get(`member:${normalized}`);
    if (!member) return "none";
    if (member.role === "admin") return "admin";
    return member.role === "manager" && member.rooms?.includes(normalizeRoom(roomId)) ? "manager" : "none";
  }

  async permittedRooms(email) {
    const normalized = normalizeEmail(email);
    const role = await this.role(normalized);
    if (role === "admin") return [...ROOMS];
    const member = await this.state.storage.get(`member:${normalized}`);
    return member?.role === "manager" ? ROOMS.filter(room => member.rooms?.includes(room)) : [];
  }

  async session(email) {
    const normalized = normalizeEmail(email);
    if (!normalized) return error("로그인 이메일이 올바르지 않습니다.", 401);
    const rooms = await this.permittedRooms(normalized);
    const admin = await this.role(normalized) === "admin";
    return json({ email: normalized, admin, rooms, owner: (await this.owner()) === normalized });
  }

  async authorizeRoom(email, roomId) {
    const room = normalizeRoom(roomId);
    return room && await this.role(email, room) !== "none" ? json({ allowed: true }) : error("이 호실에 대한 권한이 없습니다.", 403);
  }

  async rooms(email) {
    const allowed = await this.permittedRooms(email);
    const values = [];
    for (const roomId of allowed) {
      const ids = await this.state.storage.get(`roomDevices:${roomId}`) || [];
      const devices = (await Promise.all(ids.map(id => this.state.storage.get(`device:${id}`)))).filter(Boolean).filter(device => !device.revokedAtUtc);
      const connected = new Set(this.state.getWebSockets(roomId).map(socket => socket.deserializeAttachment()?.deviceId).filter(Boolean));
      const device = devices.sort((left, right) => {
        const onlineOrder = Number(connected.has(right.deviceId)) - Number(connected.has(left.deviceId));
        return onlineOrder || String(right.lastSeenAtUtc || right.registeredAtUtc).localeCompare(String(left.lastSeenAtUtc || left.registeredAtUtc));
      })[0];
      values.push(device ? {
        roomId, registered: true, online: connected.has(device.deviceId), deviceId: device.deviceId,
        installationId: device.installationId, machineName: device.machineName, agentVersion: device.agentVersion || "",
        registeredAtUtc: device.registeredAtUtc, lastSeenAtUtc: device.lastSeenAtUtc || null,
        app: normalizeAppStatus(device.app), audio: device.audio || null, inspection: device.inspection || null, recovery: device.recovery || null,
        playlists: device.playlists || null,
        uploads: mergeUploads(device.uploads, device.app, device.lastSeenAtUtc),
        cloudShutdownRequest: await this.shutdownRequest(device),
        role: await this.role(email, roomId)
      } : { roomId, registered: false, online: false, deviceId: "", installationId: "", machineName: "", agentVersion: "", registeredAtUtc: null, lastSeenAtUtc: null, app: null, audio: null, inspection: null, recovery: null, playlists: null, role: await this.role(email, roomId) });
    }
    return json(values);
  }

  async members(body) {
    if (await this.role(body.email) !== "admin") return error("관리자 권한이 필요합니다.", 403);
    if (!body.member) {
      const listed = await this.state.storage.list({ prefix: "member:" });
      return json([...listed.values()].sort((a, b) => a.email.localeCompare(b.email)));
    }
    const email = normalizeEmail(body.member.email);
    const role = body.member.role === "admin" ? "admin" : body.member.role === "manager" ? "manager" : "";
    const rooms = [...new Set((Array.isArray(body.member.rooms) ? body.member.rooms : []).map(normalizeRoom).filter(Boolean))];
    if (!email || !role || role === "manager" && rooms.length === 0) return error("담당자 이메일, 역할, 호실을 확인해 주세요.", 400);
    const member = { email, role, rooms: role === "admin" ? [...ROOMS] : rooms, createdAtUtc: new Date().toISOString() };
    await this.state.storage.put(`member:${email}`, member);
    return json(member);
  }

  async deleteMember(body) {
    if (await this.role(body.email) !== "admin") return error("관리자 권한이 필요합니다.", 403);
    const target = normalizeEmail(body.target);
    if (!target || target === await this.owner()) return error("최초 소유자는 삭제할 수 없습니다.", 409);
    await this.state.storage.delete(`member:${target}`);
    return json({ deleted: true });
  }

  async authorizeEnrollment(email, roomId) {
    const room = normalizeRoom(roomId);
    if (!room || await this.role(email, room) === "none") return error("이 호실을 설치할 권한이 없습니다.", 403);
    const code = randomSecret();
    const hash = await sha256(code);
    await this.state.storage.put(`ticket:${hash}`, {
      roomId: room, createdBy: normalizeEmail(email), createdAtUtc: new Date().toISOString(), expiresAt: Date.now() + 10 * 60 * 1000
    });
    return json({ code });
  }

  async enroll(body) {
    const request = body.request || {};
    const roomId = normalizeRoom(request.roomId);
    if (!roomId || !safeId(request.installationId, 128) || typeof request.machineName !== "string" || request.machineName.length < 1 || request.machineName.length > 128 || !safeSecret(request.enrollmentCode))
      return error("기기 등록 요청이 올바르지 않습니다.", 400);
    const hash = await sha256(request.enrollmentCode);
    const ticketKey = `ticket:${hash}`;
    const ticket = await this.state.storage.get(ticketKey);
    if (!ticket || ticket.roomId !== roomId || ticket.expiresAt <= Date.now()) return new Response(null, { status: 401 });
    await this.state.storage.delete(ticketKey);
    const deviceId = crypto.randomUUID().replaceAll("-", "");
    const deviceSecret = randomSecret();
    const now = new Date().toISOString();
    const device = {
      deviceId, roomId, installationId: request.installationId, machineName: request.machineName.trim(),
      secretHash: await sha256(deviceSecret), registeredBy: ticket.createdBy, registeredAtUtc: now,
      lastSeenAtUtc: null, revokedAtUtc: null, agentVersion: ""
    };
    await this.state.storage.put(`device:${deviceId}`, device);
    const ids = await this.state.storage.get(`roomDevices:${roomId}`) || [];
    await this.state.storage.put(`roomDevices:${roomId}`, [...new Set([...ids, deviceId])].slice(-20));
    const deviceBase = String(body.deviceBaseUri).replace(/\/$/, "");
    const wsBase = deviceBase.replace(/^https:/, "wss:").replace(/^http:/, "ws:");
    return json({
      roomId, deviceId, installationId: request.installationId, deviceSecret,
      controlPlaneBaseUri: String(body.publicBaseUri).replace(/\/$/, ""),
      deviceWebSocketUri: `${wsBase}/api/device/connect`, packageDownloadUri: `${deviceBase}/api/device/package`,
      packageSha256: body.packageInfo.hash, packageLength: body.packageInfo.length, registeredAtUtc: now
    });
  }

  async authenticate(value) {
    const auth = parseDeviceAuthorization(value);
    if (!auth) return null;
    const device = await this.state.storage.get(`device:${auth.deviceId}`);
    if (!device || device.roomId !== auth.roomId || device.revokedAtUtc) return null;
    return constantTimeEqual(device.secretHash, await sha256(auth.deviceSecret)) ? device : null;
  }

  async self(authorization) {
    const device = await this.authenticate(authorization);
    if (!device) return new Response(null, { status: 401 });
    const connected = this.state.getWebSockets(device.roomId).some(socket => socket.deserializeAttachment()?.deviceId === device.deviceId);
    return json({ roomId: device.roomId, deviceId: device.deviceId, connected, reporting: Boolean(device.inspection) });
  }

  async connect(request) {
    const device = await this.authenticate(request.headers.get("authorization") || "");
    if (!device || request.headers.get("upgrade")?.toLowerCase() !== "websocket") return new Response(null, { status: 401 });
    for (const socket of this.state.getWebSockets(device.roomId)) {
      try { socket.close(1000, "replaced"); } catch { }
    }
    const ids = await this.state.storage.get(`roomDevices:${device.roomId}`) || [];
    const updates = {};
    const now = new Date().toISOString();
    for (const id of ids) {
      if (id === device.deviceId) continue;
      const previous = await this.state.storage.get(`device:${id}`);
      if (previous && !previous.revokedAtUtc) updates[`device:${id}`] = { ...previous, revokedAtUtc: now };
    }
    if (Object.keys(updates).length) await this.state.storage.put(updates);
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    this.state.acceptWebSocket(server, [device.roomId]);
    server.serializeAttachment({ roomId: device.roomId, deviceId: device.deviceId, installationId: device.installationId });
    return new Response(null, { status: 101, webSocket: client });
  }

  async shutdownRequest(device) {
    if (!device?.deviceId) return null;
    const request = await this.state.storage.get("shutdownRequest:" + device.roomId);
    if (!request || request.deviceId !== device.deviceId || request.installationId !== device.installationId) return null;
    if (request.state === "queued" && Date.parse(request.targetUtc) - Date.now() < 180000) {
      return this.state.storage.transaction(async storage => {
        const current = await storage.get("shutdownRequest:" + device.roomId);
        if (current?.id !== request.id || current.state !== "queued") return current;
        const expired = {...current,state:"expired",message:"PC 연결 전에 등록 기한이 지나 요청이 만료됐습니다. 뒤늦게 실행하지 않습니다."};
        await storage.put("shutdownRequest:" + device.roomId, expired);
        return expired;
      });
    }
    return request;
  }

  async saveOfflineShutdown(body, roomId) {
    const ids = await this.state.storage.get("roomDevices:" + roomId) || [];
    const devices = (await Promise.all(ids.map(id=>this.state.storage.get("device:"+id))))
      .filter(device=>device&&!device.revokedAtUtc).sort((a,b)=>String(b.lastSeenAtUtc||b.registeredAtUtc).localeCompare(String(a.lastSeenAtUtc||a.registeredAtUtc)));
    const device=devices[0];
    if (!device) return error("등록된 PC가 없습니다.",409);
    const key="shutdownRequest:"+roomId, targetUtc=body.body?.targetUtc, remaining=Date.parse(targetUtc)-Date.now();
    if (!Number.isFinite(remaining)||remaining<180000||remaining>30*86400000)
      return error("현재부터 3분 이후, 30일 이내의 시각을 선택하세요.",400);
    return this.state.storage.transaction(async storage=>{
      const old=await storage.get(key);
      if (old&&(old.state==="dispatching"||old.state==="queued"&&Date.parse(old.targetUtc)-Date.now()>=180000))
        return error("기존 클라우드 요청을 취소한 뒤 새로 예약하세요.",409);
      const local=device.app?.shutdownSchedule;
      if (local&&(["pending","executing"].includes(local.status)||local.status==="scheduled"&&Date.parse(local.targetUtc)>Date.now()))
        return error("마지막 PC 기록에 실행 중인 종료 예약이 있습니다. PC 연결 후 확인·취소하세요.",409);
      const request={id:"cloud-"+crypto.randomUUID(),deviceId:device.deviceId,installationId:device.installationId,
        roomId,targetUtc:new Date(targetUtc).toISOString(),requestedBy:normalizeEmail(body.email),
        createdAtUtc:new Date().toISOString(),state:"queued",
        message:"클라우드에 저장했습니다. PC가 종료 3분 전까지 연결되면 PC 예약으로 등록합니다. 아직 PC 등록은 완료되지 않았습니다."};
      await storage.put(key,request);
      return json({ok:true,body:JSON.stringify({ok:true,message:request.message})},202);
    });
  }

  async cancelCloudShutdown(body,roomId) {
    return this.state.storage.transaction(async storage=>{
      const key="shutdownRequest:"+roomId,request=await storage.get(key);
      if (!request||request.id!==body.body?.id) return error("클라우드 예약 요청이 변경됐습니다. 새로고침하세요.",409);
      if (request.state!=="queued") return error("이미 전달됐거나 종료된 요청입니다. 최신 PC 예약 상태에서 취소하세요.",409);
      await storage.put(key,{...request,state:"cancelled",message:"연결 대기 중인 종료 예약 요청을 취소했습니다."});
      return json({ok:true,body:JSON.stringify({ok:true,message:"클라우드 예약 요청을 취소했습니다."})});
    });
  }

  async deliverShutdownRequest(socket,device) {
    const request=await this.shutdownRequest(device);
    if (request?.state!=="queued"||!device.app?.shutdownScheduleControl||!device.app?.remoteShutdownScheduleCommands) return;
    const authorized=await this.role(request.requestedBy,device.roomId)==="admin";
    const claimed=await this.state.storage.transaction(async storage=>{
      const key="shutdownRequest:"+device.roomId,current=await storage.get(key);
      if (current?.id!==request.id||current.state!=="queued")return false;
      if (!authorized||current.deviceId!==device.deviceId||current.installationId!==device.installationId) {
        await storage.put(key,{...current,state:"failed",message:"예약 요청 권한 또는 대상 PC가 변경돼 전달하지 않았습니다."});return false;
      }
      if (Date.parse(current.targetUtc)-Date.now()<180000) {
        await storage.put(key,{...current,state:"expired",message:"등록 기한이 지나 만료됐습니다."});return false;
      }
      await storage.put(key,{...current,state:"dispatching",message:"연결된 PC에 예약을 등록하고 있습니다."});return true;
    });
    if (!claimed)return;
    // Claim once before sending. Never automatically retry an ambiguous delivery.
    let success=false,message="PC 응답을 확인하지 못했습니다. 최신 PC 예약 상태를 확인하세요.";
    try {
      const response=await this.command({roomId:device.roomId,email:request.requestedBy,
        action:"system.shutdown.schedule",body:{targetUtc:request.targetUtc},queuedDelivery:true,
        queuedDeviceId:device.deviceId,queuedInstallationId:device.installationId});
      const result=await response.json();let local={};try{local=JSON.parse(result.body||"{}")}catch{}
      success=result.ok&&local.ok!==false;
      message=local.message||local.error||result.error||(success?"PC에 종료 예약을 등록했습니다.":message);
    }catch{}
    await this.state.storage.transaction(async storage=>{
      const key="shutdownRequest:"+device.roomId,current=await storage.get(key);
      if(current?.id===request.id&&current.state==="dispatching")
        await storage.put(key,{...current,state:success?"applied":"failed",message,completedAtUtc:new Date().toISOString()});
    });
  }


  async command(body) {
    const roomId = normalizeRoom(body.roomId);
    const target = ACTIONS[body.action];
    const role = await this.role(body.email, roomId);
    if (!roomId || !target) return error("지원하지 않는 명령입니다.", 404);
    if (role === "none" || target.role === "admin" && role !== "admin") return error("이 명령을 실행할 권한이 없습니다.", 403);
    if (body.action === "system.shutdown.schedule.cancel" && String(body.body?.id||"").startsWith("cloud-"))
      return this.cancelCloudShutdown(body,roomId);
    const socket = body.queuedDelivery ? this.state.getWebSockets(roomId).find(socket=>{
      const attachment=socket.deserializeAttachment();
      return attachment?.deviceId===body.queuedDeviceId&&attachment?.installationId===body.queuedInstallationId;
    }) : this.state.getWebSockets(roomId)[0];
    if (!socket && body.action === "system.shutdown.schedule" && !body.queuedDelivery)
      return this.saveOfflineShutdown(body,roomId);
    const commandId = crypto.randomUUID().replaceAll("-", "");
    const now = new Date().toISOString();
    const payload = { method: target.method, path: target.path, body: body.body && Object.keys(body.body).length ? body.body : null };
    await this.state.storage.put(`command:${commandId}`, { commandId, roomId, action: body.action, requestedBy: normalizeEmail(body.email), requestedAtUtc: now, state: "queued" });
    if (!socket) {
      const result = { commandId, ok: false, statusCode: 503, contentType: "application/json", body: "", error: "강의실 PC가 오프라인입니다.", completedAtUtc: now };
      await this.completeCommand(result);
      return json(result, 503);
    }
    const dispatch = { commandId, action: body.action, payload, requestedBy: normalizeEmail(body.email), requestedAtUtc: now };
    const envelope = { schemaVersion: 1, type: "command", requestId: commandId, payload: dispatch };
    let timer;
    const completion = new Promise(resolve => {
      timer = setTimeout(() => resolve({ commandId, ok: false, statusCode: 504, contentType: "application/json", body: "", error: "강의실 PC 응답 시간이 초과되었습니다.", completedAtUtc: new Date().toISOString() }), body.action === "system.shutdown.schedule.cancel" ? 90000 : body.action === "system.shutdown" ? 65000 : 20000);
      this.pending.set(commandId, result => { clearTimeout(timer); resolve(result); });
    });
    try { socket.send(JSON.stringify(envelope)); }
    catch {
      clearTimeout(timer);
      this.pending.delete(commandId);
      const failed = { commandId, ok: false, statusCode: 503, contentType: "application/json", body: "", error: "강의실 PC 연결이 끊어졌습니다.", completedAtUtc: new Date().toISOString() };
      await this.completeCommand(failed);
      return json(failed, 503);
    }
    const result = await completion;
    this.pending.delete(commandId);
    await this.completeCommand(result);
    return json(result, Number(result.statusCode) || (result.ok ? 200 : 500));
  }

  async completeCommand(result) {
    const key = `command:${result.commandId}`;
    const command = await this.state.storage.get(key);
    if (command) await this.state.storage.put(key, { ...command, state: result.ok ? "completed" : "failed", completedAtUtc: result.completedAtUtc, statusCode: result.statusCode, error: result.error || null });
    const commands = await this.state.storage.list({ prefix: "command:" });
    if (commands.size > 500) {
      const oldest = [...commands.entries()].sort((a, b) => String(a[1].requestedAtUtc).localeCompare(String(b[1].requestedAtUtc))).slice(0, commands.size - 500).map(entry => entry[0]);
      await this.state.storage.delete(oldest);
    }
  }

  async webSocketMessage(socket, message) {
    const attachment = socket.deserializeAttachment();
    if (!attachment) return;
    let envelope;
    try { envelope = JSON.parse(typeof message === "string" ? message : new TextDecoder().decode(message)); }
    catch { return; }
    if (envelope?.schemaVersion !== 1) return;
    if (envelope.type === "snapshot") {
      const snapshot = envelope.payload;
      if (normalizeRoom(snapshot?.roomId) !== attachment.roomId || snapshot?.installationId !== attachment.installationId) return;
      const key = `device:${attachment.deviceId}`;
      const device = await this.state.storage.get(key);
      if (!device || device.revokedAtUtc) return;
      const incomingPlaylists = normalizePlaylistHistory(snapshot.playlists, attachment.roomId)
        ?? normalizePlaylistHistory(snapshot.app?.playlists, attachment.roomId);
      const retainedPlaylists=new Map((device.playlists?.items||[]).map(item=>[item.date,item]));
      for(const item of incomingPlaylists?.items||[])
        if(retainedPlaylists.get(item.date)?.status!=="ready"||item.status==="ready")retainedPlaylists.set(item.date,item);
      const playlists = incomingPlaylists ? normalizePlaylistHistory({room:attachment.roomId,
        items:[...retainedPlaylists.values()]}, attachment.roomId) : device.playlists;
      await this.state.storage.put(key, {
        ...device, machineName: String(snapshot.machineName || device.machineName).slice(0, 128), agentVersion: String(snapshot.agentVersion || "").slice(0, 64),
        lastSeenAtUtc: snapshot.capturedAtUtc || new Date().toISOString(), app: normalizeAppStatus(snapshot.app), audio: snapshot.audio || null,
        inspection: snapshot.inspection || null, recovery: snapshot.recovery || null,
        playlists: playlists ?? device.playlists ?? null,
        uploads: mergeUploads(mergeUploads(device.uploads,device.app,device.lastSeenAtUtc), snapshot.app, snapshot.capturedAtUtc)
      });
      await this.deliverShutdownRequest(socket, {...device,app:snapshot.app});
      await this.replayJobs.observe({...device,deviceId:attachment.deviceId,app:normalizeAppStatus(snapshot.app)});
    } else if (envelope.type === "hello") {
      const hello = envelope.payload;
      if (normalizeRoom(hello?.roomId) !== attachment.roomId || hello?.installationId !== attachment.installationId) return;
      const key = `device:${attachment.deviceId}`;
      const device = await this.state.storage.get(key);
      if (device) await this.state.storage.put(key, { ...device, machineName: String(hello.machineName || device.machineName).slice(0, 128), agentVersion: String(hello.agentVersion || "").slice(0, 64), lastSeenAtUtc: new Date().toISOString() });
    } else if (envelope.type === "commandResult") {
      const result = envelope.payload;
      if (!safeId(result?.commandId, 128)) return;
      const resolve = this.pending.get(result.commandId);
      if (resolve) resolve(result);
      else await this.completeCommand(result);
    }
  }

  async oauthBegin(body) {
    if (!safeSecret(body.state) || !safeSecret(body.verifier) || body.verifier.length > 256)
      return error("로그인 요청이 올바르지 않습니다.", 400);
    if(body.purpose==="youtube" && (!normalizeRoom(body.roomId)||await this.role(body.email,body.roomId)!=="admin"))
      return error("관리자 권한이 필요합니다.",403);
    const key = `oauthState:${await sha256(body.state)}`;
    await this.state.storage.put(key, { verifier: body.verifier, returnTo: safeReturnPath(body.returnTo), expiresAt: Date.now() + 10 * 60 * 1000,
      ...(body.purpose==="youtube"?{purpose:"youtube",email:normalizeEmail(body.email),roomId:normalizeRoom(body.roomId)}:{}) });
    return json({ saved: true });
  }

  async oauthConsume(body) {
    if (!safeSecret(body.state)) return error("로그인 요청이 올바르지 않습니다.", 400);
    const key = `oauthState:${await sha256(body.state)}`;
    const value = await this.state.storage.transaction(async store=>{
      const entry=await store.get(key);await store.delete(key);return entry;
    });
    return value && value.expiresAt > Date.now() ? json(value) : error("로그인 요청이 만료되었습니다.", 400);
  }

  async alarm() { await this.replayJobs.runDue(); }

  async oauthSessionCreate(body) {
    const email = normalizeEmail(body.email);
    const requestedRoom = body.roomId === undefined || body.roomId === null ? "" : normalizeRoom(body.roomId);
    if (!email || body.roomId !== undefined && body.roomId !== null && !requestedRoom)
      return error("로그인 세션 요청이 올바르지 않습니다.", 400);
    const token = randomSecret() + randomSecret();
    await this.state.storage.put(`session:${await sha256(token)}`, {
      email,
      roomId: requestedRoom || null,
      expiresAt: Date.now() + 12 * 60 * 60 * 1000
    });
    return json({ token });
  }

  async oauthSession(body) {
    if (!safeSecret(body.token)) return new Response(null, { status: 401 });
    const key = `session:${await sha256(body.token)}`;
    const value = await this.state.storage.get(key);
    if (!value || value.expiresAt <= Date.now()) {
      await this.state.storage.delete(key);
      return new Response(null, { status: 401 });
    }
    return json({ email: value.email, roomId: value.roomId || null });
  }

  async oauthLogout(body) {
    if (safeSecret(body.token)) await this.state.storage.delete(`session:${await sha256(body.token)}`);
    return json({ loggedOut: true });
  }

  async roomHandoffCreate(body) {
    const email = normalizeEmail(body.email);
    const roomId = normalizeRoom(body.roomId);
    if (!email || !roomId || await this.role(email, roomId) === "none")
      return error("이 호실에 대한 권한이 없습니다.", 403);
    const code = randomSecret() + randomSecret();
    await this.state.storage.put(`roomHandoff:${await sha256(code)}`, {
      email,
      roomId,
      returnTo: safeRoomReturnPath(body.returnTo),
      expiresAt: Date.now() + 2 * 60 * 1000
    });
    return json({ code });
  }

  async roomHandoffConsume(body) {
    const roomId = normalizeRoom(body.roomId);
    if (!roomId || !safeSecret(body.code)) return error("호실 로그인 인계 요청이 올바르지 않습니다.", 400);
    const key = `roomHandoff:${await sha256(body.code)}`;
    const value = await this.state.storage.get(key);
    await this.state.storage.delete(key);
    if (!value || value.roomId !== roomId || value.expiresAt <= Date.now())
      return error("호실 로그인 인계 요청이 만료되었습니다.", 401);
    if (await this.role(value.email, roomId) === "none")
      return error("이 호실에 대한 권한이 없습니다.", 403);
    return json({ email: value.email, roomId, returnTo: safeRoomReturnPath(value.returnTo) });
  }
}

async function sha256(value) {
  const digest = await crypto.subtle.digest("SHA-256", textEncoder.encode(String(value)));
  return [...new Uint8Array(digest)].map(byte => byte.toString(16).padStart(2, "0")).join("");
}

async function sha256Base64Url(value) {
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", textEncoder.encode(String(value))));
  let binary = "";
  for (const byte of digest) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
}

function randomSecret() {
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
}

function constantTimeEqual(left, right) {
  if (typeof left !== "string" || typeof right !== "string" || left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index++) difference |= left.charCodeAt(index) ^ right.charCodeAt(index);
  return difference === 0;
}
