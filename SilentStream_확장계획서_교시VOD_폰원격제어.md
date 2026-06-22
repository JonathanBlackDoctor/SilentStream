# SilentStream 확장 계획서 — 교시 자동 분할 VOD 업로드 + 스마트폰 원격 제어

> 본 문서는 [`SilentStream_개발계획서.md`](./SilentStream_개발계획서.md)(단일 진실 공급원, Phase 0~6 완료)에
> **새 기능 두 가지를 추가**하기 위한 확장 계획서입니다. 기존 계획서를 대체하지 않고 **이어붙입니다**.
> 실행 규약(브랜치/커밋/AC/언어)은 [`CLAUDE.md`](./CLAUDE.md) 와 동일하게 따릅니다.
>
> - 작성일: 2026-06-18
> - 개발 브랜치: `claude/relaxed-davinci-qmmjwl` (기존 규약 유지)
> - 확장 Phase 번호: **E1 ~ E5** (기존 Phase 0~6과 구분)

---

## 0. 한 줄 요약

> **하루 종일 1개 라이브 스트림은 그대로 유지**하면서, 사용자가 입력한 **교시 시간표(초 단위)** 에 맞춰
> 로컬 녹화본을 **교시별로 잘라 "N교시 - 날짜" 제목으로 YouTube에 자동 업로드**하고,
> 이 모든 것을 **스마트폰 브라우저로 원격 제어**한다.

---

## 1. 확정된 결정 사항 (Decision Log)

> 사용자와 합의 완료(2026-06-18). 이후 변경은 본 표를 갱신한다.

| # | 결정 항목 | 확정 내용 |
|---|---|---|
| D1 | 라이브 송출 방식 | **하루 종일 1개 연속 스트림** (기존 §3.7 라이브 동작을 그대로 유지) |
| D2 | VOD 분할 | **교시 시간표대로 분할** — 각 교시 = 영상 1개 (쉬는 시간은 VOD에서 제외) |
| D3 | 라이브 + VOD 관계 | **둘 다 동시 제공** — 라이브는 실시간, VOD는 교시별 보관본 |
| D4 | VOD 공개 범위 | **미등록(unlisted)** — 라이브와 동일 |
| D5 | 시간표 입력 방식 | **요일별 고정 기본값 + 그날 통째 덮어쓰기(override)** 둘 다 지원 |
| D6 | 제목 형식 | **교시 번호 + 날짜** (예: `1교시 - 2026-06-14`) |
| D7 | 업로드 한도 초과(7교시+) | **quota 증설 신청 전제**. 초과·실패분은 **로컬 보관 후 다음날 자동 업로드** (영속 큐) |
| D8 | 원격 제어 도달 범위 | **LAN(같은 와이파이) 우선 + Tailscale 확장 대비** (3단계 클라우드 릴레이는 비채택) |
| D9 | OAuth 스코프 | 현재 `YouTubeService.Scope.Youtube`(전체)로 `videos.insert` 포함 → **재로그인 불필요** |
| D10 | 업로드 타이밍(구 U1) | **즉시 + 업링크 대역 제한**(`immediate-throttled`). 업링크가 좁으면 `after-hours`로 전환 가능 |
| D11 | 원격 인증(구 U2) | **PIN 페어링 + 기기 토큰**. 집 밖은 Tailscale(트래픽 암호화) 권장 |
| D12 | 멀티 PC·1채널 호실명 분류 | 여러 PC가 **한 채널**에 송출할 때 PC별 **호실명**(`config.deviceName`, 호실명)을 라이브·VOD 제목의 `{호실}` 토큰으로 치환 → `[201호] 1교시 - 2026-06-23`. 빈 값이면 `[{호실}] ` 접두사 자동 생략. 기본 템플릿에만 접두사 포함(마이그레이션은 기존 커스텀 템플릿 미변경). 호스트명 자동 시드는 **신규 설치에 한함**(기존 설정 미각인). 채널 공유는 **브랜드 계정 + 호실별 매니저 권한**, API 할당량은 Cloud 프로젝트 단위 공유라 PC별 프로젝트 분리/증설 권장 |

### 1.1 기본값으로 확정 (2026-06-18)
초기 "미확정"으로 두었던 U1·U2를 권장 기본값으로 **확정**했다(위 D10·D11). 학교 업링크 환경/보안 요구가
바뀌면 config로 사후 조정 가능하다(§4.3·§4.4).

---

## 2. 기능 개요

### 2.1 기능 A — 교시 자동 분할 VOD 업로드 (신규)
사용자가 교시 시간표(예: `1교시 09:00:00~09:50:00`)를 입력하면, 앱이 교시 경계 시각에 맞춰
**이미 진행 중인 로컬 녹화본에서 해당 구간을 무손실로 잘라** 교시 영상 파일을 만들고,
`videos.insert`(재개 가능 업로드)로 YouTube에 **미등록** 영상으로 올린다. 제목은 `N교시 - yyyy-MM-dd`.

### 2.2 기능 B — 스마트폰 원격 제어 (신규)
앱 안에 작은 웹서버를 띄워, 같은 와이파이(또는 Tailscale)에 있는 **폰 브라우저로 접속**해
시간표 입력/수정, 라이브 시작·중지, 상태/업로드 진행 확인을 할 수 있게 한다. 폰에는 앱 설치 불필요.

### 2.3 기존 기능과의 관계 (중요)
- **라이브 송출·tee 파이프라인·세션 녹화는 건드리지 않는다.** (라이브 안정성 최우선)
- 신규 기능은 **세션 녹화 mp4를 읽기(read-only)** 만 하고, 별도 모듈로 분할·업로드·원격제어를 얹는다.
- 따라서 기존 Phase 0~6의 검증 결과는 영향을 받지 않는다.

---

## 3. 사용자 시나리오

```
[전날/학기초] 폰으로 접속 → 요일별 기본 시간표 입력 (월: 1~6교시 / 화: 1~7교시 ...)  ← D5
[당일 아침]   PC 부팅 → (기존) 30초 후 라이브 자동 시작 + 세션 녹화 시작
[단축수업일]  폰으로 "오늘만 덮어쓰기" → 그날 교시표 교체                              ← D5
[1교시 종료]  스케줄러가 09:50:00 감지 → 세션 녹화본에서 [09:00:00~09:50:00] 무손실 컷
              → "1교시 - 2026-06-14.mp4" 생성 → 업로드 큐에 적재 → 업로드(미등록)        ← D2,D4,D6
[7교시 종료]  당일 6개 한도 초과분은 큐에 남음 → quota 리셋(다음날) 후 자동 업로드        ← D7
[하교]        폰으로 라이브 중지(또는 PC 종료 시 자동 complete)
```

---

## 4. 아키텍처

### 4.0 신규 모듈 배치 (기존 §4 런타임 그림에 추가)

```
SilentStream.exe
│
├── StreamOrchestrator ★ (기존, 변경 없음) ── 라이브/세션 녹화
│
├── [신규] PeriodScheduler        ← 벽시계 타이머, 교시 시작/종료 이벤트 발행
├── [신규] PeriodScheduleStore    ← 요일별 기본 + 날짜별 override 저장/해석
├── [신규] VodSegmentService      ← 세션 mp4에서 교시 구간 무손실 컷
├── [신규] YouTubeUploadService   ← videos.insert (재개 가능 업로드)
├── [신규] UploadQueue (영속)     ← quota 인식 업로드 큐 + 재시도/다음날 처리
└── [신규] RemoteControlServer    ← Kestrel 임베디드 웹서버 + 모바일 웹 UI
        ├─ REST API (시간표 CRUD / 라이브 제어 / 상태)
        └─ WebSocket (실시간 상태 push)
```

데이터 흐름:
```
PeriodScheduler.PeriodEnded
      → VodSegmentService.ExtractPeriodAsync(세션mp4, [start,end]) → 교시 mp4
      → UploadQueue.Enqueue(교시 mp4, 제목)
      → (워커) YouTubeUploadService.UploadAsync → videoId / quota 초과 시 보류
```

### 4.1 교시 분할 전략 — 무손실 사후 컷 (채택)

기존 인코더는 **FFmpeg `tee`** 로 RTMP와 **세션 단위 단일 mp4** 를 동시에 출력한다(부팅~종료 1파일).
교시별 파일을 얻기 위한 후보들을 비교했다.

| 방식 | 라이브 영향 | 추가 인코딩 부하 | 교시 경계 정확도 | 채택 |
|---|---|---|---|---|
| tee 파일 레그를 교시마다 재시작 | **높음**(tee 출력셋은 런타임 변경 곤란, 라이브 끊김 위험) | 없음 | 정확 | ✗ |
| 별도 녹화 인코더를 교시마다 start/stop | 낮음 | **있음**(2번째 인코딩) | 정확 | △ 폴백 |
| **세션 녹화본에서 무손실 사후 컷(`-c copy`)** | **없음**(읽기만) | **없음** | GOP 단위 오차(≤2초) | **✓ 채택** |
| FFmpeg segment 먹서(고정 길이) | 낮음 | 없음 | 교시 경계와 불일치 | ✗ |

**채택안 동작:**
```
교시 종료 시각 도달 →
ffmpeg -ss {교시시작} -to {교시종료} -i "SilentStream_REC_...mp4" \
       -c copy -movflags +faststart "N교시_2026-06-14.mp4"
```
- `-c copy` 이므로 **재인코딩 없음 → CPU 거의 0, 수 초 내 완료**, 화질=원본.
- **키프레임 경계 한계**: `-c copy` 는 가장 가까운 키프레임에서 시작하므로 시작점이 최대 GOP 길이만큼 어긋날 수 있다.
  - 완화 1: 인코더 GOP를 1~2초로 설정(라이브 표준 범위) → 오차 ≤2초.
  - 완화 2(정밀 필요 시): 교시 시작 시각에 강제 키프레임(`-force_key_frames`) — E2 옵션으로 보류.
  - 수업 녹화 특성상 시작 ±2초는 허용 가능으로 판단(D2).
- **쉬는 시간**: 세션 파일에는 남지만, VOD는 교시 구간만 추출하므로 업로드되지 않는다(로컬 7일 보존 정책 §3.6에는 포함).
- **엣지 케이스**: 교시 도중 PC 재부팅 등으로 세션 파일이 끊기면 해당 교시는 **있는 구간까지만 best-effort 추출 + 로그 경고**(§11 R3).

### 4.2 VOD 업로드 + quota 인식 영속 큐

- **업로드 API**: YouTube Data API v3 `videos.insert`, **재개 가능(resumable) 업로드** 사용(대용량·네트워크 끊김 내성). 공개범위 `unlisted`(D4), `selfDeclaredMadeForKids=false`.
- **영속 큐**: `%AppData%\SilentStream\upload_queue.json` 에 작업 영속화 → **재부팅·다음날에도 유지**(D7 핵심).
- **quota 처리**:
  - `videos.insert` = **1,600 units/건**, 기본 일일 한도 **10,000** → **하루 약 6건**.
  - `403 quotaExceeded`/`dailyLimitExceeded` 응답 시 해당 작업을 **pending 유지**하고 워커를 **일시중단**.
  - YouTube quota는 **태평양 시간(PT) 자정**에 리셋 → 한국시간 **약 16~17시**. 워커는 리셋 이후 자동 재개하여 잔여분 업로드.
  - 일일 사용량 근사 카운터(1건=1600)로 증설 승인 전엔 6건/일 상한을 넘지 않도록 가드.
- **quota 증설(D7)**: Google Cloud Console → YouTube Data API → *Audit and Quota Extension* 양식 신청(무료, 심사 수일~수주). 승인 전에도 큐 덕분에 누락 없이 다음날 업로드됨.

### 4.3 업로드 타이밍과 업링크 보호 (확정: 즉시+대역제한 · D10)

라이브 송출과 VOD 업로드가 **동시에 업로드 대역(업링크)** 을 쓰면 라이브가 끊길 수 있다(특히 학교 회선).

| 옵션 | 설명 | 라이브 보호 | 공개 신속성 |
|---|---|---|---|
| **즉시 + 대역 제한(권장 기본값)** | 교시 종료 직후 업로드하되 업링크를 throttle | 보통 | 빠름 |
| 방과 후/야간 일괄 | 모든 교시 VOD를 라이브 종료 후 업로드 | 높음 | 느림(당일 저녁 이후) |

> **확정(D10)**: `uploadTiming = immediate-throttled`(기본). 학교 업링크가 좁으면 `after-hours` 로 전환.

### 4.4 스마트폰 원격 제어 서버

- **호스팅**: WPF 앱 프로세스 내에 **ASP.NET Core(Kestrel) 임베디드**(`<FrameworkReference Include="Microsoft.AspNetCore.App"/>`). 별도 서비스/프로세스 없음.
- **바인딩 모드(설정)**:
  | 모드 | 바인딩 | 용도 |
  |---|---|---|
  | `off`(기본) | 미기동 | 원격 미사용 |
  | `lan` | `0.0.0.0:8787` + Windows 방화벽 인바운드 규칙 | 같은 와이파이 |
  | `tailscale` | Tailscale 인터페이스 IP | 집 밖에서 |
- **인증(확정 · D11)**: 최초 1회 **PIN 페어링** — 제어창(또는 상태)에 표시된 PIN을 폰이 입력 → **기기 토큰 발급/저장**, 이후 토큰 인증. 토큰은 해시로 config에 저장.
  - LAN은 신뢰망 전제로 HTTP. **집 밖 사용은 Tailscale 권장**(트래픽 암호화). 공인 IP 직접 노출/포트포워딩은 비권장.
  - **확정(D11)**: PIN 페어링 + 기기 토큰. (추가 보안 필요 시 상향 가능)
- **모바일 웹 UI**: 단일 반응형 페이지(앱 내 정적 자원으로 번들). 교시 표 편집, 라이브 시작/중지, 상태·업로드 진행 표시. 아이폰/안드로이드 브라우저 공용.

---

## 5. 신규 계약(Contracts) — `Core/Contracts`

> 기존 인터페이스는 변경하지 않는다(인터페이스 고정 규칙). 아래는 **추가만** 한다.

```csharp
// ---- 교시 모델 ----
public sealed record SchoolPeriod(int Number, TimeOnly Start, TimeOnly End);
public sealed record DaySchedule(IReadOnlyList<SchoolPeriod> Periods);
public sealed record PeriodBoundary(DateOnly Date, int PeriodNumber,
                                    DateTime StartLocal, DateTime EndLocal);

// ---- 시간표 저장/해석 (D5: 요일 기본 + 날짜 override) ----
public interface IPeriodScheduleStore
{
    DaySchedule GetWeekdayDefault(DayOfWeek day);
    void SetWeekdayDefault(DayOfWeek day, DaySchedule schedule);
    DaySchedule? GetOverride(DateOnly date);          // null = 기본값 사용
    void SetOverride(DateOnly date, DaySchedule schedule);
    void ClearOverride(DateOnly date);
    DaySchedule ResolveForDate(DateOnly date);         // override ?? weekday default
}

// ---- 교시 경계 스케줄러 ----
public interface IPeriodScheduler
{
    event EventHandler<PeriodBoundary>? PeriodStarted;
    event EventHandler<PeriodBoundary>? PeriodEnded;
    void Start(CancellationToken ct);                  // 벽시계 루프 시작
}

// ---- 교시 무손실 컷 ----
public interface IVodSegmentService
{
    // 현재 세션 mp4에서 [period.Start, period.End] 구간을 무손실 추출 → 생성 파일 경로
    Task<string?> ExtractPeriodAsync(PeriodBoundary period, CancellationToken ct);
}

// ---- VOD 업로드 ----
public interface IYouTubeUploadService
{
    // 성공 시 videoId 반환. quota 초과는 QuotaExceededException 으로 구분.
    Task<string> UploadAsync(string filePath, string title, string privacy, CancellationToken ct);
}

// ---- 영속 업로드 큐 (quota 인식 워커 포함) ----
public interface IUploadQueue
{
    void Enqueue(UploadJob job);
    IReadOnlyList<UploadJob> Snapshot();
    void Start(CancellationToken ct);                  // 백그라운드 워커 기동
}
public sealed record UploadJob(string Id, string FilePath, string Title,
                               DateOnly Date, int PeriodNumber,
                               string Status, int Attempts, string? VideoId);

// ---- 원격 제어 서버 ----
public enum RemoteBindMode { Off, Lan, Tailscale }
public interface IRemoteControlServer
{
    Task StartAsync(RemoteBindMode mode, int port, CancellationToken ct);
    Task StopAsync();
}
```

**기존 `TitleTemplater` 확장(시그니처 비파괴 — 오버로드 추가):**
```csharp
// 기존: Expand(template, DateTime) — 날짜 토큰만
// 추가: 교시 토큰 {교시}, {교시:00} 치환 후 날짜 토큰 적용
public static string Expand(string template, DateTime timestamp, int periodNumber);
// 예) "{교시}교시 - {yyyy-MM-dd}" + (period=1, 2026-06-14) → "1교시 - 2026-06-14"   (D6)
```

---

## 6. 데이터 스키마 확장 (`config.json`)

> 기존 키는 유지하고 **추가만** 한다. 마이그레이션: 누락 시 기본값 채움(`version` 올림).

> v5(D12): 최상위 `deviceName`(호실명) 추가. 라이브·VOD 제목의 `{호실}` 토큰을 치환한다. 신규 설치에서만
> 호스트명으로 시드되고, 기존 파일은 빈 값으로 남는다(호스트명 미각인). 기본 `titleTemplate`에 `[{호실}] ` 접두사 포함.

```json
{
  "version": 5,
  "deviceName": "201호",
  "youtube": {
    "titleTemplate": "[{호실}] 라이브 - {yyyy-MM-dd HH:mm}",
    "privacy": "unlisted"
  },
  "periods": {
    "weekdayDefaults": {
      "Mon": [ { "no": 1, "start": "09:00:00", "end": "09:50:00" },
               { "no": 2, "start": "10:00:00", "end": "10:50:00" } ],
      "Tue": [ ... ], "Wed": [ ... ], "Thu": [ ... ], "Fri": [ ... ]
    },
    "overrides": {
      "2026-06-15": [ { "no": 1, "start": "09:00:00", "end": "09:30:00" } ]
    },
    "titleTemplate": "[{호실}] {교시}교시 - {yyyy-MM-dd}",
    "vodPrivacy": "unlisted",
    "uploadTiming": "immediate-throttled"
  },
  "remote": {
    "mode": "off",
    "port": 8787,
    "deviceTokens": [ "<sha256 hash>" ]
  }
}
```
- 업로드 큐는 별도 파일 `%AppData%\SilentStream\upload_queue.json` (영속, 민감정보 아님).
- `config.json`/토큰은 기존 규약대로 **커밋 금지**.

---

## 7. 원격 API (초안)

| 메서드 | 경로 | 설명 |
|---|---|---|
| POST | `/api/pair` | `{pin}` → 기기 토큰 발급 |
| GET | `/api/status` | 상태·성능·녹화·오늘 시간표·업로드 큐 요약 |
| GET/PUT | `/api/schedule` | 요일별 기본 시간표 조회/저장 |
| GET/PUT/DELETE | `/api/schedule/today` | 오늘 override 조회/설정/해제 |
| POST | `/api/live/start` · `/api/live/stop` | 라이브 시작/중지(=`IStreamOrchestrator`) |
| WS | `/ws/status` | 실시간 상태 push(상태배지·업로드 진행) |

- 모든 `/api/*`(페어링 제외)는 **기기 토큰 필수**. 토큰 없거나 무효 → 401.

---

## 8. 개발 단계 (확장 Phase E1~E5)

> 각 Phase는 **산출물 / 완료 기준(AC) / 검증**을 가진다. E1→E2→E3는 폰 UI 없이 config 직접 편집으로 테스트 가능하므로
> **핵심 파이프라인을 먼저** 완성하고, E4(폰 UI)에서 입력 수단을 얹는다.
> (Windows 전용 실행 검증 항목은 로컬에서 수행; 클라우드 Linux 세션은 코드·정적 검증까지 — CLAUDE.md 규약.)

### Phase E1 — 교시 시간표 모델·스토어·스케줄러 (예상 0.5주)
- **산출물**: `SchoolPeriod`/`DaySchedule`, `IPeriodScheduleStore`(요일 기본 + 날짜 override 해석), `IPeriodScheduler`(벽시계 이벤트), config `periods` 스키마 + 마이그레이션, `TitleTemplater.{교시}` 오버로드.
- **AC**:
  1. `ResolveForDate` 가 override 우선·없으면 요일 기본 반환(단위 테스트).
  2. 스케줄러가 교시 시작/종료 시각에 `PeriodStarted/PeriodEnded` 정확히 1회 발행(시간 가속 모킹 테스트).
  3. `TitleTemplater.Expand("{교시}교시 - {yyyy-MM-dd}", 2026-06-14, 1)` == `"1교시 - 2026-06-14"`.
- **검증**: 단위 테스트(스토어 라운드트립, 경계 발행, 제목 토큰).

### Phase E2 — 교시 무손실 컷 (예상 0.5주)
- **산출물**: `IVodSegmentService` 구현(세션 mp4 read-only → `-ss/-to -c copy` 추출), 진행 중 세션 파일 안전 추출 처리, GOP 경계 한계 문서화.
- **AC**:
  1. 임의 mp4에서 [start,end] 구간이 무손실(재인코딩 없이) 추출되어 재생 가능.
  2. 진행 중(아직 기록 중)인 세션 파일에서 이미 기록된 구간 추출 성공.
  3. 추출 실패/구간 부족 시 null 반환 + 로그 경고(앱 비충돌).
- **검증**: 로컬 Windows에서 실제 세션 녹화본으로 교시 컷 → 재생 확인.

### Phase E3 — VOD 업로드 + quota 인식 영속 큐 (예상 1주)
- **산출물**: `IYouTubeUploadService`(`videos.insert` 재개 가능 업로드, unlisted), `IUploadQueue`(영속 JSON, 백그라운드 워커, quota 초과 시 보류·PT 자정 후 재개), `QuotaExceededException`, 업링크 throttle(D10: immediate-throttled).
- **AC**:
  1. 교시 파일이 실제 채널에 **미등록**으로 업로드되고 제목이 `N교시 - 날짜`(D4,D6).
  2. quota 초과(403) 시 작업이 큐에 **보존**되고 워커가 멈춤 → 재시작/리셋 후 자동 재개(모킹 + 영속성 테스트).
  3. 앱 재부팅 후에도 큐 잔여 작업이 유지되어 다음날 업로드됨(D7).
- **검증**: 실제 Google 계정 1건 업로드 확인 + quota 초과 경로는 모킹/주입으로 검증.

### Phase E4 — 스마트폰 원격 제어 (예상 1.5주)
- **산출물**: `IRemoteControlServer`(Kestrel 임베디드, bind 모드 off/lan/tailscale, 방화벽 규칙 안내), REST API(§7) + `/ws/status`, PIN 페어링·기기 토큰 인증(D11), 모바일 반응형 웹 UI(시간표 편집/라이브 제어/상태·업로드 진행).
- **AC**:
  1. 같은 와이파이의 폰 브라우저로 접속·페어링 후 **시간표 입력→config 반영→스케줄러 적용**.
  2. 폰에서 라이브 시작/중지가 `IStreamOrchestrator` 에 반영, 상태가 실시간 갱신(WS).
  3. 미인증 요청 401, 잘못된 PIN 거부.
- **검증**: 로컬 Windows + 실제 폰(또는 동일망 기기 브라우저)로 수동 E2E.

### Phase E5 — 통합·복원력·문서 (예상 0.5주)
- **산출물**: 종일 시나리오 통합(라이브 지속 + 교시 컷·업로드 동시), Tailscale 연결 가이드, 7교시+ 큐 이월 시나리오, `docs/USER_GUIDE.md` 보강(교시 설정·폰 사용·quota 안내), 설치 시 원격 포트 방화벽 옵션.
- **AC**:
  1. 모의 종일 운영에서 라이브 무중단 + 교시별 VOD 생성·업로드(또는 큐 이월) 일관 동작.
  2. 6개 초과(7교시+) 시 초과분이 다음날 자동 업로드(시간 가속/주입 검증).
  3. 사용자 문서로 비개발자가 시간표·폰 연결을 설정 가능.

**확장 총 예상: 약 4주** (E1·E2 선행 후 E3/E4 일부 병렬 가능)

---

## 9. 테스트 계획(요약)
- **단위**: 시간표 해석(override 우선), 교시 경계 발행, 제목 토큰, quota 초과 분기, 큐 영속 라운드트립.
- **통합**: 세션 mp4 → 교시 무손실 컷 → 업로드(모킹) → 큐 상태 전이. 원격 API 인증/시간표 CRUD.
- **E2E(로컬 Windows)**: 종일 운영(라이브 + 교시 컷·업로드 동시), 폰 시간표 입력→적용, 7교시+ 다음날 이월, 재부팅 후 큐 유지.

## 10. 의존 라이브러리 추가
| 라이브러리 | 용도 |
|---|---|
| `Microsoft.AspNetCore.App`(FrameworkReference) | 임베디드 원격 제어 웹서버(Kestrel) |
| Google.Apis.YouTube.v3(기존) | `videos.insert` 재개 가능 업로드(추가 패키지 불필요) |
| FFmpeg(기존, 번들) | 교시 무손실 컷(`-c copy`) |

## 11. 리스크 및 대응
| # | 리스크 | 대응 |
|---|---|---|
| R1 | quota 6건/일 한도로 7교시+ 당일 누락 | 영속 큐로 다음날 자동 업로드(D7) + quota 증설 신청 |
| R2 | VOD 업로드가 라이브 업링크와 경쟁 → 라이브 끊김 | 업로드 throttle(U1 기본값) 또는 방과 후 일괄 옵션 |
| R3 | 무손실 컷 키프레임 경계 오차 / 재부팅로 세션 끊김 | GOP 단축, best-effort 추출 + 로그, (옵션)강제 키프레임 |
| R4 | LAN HTTP 평문 노출 | 신뢰망 전제 + 집 밖은 Tailscale 권장, 토큰 인증, 기본 off |
| R5 | 라이브/녹화 파이프라인 회귀 | 신규 모듈은 세션 파일 **읽기만** — 기존 tee/오케스트레이터 불변 |
| R6 | YouTube 자동화 업로드 정책/제출 한도 | unlisted 한정, 본인 채널, 합리적 빈도, 약관 준수(§EULA 본인 PC 한정) |

## 12. 확정 완료 (2026-06-18)
- **U1(E3) 업로드 타이밍** → `immediate-throttled`(즉시+대역제한)로 확정. (D10)
- **U2(E4) 원격 인증** → PIN 페어링 + 기기 토큰으로 확정. (D11)

> 둘 다 config로 사후 조정 가능 — 업링크가 좁으면 `after-hours`, 보안 강화가 필요하면 인증 상향.

## 13. 산출물 체크리스트 (Orchestrator용)
- [x] E1 교시 모델·스토어·스케줄러·제목 토큰 — 구현 + 단위 테스트 통과(스토어 라운드트립/override 우선, 경계 1회 발행(가속 클럭), {교시} 토큰). (2026-06-18)
- [x] E2 교시 무손실 컷 — `VodSegmentService`(`-ss/-t -c copy`), `IRecordingSessionInfo`로 세션 mp4+시작시각 노출, best-effort/null 처리 + 단위 테스트. **실제 세션 녹화본 재생 검증은 로컬 Windows 수동 필요.** (2026-06-18)
- [x] E3 VOD 업로드 + quota 영속 큐 — `YouTubeUploadService`(videos.insert resumable, unlisted, throttle), `UploadQueue`(영속 JSON·워커·PT 자정 리셋 후 재개·일일 캡·재부팅 보존) + 단위 테스트. **실 채널 1건 업로드 검증은 로컬 Windows 수동 필요.** (2026-06-18)
- [x] E4 폰 원격 제어 서버·모바일 UI·인증 — `RemoteControlServer`(Kestrel 임베디드, off/lan/tailscale, §7 REST + `/ws/status`, PIN 페어링·기기 토큰), 반응형 모바일 웹 UI 임베드, `RemoteAuth` 단위 테스트, 제어창 PIN 표시. **실기기 수동 E2E는 로컬 Windows 필요.** (2026-06-18)
- [x] E5 통합·복원력·문서·인스톨러 — `VodCoordinator`(PeriodEnded→컷→큐) + App 와이어링(StartupSequence), USER_GUIDE §6~8 보강(교시/폰/quota/Tailscale), 인스톨러 원격 포트 방화벽 옵션. **종일 시나리오·다음날 이월 E2E는 로컬 Windows 수동 필요.** (2026-06-18)
- [x] `SilentStream_개발계획서.md` §10 및 `CLAUDE.md` 에 확장 포인터 추가 (2026-06-18)
