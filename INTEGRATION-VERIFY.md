# CHome 통합 버전 검증 상태

기존 PC/태블릿 녹화 앱과 소유자 Google 계정은 유지한다. 토큰·개인 UID는 저장소에 저장하지 않는다.

## 추가된 소스

- Windows 녹화기: CONTINUOUS / MOTION(최근 30초+감지 후 120초 이벤트 영상) 분리.
  MOTION 임시 조각은 raw-motion 폴더에 두고, 합쳐진 이벤트 영상만 MYBOX로 전송한다.
- Windows 릴레이: 동일한 원격 제어 구조에서 모드 검증 및 실제 처리 확인.
- Y700: 기존 CameraX 분석·녹화 경로를 재사용하는 WebRTC 송신.
- 웹: PC/Y700 별도 실시간 보기, 사용자가 요청할 때만 WebRTC 연결.
- Windows PC WebRTC 송신기: 기존 녹화 파일의 완결 TS 조각을 읽음. USB 카메라 중복 점유 없음.
- Firebase: 사용자·기기별 SDP 신호 접근 규칙. 영상 데이터는 데이터베이스에 저장하지 않음.

## 자동 검증 완료

- C# recorder/relay 컴파일 및 CloudProtocol 모드 테스트 16개.
- Windows PC live view aiortc 샘플 TS 디코딩 프레임 테스트.
- PC WebRTC 실제 Firebase offer/answer, ICE connected 확인.
- Firebase 배포 시 기존 PC/Y700 접근 규칙 동등성 검증.
- Android GitHub Actions APK 컴파일(최신 커밋 별도 확인 필요).
- 웹 번들 생성 및 JS 구문 검사.

## 추가 실기기 테스트 필요

- PC MOTION 모드에서 30초 사전 영상 포함, 상시 영상 미업로드 및 이벤트 업로드.
- 모바일 실제 PC 실시간 영상 재생.
- Y700 실시간 영상 재생 / 화면 꺼짐 상태 카메라 및 Web Push 발송.
- 외부망 NAT 환경에서 WebRTC 연결. TURN 서버가 없는 구성은 일부 망에서 ICE 실패 가능.
- Android MYBOX 업로드는 기존 구현 상태 그대로이며 실계정 검증 미완료.

## 소유자 안전

- 운영 RTDB에 루트 공개 규칙 없음.
- Android RTDB push VAPID 비밀키는 기기 승인 경로에서만 조회 가능.
- WebRTC viewer는 소유자 인증 후 신호를 등록하고 종료 시 세션을 정리.
- 실시간 영상 실행은 기존 녹화 자체를 시작하지 않음.