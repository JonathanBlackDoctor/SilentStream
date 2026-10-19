import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import worker, {
  ControlPlane,
  isSafeLoopbackCallback,
  normalizePlaylistHistory,
  normalizeRoom,
  parseDeviceAuthorization,
  roomDisplayName,
  roomForHostname
} from "../src/index.js";

const productionEnv = {
  PUBLIC_BASE_URI: "https://admin.silentstream.win",
  DEVICE_BASE_URI: "https://agent.silentstream.win",
  GOOGLE_CLIENT_ID: "test-client-id",
  GOOGLE_CLIENT_SECRET: "test-client-secret"
};

test("serves the authenticated control document without an asset redirect loop", async () => {
  const config = JSON.parse(await readFile(new URL("../wrangler.jsonc", import.meta.url), "utf8"));
  assert.equal(config.assets.html_handling, "none");
  assert.equal(config.assets.run_worker_first, true);
});

test("redirects every unauthenticated human page and status probe to login without a body", async () => {
  for (const path of ["/", "/control/", "/control/app.js", "/health", "/ready", "/unknown"]) {
    const response = await worker.fetch(new Request(`https://admin.silentstream.win${path}`), productionEnv);
    assert.equal(response.status, 302, path);
    assert.equal(new URL(response.headers.get("location")).pathname, "/auth/login", path);
    assert.equal(response.headers.get("cache-control"), "no-store", path);
    assert.equal(await response.text(), "", path);
  }
});

test("returns a bodyless 401 for unauthenticated human APIs", async () => {
  const response = await worker.fetch(
    new Request("https://admin.silentstream.win/api/control/rooms"),
    productionEnv
  );
  assert.equal(response.status, 401);
  assert.equal(response.headers.get("cache-control"), "no-store");
  assert.equal(await response.text(), "");
});

test("installer browser navigation resumes its room, state and callback after login", async () => {
  const requestUrl = new URL("https://admin.silentstream.win/api/install/authorize");
  requestUrl.search = new URLSearchParams({room: "m112", state: "installer-state-1234567890", callback: "http://127.0.0.1:54321/callback/"}).toString();
  const response = await worker.fetch(new Request(requestUrl), productionEnv);
  assert.equal(response.status, 302);
  const login = new URL(response.headers.get("location"));
  assert.equal(login.origin, productionEnv.PUBLIC_BASE_URI);
  assert.equal(login.pathname, "/auth/login");
  assert.equal(login.searchParams.get("returnTo"), requestUrl.pathname + requestUrl.search);
  assert.equal(response.headers.get("cache-control"), "no-store");
  const post = await worker.fetch(new Request(requestUrl, {method: "POST"}), productionEnv);
  assert.equal(post.status, 401);
});

test("redirects the apex and www hostnames to the protected admin portal without content", async () => {
  for (const hostname of ["silentstream.win", "www.silentstream.win"]) {
    const response = await worker.fetch(new Request(`https://${hostname}/`), productionEnv);
    assert.equal(response.status, 302, hostname);
    assert.equal(response.headers.get("location"), "https://admin.silentstream.win/control/", hostname);
    assert.equal(response.headers.get("cache-control"), "no-store", hostname);
    assert.equal(await response.text(), "", hostname);
  }
});

test("keeps machine health probes on the device-only hostname", async () => {
  const response = await worker.fetch(
    new Request("https://agent.silentstream.win/health"),
    productionEnv
  );
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { status: "ok", runtime: "cloudflare-workers" });
});

test("normalizes only known rooms", () => {
  assert.equal(normalizeRoom(" M112 "), "m112");
  assert.equal(normalizeRoom("unknown"), "");
  assert.equal(normalizeRoom("../m112"), "");
  assert.equal(roomDisplayName("baek202"), "백202");
  assert.equal(roomForHostname("m112.silentstream.win", productionEnv), "m112");
  assert.equal(roomForHostname("m112.example.com", productionEnv), "");
});

test("serves a distinct install manifest from every room origin", async () => {
  const expectedNames = { m111: "M111", m112: "M112", m113: "M113", baek202: "백202" };
  for (const [room, displayName] of Object.entries(expectedNames)) {
    const response = await worker.fetch(
      new Request(`https://${room}.silentstream.win/manifest.webmanifest`),
      productionEnv
    );
    assert.equal(response.status, 200, room);
    assert.match(response.headers.get("content-type"), /^application\/manifest\+json/, room);
    const manifest = await response.json();
    assert.equal(manifest.id, "/", room);
    assert.equal(manifest.start_url, "/?source=pwa", room);
    assert.equal(manifest.scope, "/", room);
    assert.equal(manifest.short_name, displayName, room);
    assert.equal(manifest.icons.length, 3, room);
    assert.match(manifest.icons[2].src, /icon-maskable\.png/, room);
  }
  for (const path of ["/manifest.webmanifest", "/service-worker.js", "/icons/icon-192.png?v=1"]) {
    const assets = { fetch: request => new Response(null, { status: 200, headers: { "content-type": request.url.endsWith(".js") ? "text/javascript" : "image/png" } }) };
    const response = await worker.fetch(new Request(`https://m112.silentstream.win${path}`, { method: "HEAD" }), { ...productionEnv, ASSETS: assets });
    assert.equal(response.status, 200, `HEAD ${path}`);
  }
});

test("redirects an unauthenticated room app to the central room handoff", async () => {
  const response = await worker.fetch(
    new Request("https://m112.silentstream.win/?source=pwa"),
    productionEnv
  );
  assert.equal(response.status, 302);
  const location = new URL(response.headers.get("location"));
  assert.equal(location.origin, "https://admin.silentstream.win");
  assert.equal(location.pathname, "/auth/room");
  assert.equal(location.searchParams.get("room"), "m112");
  assert.equal(location.searchParams.get("returnTo"), "/?source=pwa");
});

test("room origin returns only its own room and rejects cross-room commands", async () => {
  const calls = [];
  const control = {
    async fetch(request) {
      const path = new URL(request.url).pathname;
      const body = await request.json();
      calls.push({ path, body });
      if (path === "/authorize-room") return new Response(JSON.stringify({ allowed: true }), { status: 200 });
      if (path === "/rooms") return new Response(JSON.stringify([
        { roomId: "m112", registered: true, online: true, role: "manager" },
        { roomId: "m113", registered: true, online: true, role: "manager" }
      ]), { status: 200 });
      if (path === "/command") return new Response(JSON.stringify({ ok: true }), { status: 200 });
      return new Response("Not Found", { status: 404 });
    }
  };
  const env = {
    ...productionEnv,
    ENABLE_DEVELOPMENT_IDENTITY: "true",
    CONTROL: { idFromName: () => "global", get: () => control }
  };
  const headers = { "x-silentstream-dev-email": "manager@example.com" };
  const roomsResponse = await worker.fetch(
    new Request("https://m112.silentstream.win/api/control/rooms", { headers }),
    env
  );
  assert.equal(roomsResponse.status, 200);
  assert.deepEqual((await roomsResponse.json()).map(room => room.roomId), ["m112"]);

  const rejected = await worker.fetch(new Request(
    "https://m112.silentstream.win/api/control/rooms/m113/actions/live.start",
    { method: "POST", headers: { ...headers, origin: "https://m112.silentstream.win", "content-type": "application/json" }, body: "{}" }
  ), env);
  assert.equal(rejected.status, 404);
  assert.equal(calls.some(call => call.path === "/command"), false);

  const accepted = await worker.fetch(new Request(
    "https://m112.silentstream.win/api/control/rooms/m112/actions/live.start",
    { method: "POST", headers: { ...headers, origin: "https://m112.silentstream.win", "content-type": "application/json" }, body: "{}" }
  ), env);
  assert.equal(accepted.status, 200);
  assert.equal(calls.filter(call => call.path === "/command").length, 1);
});

test("room handoff is permission checked, room bound, and single use", async () => {
  const values = new Map();
  const storage = {
    async get(key) { return values.get(key); },
    async put(key, value) { values.set(key, value); },
    async delete(key) { values.delete(key); },
    async list({ prefix }) { return new Map([...values].filter(([key]) => key.startsWith(prefix))); }
  };
  await storage.put("ownerEmail", "owner@example.com");
  await storage.put("member:manager@example.com", { email: "manager@example.com", role: "manager", rooms: ["m112"] });
  const plane = new ControlPlane({ storage, getWebSockets: () => [] }, {});
  const issued = await plane.roomHandoffCreate({ email: "manager@example.com", roomId: "m112", returnTo: "/?source=pwa" });
  assert.equal(issued.status, 200);
  const { code } = await issued.json();
  const consumed = await plane.roomHandoffConsume({ code, roomId: "m112" });
  assert.equal(consumed.status, 200);
  assert.deepEqual(await consumed.json(), { email: "manager@example.com", roomId: "m112", returnTo: "/?source=pwa" });
  assert.equal((await plane.roomHandoffConsume({ code, roomId: "m112" })).status, 401);
  assert.equal((await plane.roomHandoffCreate({ email: "manager@example.com", roomId: "m113", returnTo: "/" })).status, 403);

  const sessionResponse = await plane.oauthSessionCreate({ email: "manager@example.com", roomId: "m112" });
  const sessionToken = (await sessionResponse.json()).token;
  const session = await plane.oauthSession({ token: sessionToken });
  assert.equal((await session.json()).roomId, "m112");
});

test("central login hands a one-time code to the room origin and sets a host-only room session", async () => {
  const code = "h".repeat(64);
  const token = "t".repeat(64);
  const adminToken = "a".repeat(64);
  const control = {
    async fetch(request) {
      const path = new URL(request.url).pathname;
      const body = await request.json();
      if (path === "/oauth/session") return new Response(JSON.stringify({ email: "manager@example.com", roomId: null }), { status: 200 });
      if (path === "/room-handoff/create") {
        assert.equal(body.roomId, "m112");
        return new Response(JSON.stringify({ code }), { status: 200 });
      }
      if (path === "/room-handoff/consume") {
        assert.equal(body.roomId, "m112");
        assert.equal(body.code, code);
        return new Response(JSON.stringify({ email: "manager@example.com", roomId: "m112", returnTo: "/?source=pwa" }), { status: 200 });
      }
      if (path === "/oauth/session/create") {
        assert.equal(body.roomId, "m112");
        return new Response(JSON.stringify({ token }), { status: 200 });
      }
      return new Response("Not Found", { status: 404 });
    }
  };
  const env = { ...productionEnv, CONTROL: { idFromName: () => "global", get: () => control } };
  const issued = await worker.fetch(new Request(
    "https://admin.silentstream.win/auth/room?room=m112&returnTo=%2F%3Fsource%3Dpwa",
    { headers: { cookie: `__Host-SilentStreamSession=${adminToken}` } }
  ), env);
  assert.equal(issued.status, 302);
  const target = new URL(issued.headers.get("location"));
  assert.equal(target.origin, "https://m112.silentstream.win");
  assert.equal(target.pathname, "/auth/handoff");
  assert.equal(target.searchParams.get("code"), code);

  const accepted = await worker.fetch(new Request(`https://m112.silentstream.win/auth/handoff?code=${code}`), env);
  assert.equal(accepted.status, 302);
  assert.equal(accepted.headers.get("location"), "https://m112.silentstream.win/?source=pwa");
  const cookie = accepted.headers.get("set-cookie");
  assert.match(cookie, new RegExp(`^__Host-SilentStreamSession=${token}`));
  assert.match(cookie, /Secure/);
  assert.doesNotMatch(cookie, /Domain=/i);
});

test("accepts only the device authorization format", () => {
  assert.deepEqual(parseDeviceAuthorization(`Device m111:${"a".repeat(32)}:${"b".repeat(32)}`), {
    roomId: "m111", deviceId: "a".repeat(32), deviceSecret: "b".repeat(32)
  });
  assert.equal(parseDeviceAuthorization("Bearer token"), null);
  assert.equal(parseDeviceAuthorization("Device m111:id:short"), null);
});

test("normalizes playlist history and derives safe YouTube links", () => {
  const history = normalizePlaylistHistory({ items: [
    { date: "2026-09-03", title: "M112 | 2026-09-03", status: "ready", playlistId: "PL_previous", url: "https://evil.test/" },
    { date: "2026-09-05", title: "M112 | 2026-09-05", status: "ready", playlistId: "PL_today" },
    { date: "2026-02-31", title: "invalid date", status: "ready", playlistId: "PL_invalid" },
    { date: "2026-09-04", title: "auth", status: "auth-required", playlistId: null, error: "로그인 필요" }
  ] }, "m112");

  assert.equal(history.room, "m112");
  assert.deepEqual(history.items.map(item => item.date), ["2026-09-05", "2026-09-04", "2026-09-03"]);
  assert.equal(history.items[0].url, "https://www.youtube.com/playlist?list=PL_today");
  assert.equal(history.items[2].url, "https://www.youtube.com/playlist?list=PL_previous");
  assert.equal(history.items[1].url, null);
});

test("central mobile portal links to the room-specific manager apps", async () => {
  const script = await readFile(new URL("../public/control/app.js", import.meta.url), "utf8");
  assert.doesNotMatch(script, /playlistPanel/);
  assert.doesNotMatch(script, /data-copy-playlist/);
  assert.match(script, /강의실 PC 설치/);
  assert.match(script, /담당자 앱/);
  assert.match(script, /session\?\.roomApps/);
});

test("room manager app prioritizes today's link and explicitly browses earlier dates", async () => {
  const app = await readFile(new URL("../public/room/app.js", import.meta.url), "utf8");
  const document = await readFile(new URL("../public/room/index.html", import.meta.url), "utf8");
  assert.match(app, /HISTORY_PAGE_SIZE=5/);
  assert.match(app, /items\.slice\(start,start\+HISTORY_PAGE_SIZE\)/);
  assert.match(app, /다른 날짜 찾기/);
  assert.match(app, /id="playlistDate" type="date"/);
  assert.match(app, /data-playlist-older/);
  assert.match(app, /data-playlist-newer/);
  assert.match(app, /data-copy-playlist/);
  assert.match(app, /data-copy-replay/);
  assert.match(app, /교시별 영상과 별도로 저장되는 전체 방송/);
  assert.match(app, /endedAtUtc/);
  assert.match(app, /uploadPanel/);
  assert.match(app, /UploadView\.render/);
  assert.match(app, /navigator\.clipboard\.writeText/);
  assert.match(app, /document\.execCommand\("copy"\)/);
  assert.match(document, /playlist-history-toggle/);
  assert.doesNotMatch(document, /app\.js\?v=3/);
  assert.match(document, /app\.js\?v=15/);
});

test("room PWA assets include install guidance, a safe offline page, and valid PNG sizes", async () => {
  const app = await readFile(new URL("../public/room/app.js", import.meta.url), "utf8");
  const document = await readFile(new URL("../public/room/index.html", import.meta.url), "utf8");
  const serviceWorker = await readFile(new URL("../public/room/service-worker.js", import.meta.url), "utf8");
  assert.match(document, /rel="manifest"/);
  assert.match(document, /apple-touch-icon/);
  assert.match(document, /\/app\.js\?v=15/);
  assert.match(document, /id="installButton">앱 설치<\/button>/);
  assert.doesNotMatch(document, /id="installButton" hidden/);
  assert.match(app, /beforeinstallprompt/);
  assert.match(app, /홈 화면에 추가/);
  assert.match(app, /openInstallHelp/);
  assert.match(app, /설치 주소를 복사했습니다/);
  assert.match(serviceWorker, /모든 원격 제어를 숨겼습니다/);
  assert.doesNotMatch(serviceWorker, /caches\./);

  for (const room of ["m111", "m112", "m113", "baek202"]) {
    for (const [file, size] of [["icon-192.png", 192], ["icon-512.png", 512], ["icon-maskable.png", 512], ["apple-touch-icon.png", 180]]) {
      const png = await readFile(new URL(`../public/room/icons/${room}/${file}`, import.meta.url));
      assert.equal(png.subarray(1, 4).toString("ascii"), "PNG", `${room}/${file}`);
      assert.equal(png.readUInt32BE(16), size, `${room}/${file} width`);
      assert.equal(png.readUInt32BE(20), size, `${room}/${file} height`);
    }
  }
});

test("installation callback is loopback HTTP with a non-privileged port", () => {
  assert.equal(isSafeLoopbackCallback("http://127.0.0.1:49152/callback/"), true);
  assert.equal(isSafeLoopbackCallback("http://localhost:49152/callback/"), true);
  assert.equal(isSafeLoopbackCallback("https://example.com/callback"), false);
  assert.equal(isSafeLoopbackCallback("http://127.0.0.1:80/callback/"), false);
});
