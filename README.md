# CHome

모바일 웹앱: https://box195.github.io/chome/

GitHub Pages → Firebase Realtime Database → Windows USB 카메라의 녹화를 제어합니다. 영상은 PC와 네이버 MYBOX에 저장됩니다. 휴대폰에는 Tailscale 설치가 필요 없습니다.

## 사용

1. PC에서 녹화기 `USBHomeCam-NAVER-Controlled.exe`와 수신기 `HomeCamRemote-CHome.exe`를 실행합니다.
2. 최초 한 번 PC 브라우저에서 `http://localhost:18742/chome.html`을 열고 소유자 Google 계정으로 로그인한 뒤 PC의 연결 코드를 입력합니다. 소유자 UID 설정과 Firebase 보안 규칙 배포가 선행되어야 합니다.
3. 휴대폰에서 모바일 웹앱에 같은 계정으로 로그인합니다. PC 온라인 상태를 확인하고 시작·중지 버튼을 사용합니다.

`Firebase에 명령 등록`은 전송 단계입니다. **녹화기 실행 확인**이 표시되어야 PC의 처리 영수증과 실제 녹화 상태가 확인된 것입니다. 상태가 20초 이상 갱신되지 않으면 연결 끊김으로 표시합니다. 90초 이상 지난 명령, 중복 ID 및 역순 명령은 재실행하지 않습니다.

Android Chrome에서 **휴대폰 알림 켜기 → 허용 → 시험 알림 보내기**를 누릅니다. PC가 표준 Web Push로 발송하고 서비스 워커가 알림을 표시하므로 페이지를 열어 둔 경우와 탭을 닫은 경우 모두 같은 경로를 사용합니다. 시험 결과의 `푸시 서버 발송 성공`은 실제 휴대폰 표시 확인과 구분됩니다. PC와 인터넷이 켜져 있어야 하며 Chrome 강제 종료, Android 알림 차단·절전 제한은 수신을 막을 수 있습니다. 움직임 알림 간격은 최소 120초입니다. 로그아웃하면 해당 기기의 구독을 해제합니다. 유료 서비스나 Firebase 요금제 변경은 사용하지 않습니다.

`PC 최초 연결` 코드는 설치할 때 PC에서 한 번 사용하는 기능입니다. 이미 연결된 휴대폰의 공개 페이지에는 표시하지 않습니다.

## 소스와 빌드

- `chome.js`: Firebase 웹 클라이언트 소스, `chome.bundle.js`: 동일 출처에서 제공하는 Firebase SDK 포함 배포본.
- `windows/recorder`: 기존 1초 TS 저장·MYBOX·움직임 감지와 새 명령 처리 소스.
- `windows/relay`: Google 소유자 인증, DPAPI 보호, 명령 수신·실제 처리 결과 확인.
- `windows/push-sender`: PC Web Push 발송기. `npm ci`로 의존성을 설치하고 `ReadPushSecrets.cs`를 .NET Framework 컴파일러에서 System.Security.dll 및 System.Web.Extensions.dll 참조로 빌드합니다. 이 폴더를 수신기 옆 `push-sender/`에 놓으면 수신기가 실행·재실행을 관리합니다. `node --test core.test.mjs`로 유효성 검사를 실행합니다.

푸시 VAPID 개인 키는 PC에서 생성하고 `CHome-WebPush-v1` 엔트로피의 Windows DPAPI CurrentUser로 보호한 `push-vapid.dat`에 저장합니다. 공개 키만 `push-config.json`에 게시합니다. 기존 Firebase 갱신 토큰도 현재 Windows 사용자의 암호화 파일에서 읽으며 관리자 키는 사용하지 않습니다. 개인 키를 바꾸면 휴대폰에서 다시 등록해야 합니다. 발송기는 구독·시험 요청·결과를 소유자 전용 `push/` 경로에 기록하며 오래된 시험 요청과 재시작 전 움직임을 재발송하지 않습니다.

새 PC 설치 시 `scripts/build-push.ps1`로 보조 프로그램을 빌드하고 의존성을 설치한 후 `node windows/push-sender/provision.mjs`를 최초 한 번 실행합니다. 기존 개인 키가 있으면 덮어쓰지 않고 실패합니다. 생성된 공개 설정을 웹에 배포하고 발송기 폴더를 설치하세요. 이미 설정된 현재 PC에서는 키를 다시 생성하지 않습니다.

웹: Node.js 설치 후 `npm install`, `npm run build`. GitHub Pages는 main 브랜치 루트를 게시합니다.

Windows: .NET Framework C# 컴파일러와 정식 FFmpeg 실행파일이 필요합니다. `scripts/build-windows.ps1 -Ffmpeg <ffmpeg.exe 경로>`로 `dist/`에 빌드합니다. 기존 실행파일과 설정을 백업한 뒤 설치하세요. 수신기와 같은 폴더에 기존 예비 직접 제어용 `index.html`, `app.js`, CHome용 `chome.html`, `chome.bundle.js`를 놓습니다. `chome.html`은 배포된 `index.html`에서 스크립트 주소를 `./chome.js`로 바꾼 사본입니다. 수신기의 `/chome.js`는 번들 파일을 제공합니다.

## Firebase와 비밀정보

이 프로젝트의 기존 Firebase 웹 설정을 재사용합니다. 공개 웹 API 키는 클라이언트 식별자이며 권한은 보안 규칙으로 제한됩니다.

`database.rules.json`은 기본 차단용입니다. 검증한 소유자 Google UID로 `Prepare-Owner-Rules.ps1 -OwnerUid <UID>`를 실행하면 로컬 전용 `database.rules.owner.json`이 생성됩니다. `firebase.json`은 이 파일을 사용합니다. 프로젝트 ID를 확인한 뒤 Firebase 공식 CLI로 `firebase deploy --only database --project chome-a8970`을 실행합니다. PC의 `%LOCALAPPDATA%\USBHomeCamNAVER\remote\owner-uid.txt`에도 같은 UID가 필요합니다.

MYBOX 토큰, Firebase 관리자 자격증명·갱신 토큰, 연결 코드, 녹화 영상, 개인 설정 및 로그는 공개하지 않습니다. 갱신 토큰은 Windows DPAPI CurrentUser로 암호화됩니다. 기존 Tailscale 직접 제어와 443 Funnel/MCP 설정은 보존합니다.

## 검증 범위

명령 프로토콜 자체 테스트 12건 및 오프라인 검증 9그룹 통과. 라이브 검증 결과는 별도 실행 보고서에 기록합니다. 실제 다른 Google 계정 및 휴대폰 기종별 테스트는 별도 확인이 필요합니다.
