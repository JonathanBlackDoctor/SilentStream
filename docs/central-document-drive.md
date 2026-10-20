# 공용 Drive 한 번 연결하기

0.4.17부터 중앙 관리의 **공용 Google 계정 한 번 연결**에서 기기별 폴더를 소유한 공용 계정으로 Google 권한을 승인한다. 담당자의 중앙 로그인 세션은 유지한다. 모든 기기가 같은 중앙 연결을 사용하며 기기마다 공용 계정을 로그인하지 않는다. Google이 승인을 만료·취소하면 중앙에서 다시 연결한다.

중앙 관리에 등록한 각 기기 폴더는 공용 계정 소유여야 한다. 소유 공용 계정과 해당 담당자(reader)만 남도록 공유 권한을 적용하고 재조회한다. 담당자 미정 기기도 소유자 전용 폴더에 자료를 자동 보관하며, 담당자 바로가기만 비활성화한다. 외부 권한이 상위 폴더에서 상속되면 자동으로 상위 공유를 바꾸지 않고 중단한다. 담당자 변경·해제의 실효 상태도 Google 확인 뒤 갱신한다.

공용 연결은 기존 로그인과 분리한 OAuth state·PKCE·HttpOnly 브라우저 쿠키로 확인한다. 예상 공용 계정과 실제 Google 계정을 비교한다. 서버의 refresh token과 resumable upload 세션 주소는 별도 Cloudflare secret 키로 AES-GCM 암호화하여 비공개 Durable Object 저장소에만 둔다. Google 접근 범위는 기존 웹 생성 폴더 사용을 위한 전체 Drive 범위이므로 특정 폴더에만 제한되는 동의는 아니다. 공용 토큰을 PC·담당자·공개소스에 복사하지 않는다.

연결 후 서버의 결정적 alarm 작업이 온라인 기기의 USB 보관 목록을 확인한다. 개별 PC 인증과 경합하지 않도록 중앙 모드 지원 앱 0.4.17 이상에서만 전송한다. 앱은 중앙 모드 정책을 받아 개별 Google 로그인·업로드·공유 변경을 중단한다. 서버는 기기/설치 ID가 일치하는 기존 USB 인증 bridge로 자료 조각을 읽는다. 기기별 폴더와 YYYY-MM-DD 원래파일명 규칙은 유지한다.

한 번에 한 파일, 실행당 최대8개256KiB 조각, 파일 최대256MiB·전송10분이다. 업로드 ID와 전송 세션·위치를 먼저 영속화하고 다음 실행에서 Google의 실제 수신 위치를 조회한다. 재시작/응답 유실이 있어도 새 파일 ID를 만들지 않는다. Drive의 실제 SHA256·크기·부모 폴더·자료 해시 속성을 확인해야 완료로 기록한다. SHA256 미제공도 성공으로 처리하지 않는다. 서버에는 자료 전체를 버퍼링하지 않는다.

실패는 영속화하며 **중앙 Drive 실패 작업 재시도**에서만 다시 처리한다. OAuth 갱신 실패는 자동 반복하지 않고 다시 연결을 요구한다. 작업/재시도 목록은 페이지 단위로 처리하며 남은 항목을 이어서 확인한다. 파일명은 일반 명령 기록이나 업로드 상태 저장소에 남기지 않으며 내용을 구분할 때 해시를 사용한다. AI 작업·채팅 자동화를 호출하지 않는다.

PC가 오프라인이거나 USB 감지 도우미가 정상 동작하지 않으면 실제 자료는 업로드할 수 없다. 중앙 Google 연결·기기별 권한 완료와 실제 PC 설치/USB→Drive 전송 검증을 구분한다. 로컬 자료는 업로드 성공 여부와 무관하게 기존7일 보관 규칙을 따른다.

근거: [Google 웹 서버 OAuth](https://developers.google.com/identity/protocols/oauth2/web-server), [Drive resumable 업로드](https://developers.google.com/workspace/drive/api/guides/manage-uploads), [Drive 파일 SHA256](https://developers.google.com/workspace/drive/api/reference/rest/v3/files), [Durable Object alarms](https://developers.cloudflare.com/durable-objects/api/alarms/).
