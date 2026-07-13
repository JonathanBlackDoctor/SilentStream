# 호실 자동 설정 서버

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
