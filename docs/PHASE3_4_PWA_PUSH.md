# 원격 컨트롤러 Phase 3/4 — PWA + Web Push (설계·핸드오프)

> 원격 컨트롤러 개선 로드맵(메모 `silentstream-remote-controller-roadmap`)의 **Phase 3(UI 재구조화 + PWA)** 와
> **Phase 4(작은 개선)** 착수 문서. Phase 0~2(HealthMonitor·텔레그램 푸시·멀티호실 그리드)는 완료됨.

## 0. 배경과 이 브랜치의 원칙

- **Phase 3 "UI 재구조화"는 이미 완료됨** — `400db86` Command Deck 리디자인(그리드↔상세, 하드웨어 뒤로가기, 데모 모드)
  + `706cd5d` 폰 승인 카드로 index.html이 이미 재구조화되어 있다. 따라서 Phase 3의 **실제 남은 일 = PWA(설치가능) + Web Push**.
- **Phase 4의 "미리보기 WS 전환" 토대도 이미 존재** — index.html에 `/ws/status` WebSocket 배선이 있다.
- 이 브랜치(`claude/phase3-pwa`)는 **병렬 세션("교시 승인" 6부작)과 충돌을 피하려고 신규 파일만** 추가한다.
  `index.html`·`RemoteControlServer.cs`·`App.xaml.cs`(DI) 편집은 **§4 배선 체크리스트**로 미뤄, 그 세션이 착지한 뒤 한다.

## 1. Web Push 아키텍처 (표준: RFC 8291 + RFC 8292 + RFC 8188)

기존 텔레그램 푸시(`INotifier`)와 **같은 팬아웃**에 붙는다 — `HealthNotificationService`가 심각도 필터를 통과한
`HealthEvent`를 등록된 모든 `INotifier`에 뿌린다. Web Push는 두 번째 `INotifier` 구현일 뿐이다.

```
HealthNotificationService ──▶ INotifier[]
                               ├─ TelegramNotifier   (Phase 1, 기존)
                               └─ WebPushNotifier     (Phase 3, 신규)
                                    ├─ IVapidKeyStore        VAPID P-256 키쌍(1회 생성, 개인키 DPAPI)
                                    ├─ IPushSubscriptionStore 브라우저 구독 목록(전용 JSON 파일)
                                    ├─ WebPushEncryptor      본문 암호화 (RFC 8291 aes128gcm)
                                    └─ VapidSigner           Authorization: vapid JWT (ES256)
```

**전송 1건 흐름:** 구독마다 → `aud`=엔드포인트 origin으로 VAPID JWT 서명 → 알림 JSON을 구독의 p256dh/auth로
aes128gcm 암호화 → `POST <endpoint>` (`Authorization: vapid …`, `Content-Encoding: aes128gcm`, `TTL`). 404/410 = 죽은
구독 → 스토어에서 자동 제거.

**비밀·저장:**
- VAPID 개인키(32B D 스칼라)는 `ITokenProtector`(DPAPI)로 암호화 저장 — OAuth/텔레그램 토큰과 동일 규약.
- 구독·키는 `AppPaths.AppDataDir` 아래 전용 파일(`push_subscriptions.json`, `vapid_keys.json`)에 둔다.
  → **config schema(v8)를 건드리지 않음** = 병렬 세션과 무충돌.

## 2. 신규 파일 (이 브랜치에서 완료)

| 파일 | 역할 |
|---|---|
| `Core/Remote/WebPush/Base64Url.cs` | 무패딩 base64url (RFC 4648 §5) |
| `Core/Remote/WebPush/PushModels.cs` | `StoredPushSubscription` 레코드 |
| `Core/Remote/WebPush/VapidKeys.cs` | VAPID 키쌍 값 + 공개키 base64url |
| `Core/Remote/WebPush/WebPushEncryptor.cs` | **RFC 8291 aes128gcm 본문 암호화 (핵심)** |
| `Core/Remote/WebPush/VapidSigner.cs` | RFC 8292 `Authorization: vapid` JWT(ES256) |
| `Core/Remote/WebPush/PushRemote.cs` | 엔드포인트용 DTO+검증(SplitRemote 패턴, 배선은 미뤄짐) |
| `Core/Contracts/IPushSubscriptionStore.cs`, `IVapidKeyStore.cs` | 계약 |
| `Core/Implementations/PushSubscriptionStore.cs` | 구독 영속(전용 JSON, 원자적 쓰기, endpoint 중복제거) |
| `Core/Implementations/VapidKeyStore.cs` | VAPID 키 생성/영속(개인키 DPAPI) |
| `Core/Implementations/WebPushNotifier.cs` | `INotifier` — 전 구독 전송 + 죽은 구독 정리 |
| `App/Remote/manifest.webmanifest` | PWA 설치 매니페스트 |
| `App/Remote/service-worker.js` | `push`/`notificationclick` 핸들러 |
| `App/Remote/icon-192.png`, `icon-512.png`, `icon.svg` | 앱 아이콘 |
| `tests/…` | 위 각 단위 테스트 (암호화는 RFC 8291 §5 KAT + 왕복) |

## 3. 완료 기준 (AC)

- `WebPushEncryptor`가 **RFC 8291 §5 알려진 답(KAT)** 을 바이트 일치로 재현한다.
- `encrypt→decrypt` 왕복이 임의 메시지에서 원문을 복원한다(자기일관성 테스트).
- `VapidSigner` JWT가 공개키로 검증되고 `aud/exp/sub` 클레임이 올바르다.
- 스토어가 재시작 후에도 구독/키를 보존하고, 개인키는 평문으로 디스크에 남지 않는다.
- `WebPushNotifier`가 오프라인 스텁으로 헤더를 정확히 싣고, 410 응답에 구독을 정리한다.
- **전 신규 테스트 그린 + 기존 테스트 회귀 없음.**

## 4. 배선 체크리스트 (병렬 세션 착지 후 — 이 브랜치에서 미실행)

> 아래는 공유 파일을 건드리므로 "교시 승인" 6부작이 끝난 뒤 메인 브랜치에서 적용한다.

1. **DI 등록** (`App.xaml.cs`): `IVapidKeyStore→VapidKeyStore(AppPaths…)`, `IPushSubscriptionStore→PushSubscriptionStore(AppPaths…)`,
   `WebPushNotifier`를 `INotifier` 팬아웃에 추가(텔레그램과 나란히). `HttpClient`는 텔레그램처럼 생성자 기본값.
2. **`AppPaths.cs`**: `PushSubscriptionsFile`, `VapidKeysFile` 추가(선택 — 현재는 스토어가 파일명을 내부 보유).
3. **정적 파일 서빙** (`RemoteControlServer.cs`): `/manifest.webmanifest`, `/service-worker.js`, `/icon-*.png` 라우트.
   서비스워커는 **루트 스코프**로 서빙해야 `push` 수신 범위가 전체가 된다(경로 주의).
4. **REST 엔드포인트** (`RemoteControlServer.cs`, `PushRemote` 사용):
   `GET /api/push/vapid` → 공개키, `POST /api/push/subscribe`, `DELETE /api/push/subscribe`. 인증은 기존 원격 토큰 게이트 뒤.
5. **CSP**: index.html의 CSP에 서비스워커/매니페스트 허용 확인(같은 출처라 대개 OK).
6. **`App.csproj`**: 신규 정적 파일을 출력에 복사(현재 index.html 번들 방식과 동일하게).
7. **index.html**: `<link rel="manifest">`, 서비스워커 등록, "폰 알림 켜기" 버튼 →
   `Notification.requestPermission` → `PushManager.subscribe({applicationServerKey})` → `POST /api/push/subscribe`.
8. **HTTPS 전제**: 서비스워커·Web Push는 보안 컨텍스트 필수 — Cloudflare 호스트네임(HTTPS)에서만 동작, LAN 평문 http는 불가.
   (텔레그램 푸시가 LAN 폴백을 계속 담당.)

## 5. Phase 4 백로그 (미착수 — 대부분 index.html/서버 편집 필요, §4와 함께)

진단 신호등, 최근 에러로그 뷰, 교시 카운트다운, 강제 재시도/스냅샷/앱 원격 재시작, 시간표 타임피커,
미리보기 WS 완전 전환. 공유 파일 편집이 많아 배선 국면에서 함께 진행.
