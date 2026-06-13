# SilentStream 개발 계획서 (Claude Code 실행용)

> 본 문서는 **Claude Code AI가 직접 실행**하는 것을 전제로 작성된 구현 계획서입니다.
> 전체 작업은 **오케스트레이터(Orchestrator) 패턴**으로 진행됩니다. 상위 오케스트레이터가
> 단계(Phase)별로 전문 서브 에이전트에게 작업을 위임하고, 각 단계의 산출물과 완료 기준
> (Acceptance Criteria)을 검증한 뒤 다음 단계로 넘어갑니다.

---

## 0. 문서 사용 규약 (Claude Code 필독)

| 항목 | 규칙 |
|---|---|
| **개발 브랜치** | `claude/relaxed-davinci-qmmjwl` 에서만 개발/커밋/푸시 |
| **커밋 단위** | 각 Phase의 하위 작업(Task) 완료마다 의미 단위로 커밋 |
| **PR 생성** | 사용자가 명시적으로 요청하기 전까지 **PR 생성 금지** |
| **검증 우선** | 각 Task는 "완료 기준(AC)"을 충족하고, 가능한 경우 자동 테스트가 통과해야 "완료"로 표시 |
| **불확실성 처리** | 명세가 모호하면 추측하지 말고 `AskUserQuestion`으로 질문 후 진행 |
| **언어** | 코드 주석/식별자는 영어, 사용자 대면 UI 문자열은 한국어 |

---

## 1. 프로젝트 개요

| 항목 | 내용 |
|---|---|
| **프로그램명** | SilentStream |
| **목적** | OBS Studio 라이브 스트리밍 기능 대체 — 완전 자동·비가시적 YouTube 송출 **+ 로컬 백업 녹화** |
| **플랫폼** | Windows 10/11 (x64) |
| **기술 스택** | C# / .NET 8.0 (WPF) + FFmpeg (번들) |
| **UI 언어** | 한국어 |
| **배포 형태** | Inno Setup `.exe` 인스톨러 + Squirrel 자동 업데이트 |

### 1.1 법적·윤리적 고지 (구현 시 명시)
이 프로그램은 화면/오디오를 **비가시적으로 캡처·송출·녹화**합니다. 따라서:
- 설치 마법사 및 최초 실행 시 **사용자 동의 화면**을 1회 표시한다(녹화/송출/저장 범위 고지).
- 본 도구는 **사용자 본인 소유 PC의 자기 기록·방송 목적**을 전제로 한다. 타인의 PC·화면·음성을 동의 없이 캡처하는 용도로 사용할 수 없음을 약관(EULA)에 명시한다.
- 송출 대상 YouTube 채널은 **사용자 본인 계정**(OAuth 로그인)으로 한정한다.

---

## 2. Claude Code 오케스트레이션 실행 모델

### 2.1 오케스트레이터 ↔ 서브 에이전트 구조

```
┌─────────────────────────────────────────────────────────────┐
│                  Orchestrator (상위 조율자)                    │
│  - Phase 진행 관리 / 완료 기준 검증 / 커밋·푸시 / 의존성 해소     │
└───────────────┬─────────────────────────────────────────────┘
                │ 위임(Task) + 산출물 계약(Contract) 전달
   ┌────────────┼────────────┬────────────┬────────────┐
   ▼            ▼            ▼            ▼            ▼
[Infra Agent] [Media Agent] [YouTube Agent] [UI Agent] [Packaging Agent]
 Config/Log    Capture/      OAuth/Live      StatusBox/  Installer/
 프로젝트뼈대   Audio/Encode  API/RTMP        ControlUI/  Update/
               /Recording                    Hotkey      AutoStart
```

- **Orchestrator**: 본 계획서를 단일 진실 공급원(SoT)으로 삼아 Phase를 순차/병렬 디스패치한다. 각 서브 에이전트에게 **입력 계약**(필요한 인터페이스/설정)과 **출력 계약**(제공해야 할 인터페이스/이벤트)을 명시한다.
- **서브 에이전트**: 자기 영역만 구현하고, 다른 영역은 **인터페이스(추상화)** 에만 의존한다. 구현 충돌을 막기 위해 모든 모듈 간 통신은 `Core/Contracts` 의 인터페이스와 이벤트로만 이뤄진다.

### 2.2 모듈 간 계약(Contracts) — 병렬 개발의 핵심
서브 에이전트들이 동시에 작업해도 충돌하지 않도록, Phase 1에서 **인터페이스를 먼저 고정**한다.

```csharp
// Core/Contracts/IStreamOrchestrator.cs
public interface IStreamOrchestrator {
    StreamState State { get; }
    event EventHandler<StreamState> StateChanged;
    event EventHandler<MetricsSnapshot> MetricsUpdated;   // bps, fps, cpu, gpu
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}

public interface IScreenCaptureSource { /* 프레임 제공 */ }
public interface IAudioMixer {            /* 시스템+마이크 PCM 제공, 볼륨 */ }
public interface IEncoderPipeline {       /* tee: RTMP + 파일 동시 출력 */ }
public interface IYouTubeLiveService {    /* OAuth, 브로드캐스트 생성/종료 */ }
public interface IRecordingManager {      /* 세션 파일, 용량/보존 정책 */ }
public interface IConfigStore { AppConfig Load(); void Save(AppConfig c); }
public interface ILogService { /* NLog 래퍼 */ }
```

> **규칙**: 인터페이스 시그니처 변경이 필요하면 Orchestrator가 승인하고, 의존하는 모든 에이전트에 전파한다.

---

## 3. 핵심 기능 요구사항

### 3.1 완전 백그라운드 자동 실행
- PC 전원 ON → **30초 대기**(네트워크 안정화) → YouTube 라이브 + 로컬 녹화 자동 시작.
- 어떤 일반 창도 표시하지 않음(작업표시줄/Alt+Tab 비노출).
- 자동 시작 방식은 **설치 시 선택**:
  - **A) Windows 시작 프로그램**(레지스트리 `Run`, 로그인 후 실행)
  - **B) Windows 작업 스케줄러**(`ONLOGON`/`ONSTART`, 로그인 전 실행 가능, 최고 권한)
- 단일 인스턴스 보장(Mutex). 중복 실행 시 기존 인스턴스에 신호만 전달.

### 3.2 상태 표시기 (9px 박스)
| 항목 | 값 |
|---|---|
| 위치 | 주 모니터 좌측 상단 (0, 0) |
| 크기 | 9 × 9 px |
| 색상 | 🟢 정상 송출 / 🟡 연결 중 / 🔴 오류·중단 |
| 동작 | 정적(애니메이션 없음), `TopMost`, 마우스 이벤트 통과 |

- 구현: WPF 무테두리 투명 창 + `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW`(Alt+Tab/작업표시줄 비노출, 클릭 통과).
- **녹화 상태 보조 표시(선택)**: 박스 색상은 송출 상태를 따르되, 녹화 실패 시 박스 우하단 1px를 보라색으로 표시(육안 식별은 어렵지만 진단 로그와 함께 사용).

### 3.3 화면 캡처
- 범위: **주 모니터 전체**, 마우스 커서 포함, 오버레이/워터마크 없음.
- 구현: **DXGI Desktop Duplication API**(SharpDX 또는 Windows.Graphics.Capture). 해상도/주사율은 주 모니터 설정을 따른다.
- 캡처 실패(전체화면 독점·해상도 변경) 시 자동 재초기화.

### 3.4 오디오
- 소스: **시스템 오디오(WASAPI Loopback) + 마이크** 동시 혼합(NAudio).
- UI에서 두 소스 **독립 볼륨 슬라이더**, **마이크 장치 선택 드롭다운**(다중 장치 중 택1).
- 장치 분리/변경 감지 시 재초기화(마이크 미연결이어도 시스템 오디오만으로 송출 지속).

### 3.5 영상 인코딩
- **GPU 자동 감지 순서**: ① NVIDIA NVENC → ② AMD AMF → ③ Intel Quick Sync → ④ CPU x264(폴백).
- 감지 방법: FFmpeg `-encoders` 조회 + 실제 1프레임 테스트 인코딩으로 가용성 확인.
- YouTube 권장 비트레이트 자동 적용:

  | 해상도/FPS | 비트레이트 |
  |---|---|
  | 1080p 60fps | 9,000 kbps |
  | 1080p 30fps | 6,000 kbps |
  | 720p 30fps | 3,500 kbps |

- **자원 사용 제한**: UI 선택 25% / 50% / 75% / 제한 없음.
  - 구현: x264 `-threads` 제한 + 프로세스 우선순위(`BelowNormal`) + 인코더 프리셋 조정으로 근사. (정확한 % 제한이 아닌 상한 가이드임을 UI 툴팁에 명시.)

### 3.6 로컬 백업 녹화 (신규)
> 결정 사항 반영: **송출 스트림 재사용(tee) / 세션 단위 1파일 / 사용자 지정 폴더 + 용량 한도 / 송출과 독립 동작 / 7일 자동 삭제**

| 항목 | 사양 |
|---|---|
| 방식 | **FFmpeg `tee` 먹서로 인코딩된 단일 스트림을 RTMP와 파일에 동시 출력** (추가 인코딩 부하 없음, 화질=송출과 동일) |
| 파일 분할 | **세션 단위**: 부팅(자동 시작)~PC 종료/중지까지 1개 파일 |
| 파일명 | `SilentStream_REC_2026-06-12_0930.mp4` (세션 시작 시각 기준) |
| 컨테이너 | `.mp4` (장시간 안정성을 위해 `-movflags +frag_keyframe+empty_moov` 적용 → 비정상 종료에도 재생 가능) |
| 저장 위치 | **사용자 지정 폴더**(설치/설정에서 지정, 기본값 `%USERPROFILE%\Videos\SilentStream`). 별도 드라이브 지정 가능 |
| 용량 한도 | UI에서 설정(기본 100GB). **초과 시 가장 오래된 파일부터 삭제** |
| 보존 정책 | **7일(168시간) 경과 파일 자동 삭제** (파일 생성 시각 기준). 시작 시 1회 + 1시간 주기 정리 |
| 독립성 | **송출 실패와 무관하게 녹화 지속** — `tee` 의 RTMP 출력에 `onfail=ignore` 적용. RTMP가 끊겨도 파일 출력은 계속됨 |
| 디스크 부족 | 남은 공간 < 임계치(기본 5GB)면 오래된 파일 즉시 정리 → 그래도 부족하면 녹화만 일시중단(송출은 유지)하고 🔴/로그 경고 |

**FFmpeg tee 출력 예시(개념):**
```
ffmpeg <입력: 캡처 영상 + 믹스 오디오> \
  -c:v h264_nvenc -b:v 6000k ... -c:a aac -b:a 160k \
  -f tee -map 0:v -map 1:a \
  "[f=flv:onfail=ignore]rtmp://a.rtmp.youtube.com/live2/<streamkey>| \
   [f=mp4:movflags=+frag_keyframe+empty_moov]C:/.../SilentStream_REC_....mp4"
```
> RTMP 측 `onfail=ignore` 덕분에 네트워크 끊김 시에도 파일 녹화는 유지된다. RTMP 재연결은 별도 감독 로직이 인코더 파이프라인을 재구성하여 처리한다(아래 4.4 참고).

### 3.7 YouTube Live 연동
| 항목 | 내용 |
|---|---|
| 인증 | OAuth2(설치된 앱 흐름). 최초 1회 브라우저 로그인 → 이후 refresh token 자동 갱신 |
| 자격증명 | 앱의 OAuth Client ID/Secret 내장(데스크톱 앱 흐름, secret은 기밀이 아님). 사용자는 본인 Google 계정만 로그인 |
| 공개 범위 | **Unlisted(일부 공개)** |
| 제목 | 자동 생성 `라이브 - 2026-06-12 09:30` |
| 재시작 처리 | PC 켤 때마다 **새 브로드캐스트 생성**(liveBroadcasts.insert + liveStreams.bind) |
| 종료 처리 | PC 종료/중지 시 브로드캐스트를 `complete`로 전이(가능 시), 실패해도 다음 부팅에 새로 생성 |
| 실패 시 | **무한 자동 재시도**(지수 백오프, 상한 캡) |
| RTMP | `rtmp://a.rtmp.youtube.com/live2/<streamKey>` |

### 3.8 제어 UI (`Ctrl+Shift+F12`, 변경 가능)
- 다크 테마, 미니멀.
- 구성: 상태 배지(LIVE/연결 중/오류) · 시작/중지 버튼 · 성능(업로드 bps, FPS, CPU/GPU) · **오디오(시스템·마이크 슬라이더, 마이크 드롭다운)** · 자원 제한(25/50/75/없음) · **녹화 상태(현재 파일, 누적 용량, 남은 디스크)** · 로그 뷰어(최근 1000줄) · 설정 패널(단축키/오디오 장치/녹화 폴더·용량 한도/자동 시작 방식).

### 3.9 로그 관리
- 위치 `%AppData%\SilentStream\logs\`, 파일 `SilentStream_2026-06-12.log`, **180일 경과 자동 삭제**(NLog 아카이브 정책).

### 3.10 설정 파일
- 위치 `%AppData%\SilentStream\config.json`.
- 저장 항목: OAuth 토큰(**DPAPI 암호화**), 인코딩 설정, 오디오 설정·마이크 선택, 단축키, 자원 제한, 자동 시작 방식, **녹화 폴더/용량 한도/보존일수**.

### 3.11 설치·배포·업데이트
- Inno Setup `.exe`. 설치 중 자동 시작 방식(시작 프로그램 vs 작업 스케줄러) + **녹화 폴더** 선택.
- FFmpeg 바이너리 번들 포함. 동의 화면(3.1.1) 포함.
- Squirrel.Windows 자동 업데이트(새 버전 감지 → 알림 → 적용).

---

## 4. 시스템 아키텍처 (앱 런타임 오케스트레이션)

```
SilentStream.exe  (단일 프로세스, 백그라운드 호스트)
│
├── StreamOrchestrator ★ (런타임 중앙 조율자 / 상태머신)
│   ├─ 30초 지연 → 시작 시퀀스 구동
│   ├─ 컴포넌트 수명주기·재시도·상태(StreamState) 관리
│   └─ MetricsUpdated / StateChanged 이벤트 발행 → StatusBox·UI 구독
│
├── Core
│   ├── ScreenCapture        ← DXGI Desktop Duplication
│   ├── AudioMixer           ← WASAPI Loopback + 마이크 (NAudio)
│   ├── EncoderPipeline      ← FFmpeg tee (RTMP + 녹화 파일 동시)
│   ├── RecordingManager     ← 세션 파일/용량 한도/7일 보존
│   └── YouTubeLiveService   ← YouTube Data API v3 (OAuth2)
│
├── StatusIndicator          ← 9px TopMost 투명 창 (클릭 통과)
├── ControlUI                ← Ctrl+Shift+F12 (WPF, StatusOrchestrator 구독)
│   ├── StatusPanel / PerformancePanel / AudioPanel / RecordingPanel / LogViewer / SettingsPanel
├── HotkeyManager            ← 전역 단축키
├── AutoStartManager         ← 시작 프로그램 / 작업 스케줄러
├── UpdateManager            ← Squirrel 자동 업데이트
└── ConfigManager            ← config.json (+DPAPI)
```

### 4.1 StreamState 상태머신
```
Idle → Warmup(30s) → ConnectingYouTube(🟡) → Live(🟢)
                                  │              │
                                  └──fail──► Retrying(🔴, 지수 백오프) ──► ConnectingYouTube
Live ──(stop/shutdown)──► Stopping → Idle
* 녹화는 Warmup 직후 인코더 기동과 함께 시작되어 Retrying 동안에도 유지(독립 동작).
```

### 4.2 시작 시퀀스(부팅 후)
1. 단일 인스턴스 확인 → 30초 대기.
2. Config 로드 + 로그 초기화.
3. 인코더 GPU 감지 → 캡처/오디오 초기화.
4. **RecordingManager가 세션 파일 경로 결정 + 보존/용량 정리 1회 수행.**
5. YouTubeLiveService: 토큰 갱신 → 브로드캐스트 생성 → streamKey 획득.
6. EncoderPipeline: tee 출력(RTMP `onfail=ignore` + 녹화 파일) 구성·기동.
7. 상태 🟢, Metrics 폴링 시작.

### 4.3 종료 시퀀스(PC 종료/중지)
- `WM_QUERYENDSESSION`/`SystemEvents.SessionEnding` 후킹 → 인코더에 정상 종료 신호(`q`) → mp4 무손실 마감(frag_keyframe로 비정상 종료에도 안전) → 브로드캐스트 `complete` 전이 시도 → 로그 flush.

### 4.4 재시도/감독 정책
- **YouTube/RTMP 실패**: 지수 백오프(1→2→4→…→최대 60초) 무한 재시도. 필요 시 브로드캐스트 재생성. 재시도 중에도 **녹화 유지**.
- **캡처/오디오 장치 오류**: 재초기화 시도. 마이크 단독 실패 시 시스템 오디오만으로 지속.
- **인코더 프로세스 사망**: 워치독이 감지 → 파이프라인 재구성(새 세션 파일이 아닌, 가능 시 동일 세션 이어쓰기 또는 part2 파일 생성).

---

## 5. 주요 의존 라이브러리

| 라이브러리 | 용도 |
|---|---|
| FFmpeg(번들) | 인코딩 + tee(RTMP/녹화) |
| Google.Apis.YouTube.v3 | YouTube Live API |
| NAudio | WASAPI Loopback + 마이크 믹싱 |
| SharpDX / Windows.Graphics.Capture | DXGI 화면 캡처 |
| System.Text.Json | 설정 직렬화 |
| NLog | 로깅/아카이브 |
| Squirrel.Windows | 자동 업데이트 |
| (전역 단축키) | `RegisterHotKey` 직접 P/Invoke 또는 NHotkey/GlobalHotKey |
| TaskScheduler(Microsoft.Win32.TaskScheduler) | 작업 스케줄러 등록 |

---

## 6. 데이터 스키마 (`config.json`)

```json
{
  "version": 1,
  "youtube": { "refreshTokenEnc": "<DPAPI>", "privacy": "unlisted",
               "titleTemplate": "라이브 - {yyyy-MM-dd HH:mm}" },
  "encoding": { "preferredGpu": "auto", "resolution": "source", "fps": "source",
                "resourceLimit": "none" },
  "audio": { "systemVolume": 1.0, "micVolume": 1.0, "micDeviceId": "<id>" },
  "recording": { "enabled": true, "folder": "C:\\Users\\..\\Videos\\SilentStream",
                 "maxSizeGb": 100, "retentionDays": 7, "minFreeGb": 5 },
  "hotkey": "Ctrl+Shift+F12",
  "autostart": "startup" 
}
```

---

## 7. 개발 단계 (Phase 오케스트레이션)

> 각 Phase는 **산출물 / 완료 기준(AC) / 검증 방법**을 가진다. Orchestrator는 AC 충족 시에만 다음 Phase로 진행한다. Phase 2~4의 일부 모듈은 인터페이스 고정(Phase 1) 후 **병렬 위임** 가능.

### Phase 0 — 합의·스캐폴딩 (0.5주) · *Infra Agent*
- **산출물**: 솔루션 구조, `Core/Contracts` 인터페이스 전체 정의, CI(빌드) 설정, 코딩 규약.
- **AC**: 빈 구현 + 인터페이스로 솔루션이 빌드된다. DI 컨테이너 구성 완료.
- **검증**: `dotnet build` 성공.

### Phase 1 — 설정·로그 인프라 (0.5주) · *Infra Agent*
- ConfigManager(DPAPI 암호화 포함), NLog(180일 아카이브), 단일 인스턴스 Mutex.
- **AC**: config 저장/로드 라운드트립 테스트 통과, 토큰 암복호화 단위 테스트 통과.

### Phase 2 — 미디어 파이프라인 (3주) · *Media Agent* (서브태스크 병렬)
1. ScreenCapture(DXGI, 커서 포함) — **AC**: 30fps로 주 모니터 프레임 획득.
2. AudioMixer(시스템+마이크, 독립 볼륨) — **AC**: 두 소스 믹싱 PCM 출력, 볼륨 반영.
3. EncoderPipeline(GPU 감지 + FFmpeg) — **AC**: NVENC/AMF/QSV/x264 자동 선택 검증.
4. **tee 출력 + RecordingManager** — **AC**: ① RTMP+mp4 동시 출력 ② RTMP 차단 시(방화벽 테스트) mp4 계속 기록 ③ 세션 파일명/용량 한도/7일 정리 동작.
- **검증**: 로컬 RTMP 더미 서버(nginx-rtmp 또는 `ffmpeg -listen`)로 송출 확인 + 녹화 파일 재생 확인.

### Phase 3 — YouTube 연동 (1주) · *YouTube Agent*
- OAuth2 설치형 흐름, 토큰 갱신, liveBroadcast/liveStream 생성·바인드·전이.
- **AC**: 실제 채널에 Unlisted 라이브 생성 → streamKey로 송출 → 종료 시 complete 전이.

### Phase 4 — 표시기·단축키·제어 UI (2주) · *UI Agent* (Phase 2/3와 병렬 가능)
- 9px StatusBox(클릭 통과/TopMost), 전역 단축키, ControlUI(성능·오디오·**녹화 패널**·로그 뷰어·설정).
- **AC**: 상태 색상이 StreamState와 동기화, 단축키 토글, 슬라이더/드롭다운이 실시간 반영, 녹화 패널에 현재 파일·용량·남은 디스크 표시.

### Phase 5 — 자동화·복원력 (1주) · *Infra/Media Agent*
- AutoStartManager(시작 프로그램/작업 스케줄러), 30초 지연 시작, 무한 재시도·워치독, 종료 시퀀스(세션 종료 후킹).
- **AC**: 재부팅 시 자동 송출+녹화 시작, 네트워크 차단/복구 시 자동 재연결(녹화 무중단), 종료 시 mp4 정상 마감.

### Phase 6 — 패키징·업데이트·최종 테스트 (1주) · *Packaging Agent*
- Inno Setup 인스톨러(자동 시작 방식·녹화 폴더 선택, 동의 화면, FFmpeg 번들), Squirrel 업데이트, E2E 시나리오.
- **AC**: 클린 설치 → 재부팅 → 송출/녹화/UI/업데이트 전 기능 동작. 제거 시 자동 시작 항목·잔여 정리.

**총 예상: 약 9주** (Phase 2~4 병렬화로 단축 여지 있음)

---

## 8. 테스트 계획(요약)
- **단위**: Config 라운드트립/암복호화, 보존·용량 정리 로직(파일 만료/한도), GPU 감지 폴백, 비트레이트 매핑.
- **통합**: 로컬 RTMP 서버 송출, tee 독립성(RTMP 차단 시 녹화 지속), 마이크 분리/복구.
- **E2E**: 재부팅 자동 시작 → 송출+녹화 → 네트워크 단절·복구 → 정상 종료 → 7일 경과 파일 정리(시간 가속 모킹).

## 9. 리스크 및 대응
| 리스크 | 대응 |
|---|---|
| 데스크톱 OAuth secret 노출 | 데스크톱 흐름은 secret 비기밀 전제. 토큰은 DPAPI 암호화 저장 |
| 자원 % 제한의 정확도 한계 | 스레드/우선순위/프리셋 근사임을 UI 고지 |
| 장시간 mp4 손상 | `frag_keyframe+empty_moov`로 비정상 종료 내성 확보 |
| 전체화면 독점 시 캡처 실패 | DXGI 재초기화 + WGC 폴백 |
| 비가시성 오남용 | EULA·동의 화면·본인 PC 한정 명시 |

---

## 10. 산출물 체크리스트 (Orchestrator용)
- [x] Phase 0 인터페이스 고정 & 빌드
- [x] Phase 1 Config/Log/단일인스턴스
- [x] Phase 2 캡처/오디오/인코딩/tee+녹화 (캡처·오디오 실행 검증은 로컬 Windows 필요)
- [x] Phase 3 YouTube OAuth/Live (실 계정·채널 검증은 로컬 Windows 필요)
- [x] Phase 4 9px 박스/단축키/제어 UI (WPF 실행 검증은 로컬 Windows 필요)
- [x] Phase 5 자동시작/재시도/종료 처리 (재부팅·세션 종료 E2E는 로컬 Windows 필요)
- [x] Phase 6 인스톨러/업데이트/E2E (인스톨러 빌드·E2E 수행은 로컬 Windows 필요)
- [x] 사용자 문서(설치·사용·문제해결) 작성 (`docs/USER_GUIDE.md`)
