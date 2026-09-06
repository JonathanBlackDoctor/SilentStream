# SilentStream 상시 중앙 포털

이 ASP.NET Core 서비스 하나를 24시간 켜진 서버에 배포하면 `admin.silentstream.win`,
`m111.silentstream.win`, `m112.silentstream.win`, `m113.silentstream.win`,
`baek202.silentstream.win`이 강의실 PC의 전원 상태와 무관하게 열린다. 강의실 PC는
`agent.silentstream.win`으로 **아웃바운드 WebSocket**만 연결하며 인바운드 포트나 강의실별
Cloudflare Tunnel을 열지 않는다. PC가 꺼져 있으면 사이트가 죽는 대신 중앙 포털에 오프라인으로 표시된다.

## 배포 구성

Cloudflare Tunnel/DNS에서 다음 호스트를 모두 같은 중앙 서버로 라우팅한다.

- `admin.silentstream.win`, 네 개의 호실 호스트: 사람용 포털
- `agent.silentstream.win`: 설치 패키지 다운로드와 장치 WebSocket 전용

사람용 호스트에는 Cloudflare Access 애플리케이션과 Google IdP를 적용한다. `agent` 호스트에는
사람용 Access 로그인을 적용하지 않는다. 대신 Origin의 `/api/device/*`가 일회용 등록 코드와
장치별 256-bit 비밀값을 검증한다. `agent` 호스트에서 `/control`, `/install`, `/api/control`,
`/api/install` 경로는 공개하지 않는다. 방화벽에서는 중앙 Origin을 Cloudflare Tunnel 또는 신뢰한
프록시에서만 접근할 수 있게 제한한다.

`appsettings.json`을 직접 수정하기보다 배포 환경 변수/비밀 저장소로 다음 값을 설정한다.

```text
ControlPlane__Enabled=true
ControlPlane__PublicBaseUri=https://admin.silentstream.win
ControlPlane__DeviceBaseUri=https://agent.silentstream.win
ControlPlane__AccessTeamDomain=<조직이름>.cloudflareaccess.com
ControlPlane__AccessAudiences__0=<Access application AUD>
ControlPlane__AdminEmails__0=admin@example.com
ControlPlane__RoomManagers__m112__0=manager@example.com
ControlPlane__StateFile=/보호된볼륨/control-plane-state.json
ControlPlane__PreparedPackagePath=/보호된볼륨/SilentStream-prepared-package.zip
ControlPlane__InstallerPath=/보호된볼륨/SilentStream-Central-Installer.exe
```

호스트마다 별도 Access 애플리케이션을 만들었다면 각 AUD를 `AccessAudiences__0`,
`AccessAudiences__1`처럼 모두 등록한다. 관리자는 모든 호실, 호실 담당자는 배정된 호실만 볼 수 있다.
Origin은 `Cf-Access-Jwt-Assertion`의 서명, 발급자, AUD, 만료, 이메일을 다시 검증하므로 프록시 헤더만
임의로 넣어서는 로그인할 수 없다.

릴리스의 다음 파일을 사용한다.

- `SilentStream-ControlPlane-vX.Y.Z.zip`: 중앙 서버 실행 파일
- `SilentStream-Central-Installer-vX.Y.Z.exe`: 웹에서 내려줄 작은 부트스트랩 설치기
- `inspection-package-unsigned-base-vX.Y.Z.zip`: 현장 배포 금지. 운영자 개인키로 `deployment prepare`를
  실행해 서명된 `SilentStream-prepared-package.zip`을 만든 뒤 서버에 둔다.

배포 후 `/health`는 프로세스 생존, `/ready`는 Google Access 설정·RBAC·두 배포 파일의 준비 여부를
검사한다. 상태 파일과 준비 패키지는 서비스 계정만 읽고 쓸 수 있는 영구 볼륨에 둔다.

## 현장 설치 흐름

1. 담당자가 해당 호실 사이트에 접속해 Google 로그인 후 **설치**를 누른다.
2. 내려받은 `SilentStream-Install-m112.exe` 같은 파일을 실행하고 UAC에 한 번 동의한다.
3. 설치기가 기존 Google Access 세션으로 10분짜리 일회용 등록을 받고, 서명 준비 패키지의 길이와
   SHA-256을 확인한 뒤 앱·서비스·부팅 자동 점검을 설치한다.
4. 기존 버전이 있으면 사용자 설정과 YouTube 인증을 유지한다. 새 에이전트가 중앙 서버에 실제로
   연결되기 전까지 기존 장치 등록도 폐기하지 않는다.
5. 이후에는 관리자/담당자가 별도 PIN이나 기기 승인을 하지 않고 Google 로그인만으로 상태 확인,
   안전 점검 재실행, 앱 재시작과 허용된 방송 명령을 수행한다.

Google Access 로그인과 YouTube OAuth는 같은 Google 계정을 쓸 수 있지만 토큰과 권한 범위는 의도적으로
분리된다. 실제 사람 음성·배선·무선 수신기 전원은 소프트웨어만으로 보증할 수 없으므로 최초 현장 확인은
남는다. 소프트웨어는 endpoint, 신호, 시험음/녹음, 점검 결과 전송을 자동화한다.

## 이전 호실별 Tunnel 방식(호환용)

이 서비스는 설치본에 터널 토큰을 넣지 않는다. 설치 PC는 호실을 선택한 뒤 이 서버에서 **그 호실의 설정만** HTTPS로 받아, 토큰을 곧바로 해당 Windows 사용자 DPAPI로 암호화한다.

## 최초 준비

1. 이 폴더의 `data/rooms.example.json`을 서버 전용 경로의 `data/rooms.json`으로 복사한다.
2. 각 호실의 `cloudflareTunnelToken`과 `cloudflareHostname`만 채운다. `rooms.json`은 절대 Git에 올리지 않는다.
3. 기본 보안 모드에서는 호실별 등록 코드가 필요하다. 임의의 긴 코드를 정하고 아래 명령으로 해시를 만든 뒤 `activationCodeHash`에 넣는다.

   ```powershell
   dotnet run --project src/SilentStream.ProvisioningServer -- hash-code "긴_임의_등록코드"
   ```

4. 서버 관리자 토큰을 환경 변수로 설정한다. 설정 파일에 쓰지 않는다.

   ```powershell
   $env:Provisioning__AdminToken = "긴_관리자_토큰"
   dotnet run --project src/SilentStream.ProvisioningServer
   ```

5. 서버를 `https://provision.내도메인`처럼 HTTPS 뒤에 배포한다. 실제 터널 토큰이 들어 있으므로 서버의 `data/` 볼륨은 서비스 계정만 읽게 한다.

## 여러 호실의 폰 알림

모든 호실에서 하나의 폰 브라우저 구독을 함께 쓰려면, 서버 전용 `rooms.json` 최상위에 공유 VAPID 키쌍을 넣는다.
키는 관리자 PC에서 한 번만 만들며, 개인키를 Git·릴리스 패키지·앱 `config.json`에 넣지 않는다.

```powershell
dotnet run --project src/SilentStream.ProvisioningServer -- generate-vapid
```

명령 출력의 객체를 `rooms.json`의 `sharedVapid` 값으로 그대로 복사한다. 예시는
`data/rooms.example.json`에 있다. 이후 새로 등록하는 호실은 claim 응답으로 키를 받고, 이미 등록된
호실은 다음 앱 시작 때 `roomId + installationId + DPAPI로 복호화한 해당 호실 터널 토큰`으로 갱신 요청을
인증해 키를 받는다. 토큰은 서버 응답·로그에 다시 노출하지 않는다.

키를 교체하면 앱은 이전 키의 서버 구독을 비우므로, 운영자는 각 폰에서 **폰 알림 켜기**를 다시 눌러야 한다.
공유 키가 없거나 갱신에 실패한 기존 단일 호실/quick-tunnel 설치는 기존처럼 로컬 VAPID 키로 계속 동작하지만,
다른 호실과 전체 알림 대상이 되지는 않는다.

## “호실만 선택” 모드

학교 내부망·설치 기간처럼 접근이 통제된 환경에서만 다음 환경 변수를 켜면, 설치 화면은 등록 코드 없이 호실 선택만 보여 준다.

```powershell
$env:Provisioning__AllowRoomOnlyEnrollment = "true"
```

공개 인터넷에 이 모드를 계속 켜 두면 누구나 호실을 선점할 수 있으므로, 배포가 끝나면 반드시 `false`로 되돌린다. 기본값은 `false`다.

## 설치본 연결

GitHub 저장소의 **Variables**에 `ROOM_PROVISIONING_URL`을 `https://provision.내도메인/`으로 추가한 뒤 새 버전을 릴리스한다. 릴리스 빌드는 URL만 든 `provisioning.json`을 패키지에 넣는다. 토큰은 빌드·설치본·저장소에 포함되지 않는다.

## 호실 재배정

PC 교체 또는 잘못 선택한 호실은 다음처럼 해제한다.

```powershell
Invoke-RestMethod -Method Post -Uri "https://provision.내도메인/api/admin/rooms/m111/release" -Headers @{ "X-Admin-Token" = "관리자_토큰" }
```

그 PC의 `%AppData%\MediaCaptureHelper\config.json`에서 `provisioning.completed`를 `false`로 바꾸거나, 해당 앱 데이터를 초기화한 뒤 다시 실행하면 호실 선택 화면이 다시 나온다.
