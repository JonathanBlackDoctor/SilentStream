// Media Capture Helper 원격 컨트롤러 — 서비스워커 (원격 컨트롤러 개선 Phase 3, PWA + Web Push).
//
// 역할은 셋: ① 설치 즉시 활성화 ② 서버가 보낸 암호화 푸시(WebPushNotifier, RFC 8291)를 받아
// 시스템 알림으로 표시 ③ 알림 탭 시 컨트롤러 창으로 포커스. 오프라인 캐싱은 하지 않는다 — 컨트롤러는
// 항상 라이브 서버 상태가 필요하므로, fetch 는 네트워크로 그대로 통과시킨다(단, 핸들러 존재 자체가
// 일부 브라우저의 "설치 가능" 판정 조건이다).

self.addEventListener("install", () => {
  // 새 워커를 대기 없이 즉시 활성화 — 컨트롤러는 단일 페이지라 버전 꼬임 위험이 낮다.
  self.skipWaiting();
});

self.addEventListener("activate", (event) => {
  event.waitUntil(self.clients.claim());
});

// 네트워크 패스스루. 실패해도 앱은 자체적으로 재시도/오류표시하므로 여기서 캐시 폴백은 하지 않는다.
self.addEventListener("fetch", () => {});

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
