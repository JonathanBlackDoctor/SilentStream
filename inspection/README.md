# SilentStream 원격 점검 v0.4.0

이 패키지는 앱과 분리된 관리 서비스, 제한된 관리자 브로커, 로그인 사용자 세션의 숨김 화면 에이전트로 구성됩니다. 앱이 종료·제거·재설치되는 동안에도 점검 기록과 원격 포털은 유지됩니다.

## 현장 작업 없는 사전 준비본

작업실에서 `SilentStreamInspectionCli deployment prepare ...`를 실행하면 개인키는 현재 Windows 사용자의 비내보내기 CNG 저장소에만 남고, 실기기용 ZIP에는 공개키·서명된 기본 점검·관리자 토큰 해시만 들어갑니다. 원문 관리자 토큰은 별도의 `operator-vault.json`에 기록되고 현재 작업실 사용자만 읽도록 ACL이 고정됩니다. 이 보관소 파일은 실기기에 복사하지 않습니다.

사전 준비본을 실기기에 풀고 `원격점검_설치.exe`만 실행하면 공개키와 관리자 권한, 기본 점검이 자동 등록됩니다. 현장에서는 최초 UAC 한 번 외에 키 생성·PIN 전달·PowerShell 실행·점검 ZIP 업로드가 필요하지 않습니다. 작업실 브라우저에는 보관소의 관리자 키를 한 번 입력합니다.

이후 점검 서비스 업데이트 ZIP은 작업실 개인키로 서명해 포털에 올리거나 앱 업데이트의 `inspection-update` 폴더에 포함할 수 있습니다. Coordinator와 Broker가 각각 서명과 파일 해시를 검증하고, 별도 무창 복구 실행기가 서비스를 교체합니다. 새 버전의 로컬 건강 확인이 60초 안에 통과하지 않으면 이전 설치 디렉터리로 자동 롤백합니다.

```powershell
SilentStreamInspectionCli agent-update sign .\새버전-bin 0.4.1 school-ops-2026 .\inspection-agent-0.4.1.zip
```

## 최초 1회 설치

1. 관리자 PC에서 `SilentStreamInspectionCli key init school-ops-2026 public-key.json`으로 비내보내기 ECDSA P-256 서명키를 만듭니다.
2. 공개키 파일을 패키지 폴더에 둔 뒤 실기기에서 `원격점검_설치.exe`를 실행합니다. UAC는 이때 한 번만 표시되고 설치 PowerShell도 숨김으로 실행됩니다. 이후 점검 중에는 명령 프롬프트, PowerShell, UAC가 나타나지 않습니다.
3. 생성된 `초기_원격점검_연결정보.txt`의 주소로 접속합니다.
4. Cloudflare Tunnel Public Hostname에 경로 두 개를 둡니다: `/inspect/* → http://localhost:8790`, 기본 경로 → `http://localhost:8787`. WebSocket이 그대로 허용되어야 합니다.

설치기는 기존 연결 토큰을 `operator`로 옮기고, 내부 앱 점검 전용 토큰을 별도로 생성합니다. 원문 토큰은 설정 파일에 저장하지 않고 SHA-256 해시 또는 LocalMachine DPAPI 암호문으로만 보관합니다. Windows 자동 로그인은 만들거나 변경하지 않습니다. 로그인 세션이 없을 때 포털·점검 기록은 유지되지만 화면/입력 단계는 성공 처리되지 않습니다.

실제 제거·재설치 점검을 하려면 현재 배포용 `MediaCaptureHelper-Setup-*.exe`를 `원격점검_설치.exe`와 같은 폴더에 둔 상태에서 최초 설치합니다. 설치기는 이 파일을 서비스 전용 캐시에 복사하고 SHA-256을 고정합니다. 제거 직전 Broker가 캐시를 다시 검증하며, 실제 제거와 설치는 로그인 사용자 세션에서 숨김으로 실행됩니다. 검증된 캐시가 없거나 해시가 달라지면 제거부터 거부합니다.
정식 배포 ZIP에는 같은 소스에서 만든 v0.4.0 Setup이 포함됩니다. 별도로 조립할 때는 `Build-AppSetup.ps1`을 먼저 실행한 뒤 `Build-InspectionPackage.ps1`을 실행합니다.

## 새 점검 만들기

`bundles/full-v0.4.0`처럼 `plan.json`과 필요한 스크립트를 한 폴더에 둔 뒤 다음 명령으로 서명합니다.

```powershell
SilentStreamInspectionCli bundle sign .\full-v0.4.0 silentstream-full 0.4.0 0.4.0 school-ops-2026 .\silentstream-full-0.4.0.zip
```

포털의 **새 점검 패키지**에서 ZIP을 올리면 호실에 설치된 공개키, ZIP 경로, 파일별 SHA-256, 실행 DAG를 모두 검증한 뒤에만 등록됩니다. 임의 관리자 PowerShell은 지원하지 않습니다.

## 안전 중단

실기기에서 `Ctrl+Alt+Shift+F12`를 누르면 원격 입력이 즉시 해제됩니다. NIC 차단은 최대 30초이며 별도 복구 감시 프로세스가 먼저 시작됩니다. 제거/재설치는 검증된 캐시 설치 파일과 관리자 5분 승인 없이는 실행되지 않습니다.
