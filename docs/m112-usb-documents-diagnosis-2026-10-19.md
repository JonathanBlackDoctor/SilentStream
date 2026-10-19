# M112 강의록 감지 장애 점검 — 2026-10-19

## 확인 결과

15:04~15:05 KST 인증된 M112 원격 화면에서 온라인·방송 중, 연결 도우미 0.4.11, 앱 USB 자료 지원을 확인했다. USB 자동 보관은 켜져 있으나 감지 상태가 다음과 같았으며 2026-10-19 보관 자료는 0개였다.

> ETW 시작 실패: 관리자 서비스·권한을 확인하고 껐다 켜세요.

자료 목록 응답은 정상적으로 표시됐다. 현재 직접 확인한 장애는 다운로드 이전의 USB 문서 감지 초기화 실패다. 보관 파일이 없어 운영에서 실제 파일 전송을 시험하지는 못했다. 연결 도우미 버전은 확인했으나 PC 앱 실행 파일 버전은 별도 확인하지 않았다.

## 코드에서 발견한 원인

`UsbReadCollector.Start`가 `session.Source.Kernel.FileIORead`에 먼저 접근한 후 `EnableKernelProvider`를 호출했다. TraceEvent 3.1.21의 `Source` getter는 세션이 없으면 `EnsureStarted`로 일반 세션을 시작한다. 이후 `EnableKernelProvider`는 이미 유효한 세션이면 다음 예외를 던진다.

> The kernel provider must be enabled first and only once in a session.

이는 권한이 충분해도 실패하는 호출 순서다. 기존 catch가 예외 종류와 코드를 숨기고 일괄적으로 관리자 권한 안내를 표시했다. 현장 예외 원문·HRESULT는 기존 코드에서 전달하지 않아, 해당 PC의 추가 권한·OS 제약 유무까지 확정하지는 않는다.

근거: [Microsoft PerfView v3.1.21 TraceEventSession.cs](https://github.com/microsoft/perfview/blob/v3.1.21/src/TraceEvent/TraceEventSession.cs), `EnableKernelProvider` 600~602행과 `Source` 1338~1346행.

## 로컬 수정

- 커널 제공자 활성화 → Source 생성 → 이벤트 구독 → 소비 시작 순서로 수정했다.
- Poll/Stop은 실제로 생성해 보관한 Source 참조만 사용한다. 초기화 실패 후 오류 집계·정리를 하면서 Source getter가 세션을 새로 시작하는 일을 방지한다.
- 초기화 오류에 예외 종류와 HRESULT만 표시한다. 원문 메시지의 로컬 경로·파일명은 원격으로 노출하지 않는다.
- 실패 자동 반복 정책·USB 대상 확장자·보관/전송 방식은 기존 동작을 유지한다.

## 검증

- .NET SDK 8.0.426으로 Broker Release 빌드 성공: 경고 0, 오류 0.
- Core/API의 `FullyQualifiedName~UsbDocument` 필터: 14/14 통과.
- 중앙 USB 다운로드 테스트: 12/12 통과. 원본 SHA-256 재구성, 취소, 잘못된 조각, 권한 변경, 연결 종료, 동시 전송 제한 포함.
- 소스 diff 공백 오류 없음.

26개 테스트는 보관·API·전송 검증이며 실제 관리자 ETW 세션 시작 및 PowerPoint/PDF 읽기 감지 시험을 대체하지 않는다. 현장 운영 수정·재시작·설정 토글·실제 파일 다운로드는 수행하지 않았다.

## 다음 단계

수정된 Broker가 포함된 후속 도우미를 배포·적용하고, 실제 M112에서 감지 상태가 정상으로 바뀌는지 확인한다. 기능을 켠 뒤 USB의 PPT/PPTX/PDF를 다시 열어 자료 등록·보관 완료·원격 다운로드 및 SHA-256 일치를 검증한다. 실패가 남으면 새 예외 종류·HRESULT를 기준으로 추가 진단한다. 실제 파일 읽기가 없었던 과거 자료를 소급 수집한다고 가정하지 않는다.

점검 당시 M112는 방송 중이었으므로 현장 도우미 교체·재시작을 수행하지 않았다. 이번 요청의 로컬 수정은 운영에 반영되지 않았다.
