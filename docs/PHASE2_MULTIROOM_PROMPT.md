# Phase 2 작업 지시서 — 멀티 호실 통합 그리드 (원격 컨트롤러 개선)

> 이 문서는 다음 에이전트 세션에 그대로 전달되는 **자기완결 작업 프롬프트**다.
> 이 저장소를 처음 보는 에이전트도 이 문서만으로 Phase 2를 완수할 수 있어야 하며,
> 여기 적힌 결정사항은 사용자가 이미 확정한 것이므로 재논의하지 않는다.

---

## 1. 프로젝트 한 줄 요약

**Media Capture Helper**(구명 SilentStream): OBS 대체용 완전 자동·비가시적 YouTube 송출 + 로컬 백업 녹화 프로그램.
C# / .NET 8 (WPF) + FFmpeg, Windows 10/11 전용, 한국어 UI. 학원/학교 여러 호실(교실)의 PC에 설치되어
한 채널로 송출하며, 운영자가 **폰 웹 UI**로 원격 모니터링/제어한다.

- 저장소 루트: `D:\조나단\일회성 프로젝트\SilentStraem\obs-alternative`
- 개발 규약: 루트의 `CLAUDE.md` **반드시 준수** (브랜치 `claude/relaxed-davinci-qmmjwl`에서만 작업,
  PR 생성 금지, 코드 식별자·주석은 영어 / UI·로그 문자열은 한국어, 불확실하면 AskUserQuestion)
- 빌드 환경 주의: `dotnet`이 새 셸 PATH에 없다 — 매 셸마다
  `$env:Path = "C:\Program Files\dotnet;" + $env:Path` 선행. 이상한 빌드 캐시 증상이 보이면
  `dotnet build-server shutdown` 후 재빌드.
- 빌드/테스트:
  ```powershell
  dotnet build "<repo>\SilentStream.sln" -c Debug --nologo -v minimal      # 경고 0/오류 0 유지
  dotnet test  "<repo>\tests\SilentStream.Tests\SilentStream.Tests.csproj" # 현재 209개 전부 통과
  ```

## 2. 지금까지 된 것 (선행 Phase, 이미 커밋됨)

4-Phase 로드맵: **P0 헬스/이벤트 레이어 → P1 텔레그램 푸시 → P2 멀티 호실 그리드(이번 작업) → P3 UI 재구조화+PWA.**

- **Phase 0 (`e5118ed`)**: `SilentStream.Core`에 `IHealthMonitor`/`HealthMonitor` — 오케스트레이터·믹서·녹화·업로드 큐를
  구독/폴링해 타입드 `HealthEvent`(MicSilent/RtmpDown/DiskLow/UploadFailed/LiveStarted/LiveStopped)를 방출.
  `ActiveEvents` 프로퍼티로 **현재 진행 중인 이상 상태 스냅샷**을 아무 스레드에서나 읽을 수 있다 ← Phase 2에서 활용 가치 높음.
- **Phase 1 (`655c23f`)**: `INotifier`/`TelegramNotifier`/`HealthNotificationService` — 헬스 이벤트를 텔레그램으로 푸시.
  config 스키마 v6(`NotificationsConfig`).

## 3. 원격 컨트롤러 현재 구조 (Phase 2가 손댈 대상)

| 구성요소 | 파일 | 요점 |
|---|---|---|
| 서버(백엔드) | `src/SilentStream.App/Remote/RemoteControlServer.cs` | 임베디드 Kestrel. 엔드포인트: `GET /`(index.html), `POST /api/pair`(6자리 PIN→디바이스 토큰), `GET /api/status`, `GET/PUT /api/schedule(today)`, `GET /api/audio`+`POST mute/gain`, `GET /api/preview.jpg`, `POST /api/live/start|stop`, `WS /ws/status`. 인증 미들웨어 `ConfigureAuth`: `/api`·`/ws`는 토큰 필수(`Authorization: Bearer` / `X-Device-Token` / `?token=`), `/api/pair`만 예외. |
| 폰 UI(프론트) | `src/SilentStream.App/Remote/index.html` | 순수 HTML/CSS/JS 단일 파일(외부 라이브러리 금지). 토큰을 `localStorage["ss_token"]`에 저장, 모든 fetch가 **상대경로**(동일출처 전제). WS 푸시 + 5초 폴링 폴백, 미리보기 2초 폴링. |
| 계약 | `src/SilentStream.Core/Contracts/IRemoteControlServer.cs` | `RemoteBindMode { Off, Lan, Tailscale, Cloudflare }`, StartAsync/StopAsync만. |
| 인증 유틸 | `src/SilentStream.Core/Remote/RemoteAuth.cs`, `PairingThrottle.cs` | 토큰은 SHA-256 해시만 config에 저장. PIN 브루트포스 잠금. |
| 접속 방식 | PC마다 LAN IP(`http://192.168.x.x:8787`) 또는 **호실별 Cloudflare Tunnel HTTPS URL**(명명형/quick). 허브 서버는 없다. |
| 호실명 | `AppConfig.DeviceName`(호실명, 예 "201호") — `/api/status` 응답의 `room` 필드로 이미 노출됨. |

## 4. 사용자가 확정한 Phase 2 설계 (재논의 금지)

1. **허브 없음 — 각 PC 개별 접속.** 새 중앙 서버/수집 백엔드를 만들지 않는다.
2. **폰(브라우저)이 호실 레지스트리를 보관**: `{ label, baseUrl, token }` 목록을 localStorage에 저장하고,
   폰이 각 호실의 API를 **직접** 호출해 그리드를 구성한다.
3. **페어링은 "교체"가 아니라 "추가"**: 호실 하나 페어링해도 기존 호실 목록이 유지된다.
4. **그리드 대시보드**: 호실별 카드(미리보기 썸네일 + 상태 컬러 + 호실명 + 핵심 지표). 문제 있는 호실이
   한눈에 보이고, **"문제만 보기" 필터** 제공. 카드를 탭하면 기존 단일 호실 대시보드로 드릴다운.
5. **서버측 필수 작업 = CORS 허용 + 경량 `/api/summary`**. 그 외 서버 동작은 바꾸지 않는다.

## 5. 구현 요구사항

### 5-A. 백엔드 (`RemoteControlServer.cs`)

1. **`GET /api/summary`** (신규, 토큰 필수): 그리드 카드용 경량 JSON.
   최소 필드: `room`, `state`, `badge`, `live`(bool), `micWarning`(bool), `silent`(무음 마이크 이름 배열),
   `bitrateKbps`, `freeBytes`, `issues`(선택: `IHealthMonitor.ActiveEvents`를 `{kind, severity, message}` 배열로 —
   주입하려면 생성자에 `IHealthMonitor` 추가; DI에 이미 싱글턴 등록돼 있음). 기존 `/api/status` DTO 빌더 로직을
   재사용하되 시간표/큐 등 무거운 부분은 빼서 **가볍게** 유지.
2. **CORS**: 폰이 호실 A에서 받은 페이지로 호실 B의 API를 호출하므로 교차출처 fetch가 발생한다.
   - `/api/*` 응답에 `Access-Control-Allow-Origin: <요청 Origin 반영>`(와일드카드 말고 반영 방식),
     `Access-Control-Allow-Headers: Authorization, Content-Type, X-Device-Token`,
     `Access-Control-Allow-Methods: GET, POST, PUT, DELETE, OPTIONS`.
   - **함정 ①: OPTIONS 프리플라이트에는 토큰이 없다.** 인증 미들웨어(`ConfigureAuth`)보다 **앞에서**
     OPTIONS를 CORS 헤더와 함께 204로 즉시 응답해야 한다. 안 그러면 모든 교차출처 호출이 401 프리플라이트로 죽는다.
   - **함정 ②: 인증 실패(401) 응답에도 CORS 헤더가 있어야** 프론트가 "토큰 만료"를 구분할 수 있다(없으면 네트워크 오류로만 보임).
   - 보안 원칙 유지: CORS는 응답 읽기 허용일 뿐, **토큰 인증은 그대로**다. 완화하지 말 것.
3. WebSocket(`/ws/status`)은 CORS 대상이 아니므로 손대지 않는다(브라우저는 교차출처 WS 허용, 토큰 쿼리로 이미 인증).

### 5-B. 프론트 (`index.html` — 단일 파일 유지, 외부 라이브러리 금지)

1. **레지스트리**: `localStorage["ss_rooms"] = [{label, baseUrl, token}]`.
   - **하위호환 마이그레이션 필수**: 기존 사용자의 `ss_token`이 있으면 `{label: (status.room 또는 "이 호실"), baseUrl: location.origin, token}`으로 자동 승격 후 `ss_token` 제거.
   - `api(room, path, opts)`처럼 **호실 인자를 받는 형태로 fetch 래퍼를 리팩터**(현재는 전역 token+상대경로).
2. **페어링 화면 확장**: PIN 입력 + (선택) 다른 호실 추가 시 주소 입력란. 현재 페이지 출처(`location.origin`)가
   기본 baseUrl. 페어링 성공 시 레지스트리에 **추가**.
3. **그리드 뷰**(호실 2개 이상일 때 기본 진입 화면):
   - 카드: 호실명, 상태 배지(LIVE 초록/재시도 빨강/대기 회색), ⚠ 무음 표시, 남은 디스크, 비트레이트, 미리보기 썸네일(`{baseUrl}/api/preview.jpg?token=...`).
   - 폴링: `/api/summary`를 호실별 5~10초 간격(전 호실 동시 fetch, `Promise.allSettled`), 미리보기는 그리드에선 10초면 충분.
   - 접속 불가 호실은 "오프라인" 카드로 표시(빨간 테두리) — **오프라인도 '문제'다**.
   - **"⚠ 문제만 보기" 토글**: 문제 판정 = 오프라인 ∨ micWarning ∨ state==Retrying ∨ (issues에 warn/critical 존재).
   - 카드 탭 → 해당 호실의 **기존 단일 대시보드**(현 UI 전체)로 전환, 뒤로가기로 그리드 복귀. 단일 호실만 등록된 경우 기존처럼 바로 단일 대시보드.
4. **기존 단일 대시보드는 room-aware로만 수정**: 모든 fetch/WS/이미지 URL이 선택된 호실의 baseUrl+token을 쓰도록.
   WS URL은 `baseUrl`의 프로토콜에 맞춰 `ws://`/`wss://` 결정.
5. **함정 ③ (혼합 콘텐츠)**: HTTPS로 로드된 페이지는 `http://` 호실을 fetch할 수 없다(브라우저 차단).
   기술적 우회를 시도하지 말고, 페어링 화면에 안내 문구 한 줄("모든 호실은 같은 방식(전부 HTTPS 터널 또는 전부 LAN)으로 접속해야 합니다")과
   레지스트리에 http/https 혼재 시 경고 표시로 처리.
6. UI 문자열 전부 한국어, 기존 다크 테마/스타일 변수 재사용.

### 5-C. 테스트

- 테스트 프로젝트는 `SilentStream.Core`만 참조한다(App/Kestrel 통합 테스트 불가). 서버측 로직 중 테스트 가능한 부분
  (예: summary DTO 구성이나 CORS 정책 판단을 Core의 순수 함수/작은 클래스로 뽑아낼 수 있으면) 유닛테스트 추가.
  뽑아내기 부자연스러우면 억지로 하지 말고 실기기 검증 항목으로 남긴다.
- 기존 **209개 테스트 전부 통과** + 빌드 경고 0/오류 0 유지.
- xUnit 관례: 클래스 `XxxTests`, 메서드 snake_case 문장, 시간 의존 로직은 `Func<DateTime>` 주입(기존 테스트 파일 참조).

## 6. 검증·완료 기준 (AC)

1. 빌드 0경고/0오류, 전체 테스트 통과.
2. (정적 검증) OPTIONS 프리플라이트가 인증 앞단에서 CORS 헤더와 함께 응답하는 코드 경로 확인.
3. (정적 검증) 단일 호실 기존 사용자 시나리오: `ss_token`만 있는 localStorage → 자동 마이그레이션 → 기존과 동일하게 동작.
4. 실행 검증(폰에서 그리드 표시, 교차출처 fetch, 드릴다운)은 **"로컬 Windows + 실기기 검증 필요"로 표시**하고 검증 절차를 보고서에 남긴다.
5. 완료 후 **한 커밋**으로 정리(메시지에 Phase 2 명시), 푸시는 사용자에게 물어보고.

## 7. 강력 권장 작업 방식

1. 코드를 고치기 전에 `RemoteControlServer.cs`와 `index.html`을 **전부 정독**해라(이 문서의 라인 번호는 없다 — 심볼로 찾아라).
2. 구현 후 **적대적 자기검토**를 돌려라(서브에이전트 가능하면 병렬 리뷰→반증 검증): 특히
   ① CORS 프리플라이트/401 경로 ② 레지스트리 마이그레이션·중복 페어링 ③ 그리드 폴링의 오프라인 호실 타임아웃 처리
   (fetch에 `AbortSignal.timeout(...)` 등 — 안 하면 죽은 호실 하나가 전체 갱신을 늦춘다)
   ④ 미리보기 blob URL 누수(기존 코드의 revokeObjectURL 패턴 유지) ⑤ XSS(호실 label/room은 사용자 입력 — innerHTML에 넣을 때 이스케이프).
3. P0/P1에서 이 방식(구현→멀티에이전트 적대 리뷰→확정 버그만 수정→회귀 테스트)으로 각각 3건/10건의 실버그를 잡았다. 같은 기준을 유지해라.
