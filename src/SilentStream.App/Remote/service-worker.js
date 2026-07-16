// Media Capture Helper 원격 컨트롤러 — 서비스워커 (원격 컨트롤러 개선 Phase 3, PWA + Web Push).
//
// 역할은 넷: ① 설치 즉시 활성화 ② 컨트롤러 화면을 오프라인 앱 셸로 보관 ③ 서버가 보낸 암호화
// 푸시(WebPushNotifier, RFC 8291)를 시스템 알림으로 표시 ④ 알림 탭 시 컨트롤러 창으로 포커스.
// API와 WebSocket은 절대 캐시하지 않는다. 기기가 꺼졌을 때도 화면 자체는 열되, 장비 상태는 앱이
// 네트워크 실패를 감지해 "오프라인"으로 표시해야 오래된 상태를 정상 상태로 오인하지 않는다.

const CACHE_PREFIX = "mch-remote-shell-";
const CACHE_NAME = `${CACHE_PREFIX}v1`;
const APP_SHELL = [
  "/",
  "/manifest.webmanifest",
  "/icon.svg",
  "/icon-192.png",
  "/icon-512.png",
  "/icon-maskable.png",
];

self.addEventListener("install", (event) => {
  // 장치가 온라인인 지금 앱 셸을 먼저 저장한다. 저장에 실패하면 이 워커를 활성화하지 않아
  // 불완전한 오프라인 화면이 기존 정상 워커를 대체하지 않게 한다.
  event.waitUntil(
    caches.open(CACHE_NAME)
      .then((cache) => Promise.all(APP_SHELL.map(async (path) => {
        const separator = path.includes("?") ? "&" : "?";
        const response = await fetch(`${path}${separator}__mch_sw_install=${encodeURIComponent(CACHE_NAME)}`,
          { cache: "reload" });
        if (!response.ok) throw new Error(`앱 셸 저장 실패: ${path} (${response.status})`);
        await cache.put(path, response);
      })))
      .then(() => self.skipWaiting())
  );
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(
        keys.filter((key) => key.startsWith(CACHE_PREFIX) && key !== CACHE_NAME)
          .map((key) => caches.delete(key))
      ))
      .then(() => self.clients.claim())
  );
});

async function refreshShell(request) {
  try {
    const response = await fetch(request);
    // Cloudflare의 origin-down 응답(5xx)을 정상 화면 위에 덮어쓰지 않는다.
    if (!response.ok) return null;
    const cache = await caches.open(CACHE_NAME);
    await cache.put("/", response.clone());
    return response;
  } catch (_) {
    return null;
  }
}

self.addEventListener("fetch", (event) => {
  const request = event.request;
  if (request.method !== "GET") return;

  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;
  // 다음 버전 워커가 새 앱 셸을 받을 때 현재 워커의 캐시가 끼어들지 않게 한다.
  if (url.searchParams.has("__mch_sw_install")) return;

  // 제어·상태 데이터는 항상 장치에서 직접 받는다. 캐시된 API 응답은 안전상 허용하지 않는다.
  if (url.pathname.startsWith("/api/") || url.pathname.startsWith("/ws/")) return;

  if (request.mode === "navigate") {
    event.respondWith((async () => {
      const cached = await caches.match("/");
      const refresh = refreshShell(request);
      if (cached) {
        // 저장 화면은 즉시 보여주고, 온라인이면 다음 방문용 화면을 뒤에서 갱신한다.
        event.waitUntil(refresh);
        return cached;
      }

      const response = await refresh;
      return response || new Response(
        "<!doctype html><meta charset=utf-8><title>기기 오프라인</title>" +
        "<meta name=viewport content='width=device-width,initial-scale=1'>" +
        "<body style='font-family:sans-serif;padding:24px;background:#0e1218;color:#f2f6fb'>" +
        "<h1>기기가 오프라인입니다</h1><p>기기를 켜면 원격 컨트롤 화면이 자동으로 다시 연결됩니다.</p></body>",
        { status: 503, headers: { "Content-Type": "text/html; charset=utf-8" } }
      );
    })());
    return;
  }

  if (APP_SHELL.includes(url.pathname)) {
    event.respondWith(caches.match(request, { ignoreSearch: true }).then((cached) => cached || fetch(request)));
  }
});

self.addEventListener("push", (event) => {
  let title = "Media Capture Helper";
  let body = "송출 상태 알림";
  if (event.data) {
    try {
      const payload = event.data.json();
      title = payload.title || title;
      body = payload.body || body;
    } catch (_) {
      body = event.data.text() || body;
    }
  }
  event.waitUntil(
    self.registration.showNotification(title, {
      body,
      icon: "/icon-192.png",
      badge: "/icon-192.png",
      // 같은 tag → 새 알림이 이전 것을 대체(스팸 방지). renotify 로 진동/소리는 다시 울린다.
      tag: "mch-remote",
      renotify: true,
      data: { url: "/" },
    })
  );
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const target = (event.notification.data && event.notification.data.url) || "/";
  event.waitUntil(
    self.clients.matchAll({ type: "window", includeUncontrolled: true }).then((clients) => {
      for (const client of clients) {
        // 이미 열린 컨트롤러 탭이 있으면 그쪽으로 포커스.
        if ("focus" in client) {
          client.focus();
          if ("navigate" in client) {
            client.navigate(target).catch(() => {});
          }
          return undefined;
        }
      }
      return self.clients.openWindow ? self.clients.openWindow(target) : undefined;
    })
  );
});
