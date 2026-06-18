# 다른 PC에서 테스트하기 (CI 자동 빌드)

이 문서는 **개발/현재 PC가 아닌 다른 Windows PC**에서 SilentStream을 테스트하는 방법입니다.
설치 파일(`Setup.exe`)은 GitHub Actions가 **self-contained(런타임 포함)** 로 자동 빌드하므로,
테스트 PC에는 .NET 런타임을 따로 설치할 필요가 없습니다.

---

## 1. 변경사항을 GitHub에 올리기 (최초 1회)

CI가 돌아가려면 워크플로 변경이 GitHub에 올라가 있어야 합니다. 현재 브랜치
(`claude/relaxed-davinci-qmmjwl`)에서:

```
git add .github/workflows/build.yml docs/REMOTE_TEST_SETUP.md
git commit -m "ci: build self-contained installer artifact"
git push
```

> GitHub Desktop을 쓴다면 "Commit" → "Push origin" 버튼으로 같은 작업을 할 수 있습니다.
> ⚠️ `git add -A`(전체 추가)는 쓰지 마세요. 줄바꿈(CRLF) 차이로 잡힌 80여 개 파일까지
> 같이 올라갑니다. 위처럼 **두 파일만** 지정해서 커밋하세요.

## 2. 설치 파일 다운로드

1. 브라우저에서 저장소 열기: `https://github.com/JonathanBlackDoctor/obs-alternative`
2. 상단 **Actions** 탭 클릭.
3. 방금 푸시로 생긴 최신 **build** 실행을 클릭 (초록 체크가 될 때까지 5~15분 대기).
4. 실행 페이지 맨 아래 **Artifacts** 섹션의 **`SilentStream-Setup`** 을 클릭해 다운로드.
5. 받은 zip을 풀면 `SilentStream-Setup-0.1.0.exe` 가 나옵니다.

> Actions 첫 실행 시 "I understand my workflows, go ahead and enable them" 버튼이 보이면 눌러 활성화하세요.

## 3. 테스트 PC에 설치

1. `SilentStream-Setup-0.1.0.exe` 를 USB·클라우드 등으로 테스트 PC에 복사 후 실행.
2. 설치 마법사: **EULA 동의 → 자동 시작 방식 → 녹화 폴더 → 폰 원격 사용 여부(포트 8787)**.
3. self-contained 빌드라 **.NET 런타임 사전 설치 불필요**. 바로 실행됩니다.
4. 첫 실행 시 동의(consent) 화면이 1회 표시됩니다.

## 4. YouTube 연결 준비 (실제 송출·업로드 테스트 시 필수)

앱은 `%AppData%\SilentStream\client_secret.json` 파일로 Google 로그인을 합니다.
이 파일이 없으면 송출·VOD 업로드 테스트를 할 수 없습니다(녹화는 가능).

**A. OAuth 클라이언트 만들기 (Google Cloud Console)**

1. https://console.cloud.google.com 접속 → 프로젝트 생성(또는 선택).
2. **API 및 서비스 → 라이브러리** → "YouTube Data API v3" 검색 → **사용 설정**.
3. **Google Auth Platform(OAuth 동의 화면)** 구성:
   - User type: **외부(External)**.
   - **테스트 사용자(Test users)** 에 **본인 Google 계정**을 추가 (앱이 '테스트' 상태라 추가된 계정만 로그인 가능).
4. **사용자 인증 정보 / 클라이언트 → 클라이언트 만들기**:
   - 애플리케이션 유형: **데스크톱 앱(Desktop app)**.
   - 이름 입력 후 생성 → **JSON 다운로드**.
5. 받은 JSON 파일 이름을 **`client_secret.json`** 으로 바꿔
   테스트 PC의 **`%AppData%\SilentStream\`** 폴더에 넣기.
   (탐색기 주소창에 `%AppData%\SilentStream` 입력하면 폴더로 이동)

**B. 채널 라이브 활성화**

- YouTube Studio → 실시간 스트리밍 시작 → 전화 인증.
  처음 활성화는 **최대 24시간** 걸릴 수 있으니 미리 해두세요.

**C. 첫 연결**

- 앱 실행 → 동의 → 브라우저에서 OAuth 로그인(위에서 테스트 사용자로 추가한 계정).

## 5. 폰 원격 제어 테스트

- 설치 시 폰 원격을 켰다면(또는 제어판 설정에서 켜기), **같은 와이파이**의 폰 브라우저로 접속.
- 제어판(`Ctrl+Shift+F12`)에 표시되는 **PIN** 으로 페어링.
- 집 밖에서 테스트하려면 USER_GUIDE §7의 **Tailscale** 방식을 사용하세요.

## 6. 검증 순서 (E2E)

전체 시나리오는 [`E2E_TEST_PLAN.md`](./E2E_TEST_PLAN.md) S1~S12 참고. 우선순위:

| 순서 | 항목 | 확인 포인트 |
|---|---|---|
| S1 | 클린 설치 | 설치 후 `%AppData%\SilentStream\config.json` 생성, 최초 동의 1회 |
| S2 | 최초 인증 + 수동 송출 | 상태 🟡→🟢, YouTube Studio에 unlisted 라이브 생성, 녹화 파일 증가 |
| S3 | 재부팅 자동 시작 | 부팅 후 자동 송출·녹화 시작 |
| S4 | 네트워크 단절·복구 | 인터넷 끊겨도 **녹화 무중단**, 복구 시 송출 재개 |
| E2~E3 | 교시 VOD | 교시 종료 시 무손실 컷 → unlisted 업로드, quota 초과 시 다음날 이월 |
| E4 | 폰 원격 | 시간표 입력·라이브 시작/중지 동작 |

> 참고: 업로드 한도(quota)와 다음날 이월 동작은 USER_GUIDE §6을 함께 보세요.
> CI가 받는 ffmpeg는 BtbN GPL 정적 빌드이며 내부 테스트 용도로 충분합니다.

---

## 빠른 요약

1. 두 파일만 커밋 → push.
2. GitHub **Actions → 최신 build → Artifacts → SilentStream-Setup** 다운로드.
3. 테스트 PC에서 zip 풀고 `Setup.exe` 실행(런타임 설치 불필요).
4. 송출 테스트하려면 `client_secret.json` 을 `%AppData%\SilentStream\` 에 넣기.

## Sources

- [OAuth 2.0 for Mobile & Desktop Apps — YouTube Data API](https://developers.google.com/youtube/v3/guides/auth/installed-apps)
- [Obtaining authorization credentials — YouTube Data API](https://developers.google.com/youtube/registering_an_application)
- [Manage OAuth Clients — Google Cloud Console Help](https://support.google.com/cloud/answer/15549257?hl=en)
