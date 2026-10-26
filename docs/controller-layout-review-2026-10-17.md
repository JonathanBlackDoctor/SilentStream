# 휴대폰·웹 원격 제어 화면 재점검

- 상태: 로컬 수정·검증 완료, 운영 게시 전.
- 대상: 호실 PWA. 사용자가 휴대폰·웹 화면이라고 범위를 지정했다.

## 수정

- 기기 관리의 방송 시작/중지 버튼에 카드 내부 여백 적용.
- 단독 종료 예약 버튼과 홀수 개 버튼 묶음의 마지막 버튼은 행 전체 폭 사용.
- 접힌 카드의 보조 설명과 펼치기 표시를 오른쪽에 정렬. 기기 관리 설명에서 별도 카드로 옮겨진 종료 예약 대신 수동 방송을 표시.
- 터치 조작 영역 최소 높이 44px. 좁은 화면에서 제목·날짜 입력이 줄바꿈 가능.
- 브라우저 자산 구분: app.js v15.

## 검증

- JavaScript 회귀 검사 97/97 통과.
- headless Edge 실제 렌더링: 너비 320/360/390/768/1280px 및 640px에서 CSS 200% 확대. 가로 넘침 0, JavaScript 오류 0.
- 버튼 높이·카드 내부 여백·단독 버튼 폭 검사. 새로고침 후 날짜와 펼친 카드 유지 및 오프라인 표시 검증.
- 처음 자산 버전 변경 후 기존 버전 v14를 고정한 검사 2건 실패. 기대값을 v15로 갱신 후 전수 통과.
- 화면·로그: `artifacts/controller-layout/after.json`, `after-320.png`, `after-390.png`, `after-640.png`, `direct-mobile.png`, `js-tests.log`, `build.log` (Git 제외).

브라우저 검사는 로컬 소스와 모의 응답으로 수행했다. 운영 PC 명령·실제 예약·종료·설치·배포를 실행하지 않았다. 실기기 터치 및 운영 반영 후 확인은 별도 단계다.

재실행: `node --test tests/*test.cjs cloudflare/control-plane/test/*.test.mjs`.
브라우저 검사는 Playwright와 설치된 Edge를 사용하며 `node tests/controller-layout.browser.cjs`로 실행한다. 필요하면 `SILENTSTREAM_PLAYWRIGHT`에 Playwright 모듈 경로를 지정한다.
