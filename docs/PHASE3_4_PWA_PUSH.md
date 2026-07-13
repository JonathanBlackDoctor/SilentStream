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

## 4. 배선 체크리스트 — **완료 (커밋 62aa63e)**

> 병렬 "교시 승인" 6부작 + oauth_expiring 착지 후, 아래를 메인 브랜치에서 적용 완료. 빌드 0/0·400 테스트 그린.

1. ✅ **DI 등록** (`ServiceCollectionExtensions.cs`, App.xaml.cs 아님): `IPushSubscriptionStore→PushSubscriptionStore(AppPaths.PushSubscriptionsFile)`,
   `IVapidKeyStore→VapidKeyStore(AppPaths.VapidKeysFile, ITokenProtector)`, `WebPushNotifier`를 2번째 `INotifier`로 등록 →
   `HealthNotificationService`가 `IEnumerable<INotifier>`로 텔레그램과 함께 팬아웃. `HttpClient`는 생성자 기본값.
2. ✅ **`AppPaths.cs`**: `PushSubscriptionsFile`, `VapidKeysFile` 추가.
3. ✅ **정적 파일 서빙** (`RemoteControlServer.cs`): `/manifest.webmanifest`·`/service-worker.js`·`/icon.svg`·`/icon-*.png` 라우트
   (임베디드 리소스, `Results.Content`/`Results.Bytes`). `/api`·`/ws` 밖이라 무인증. 서비스워커는 루트에서 서빙 → 스코프 "/".
4. ✅ **REST 엔드포인트** (`RemoteControlServer.cs`, `PushRemote`): `GET /api/push/vapid`, `POST/DELETE /api/push/subscribe` —
   기존 원격 토큰 게이트 뒤(splits 패턴 그대로).
5. ✅ **CSP**: index.html에 CSP 없음 → 서비스워커/매니페스트 제약 없음(같은 출처).
6. ✅ **`App.csproj`**: 신규 정적 파일 6종을 `<EmbeddedResource>`로 추가(index.html과 동일 번들 방식).
7. ✅ **index.html**: `<link rel="manifest">`+아이콘+theme-color, 서비스워커 등록, 상세 뷰에 "폰 알림" 카드
   (`Notification.requestPermission` → `PushManager.subscribe({applicationServerKey})` → `POST /api/push/subscribe`, 해제도 지원).
   미지원·평문 컨텍스트에선 카드 숨김.
8. **HTTPS 전제(런타임 성질)**: 서비스워커·Web Push는 보안 컨텍스트 필수 — Cloudflare 호스트네임(HTTPS)에서만 동작,
   LAN 평문 http는 불가(텔레그램이 LAN 폴백 담당).

## 4.1 다중 호실 공유 VAPID — 구현 완료 (실기기 검증 대기)

- 프로비저닝 서버의 비공개 `rooms.json` 최상위 `sharedVapid`는 모든 등록 호실에 같은 VAPID 키쌍을 배포한다.
  신규 claim은 키를 즉시 받고, 기존 설치는 `roomId + installationId + DPAPI 복호화 터널 토큰`으로 인증한
  갱신 API에서만 키를 받는다. 키는 계속 `vapid_keys.json`에 DPAPI로 저장되고 `config.json`에는 기록하지 않는다.
- 키 설치는 검증·원자 교체한다. 실제 키 교체 때만 기존 `push_subscriptions.json`을 비워 이전 키로 암호화된
  푸시가 남지 않게 하며, 폰에서 알림을 다시 켜야 한다.
- PWA는 하나의 서비스워커 구독의 application-server key를 모든 호실과 비교해 같은 키의 호실에 모두 등록한다.
  등록/해제 결과는 `성공 N / 대상 M`과 실패 호실명으로 표시한다. 호실 키가 다르거나 연결할 수 없으면 기존
  브라우저 구독을 무조건 끊지 않고 `호실 업데이트·재연결 필요`로 보여 준다. 따라서 이 상태를 "전체 알림"으로
  표시하지 않는다.
- 프로비저닝되지 않은 단일 호실과 quick-tunnel 설치는 로컬 VAPID 키를 유지한다. 이들은 다중 호실 전체 알림
  대상에는 포함되지 않는다.

운영 준비·키 생성 절차는 [프로비저닝 서버 README](../src/SilentStream.ProvisioningServer/README.md)를 따른다.

**잔여 = 실기기 런타임 검증만**: HTTPS(Cloudflare) + 실제 폰에서 ①"폰 알림 켜기" → 구독 저장 ②상태 변화(무음/송출끊김)
발생 시 잠금화면 푸시 수신 ③알림 탭 → 컨트롤러 포커스 ④"폰 알림 끄기" → 구독 제거. (앱 자동송출 성질상 개발 PC 실행은 보류.)

## 5. Phase 4 작은 개선 — 구현 완료 (실기기 검증 대기)

원격 컨트롤러의 운용 기능을 한 묶음으로 배선했다.

- **진단**: 활성 헬스 신호를 심각도별 신호등으로 보여주고, 메모리 로그의 최근 WARN/ERROR 20줄을
  확인할 수 있다. 시간표는 현재 교시 종료 또는 다음 교시 시작까지 PC 기준으로 카운트다운한다.
- **복구 동작**: `POST /api/live/retry`는 재시도 백오프를 즉시 깨우거나, 라이브 중에는 기존 방송 URL을
  유지한 채 인코더를 한 번 재구성한다. 스냅샷은 `GET /api/snapshot.jpg`로 저장한다. 앱 재시작은
  `POST /api/app/restart`이며, 폰 UI에서 2초 길게 누르기+확인을 거쳐 송출/녹화를 마감한 뒤 새 인스턴스를
  시작한다.
- **조작성·실시간성**: 시간 입력을 모바일 `type=time` 피커로 바꾸고, `IPreviewProvider.FrameUpdated`의
  JPEG를 `/ws/status`에 바이너리 프레임으로 전송한다. WebSocket이 끊긴 경우에만 기존 HTTP 프리뷰
  폴링으로 폴백한다.

**검증 상태:** Core 및 프로비저닝 단위 테스트와 Windows 오디오 회귀 테스트를 자동화했다. 실제 Cloudflare 폰에서
다중 호실 알림·진단 갱신·프리뷰 프레임·스냅샷·강제 복구·안전 재시작은 별도 실기기 확인이 필요하다.
