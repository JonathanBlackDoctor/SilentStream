# USB 자료 0.4.10 운영 배포
2026-10-17 · 상태: 확인대기. 운영 게시 완료, 현장 PC 설치·실측 대기.

## 배포 결과
- [앱·도우미·통합 설치본 v0.4.10](https://github.com/JonathanBlackDoctor/SilentStream/releases/tag/v0.4.10) 게시.
- [릴리스 실행 39026489693](https://github.com/JonathanBlackDoctor/SilentStream/actions/runs/39026489693) 성공.
- 릴리스 소스: 698c768ef87d2ffcc68777454a4e49d637b64716.
- Worker: 9a07f9f5-626c-4dd8-90e5-b6a23a970994.
- 네 호실 공식 설치 경로가 검증된 0.4.10 통합 패키지와 설치 실행 파일을 제공한다.
- USB 설정이 없는 기존 설치와 신규 설치에서 자동 보관은 기본 켜짐. 명시적으로 저장한 꺼짐은 유지한다.
- 기존 운영 CNG 키 silentstream-ops-20261017 재사용. Google 로그인 secret, 권한, Durable Object 바인딩 유지.

## 검증
- 자동 테스트 709개: Core/API 528, Windows 점검 74, Windows 오디오 10, 중앙 웹 83, 직접 웹 14. 릴리스 CI 성공.
- 공개 Git 소스 40개 파일과 로컬 검증 소스 일치, WPF Release 빌드 경고·오류 0.
- 공개 업데이트 피드와 앱 Full 패키지 메타데이터의 해시·크기 일치.
- 통합 ZIP 600359850바이트와 설치 EXE 161746777바이트 전체 다운로드 SHA-256 일치. ZIP 내부 앱 Setup 해시 일치.
- Broker·Coordinator·설치 EXE ProductVersion 0.4.10 확인. 13단계 시작 점검 번들 서명·공개키 일치.
- 설치 경로 변수 8개만 변경됐으며 나머지 운영 바인딩 동일.
- 네 호실 미인증 루트 302/API 401·빈 본문/no-store, 운영 JS 2개 원본 해시 일치, GitHub latest v0.4.10 확인.
- 실제 인증된 M112 화면에 USB 자료가 표시되고 오프라인 상태에서 목록·다운로드·설정 버튼이 비활성화됨. 공식 설치 페이지 로그인 접근 확인.

## 현장에 남은 단계
M112는 마지막 연결 2026-10-16 15:05:15 KST 이후 오프라인이다. M111/M113/백202는 미등록이다. 따라서 현장 PC에 새 앱·도우미가 설치됐다고 확인하지 않았으며 기기 제어 명령을 보내지 않았다.

[M112 공식 설치 페이지](https://admin.silentstream.win/install/m112)에서 통합 설치본을 실행하고 관리자 권한으로 기존 도우미·신뢰키를 갱신해야 한다. 앱 자동 업데이트 피드 게시만으로 도우미가 갱신되지는 않는다. 현장 설치 후 USB 실제 읽기 감지, 2초 목표, PowerPoint/PDF/탐색기, 녹음 동시 성능과 다운로드 해시를 확인한다.

증거: Git 제외 artifacts/usb-release/verification.json, public-checks.json, 배포·테스트 로그, 검증된 공개 패키지.
