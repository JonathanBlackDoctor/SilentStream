# SilentStream Phase 실행 가이드

> Claude Code에게 **Phase 단위로 끊어서** 작업을 시키기 위한 실행 키트입니다.
> 각 Phase 블록의 "붙여넣기 프롬프트"를 그대로 Claude Code에 입력하면 됩니다.
> 단일 진실 공급원은 [`SilentStream_개발계획서.md`](../SilentStream_개발계획서.md).

## 사용 방법

1. 한 번에 한 Phase만 진행합니다. 아래 해당 Phase의 **붙여넣기 프롬프트**를 복사해 입력합니다.
2. Claude Code가 작업 후 **완료 기준(AC) 체크리스트**를 보고하면 검토합니다.
3. AC가 모두 충족되면 다음 Phase로, 미충족이면 보완 지시합니다.
4. 실제 빌드·캡처·송출 검증은 **로컬 Windows PC**에서 합니다(클라우드 Linux 세션은 코드 생성까지만).

## 진행 현황 트래커

| Phase | 내용 | 담당 에이전트 | 상태 |
|---|---|---|---|
| 0 | 합의·스캐폴딩(인터페이스 고정) | Infra | ✅ (WPF 앱 빌드까지 Windows 검증) |
| 1 | 설정·로그 인프라 | Infra | ✅ (DPAPI 암복호화 Windows 실검증) |
| 2 | 미디어 파이프라인(캡처/오디오/인코딩/tee+녹화) | Media | ✅ (캡처 2560×1440@75fps·시스템오디오·NVENC tee·RTMP차단독립 Windows 검증 / 마이크 믹싱은 입력장치 부재로 미검증) |
| 3 | YouTube OAuth/Live | YouTube | 🟡 (코드·graceful 실패경로 검증, 실 Google 계정 송출 검증 필요) |
| 4 | 6px 박스/단축키/제어 UI | UI | ✅ (6px 박스 색상·제어창/녹화·설정 패널 렌더 육안 검증 + 로그뷰어 크래시 버그 수정, 2026-06-13 당시 9×9 → 2026-06-19 6×6 축소) |
| 5 | 자동시작/재시도/종료 처리 | Infra·Media | 🟡 (자동시작 레지스트리·30초 warmup·지수 백오프·**녹화 독립 시작**·mp4 정상 마감 검증, 재부팅 E2E는 Windows 필요) |
| 6 | 인스톨러/업데이트/E2E | Packaging | 🟡 (인스톨러 `.exe` 빌드 성공, 클린설치/재부팅 E2E·Squirrel 피드 URL 필요) |

> 상태: ⬜ 대기 / 🟡 진행 / ✅ 완료(AC 통과)

### 로컬 Windows 검증 결과 (2026-06-13)
> 환경: Windows 11, .NET 8 SDK 8.0.422, FFmpeg 8.1, NVIDIA RTX 4070(NVENC)+Intel UHD 770(QSV).
- **빌드**: 전체 솔루션(WPF 앱 포함) 0 에러. WPF SDK가 `System.IO` implicit using을 제외해 앱 빌드가 깨지던 버그 수정(`using System.IO;` 추가).
- **테스트**: 75/75 통과(DPAPI 암복호화·실 ffmpeg GPU감지 포함).
- **미디어**: GPU 자동감지 → `h264_nvenc`. 실 NVENC tee로 RTMP(죽은 포트) 차단 시 `continuing with 1/2 slaves` + mp4 정상 생성·재생.
- **앱 실행**: DXGI 주 모니터 2560×1440@75fps 캡처, WASAPI(마이크 없음→시스템오디오 폴백), 전역 단축키 등록, 자동시작 레지스트리 기록, 백오프 1→2→4→8→16→32→60초.
- **녹화 독립성 수정·검증**: 부팅 시 YouTube 연결 안 되면 녹화도 안 되던 갭(§4.1) 수정. 오프라인 실행 시 recording-only 인코더가 떠서 `SilentStream_REC_*.mp4`(h264 2560×1440@75fps+aac, 36초, 재생가능) 실제 생성, 앱 종료 시 stdin EOF로 정상 마감.
- **인스톨러**: ISCC로 `installer/output/SilentStream-Setup-0.1.0.exe`(60.9MB, FFmpeg 번들) 빌드.

---

## 공통 프롬프트 머리말 (모든 Phase에 적용)

```
너는 SilentStream의 Orchestrator다. SilentStream_개발계획서.md 와 CLAUDE.md 규약을 따른다.
브랜치 claude/relaxed-davinci-qmmjwl 에서만 작업하고, PR은 만들지 않는다.
아래 Phase를 실행하되, 해당 Phase의 완료 기준(AC)을 충족하면 의미 단위로 커밋하고,
AC를 못 채우거나 명세가 모호하면 멈추고 보고/질문해라.
실행 검증이 필요한 AC는 (이 환경이 Linux면) "로컬 Windows 검증 필요"로 표시해라.
```

---

## Phase 0 — 합의·스캐폴딩

**붙여넣기 프롬프트**
```
(공통 머리말) + 
계획서 Phase 0를 실행해. 솔루션 구조(src/SilentStream, tests)를 만들고,
Core/Contracts 의 인터페이스 전체(IStreamOrchestrator, IScreenCaptureSource,
IAudioMixer, IEncoderPipeline, IYouTubeLiveService, IRecordingManager,
IConfigStore, ILogService)를 빈 구현과 함께 정의해. DI 컨테이너를 구성하고
dotnet build 가 통과하도록 해.
```
**AC 체크리스트**
- [x] 솔루션이 인터페이스+빈 구현으로 `dotnet build` 성공 — Core/Tests는 Linux에서 빌드·테스트 통과(11 tests). WPF App(`net8.0-windows`)은 로컬 Windows 빌드 필요.
- [x] `Core/Contracts` 인터페이스 8종 정의 완료
- [x] DI 컨테이너 구성 완료 (`AddSilentStreamCore`, 컨테이너 resolve 테스트 통과)

## Phase 1 — 설정·로그 인프라

**붙여넣기 프롬프트**
```
(공통 머리말) +
계획서 Phase 1을 실행해. ConfigManager(config.json, OAuth 토큰 DPAPI 암호화),
NLog(180일 아카이브), 단일 인스턴스 Mutex 를 구현하고 단위 테스트를 추가해.
```
**AC 체크리스트**
- [x] config 저장/로드 라운드트립 테스트 통과 (Linux에서 검증)
- [x] 토큰 DPAPI 암복호화 단위 테스트 통과 — 테스트 작성 완료, DPAPI 자체는 Windows 전용이라 로컬 Windows/CI windows 잡에서 실행 필요(비 Windows에선 PlatformNotSupportedException 검증)
- [x] NLog 180일 아카이브 정책 적용 (`LogConfigurator`, MaxArchiveDays=180)
- [x] 단일 인스턴스 Mutex 동작 (`SingleInstanceGuard` 테스트 통과)

## Phase 2 — 미디어 파이프라인 (서브태스크 병렬)

**붙여넣기 프롬프트**
```
(공통 머리말) +
계획서 Phase 2를 실행해. 다음을 순서대로 구현해:
1) ScreenCapture(DXGI, 커서 포함)
2) AudioMixer(시스템 WASAPI Loopback + 마이크, 독립 볼륨)
3) EncoderPipeline(GPU 자동 감지: NVENC→AMF→QSV→x264 폴백 + FFmpeg)
4) tee 출력 + RecordingManager(RTMP+mp4 동시, RTMP에 onfail=ignore,
   세션 단위 1파일, 사용자 지정 폴더, 용량 한도, 7일 보존)
각 단계 AC를 보고하고, RTMP 차단 시 녹화 지속 여부를 반드시 검증 항목으로 남겨.
```
**AC 체크리스트**
- [x] 주 모니터 프레임 획득(커서 포함) — DXGI 실행 검증 완료(2026-06-13: 주 모니터 2560×1440@75fps, `DxgiScreenCaptureSource`)
- [x] 시스템 오디오 믹싱 — WASAPI 실행 검증 완료(시스템 루프백, `WasapiAudioMixer`). **마이크 믹싱/독립 볼륨**은 이 PC에 입력 장치가 없어 미검증(미연결 시 시스템 오디오만으로 폴백되는 경로는 로그로 확인)
- [x] GPU 자동 선택(폴백 포함) 검증 — 우선순위/폴백 단위 테스트 + 실 ffmpeg 통합 테스트 통과
- [x] tee로 RTMP+mp4 동시 출력 — 인자 빌더 테스트 + 실 ffmpeg(Linux)로 형식 검증. `-flags +global_header` 누락 시 mp4가 깨지는 버그를 검증 중 발견·수정
- [x] **RTMP 차단 시 mp4 녹화 계속**(onfail=ignore) — 실 ffmpeg로 RTMP 연결 거부 상태에서 mp4 정상 생성·재생 확인 ("continuing with 1/2 slaves")
- [x] 세션 파일명/용량 한도/7일 정리 동작 — RecordingManager 단위 테스트 7종 통과(보존/용량/디스크 부족/현재 파일 보호/part 접미사)

## Phase 3 — YouTube 연동

**붙여넣기 프롬프트**
```
(공통 머리말) +
계획서 Phase 3을 실행해. OAuth2 설치형 흐름(최초 1회 브라우저 로그인 + refresh
토큰 자동 갱신), liveBroadcasts/liveStreams 생성·바인드·complete 전이를 구현해.
공개범위 Unlisted, 제목 자동 생성(라이브 - yyyy-MM-dd HH:mm).
OAuth Client ID/Secret 위치는 CLAUDE.local.md 를 참고하라고 안내만 하고 커밋하지 마.
```
**AC 체크리스트**
- [ ] OAuth 로그인 + 토큰 자동 갱신 — 구현 완료(설치형 흐름 + DPAPI 암호화 토큰 저장소, 저장소 로직은 단위 테스트 통과). **실 Google 계정으로 로컬 Windows 검증 필요** (`%AppData%\SilentStream\client_secret.json` 준비)
- [ ] Unlisted 브로드캐스트 생성 → streamKey 송출 — 구현 완료(insert+bind, 제목 템플릿 테스트 통과). **실 채널 검증 필요**
- [ ] 종료 시 complete 전이 — 구현 완료(실패 시 다음 부팅에 새로 생성). **실 채널 검증 필요**

## Phase 4 — 표시기·단축키·제어 UI (Phase 2/3와 병렬 가능)

**붙여넣기 프롬프트**
```
(공통 머리말) +
계획서 Phase 4를 실행해. 6px StatusBox(좌상단 0,0, TopMost, 클릭 통과,
🟢/🟡/🔴), 전역 단축키(Ctrl+Shift+F12, 변경 가능), ControlUI(다크 테마:
상태 배지/시작·중지/성능 bps·fps·CPU·GPU/오디오 슬라이더·마이크 드롭다운/
자원 제한/녹화 패널(현재 파일·누적 용량·남은 디스크)/로그 뷰어 1000줄/설정 패널)
를 구현해. StreamState 와 색상·UI 동기화를 AC로 검증해.
```
**AC 체크리스트**
- [x] 상태 박스 색상이 StreamState와 동기화, 클릭 통과 — `StatusBoxWindow`(WS_EX_TRANSPARENT|LAYERED|TOOLWINDOW|NOACTIVATE, StateChanged 구독). **Windows 육안 검증(2026-06-13)**: 창이 정확히 (0,0)에 9×9px, Warmup 상태에서 노란색 표시 확인(GetWindowRect=`9x9 at(0,0)`). **2026-06-19**: 박스 크기를 6×6으로 축소(코드 반영, 실기기 재검증 필요).
- [x] 전역 단축키로 UI 토글 — `HotkeyManager`(메시지 전용 창 + RegisterHotKey). **Windows 검증(2026-06-13)**: `Ctrl+Shift+F12` 등록 로그 확인. 제어창 토글은 단일 인스턴스 신호(2차 실행→1차에 표시 신호) 경로로도 검증.
- [x] 오디오 슬라이더/마이크 드롭다운 실시간 반영 — ViewModel→IAudioMixer 즉시 반영 + config 저장. **Windows 육안 검증(2026-06-13)**: 시스템/마이크 볼륨 슬라이더·마이크 드롭다운 렌더 확인(마이크 장치 부재로 목록은 비어 있음, 시스템 오디오 폴백).
- [x] 녹화 패널에 현재 파일·용량·남은 디스크 표시 — IRecordingManager.GetStatus 바인딩. **Windows 육안 검증(2026-06-13)**: 녹화 패널(현재 파일/누적 용량/남은 디스크) 및 설정 패널(단축키·자동시작·녹화 폴더·용량 한도·보존일수) 렌더 확인.
- [x] 로그 뷰어 최근 1000줄 — InMemoryLogSink 1000줄 캡 단위 테스트 통과, ListBox 자동 스크롤 구현. **버그 수정(2026-06-13, 커밋 6acf46f)**: 백그라운드 로그 스레드가 가상화 ListBox 항목 생성기를 desync시켜 제어창 표시 직후 앱이 크래시하던 문제를 `BindingOperations.EnableCollectionSynchronization`로 해결. 제어창 25초+ 안정 동작 확인.

## Phase 5 — 자동화·복원력

**붙여넣기 프롬프트**
```
(공통 머리말) +
계획서 Phase 5를 실행해. AutoStartManager(시작 프로그램 / 작업 스케줄러),
30초 지연 시작, 무한 재시도(지수 백오프)·워치독, 종료 시퀀스(세션 종료 후킹 →
mp4 정상 마감 → 브로드캐스트 complete)를 구현해.
재시도 중에도 녹화가 유지되는지(독립 동작) AC로 검증해.
```
**AC 체크리스트**
- [ ] 재부팅 시 자동 송출+녹화 시작 — AutoStartManager(레지스트리 Run / 작업 스케줄러 최고 권한) + 단일 인스턴스 + 30초 워밍업 구현. **재부팅 E2E는 로컬 Windows 검증 필요**
- [x] 네트워크 차단/복구 시 자동 재연결 — 지수 백오프(1→2→…→60초 캡) 무한 재시도 + 인코더 워치독, 상태머신 단위 테스트 통과(재시도 3회 후 Live, 인코더 사망 후 자동 재기동). **녹화 무중단**은 tee `onfail=ignore`로 Phase 2에서 실 ffmpeg 검증 완료
- [~] 종료 시 mp4 정상 마감 + complete 전이 — SessionEnding 후킹 + Stop 시퀀스(인코더 EOF → complete 전이) 구현, Stop 순서 단위 테스트 통과. **mp4 마감은 검증**(2026-06-13: 앱 종료 시 stdin EOF로 ffmpeg가 frag mp4를 정상 마감·재생 확인). complete 전이는 실 채널 필요, **실제 OS 종료(SessionEnding) E2E는 로컬 Windows 필요**
- 참고(2026-06-13 수정): **녹화 독립 시작** — 부팅 시 YouTube 미연결이어도 recording-only 인코더로 녹화 시작, 연결되면 tee 전환. 단위 테스트 + 오프라인 실행 E2E로 검증(계획서 §4.1).

## Phase 6 — 패키징·업데이트·최종 테스트

**붙여넣기 프롬프트**
```
(공통 머리말) +
계획서 Phase 6을 실행해. Inno Setup 인스톨러(자동 시작 방식·녹화 폴더 선택,
동의 화면, FFmpeg 번들), Squirrel 자동 업데이트, E2E 시나리오를 구성해.
클린 설치 → 재부팅 → 송출/녹화/UI/업데이트 전 기능 동작을 AC로 검증해.
```
**AC 체크리스트**
- [x] ~~Inno Setup 인스톨러~~ → **Velopack 으로 전환(2026-06-19)**. `installer/SilentStream.iss` 는 deprecated(참조용 보존). 자동 시작 방식·녹화 폴더·원격은 앱 기본값 + 설정 UI 로, 동의 화면은 앱 최초 실행(`ConsentWindow`)으로, FFmpeg·OAuth 시크릿은 Velopack 패키지 동봉으로 이전.
- [x] 자동 업데이트 동작 — `AppUpdateManager`(Velopack, 6시간 주기, GitHub Releases 피드) 구현. 라이브 송출을 끊지 않도록 다운로드 후 **다음 종료/재시작 시 적용**. **실기기 업데이트 E2E 검증 필요**
- [ ] E2E: 클린 설치→재부팅→전 기능 동작 — 시나리오 문서화(`docs/E2E_TEST_PLAN.md`). **로컬 Windows에서 수행 필요**
- [x] 제거 시 자동 시작 항목·잔여 정리 — `Program.OnBeforeUninstallFastCallback` 에서 자동 시작 해제 + 방화벽 규칙 삭제(녹화·설정·토큰 보존). **검증 필요**

### Velopack 릴리스 절차 (방법 A)

**선행 1회 설정**
1. GitHub 리포 Settings → Secrets and variables → Actions 에 `OAUTH_CLIENT_SECRET_JSON` 추가(구글 데스크톱 OAuth 클라이언트 JSON 원문 붙여넣기). CI 가 이걸 패키지에 동봉한다.

**새 버전 배포(자동)** — CI 가 빌드→패키징→게시까지 수행:
```
git tag v0.2.0
git push origin v0.2.0
```
→ Actions 의 `release-windows` 잡이 GitHub Releases 에 Setup.exe + 전체/델타 패키지 + `releases.win.json` 게시.
→ 설치된 앱들은 6시간 내 자동 감지·다운로드, 다음 재시작(보통 다음 부팅) 시 새 버전 적용.

**최초 설치(각 기기 1회)**: 해당 릴리스의 `SilentStream-win-Setup.exe` 를 받아 실행(사용자 권한, `%LocalAppData%\SilentStream` 에 설치). 이후 업데이트는 재설치 불필요.

**로컬 수동 패키징(테스트용)** — `$env:Path = "C:\Program Files\dotnet;" + $env:Path` 선행:
```
dotnet tool install -g vpk --version 1.2.0
dotnet publish src/SilentStream.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
dotnet publish src/SilentStream.UninstallHelper -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/uninstall-helper
# publish/ffmpeg/ffmpeg.exe 와 publish/client_secret.json 을 수동 배치한 뒤:
vpk pack --packId SilentStream --packVersion 0.2.0 --packDir publish --mainExe SilentStream.exe --packTitle SilentStream
# 산출물: ./Releases (Setup.exe 등). GitHub 게시는 vpk upload github ... (CI 가 대신 수행)
```
