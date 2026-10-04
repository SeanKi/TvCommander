# TvCommander

Double Commander 스타일의 멀티 패널(2·3·4창) 파일 매니저. WPF(.NET 8) + C++ 네이티브 모듈(FmCore.dll).

## 빌드 / 배포

```powershell
.\build.ps1                 # Release 빌드 (C++ FmCore → WPF 앱)
.\build.ps1 -Publish        # .\publish 에 self-contained 배포본 (.NET 설치 불필요)
```

- 필요: Visual Studio 2022 (C++ 데스크톱 워크로드), .NET 8 이상 SDK
- Visual Studio 에서는 `TvCommander.sln` 을 열고 x64 로 빌드 (FmCore 가 먼저 빌드됨)
- FmCore 는 정적 CRT(/MT) 로 빌드되어 VC++ 재배포 패키지가 필요 없다
- 배포본: `TvCommander.exe` + `FmCore.dll` 두 파일을 같은 폴더에 두면 끝

## 구조

```
src/FmCore/            C++ DLL — 디렉터리 열거, 복사/이동(CopyFileEx), 셸 삭제, 아이콘,
                       멀티스레드 파일 찾기(I/O 워치독 내장), 자연 정렬
src/TvCommander/
  Native/              P/Invoke 선언
  IO/                  GuardedIo(먹통 방지 실행기), NetworkHealth(사전 통신 체크),
                       DirectoryLoader, FileOperation(복사/이동 엔진)
  Model/               FileItem, 정렬, 설정, 경로 도우미, 아이콘 캐시
  Views/               메인 창, 파일 패널, 찾기/보기/진행/대화상자
tests/IoProbe/         타임아웃·열거 성능 점검용 콘솔
```

## 네트워크 지연 대응 (먹통 방지)

1. **UI 스레드에서 파일시스템 API 를 호출하지 않는다.** 모든 I/O 는 전용 스레드에서 돈다.
2. **사전 통신 체크**: UNC 경로·네트워크 드라이브는 접근 전에 서버의 445/139 포트로 TCP 연결을 시도, **2초** 안에 응답이 없으면 바로 오류 표시. 결과는 서버별 캐시(정상 30초, 실패 5초).
3. **진행 워치독**: 폴더 열거 중 항목이 **5초**(로컬 10초) 동안 하나도 안 들어오면 타임아웃으로 확정하고 `CancelSynchronousIo` 로 막힌 호출을 끊는다. 그래도 안 풀리는 스레드는 버린다.
4. **파일 찾기**: C++ 워커마다 I/O 시작 시각을 기록, 한 호출이 5초를 넘으면 그 스레드의 I/O 만 취소하고 다음 폴더로 진행.
5. **복사/이동**: 진행이 5초 멈추면 진행 창에 경고, [취소]는 막힌 I/O 까지 끊는다.
6. 실패한 패널은 이전 목록을 유지하고 노란 오류 줄에 [다시 시도] 를 보여 준다.

`tests/IoProbe` 측정 결과 (이 PC):

| 경로 | 결과 |
|---|---|
| `\\10.255.255.1\share` (응답 없는 IP) | 2.05초 후 연결 불가 |
| `\\no-such-host-xyz\share` | 1.5초 후 연결 불가 |
| 응답 없는 파이프 읽기 (`--hang`) | 3초 타임아웃에 정확히 끊김 |
| `C:\Windows\WinSxS` (29,848개) | 118 ms |

## 키

| 키 | 동작 |
|---|---|
| F3 / F4 | 보기 (텍스트·HEX·이미지) / 편집 (설정의 외부 편집기, 기본 메모장) |
| F5 / F6 | 복사 / 이동 (대상 패널 경로가 기본값, 단일 항목은 새 이름 입력 가능) |
| Shift+F6, F2 | 이름 바꾸기 |
| F7 / Alt+F7 | 새 폴더 / 파일 찾기 |
| F8, Del / Shift+F8, Shift+Del | 휴지통으로 삭제 / 영구 삭제 |
| F9 / F10 | 터미널 (Windows Terminal 우선, UNC 는 cmd pushd) / 종료 |
| Tab, Shift+Tab | 다음/이전 패널 |
| Ctrl+1~4 / Ctrl+Shift+1~4 | 해당 패널로 이동 / 해당 패널을 복사·이동 대상으로 고정 |
| Ctrl+← / Ctrl+→ | 커서의 폴더를 왼쪽/오른쪽 패널에서 열기 |
| Alt+← / Alt+→ | 뒤로 / 앞으로 |
| Alt+F1 / Alt+F2 | 드라이브 선택 (2창: 왼쪽/오른쪽, 3·4창: 활성/대상 패널) |
| Ctrl+U | 활성·대상 패널 경로 교체 |
| Ctrl+R / Ctrl+H / Ctrl+L | 새로고침 / 숨김 파일 / 경로 입력란 |
| Ctrl+F2 / F3 / F4 | 2창 / 3창 / 4창 |
| Ctrl+Shift+C | 선택 항목 전체 경로 복사 |
| Enter, Backspace, Ctrl+PgUp | 열기, 상위 폴더 |
| Insert, Space, Shift+↑↓, Ctrl+클릭, Shift+클릭 | 선택(빨간색) |
| Num+ / Num- / Num* / Ctrl+A | 마스크로 선택 / 해제 / 반전 / 전체 |
| 글자 입력 | 빠른 검색 (1.5초 동안 이어서 입력) |

## 3·4창 동작

- 활성 패널은 파란 테두리, **대상 패널**(F5/F6 의 기본 목적지)은 주황 테두리와 "대상" 표시.
  대상은 직전에 활성이던 패널이고, Ctrl+Shift+숫자로 고정할 수 있다. 2창에서는 항상 반대편.
- **드래그 앤 드롭**: 파일을 다른 패널(또는 그 안의 폴더 위)에 놓으면 복사/이동 메뉴가 뜬다.
  Ctrl 을 누른 채 놓으면 바로 복사, Shift 는 바로 이동. 탐색기와도 주고받을 수 있다.

## 설정

`%APPDATA%\TvCommander\settings.json` — 패널 수·배치·경로·정렬, 창 위치, `Editor`, `Terminal`.
