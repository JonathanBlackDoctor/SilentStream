# SilentStream 확장계획서 — 적응형 송출 품질 (Adaptive Quality, AQ)

> 상태: **구현 완료 (2026-07-13)** — A1 `7941d62` / A2 `144c7b7` / A3 `66f0e3e` / A4 `d91506e`, 테스트 287 그린. 구현 중 확정/변경 사항은 §13. 실기기 런타임 검증은 미완(§10 "로컬 Windows 검증 필요" 항목).
> 작성 2026-07-12
> 상위 문서: [`SilentStream_개발계획서.md`](./SilentStream_개발계획서.md), [`SilentStream_확장계획서_교시VOD_폰원격제어.md`](./SilentStream_확장계획서_교시VOD_폰원격제어.md)
> 관련 로드맵: 원격 컨트롤러 개선 로드맵 Phase 0(헬스 모니터)·Phase 1(텔레그램 푸시)·Phase 2(멀티 호실) 위에 얹힌다.

## §0 요약

방송 중 **기기 부하(인코딩 처리량)와 네트워크 혼잡을 감지해 송출 품질을 자동으로 한 단계씩 낮추고**, 여건이 회복되면(기본: 쉬는 시간에) 되돌리며, **폰 원격에서 품질 프리셋을 수동 고정**할 수도 있게 한다.

핵심 설계 결정 3가지:

1. **품질 변경 = 인코더 프로세스 교체(스왑).** ffmpeg CLI는 실행 중 비트레이트/해상도 변경이 불가능하다. 다행히 코드베이스에 이미 "라이브를 유지한 채 인코더만 갈아끼우는" 검증된 메커니즘이 있다(방송만 중지의 `_encoderSwapInProgress` 스왑, 워치독 재시작, 브로드캐스트 재사용). 이를 일반화해 재사용한다. 스트림 공백은 약 2~4초, 녹화는 새 파트 파일로 이어진다.
2. **정책(컨트롤러)과 실행(오케스트레이터)을 분리.** `AdaptiveQualityController`는 신호를 보고 "몇 단계로 가야 한다"만 결정하고, 실제 스왑·상태 진실은 `StreamOrchestrator`가 소유한다. `BuildEncoderOptions`가 이미 모든 인코더 기동(최초/재연결/워치독/스왑)의 단일 깔때기이므로, 여기에 사다리 적용 한 줄만 꽂으면 모든 경로가 일관된다.
3. **트리거 신호는 새로 만들어야 한다.** 현재 지표는 그대로 못 쓴다 — §2.3의 두 함정(누적 평균, fifo 드롭 은폐) 참조. 파서/파이프라인 확장(A2)이 선행된다.

## §1 배경 · 목표 · 비목표

### 배경
- 현장(학교)은 업로드 대역폭이 불안정하고(2차 점검에서 QUIC 차단 등 관측), 무인 운영이 전제다. 지금은 과부하/혼잡 시 품질을 유지한 채 밀어붙이다가 스톨→재시작으로만 대응한다.
- 품질 파라미터(`Encoding.Resolution/Fps/VideoBitrateKbps`)는 시작 시 1회 결정·불변이며, 원격에서는 아예 못 바꾼다 (현 원격 API: 라이브 시작/중지·시간표·오디오뿐).

### 목표
- **G1 (자동)**: Live 중 인코딩 처리량 부족·네트워크 혼잡을 감지하면 품질 사다리를 한 단계 내린다. 회복되면 보수적으로 되돌린다.
- **G2 (원격)**: 폰에서 품질 프리셋(원본/절약1/절약2/안전)을 보고·수동 고정·자동 복귀 전환할 수 있다.
- **G3 (가시성)**: 현재 품질 단계·사유가 제어창/폰/헬스 이벤트(→텔레그램 푸시)로 드러난다.

### 비목표
- 오디오 품질 조절(강의 핵심은 음성 — 160kbps AAC 고정, 절대 낮추지 않음).
- 무공백(seamless) 전환. 스왑 갭 2~4초는 수용한다(§12).
- 스트림/녹화 품질 분리(단일 인코드 tee 구조상 불가 — §12).
- 시청자 수·YouTube 측 상태 기반 조절.

## §2 현재 구조와 제약 (검증된 사실)

### 2.1 품질 결정의 단일 깔때기
`StreamOrchestrator.BuildEncoderOptions()`(src/SilentStream.Core/Implementations/StreamOrchestrator.cs:446)가 **모든** 인코더 기동 경로(최초 연결·재연결 루프·워치독 재시작·방송만-중지 스왑·녹화 전용 기동)에서 호출된다. 수동 설정(`config.Encoding`) → 없으면 `BitrateMapper`(해상도/fps 매핑)로 비트레이트를 정한다. **여기가 사다리 적용 지점이다.**

### 2.2 검증된 스왑 메커니즘 (재사용 대상)
`StopStreamingKeepRecordingAsync()`(StreamOrchestrator.cs:231)가 확립한 패턴:
- `_gate` 아래에서 상태 전이 선행 → 워치독과의 경쟁 차단
- `Interlocked` 플래그 `_encoderSwapInProgress`로 "의도된 인코더 부재" 표시
- 인코더 stop→start 후 **`_sessionStart = DateTime.Now` 재스탬프** (새 파트 파일의 위치 0 = 지금; 누락 시 교시 VOD 컷이 EOF 밖을 자르는 버그 — 코드 주석에 명시된 함정)
- 스왑 완료 후 전체-중지 경쟁(tornDown) 재확인
- 브로드캐스트 재사용: `_session ??=`(StreamOrchestrator.cs:419) 덕분에 인코더가 재시작해도 같은 인제스트 키로 재접속 → **YouTube 방송·시청 URL이 유지**된다 (현장 7개 고아 방송 사고 후 확립).

단, 현 워치독(SuperviseAsync, StreamOrchestrator.cs:512)의 **Live 분기는 이 플래그를 확인하지 않는다**(RecordingOnly 분기만 확인, :576). 지금은 Live 중 스왑하는 코드가 없어서 문제가 없었지만, 품질 스왑이 생기면 수정이 필수다(§6).

### 2.3 지표의 두 함정 — 트리거 신호를 새로 만들어야 하는 이유

**함정 1 — 누적 평균.** `FfmpegProgressParser`(Media/FfmpegProgressParser.cs:13)는 스탯 라인의 `fps=`/`bitrate=`만 뽑는데, ffmpeg의 이 값들은 **세션 시작부터의 누적 평균**이다. 3시간째 세션에서 인코더가 반토막 나도 누적 fps는 몇 % 밖에 안 움직인다. 순간 변화를 보려면 같은 스탯 라인의 **누적 카운터(`frame=`, `size=`)를 캡처해 틱 간 Δ**로 창(window) 값을 계산해야 한다. (부수 효과: 현재 제어창/폰에 표시되는 비트레이트·fps도 누적 평균이라 부정확한데, 이 확장으로 순간치 표시로 함께 개선된다.)

**함정 2 — fifo 드롭 은폐.** tee 출력이 `drop_pkts_on_overflow=1`(Media/FfmpegArgumentsBuilder.cs:144)이라, 네트워크가 느려져 RTMP가 밀리면 인코더를 역압하지 않고 **fifo가 패킷을 조용히 버린다**. 즉 인코더 스탯(fps/size)은 정상으로 보이는 채 시청자 화면만 깨진다. 네트워크 혼잡의 관측 가능한 신호는 ffmpeg **stderr의 fifo 경고 라인**뿐이다(`-loglevel warning`이라 출력됨). 현재 `OnFfmpegStderr`(Implementations/EncoderPipeline.cs:309)는 "error" 포함 라인만 로그하고 버린다 — 카운트해야 한다.

부수 사실: `CpuPercent`는 **WPF 앱 프로세스** CPU다(StreamOrchestrator.cs:608). 정작 인코딩하는 ffmpeg 자식 프로세스는 측정 안 된다. 인코딩 과부하의 참 신호는 CPU%가 아니라 "목표 fps 대비 실제 처리 fps 부족"이므로 트리거는 fps 결손으로 잡되, ffmpeg 자식 CPU 샘플은 표시용으로 선택 추가한다(A2).

### 2.4 기타 전제
- 스탯 주기 = `-stats_period 2`(2초 틱, FfmpegArgumentsBuilder.cs:81).
- 영상 프레임 큐는 bounded(8)·DropOldest(EncoderPipeline.cs:148) — 인코더가 밀리면 캡처 프레임이 조용히 버려진다(→ Δframe 기반 창 fps에 그대로 드러남).
- `ResourceLimit`(스레드/프리셋 정적 캡)은 별개 기능으로 유지 — 그건 "상시 상한", 본 기능은 "동적 대응".
- 설정 스키마 현재 v6, 헬스 이벤트 어휘·텔레그램 푸시(메시지 기반, kind 불문 통과)는 Phase 0/1에서 확립됨.

## §3 설계 개요

```
                 (신호 생산)                    (정책)                        (실행·진실)
┌──────────────────┐  MetricsSnapshot(확장) ┌──────────────────────┐ ChangeRequested ┌──────────────────┐
│ EncoderPipeline   │──────────────────────▶│ AdaptiveQuality      │────────────────▶│ StreamOrchestrator│
│ · Δ 기반 순간 fps │                        │ Controller           │                 │ · ApplyQualitySwap│
│ · Δ 기반 순간 kbps│    StateChanged        │ · 사다리(세션별 구성)│  Apply(options) │   (가드된 스왑)   │
│ · fifo 드롭 카운트│◀── EncoderStarted ────│ · 트리거/이력/쿨다운 │◀────────────────│ · BuildEncoder    │
└──────────────────┘                        │ · 수동 고정 모드     │                 │   Options 깔때기  │
                                            └──────────┬───────────┘                 └────────┬─────────┘
                                                       │ (쉬는시간 판정)                       │ QualityChanged
                                            IPeriodScheduleStore                  ┌───────────┴───────────┐
                                                                                  ▼                       ▼
                                                                          RemoteControlServer      HealthMonitor
                                                                          (GET/PUT /api/quality,   (QualityDegraded
                                                                           status·summary·폰 UI)    → 텔레그램 푸시)
```

역할 분담:
- **EncoderPipeline**: 스탯 파서 확장분으로 순간치를 계산해 `MetricsSnapshot`에 실어 보낸다. fifo 경고를 센다. (신호 생산만; 판단 없음)
- **AdaptiveQualityController** (신규, Core/Implementations): 순수 정책. 가상 클록 주입으로 단위테스트 가능(HealthMonitor/PairingThrottle 시임 패턴). 스스로는 아무것도 실행하지 않고 `ChangeRequested`만 발화.
- **StreamOrchestrator**: 요청을 받아 가드된 스왑을 수행하고, 적용된 품질의 진실(`CurrentQuality`)을 소유·공표한다. `BuildEncoderOptions` 끝에서 `controller.Apply(options)` 한 줄로 사다리를 적용 — 최초 기동·재연결·워치독 재시작·스왑 전부가 자동으로 현재 단계를 따른다. **부수 이득**: 대역폭 부족으로 끊긴 뒤의 재연결도 낮은 단계로 붙으므로 재발 확률이 줄어든다.

## §4 품질 사다리

세션 시작 시(캡처 해상도·fps·설정이 확정된 뒤) 기저(L0)에서 파생해 구성한다. 오디오는 전 단계 불변.

| 단계 | 이름(UI) | 해상도 | fps | 영상 비트레이트 | 예시 (기저 1080p60·9000kbps) |
|---|---|---|---|---|---|
| L0 | 원본 | 기저 | 기저 | 기저 | 1080p60 · 9000k |
| L1 | 절약 1단계 | 기저 | 기저 | 기저 × 0.6 | 1080p60 · 5400k |
| L2 | 절약 2단계 | 기저 | min(30, 기저) | 기저 × 0.4 | 1080p30 · 3600k |
| L3 | 안전 모드 | ≤ 1280×720 | min(30, 기저) | min(2500, L2값) | 720p30 · 2500k |

- 설계 근거(강의 화면 특성): 텍스트 가독성이 최우선 → **해상도는 끝까지 보존**, fps는 판서/슬라이드에 30이면 충분 → 중간에 절감, 비트레이트가 가장 싼 레버 → 첫 단계. 또한 비트레이트/fps 변경은 YouTube 인제스트가 무리 없이 수용하지만 해상도 변경은 플레이어 측 재동기화 딸꾹질이 있을 수 있어 최후 단계로 둔다.
- L3 해상도는 `ParseResolution`과 동일 규칙(캡처 이하로 클램프, 짝수 강제, 비율 유지 letterbox — 기존 `-vf scale...pad` 경로 재사용). 기저가 이미 720p 이하면 L3 해상도 = 기저.
- **퇴화 단계 제거**: 인접 단계와 비트레이트 차가 10% 미만이고 해상도·fps가 같으면 그 단계는 생략(예: 기저가 이미 720p30·2500k면 사다리는 L0·L1만 남음).
- GOP(2초 키프레임)는 빌더가 `EffectiveFps`에서 자동 재계산 — 추가 작업 없음.

## §5 신호와 트리거

### 5.1 지표 확장 (A2)

`FfmpegProgressParser` 확장 — 같은 스탯 라인에서 추가 캡처:
```
frame= 3210 fps= 60 q=12.0 size= 12345KiB time=00:00:53.50 bitrate=1890.7kbits/s speed=1.0x
  └─ frame(누적 프레임 수)      └─ size(누적 출력 바이트)                        (speed는 누적이라 미사용)
```

`MetricsSnapshot` 필드 추가(positional 뒤에 기본값 있는 신규 필드 — 기존 호출부 무변경):
- `FrameCount`(누적), `OutputKBytes`(누적), `TeeDropCount`(누적 fifo 경고 수), `EncoderCpuPercent`(ffmpeg 자식 CPU, 선택; 없으면 -1)

`EncoderPipeline`이 직전 스냅샷과의 Δ로 **순간치**를 계산해 기존 `Fps`/`UploadBitrateKbps` 필드에 싣는다(의미 변경: 누적 평균 → 최근 2초 창 순간치. UI/폰 표시가 그대로 정확해짐). fifo 경고는 stderr 라인 패턴 매칭으로 카운트(ffmpeg 6.1 실제 메시지로 픽스처 고정 — AC).

> **보정 리스크**: tee 구조에서 `size=`가 슬레이브 합산인지 단일 계상인지는 문서화가 불분명하다. A2 AC에 "로컬 실측으로 계수 확인"을 포함하고, 절대치 비교가 어긋나면 트리거를 자기-기준선(최근 5분 중앙값) 상대 비교로 대체한다(정책 계층만 교체, 신호 계층 불변).

### 5.2 트리거 (상수는 HealthMonitor처럼 코드 상수, 괄호는 기본값)

평가는 **Live 상태에서만**, 스왑 중·정착 창(인코더 기동 후 60초) 제외. 창 = 최근 30초(스탯 틱 15개).

| 트리거 | 조건 | 의미 |
|---|---|---|
| **ENC(인코딩 과부하)** | 창 내 순간 fps 평균 < 목표 fps × 0.85, 그리고 틱의 80% 이상이 미달 | NVENC/CPU가 못 따라감(캡처 프레임 드롭 포함) |
| **NET(네트워크 혼잡)** | 창 내 `TeeDropCount` 증가가 20초 이상에 걸쳐 관측 | RTMP가 밀려 fifo가 패킷을 버리는 중 |

→ 어느 쪽이든 **한 단계 하강** 요청(사유 기록). 연속 하강 쿨다운 90초(새 단계가 자신을 증명할 시간 + 스왑 비용 절제).

### 5.3 복귀(상승)와 이력 현상

- 조건: 자동 모드 + 현재 단계에서 **5분간 건강**(순간 fps ≥ 목표×0.95, 최근 3분 드롭 0) → 한 단계 상승.
- **수업 중 복귀 보류**: 상승 스왑은 잘 나가는 방송에 2~4초 공백을 만드는 "미용" 작업이므로, `IPeriodScheduleStore`로 현재가 교시 중인지 확인해 **쉬는 시간/방과후로 미룬다**. 시간표가 비어 있으면 dwell 충족 즉시 상승. (하강은 이미 방송이 열화 중이므로 즉시.)
- **플랩 봉인**: 상승 후 5분 내 재하강하면 그 상위 단계를 30분간 봉인.
- **안전 상한**: 자동 변경(방향 불문) 시간당 8회 초과 시 자동 조절 일시 정지 + Warn 헬스 이벤트(무한 스왑 루프 방지의 최후 보루).

### 5.4 (선택·권장) RTMP 슬레이브 사망 감지 — A2-3

`onfail=ignore`라 RTMP 슬레이브가 죽으면(현장 -10053 계열) **녹화·진행 라인은 멀쩡한 채 방송만 조용히 사라진다** — 현 워치독의 사각지대(코드 주석에 "인코더의 관심사"라고 명시만 되어 있고 실제 감지는 없음). 품질 조절로 고칠 수 있는 상태가 아니므로 별개 신호로 다룬다: stderr에서 tee 슬레이브 실패 메시지를 감지하면 `EncoderPipeline`이 `RtmpLegFailed` 이벤트를 발화하고, 오케스트레이터가 기존 재연결 경로(ConnectingYouTube 전이 → `ConnectUntilLiveAsync`)로 재수립한다. 재수립도 `Apply` 깔때기를 지나므로 혼잡이 원인이었다면 낮은 단계로 붙는다. **이 항목은 미해결 현장 버그(-10053)의 사실상 수정이다.** 오탐 방지를 위해 메시지 패턴은 ffmpeg 6.1 실측 픽스처로 고정(AC).

## §6 전환 절차 (Live 중 품질 스왑)

`StopStreamingKeepRecordingAsync`(:231)의 검증된 골격을 `SwapEncoderAsync` 헬퍼로 일반화해 둘이 공유한다:

```
1. lock(_gate): State == Live 확인 (아니면 포기), token 획득           ← 워치독과 원자적 경쟁
2. _encoderSwapInProgress = 1
3. _encoder.StopAsync()                                                ← 현재 mp4 파이널라이즈
4. _sessionStart = DateTime.Now  +  _recording.CreateSessionFilePath   ← ★ 재스탬프 (교시 VOD 오프셋)
5. _encoder.StartAsync(BuildEncoderOptions(같은 RTMP URL, 새 파트파일)) ← Apply()가 새 단계 반영
6. lock(_gate): Stopping/Idle로 이미 내려갔으면(tornDown) 인코더 재정지 후 반환
7. finally: _encoderSwapInProgress = 0
8. 성공 시: CurrentQuality 갱신 + QualityChanged 발화 + 로그
   실패 시: 로그만 — 워치독이 !IsRunning을 감지해 기존 복구 경로로 재수립(그 경로도 Apply 경유)
```

상태는 **Live 유지**(방송만-중지와 달리 전이 없음 — UI 배지가 깜빡이지 않아야 하고, 브로드캐스트도 유지되므로 의미상 계속 Live가 맞다).

**워치독 수정 2건 (필수 선행, A1)**:
1. Live 분기(:537)의 사망/스톨 판정을 `Volatile.Read(ref _encoderSwapInProgress) == 1`이면 건너뛴다 — 지금은 RecordingOnly 분기(:576)만 확인해서, Live 중 스왑의 의도적 정지 창을 "인코더 사망"으로 오판해 이중 기동한다.
2. `recordingLength < lastRecordingLength`(파일이 줄었다 = 새 파트 파일)면 성장 기준선을 리셋 — 스왑 직후 새 파일이 이전 길이를 넘어설 때까지 성장 신호가 얼어붙어 스톨 오판 여지가 있다.

**IStreamOrchestrator 계약 추가** (인터페이스 고정 규칙에 따라 본 문서가 승인 산출물):
```csharp
QualityStatus CurrentQuality { get; }                  // 적용된 진실 (단계·모드·현재 파라미터)
event EventHandler<QualityStatus>? QualityChanged;     // 스왑 성공/모드 변경 시
```

## §7 수동 · 원격 제어

### 7.1 모드 모델
- `Auto`(기본): 컨트롤러가 하강/복귀 모두 관리.
- `ManualHold(level)`: 사람이 고정. **자동 개입 완전 정지**(과부하가 와도 하강하지 않음 — 사용자 의지 존중; 헬스 경고는 계속 뜬다). 폰에서 "자동으로 복귀"를 누르거나 세션이 끝나면(Idle) `Auto`로 리셋(세션 한정 — D-AQ4).
- `encoding.adaptive.enabled=false`여도 **수동 원격 조정은 항상 가능**(enabled는 자동 조절만 게이트).
- Live가 아닐 때의 수동 변경은 즉시 스왑 없이 저장 → 다음 인코더 기동에 자연 반영.

### 7.2 원격 API (RemoteControlServer.MapEndpoints 확장)

```
GET /api/quality
→ { mode: "auto"|"manual", level: 0, levelName: "원본",
    base:    { width, height, fps, videoKbps },
    current: { width, height, fps, videoKbps },
    ladder:  [ { level, name, width, height, fps, videoKbps }, ... ],
    adaptiveEnabled: true, degradedSinceUtc: null|"..." }

PUT /api/quality   { "mode": "auto" }  또는  { "mode": "manual", "level": 2 }
→ { ok: true, applied: "swapped"|"deferred" }     // deferred = Live 아님, 다음 기동 시 적용
```

- `/api/status`(WS 포함)에 `quality { mode, level, levelName, degraded }` 블록 추가 — 기존 `BroadcastStatus` 푸시를 그대로 탄다(QualityChanged 구독 1줄).
- `/api/summary`(멀티 호실 그리드)에 `qualityLevel`/`qualityName` 추가 — 카드에 "절약1" 칩 표시.

### 7.3 UI
- **폰**: 상태 카드에 품질 줄("1080p60 · 5.4Mbps · 자동(절약 1단계)"), 탭하면 시트: `[자동(권장)] [원본] [절약1] [절약2] [안전]` + 현재 단계·사유 표시.
- **제어창(WPF)**: 하단 상태바에 현재 품질 표기(기존 버전 표시 옆). 수동 조정 UI는 폰/설정 파일로 충분 — 제어창은 표시만(범위 절제).

## §8 헬스 · 알림 통합

`HealthEventKind`에 **`QualityDegraded`** 추가 (조건형 — RtmpDown/DiskLow와 같은 SetCondition 패턴):

| 시점 | Active | Severity | 메시지 예 |
|---|---|---|---|
| 자동 하강 | true | Warn | "송출 품질을 자동으로 낮췄습니다: 절약 1단계(5,400kbps) — 네트워크 혼잡(패킷 드롭) 감지" |
| L0 복귀 | false | Info | "송출 품질이 원본으로 복구되었습니다." |
| 수동 고정/해제 | (순간형 Notify) | Info | "원격에서 품질을 수동 고정했습니다: 절약 2단계" |
| 자동조절 일시정지(상한) | true | Warn | "품질 자동 조절이 과다 동작으로 일시 정지되었습니다 — 상태를 점검하세요." |

- HealthMonitor가 `IStreamOrchestrator.QualityChanged`를 구독(기존 StateChanged 구독 옆에 1줄).
- 텔레그램 푸시는 메시지 기반이라 **추가 배선 없이 자동으로 흐른다**(기본 NotifyLevel=warn → 하강 시 관리자 폰에 도착). 호실명 스탬프도 기존 경로 그대로.

## §9 설정 스키마 v7

```jsonc
"encoding": {
  // ...기존 필드 불변...
  "adaptive": {
    "enabled": true,      // 자동 조절 on/off (D-AQ1) — 수동 원격 조정과 무관
    "autoRecover": true,  // 자동 복귀 on/off (D-AQ2). false면 하강만 자동, 복귀는 수동
    "maxLevel": 3         // 자동 하강 허용 최저 단계 (0이면 사실상 자동 조절 없음)
  }
}
```

- `AppConfig.Version` 기본값 6→7, XML 주석에 v7 항목 추가. 누락 키는 기본값으로 역직렬화되는 기존 마이그레이션 규약 그대로(v5/v6 파일 무손실 로드) + 마이그레이션 테스트(호실명 H1~H3 패턴).

## §10 구현 단계 · AC · 테스트

> 실행 검증이 필요한 AC는 클라우드(Linux) 세션에서 불가 — "로컬 Windows 검증 필요"로 표시.

### Phase A1 — 스왑 기반 정비 (선행 필수)
- `SwapEncoderAsync` 헬퍼 추출(방송만-중지와 공용화, 동작 무변경 리팩터링), 워치독 수정 2건(§6), `IStreamOrchestrator.CurrentQuality/QualityChanged` 추가(빈 구현).
- **AC**: 기존 231 테스트 전부 그린 / 신규: ①Live 중 스왑 동안 워치독 무개입 ②스왑↔전체중지 경쟁 시 고아 인코더 없음 ③스왑 후 `_sessionStart` 재스탬프 검증 ④파일 축소 시 성장 기준선 리셋.

### Phase A2 — 신호 확장
- 파서 `frame=`/`size=` 캡처, `MetricsSnapshot` 필드 추가, `EncoderPipeline` Δ 기반 순간치 계산(표시 지표 정확화 동반), fifo 드롭 stderr 카운트. (선택) ffmpeg 자식 CPU 샘플. (선택·권장, D-AQ3) RTMP 슬레이브 사망 감지→재연결.
- **AC**: 파서 단위테스트(ffmpeg 6.1 실측 스탯/경고 라인 픽스처) / 순간치 계산 테스트(가짜 스냅샷 시퀀스) / tee `size=` 계상 방식 실측 확인(**로컬 Windows 검증 필요**) / 슬레이브 사망 감지 시 재수립 테스트.

### Phase A3 — 자동 조절기
- `AdaptiveQualityController`(사다리 구성·퇴화 제거·트리거·쿨다운·정착·복귀·수업중 보류·플랩 봉인·시간당 상한·수동 모드), 설정 v7, `QualityDegraded` 헬스, DI 배선, `BuildEncoderOptions`에 `Apply` 연결, `ChangeRequested`→스왑 배선.
- **AC**: 가상 클록 정책 테스트 ≥12케이스(하강 ENC/NET·정착 창 무시·쿨다운·복귀 dwell·수업중 보류·봉인·상한 정지·수동 고정 시 무개입·세션 리셋·퇴화 사다리·enabled=false·maxLevel 클램프) / 스키마 v7 마이그레이션 / 재연결이 현재 단계로 붙는지.

### Phase A4 — 원격 · UI 노출
- `GET/PUT /api/quality`, status/summary/WS 확장, 폰 시트 UI, 제어창 상태바 표기.
- **AC**: 엔드포인트 응답·권한(토큰) 테스트 / 수동 고정→즉시 스왑, Live 아님→deferred / 실기기 폰 확인(**로컬 Windows 검증 필요**).

각 Phase = 1 커밋 원칙(규약 준수), 브랜치 `claude/relaxed-davinci-qmmjwl`.

## §11 결정 필요 항목

| # | 항목 | 선택지 | 권장 |
|---|---|---|---|
| D-AQ1 | 자동 조절 기본값 | 켬 / 끔(원격 수동만) | **켬** — 무인 운영이 제품 전제. 보수적 트리거 + 시간당 상한으로 위험 통제 |
| D-AQ2 | 자동 복귀 | 켬(쉬는시간 우선) / 끔(하강만) | **켬** — 아침 일시 혼잡이 하루 종일 저화질로 굳는 것 방지 |
| D-AQ3 | RTMP 슬레이브 사망 감지·자동 재수립(A2-3) 포함 | 포함 / 제외 | **포함** — 미해결 현장 버그(-10053, 방송만 조용히 사라짐)의 사실상 수정. 플러밍 공유로 비용 소폭 |
| D-AQ4 | 수동 고정 지속 범위 | 세션 한정 / 영구(설정 저장) | **세션 한정** — 다음 날 무인 기동은 항상 자동으로. 영구 변경은 설정 파일 몫 |

## §12 리스크와 한계

| 리스크 | 평가 · 완화 |
|---|---|
| 스왑 갭 2~4초(시청자 프리즈, 녹화 파트 분할) | 하강 시점엔 이미 방송이 열화 중이라 순이득. 복귀는 쉬는시간 우선으로 완화. 파트 분할은 워치독 재시작과 동일한 기존 의미론 |
| 스왑에 걸친 교시의 VOD 부분 손실 | `_sessionStart` 재스탬프 구조상 해당 교시 VOD는 스왑 시점부터 커버(기존 재시작과 동일). 수용 |
| 녹화 품질 동반 하락 | 단일 인코드 tee 구조상 분리 불가. L1은 화면 콘텐츠에서 체감 미미, 깊은 하강은 "방송 사망 vs 저화질" 상황에서만. 수용 |
| 해상도 변경(L3) 시 YouTube 플레이어 재동기화 딸꾹질 | L3를 최후 단계로 배치한 이유. 비트레이트/fps 단계는 무리 없음 |
| `size=` tee 계상 불확실 | **실측 확정(§13)**: tee는 `size=N/A` — 비트레이트 윈도잉은 녹화 전용에서만 유효, NET 신호는 설계대로 fifo 드롭 카운트 |
| 플랩(진동) | 쿨다운 90초 + 복귀 dwell 5분 + 봉인 30분 + 시간당 상한 8회, 전부 가상 클록 테스트로 고정 |
| fifo 경고 패턴의 ffmpeg 버전 의존 | 동봉 ffmpeg 버전 고정 배포(Velopack) — 픽스처를 동봉 버전 실측으로 작성, 버전 업그레이드 시 픽스처 갱신을 릴리스 체크리스트에 추가 |

## §13 구현 결과 노트 (2026-07-13)

구현 중 실측으로 확정되거나 설계에서 조정된 사항:

1. **[필드 버그 수정] `-stats` 플래그 부재.** 리다이렉트된(비-tty) stderr + `-loglevel warning` 조합에서 ffmpeg는 주기 스탯을 **전혀 출력하지 않음**을 실측으로 확인(8.1, 0줄). 즉 기존 배포본은 메트릭이 항상 비어 있었고 `TimeSinceProgress`가 리셋되지 않아 스톨 분기가 상시 무장 상태였다(녹화 성장 거부권만이 방어 — C2 현장 ~30초 재시작 루프의 실제 원인으로 추정). A2에서 `-stats` 명시로 수정.
2. **[실측 확정] tee 주기 스탯은 `size=N/A bitrate=N/A`.** §5.1의 보정 리스크가 현실로 확인됨 — Δsize 비트레이트 윈도잉은 녹화 전용 세션에서만 동작하고, 라이브의 NET 신호는 설계 그대로 fifo 드롭 카운트가 유일. UI/폰의 라이브 비트레이트 표시는 종전과 같이 0(변화 없음).
3. **[실측 픽스처] ffmpeg 8.1 메시지 템플릿**: 드롭 = `[fifo @ …] FIFO queue full`(8.1은 "dropping packet" 문구 없음), 개방 실패 = `[fifo @ …] Error opening rtmp://…`, tee 중도 실패 = `Slave muxer #%u failed: %s, continuing with %u/%u slaves.` — MediaTests에 고정.
4. **[설계 조정] RTMP 슬레이브 사망 전달 = 이벤트 → 폴링 프로퍼티.** `RtmpLegFailed` 이벤트 대신 세션 스티키 `IEncoderPipeline.RtmpLegDown` 프로퍼티(StartAsync에서 리셋)를 워치독이 5초 틱마다 읽는다 — 오케스트레이터 측 래치/클리어 관리가 사라져 단순·경합 無. 재수립은 전용 백오프(30초→5분 배증, 건강 유지 시 사면)로 장기 회선 장애가 녹화를 파트파일 조각으로 만들지 않게 함(`RtmpLegRetryBaseDelay/MaxDelay` 옵션).
5. **[설계 조정] 심각도 에스컬레이션**: 자동 하강 조건은 Warn이되 **안전 모드(L3) 도달 시 Critical** — 알림 서비스의 "에스컬레이션만 재푸시" 규칙을 통과해 깊은 하강이 관리자 폰에 별도로 도착한다(L1→L2는 조용, 로그·상태에는 모두 표시).
6. **[설계 조정] 시간당 상한 도달은 로그 Warn만**(헬스 이벤트 아님) — 상한에 도달했다면 이미 하강 이벤트 8건이 푸시된 뒤라 별도 이벤트는 중복. §5.3의 "Warn 헬스"는 로그 경고로 구현.
7. **[명칭 정정] `QualityStatus.DegradedSinceUtc` → `DegradedSince`** — 컨트롤러 클록은 로컬(교시 판정과 일관)이므로 이름이 실체를 따르게 함.
8. **[검증 상태] 단위/통합 테스트 287 그린**(신규 56: A1 5·A2 17·A3 26·A4 8), 솔루션·App 비증분 빌드 0경고 0오류. **런타임 검증 미완**(§10): 라이브 중 수동 스왑의 시청 URL 유지, NET 트리거 실발동(대역 제한), 복귀의 교시 보류, 텔레그램 수신 — 실기기에서 사용자와 확인 필요.
