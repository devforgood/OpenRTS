# OpenRTS

.NET 서버와 Unity 클라이언트로 만드는 오픈소스 RTS 프로젝트입니다.
서버 구조는 LeagueSandbox GameServer(LoL 4.20 서버 에뮬레이터)를 분석한 내용을 참고하지만, **코드는 가져오지 않고 새로 작성합니다** (아래 [라이선스 검토](#2-참고-자료-라이선스-검토) 참고).

---

## 1. 저장소 구성

```text
OpenRTS/
├─ Shared/                         서버·클라이언트 공용 코드 (어느 쪽에도 속하지 않음)
│  ├─ package.json                 Unity 로컬 패키지 com.openrts.shared
│  ├─ OpenRTS.Shared.csproj        .NET 빌드용 (netstandard2.1, C# 9)
│  └─ Runtime/                     소스 + OpenRTS.Shared.asmdef
├─ Server/                         .NET 11 솔루션 (OpenRTS.slnx)
│  ├─ src/OpenRTS.Server/          게임 서버 콘솔 앱 (net11.0)
│  └─ tests/OpenRTS.Server.Tests/  xUnit 테스트
├─ Client/                         Unity 6 프로젝트 (6000.6.0f1)
│  └─ Assets/Scripts/              클라이언트 코드 (OpenRTS.Client.asmdef → OpenRTS.Shared 참조)
├─ Directory.Build.props           모든 .NET 빌드 출력을 artifacts/ 로 모음
├─ global.json                     .NET SDK 11.0.100 고정 (상위 기능 밴드 허용)
└─ THIRD_PARTY_NOTICES.md          외부 라이브러리 라이선스 고지 (LiteNetLib MIT 등)
```

### 공용 코드(Shared) 관리 방식

DLL을 복사하지 않고 **같은 소스를 양쪽에서 컴파일**합니다.

- **서버**: `OpenRTS.Shared.csproj` 를 `ProjectReference` 로 참조합니다.
- **Unity**: `Client/Packages/manifest.json` 에 `"com.openrts.shared": "file:../../Shared"` 로 등록된 로컬 패키지이고, `Runtime/` 의 소스를 asmdef로 직접 컴파일합니다.
- csproj가 **netstandard2.1 / C# 9** 로 고정되어 있어서, Unity가 지원하지 않는 문법이나 API를 쓰면 `dotnet build` 에서 먼저 실패합니다.
- 빌드 출력(`bin/`, `obj/`)이 `Shared/` 안에 생기면 Unity가 그 안의 생성된 `.cs` 까지 컴파일하므로, `Directory.Build.props` 로 저장소 루트의 `artifacts/` 에 출력합니다.
- Shared에는 `UnityEngine` 이나 서버 전용 라이브러리를 참조하지 않는 순수 C#만 둡니다: 프로토콜·메시지 정의, 직렬화, 시뮬레이션 상수, 수학.
- `Shared/` 의 `.meta` 파일은 Unity가 만든 것이며, 에셋 GUID를 유지하려면 커밋해야 합니다.

### 빌드 / 실행

```bash
# 서버 (.NET 11 SDK 필요)
cd Server
dotnet build
dotnet test
dotnet run --project src/OpenRTS.Server -- --port 7777 --bots 3   # Ctrl+C 로 종료
```

클라이언트는 Unity Hub에서 `Client` 폴더를 열고 에디터 버전 **6000.6.0f1** 을 사용합니다.

### 이동 동기화 확인 (현재 목표)

1. 서버 실행: `dotnet run --project Server/src/OpenRTS.Server`. 서버가 배회하는 봇 유닛 3개를 만듭니다.
2. Unity에서 `Assets/Scenes/Main.unity` 를 열고 Play 합니다. 어떤 씬이든 `ClientBootstrap` 이 카메라·바닥·네트워크 객체를 만듭니다.
3. 두 번째 클라이언트가 필요하면 메뉴 **OpenRTS → Build Windows Player** 로 `Client/Builds/Windows/OpenRTS.exe` 를 만들어 함께 실행합니다.

| 조작 | 기능 |
| --- | --- |
| 우클릭 | 내 유닛(초록) 이동 |
| WASD / 화살표, 휠 | 카메라 이동, 줌 |
| `[` / `]` | 인위 지연(왕복) -/+ 50ms |
| `-` / `=` | 스냅샷 손실 -/+ 5% |
| F1 | 보간 전 최신 스냅샷 위치(노란 구) 표시 |

플레이어 명령줄 옵션: `-host 127.0.0.1 -port 7777 -latency 200 -loss 10 -autotest`

자동 확인: `-autotest` 로 실행하면 접속 → 내 유닛을 8유닛 이동 → 화면 위치가 목표에 정확히 도착하는지, 이동 중 화면 속도가 실제 속도의 1.5배를 넘지 않는지(튐 없음) 확인합니다. 로그에 `AUTOTEST PASS/FAIL` 을 남기고 종료 코드 0/1 로 끝납니다.

```bash
Client/Builds/Windows/OpenRTS.exe -batchmode -nographics -autotest -latency 150 -loss 10 -logFile autotest.log
```

### 현재 구현 범위

| 영역 | 구현 | 위치 |
| --- | --- | --- |
| 프로토콜 | ClientHello(버전 확인) / ServerWelcome / MoveCommand / Snapshot, 리틀 엔디언 바이너리 | `Shared/Runtime/Protocol`, `Serialization` |
| 서버 | LiteNetLib UDP, 30Hz 틱, 15Hz 스냅샷(unreliable), 직선 이동, 봇 배회 | `Server/src/OpenRTS.Server` |
| 클라이언트 보간 | 스냅샷 버퍼, 서버 시계 추정(점진 보정), 2스냅샷 지연 보간, 끊김 시 최대 0.25초 외삽, 늦게 온 스냅샷 폐기 | `Shared/Runtime/Interpolation` |
| 클라이언트 | 접속, 우클릭 이동, 유닛 표시, 디버그 HUD, 지연·손실 시뮬레이션, 자동 확인 | `Client/Assets/Scripts` |
| 테스트 | 직렬화, 이동, 보간기, 실제 UDP 접속 통합 테스트 (서버 프로젝트에서 Shared 까지 검증) | `Server/tests` |

아직 없는 것: 경로 탐색, RVO2, 양자화·델타 압축, 시야 필터 (로드맵 2~5단계). 스냅샷은 유닛 100개까지만 담습니다(unreliable 패킷 한 개 크기 제한).

---

## 2. 참고 자료 라이선스 검토

참고 대상: `D:\projects\LeagueSandbox\GameServer`

| 구성요소 | 라이선스 | 판단 | 이 프로젝트에서의 처리 |
| --- | --- | --- | --- |
| GameServerLib / GameServerCore / GameServerConsole / Scripts | **AGPL-3.0** (GameServerLib 일부 GPL-3.0 헤더) | 코드를 가져오면 이 프로젝트 전체가 AGPL이 되고, 서버를 네트워크로 운영만 해도 소스 공개 의무가 생김 | **코드 복사 금지.** 구조와 알고리즘만 참고해 새로 작성 |
| QuadTree/QuadTree.cs | Microsoft Reference Source 원본 + LeagueSandbox 수정분(AGPL) | 수정분이 AGPL이라 그대로 쓸 수 없음 | 직접 구현 (공간 해시 또는 QuadTree) |
| GameMaths/Geometry/Clipper.cs | Clipper 6.4.2, **Boost Software License 1.0** | 허용적 라이선스. 고지문 유지 시 사용 가능 | 필요해지면 복사하지 않고 공식 패키지 **Clipper2**(Boost) 를 NuGet으로 사용 |
| LeaguePackets (서브모듈) | **Apache-2.0** | 라이선스상 사용 가능하나 LoL 전용 패킷 정의라 RTS에 쓸모 없음 | 사용 안 함 |
| RVO2 Library (C# 포팅, UNC 배포본) — 참고 서버에는 없음. 충돌 회피용으로 별도 도입 | **Apache-2.0** (도입 시 원본 저장소에서 재확인) | 허용적 라이선스. LICENSE·NOTICE 고지 유지 시 사용 가능 | 3단계에서 도입. 수정한 파일에는 변경 사실을 표기 |
| Content/LeagueSandbox-Default (게임 데이터) | Riot Games 저작물 | 사용 불가 | 사용 안 함. 데이터는 전부 직접 제작 |
| 분석 문서 (`pathfinding.md`, `collision-avoidance.md`, `ai.md`, `spells.md`), `tools/collision-lab.html` | 본 프로젝트 작성자가 직접 작성 | 자유롭게 참고 가능 | 설계 근거로 사용 |

원칙:

1. AGPL/GPL 코드는 **읽고 이해한 뒤 보지 않고 새로 작성**합니다. 클래스·메서드 이름, 주석, 코드 구조를 그대로 옮기지 않습니다.
2. 외부 코드를 가져올 때는 MIT / BSD / Apache-2.0 / Boost 같은 허용적 라이선스만 쓰고, 출처와 라이선스를 `THIRD_PARTY_NOTICES.md` 에 기록합니다.
3. 원작 게임(LoL)의 데이터, 이름, 에셋은 쓰지 않습니다.

> 미정: 이 프로젝트 자체의 라이선스(MIT 등)는 아직 정하지 않았습니다. 정한 뒤 루트에 `LICENSE` 파일을 추가합니다.

---

## 3. 기술 스택

| 영역 | 선택 | 비고 |
| --- | --- | --- |
| 서버 | .NET 11 (C# 15), 콘솔 앱 | 헤드리스, 고정 틱 |
| 클라이언트 | Unity 6000.6.0f1 | 렌더 파이프라인(URP) 은 1단계에서 추가 |
| 공용 코드 | `OpenRTS.Shared` (netstandard2.1, C# 9) | 서버는 프로젝트 참조, Unity는 로컬 패키지로 같은 소스를 컴파일 |
| 충돌 회피 | RVO2 (ORCA) | 서버에서만 실행 |
| 네트워크 | LiteNetLib (MIT) 예정 | UDP, 신뢰/비신뢰 채널, .NET과 Unity 양쪽 지원 |
| 직렬화 | 직접 작성한 바이너리 직렬화 (Shared) | 패킷 크기와 할당을 직접 통제 |
| 테스트 | xUnit | 시뮬레이션 로직은 서버 프로세스 없이 단위 테스트 |

---

## 4. 아키텍처 계획

### 4.1 동기화 모델: 서버 권위(server-authoritative) 상태 동기화

전통적인 RTS(유닛 수백 개)는 입력만 주고받는 **결정론적 락스텝**을 많이 씁니다. 이 프로젝트는 참고 서버와 같은 **서버 권위** 방식을 택합니다.

| 항목 | 락스텝 | 서버 권위 (채택) |
| --- | --- | --- |
| 대역폭 | 입력만 보내므로 매우 적음 | 상태를 보내므로 많음 → 변경분·시야 필터링으로 줄임 |
| 결정론 요구 | 모든 클라이언트가 비트 단위로 같아야 함 (Unity float, 물리 사용 불가) | 서버만 계산하므로 불필요 |
| 맵핵 방지 | 모든 클라이언트가 전체 상태를 가짐 → 불가능 | 시야 안의 정보만 보냄 → 가능 |
| 중간 참가·재접속 | 처음부터 재시뮬레이션 필요 | 현재 스냅샷만 보내면 됨 |
| 서버 비용 | 거의 없음 (릴레이) | 서버가 시뮬레이션 수행 |

### 4.2 이동 동기화: 서버 위치 스냅샷

충돌 회피에 **RVO2(ORCA)** 를 쓰므로 유닛 속도가 매 틱 바뀝니다. 그래서 참고 서버처럼 경로(웨이포인트)를 보내고 클라이언트가 따라가게 하는 방식은 쓸 수 없습니다. 클라이언트가 그리는 위치가 서버 위치와 금방 어긋나기 때문입니다(`collision-avoidance.md` 6.4). 대신 **서버가 계산한 위치를 주기적으로 보내고, 클라이언트는 받은 위치 사이를 보간**합니다.

```text
서버 틱:  경로 탐색(A*) → 선호 속도 계산 → RVO2로 실제 속도 결정 → 위치 적분
송신:     스냅샷 = { 서버 틱, [유닛 ID, 위치, 속도(선택), 방향] ... }
클라이언트: 스냅샷 버퍼 → 약 2스냅샷 늦게 재생하며 두 스냅샷 사이 보간
```

위치를 계속 보내면 대역폭이 커지므로 다음 방식으로 줄입니다.

- **움직인 유닛만** 보냅니다. 정지한 유닛은 멈춘 순간 한 번만 보냅니다.
- **양자화**: 위치를 맵 크기 기준 고정소수 16비트, 방향을 8비트로 압축합니다.
- **델타 압축**: 클라이언트가 마지막으로 받았다고 알려준 스냅샷을 기준으로, 바뀐 필드만 보냅니다.
- **시야 필터**: 각 플레이어에게는 그 팀 시야 안의 유닛만 보냅니다.
- **우선순위**: 패킷 예산을 넘으면 화면 근처, 전투 중인 유닛을 먼저 보내고 나머지는 다음 스냅샷으로 미룹니다.
- 스냅샷 전송 주기는 시뮬레이션 틱과 분리합니다 (예: 30Hz 틱, 15~20Hz 스냅샷).
- 체력 등 스탯은 위치와 별도로 **바뀐 값만** 보냅니다 (dirty flag 기반 복제).

대략적인 예산: 시야 안에서 움직이는 유닛 200개 × 약 6바이트 × 15Hz ≈ 18KB/s. 실제 값은 4단계에서 측정합니다.

### 4.3 서버 틱 처리 순서

30Hz 고정 틱 (`SimConstants.TickRate`). 틱마다 다음 순서로 처리합니다.

```text
1. 네트워크 수신       클라이언트 명령을 큐에서 꺼냄 (명령에 클라이언트 틱 번호 포함)
2. 명령 적용           이동/공격/건설/생산 명령 검증 후 유닛에 할당
3. AI 판단            유닛별 상위 판단 (타겟 선택, 명령 생성)
4. 이동               경로상 다음 지점으로 선호 속도 계산 → RVO2로 회피 속도 결정 → 위치 적분
5. 전투·효과           공격 타이밍, 투사체, 데미지, 버프
6. 경제·생산           자원 채집, 생산 큐, 건설 진행
7. 시야 계산           팀별 시야 갱신
8. 상태 전송           스냅샷 주기면 위치 스냅샷 + 변경된 스탯을 시야 필터 거쳐 전송
```

참고 서버는 이동이 AI 판단보다 먼저 돌아 스크립트가 정한 명령이 다음 틱에 반영되는 1틱 지연이 있었습니다(`ai.md` 2.2). 여기서는 판단을 이동보다 먼저 둡니다.

### 4.4 서버 모듈 계획

| 모듈 | 내용 | 참고한 개념 (LeagueSandbox) |
| --- | --- | --- |
| `GameLoop` | 고정 틱, 밀린 틱 상한 *(구현됨)* | `Game.GameLoop` (60Hz) |
| `World` / `EntityManager` | 엔티티 추가·제거·조회, 범위 검색 | `ObjectManager` |
| `Spatial` | 공간 분할 (균일 격자 또는 QuadTree) | `CollisionHandler` + QuadTree |
| `Navigation` | 타일 격자, 8방향 A*, 경로 단순화(string pulling), 유닛 반지름 고려 | `NavigationGrid`, `pathfinding.md` |
| `Movement` | 경로 추종(선호 속도) + RVO2 충돌 회피, 정지 유닛 처리 | `AttackableUnit.Move`, `collision-avoidance.md` |
| `Replication` | 위치 스냅샷(양자화·델타·우선순위), 스탯 변경분 복제 | `Replication*`, `NotifyWaypointGroup` |
| `Combat` | 공격 선딜/후딜, 투사체, 데미지 | `Spell`, `SpellMissile` (`spells.md`) |
| `Abilities` | 데이터(JSON) + 코드 스크립트로 스킬 정의 | `SpellData` + `ISpellScript` |
| `AI` | 엔진 층(추적·공격 실행) / 행동 층(타겟·명령 결정) 분리 | `ObjAIBase` + `IAIScript` (`ai.md`) |
| `Vision` | 팀별 시야, 시야 기반 전송 필터 | `UpdateVisionSpawnAndSync` |
| `Economy` | 자원, 생산 큐, 건설 | (RTS 고유, 새로 설계) |
| `Net` | 접속·핸드셰이크·세션, 패킷 디스패치, 송신 | `PacketServer`, `PacketHandlerManager`, `PacketNotifier` |

### 4.5 참고 서버 분석에서 얻은 개선점

분석 문서에서 확인한 문제를 처음부터 피하도록 설계합니다.

| 참고 서버의 문제 | 이 프로젝트의 대응 |
| --- | --- |
| 겹친 뒤 위치를 순간이동으로 밀어냄. 감지 반경과 밀어내기 반경이 달라 유닛끼리 끌려감 | 반경을 하나로 통일하고 **RVO2(ORCA)** 로 겹치기 전에 속도를 조정. 위치 동기화는 서버 스냅샷으로 함(4.2) |
| ORCA의 약점: 좁은 길과 정지한 무리 앞에서 막힘 (`collision-avoidance.md` 시뮬레이션 결과) | 막힘 감지(일정 시간 진척 없음) 시 **유닛을 고려한 A\* 재경로**로 우회. 도착·정지한 유닛은 RVO 이웃에서 우선순위를 낮춰 길을 내주게 함. 3단계에서 collision-lab 시나리오로 검증 |
| 위치 보정마다 이동 패킷 전송 | 위치는 스냅샷으로만 보내므로 보정용 패킷이 따로 없음. 움직인 유닛만 델타로 전송 |
| 일부 애니메이션 패킷이 시야 필터 없이 전체에 전송됨 (안개 속 정보 노출) | 모든 서버→클라이언트 송신을 **시야 필터를 거치는 단일 경로**로만 보냄 |
| RTT 측정 없음, 요청에 타임스탬프 없음, confirm 패킷 무시 | 핸드셰이크 후 주기적 RTT 측정, 명령에 클라이언트 틱 번호 포함, 클라이언트가 받은 마지막 스냅샷 번호를 응답 (델타 기준점) |
| 서버 틱(60Hz)과 무관하게 10초마다 시간만 동기화 | 스냅샷마다 서버 틱 번호 포함, 클라이언트는 틱 기준으로 보간 |
| AI가 이동보다 늦게 돌아 1틱 지연 | 틱 순서를 판단 → 이동으로 배치 |
| 스크립트를 실행 중 Roslyn으로 컴파일 (보안·디버깅 부담) | 능력·AI는 일반 C# 코드로 서버에 컴파일해 넣고, 수치는 JSON 데이터로 분리 |

### 4.6 클라이언트 계획

- **표현 전용**: 클라이언트는 게임 판정을 하지 않고 서버 상태를 그립니다.
- 위치 스냅샷을 버퍼에 쌓고 약 2스냅샷(15Hz 기준 약 130ms) 늦게 재생하며 **두 스냅샷 사이를 보간**합니다. 스냅샷이 늦으면 마지막 속도로 짧게 외삽하고, 다음 스냅샷이 오면 부드럽게 맞춥니다.
- 내 명령에는 즉시 반응을 보여줍니다 (클릭 이펙트, 선택 유닛 방향 전환 등). 실제 이동은 서버 응답 후 시작하며, RTS 특성상 RTT 정도의 지연은 허용됩니다.
- 입력: 드래그 박스 선택, 우클릭 이동/공격, 부대 지정(Ctrl+숫자), 건설 배치.
- 카메라: 탑다운 RTS 카메라 (가장자리 스크롤, 줌).
- 안개: 서버에서 받은 시야 정보로 Fog of War 텍스처 생성.

---

## 5. 로드맵

| 단계 | 목표 | 완료 기준 |
| --- | --- | --- |
| **0. 골격** ✅ | 서버·클라이언트 프로젝트 생성, Shared 패키지 연결, 고정 틱 루프, 계획 수립 | `dotnet test` 통과, Unity에서 Shared 컴파일됨 |
| **1. 이동 동기화** *(현재)* | LiteNetLib 접속·핸드셰이크, 우클릭 직선 이동, 위치 스냅샷 전송, 클라이언트 스냅샷 보간, 지연·손실 시뮬레이션 | 지연 150ms·손실 10% 에서도 두 클라이언트의 유닛이 튀지 않고 같은 위치에 도착 (`-autotest` 통과) |
| **2. 이동** | 격자 맵, A* + 경로 단순화, 박스 선택·다중 유닛 명령, URP 설정 | 유닛 여러 개를 선택해 장애물을 돌아 이동 |
| **3. 군집 이동** | 공간 분할, RVO2 도입, 막힘 감지 + 재경로, 스냅샷 양자화·델타 압축 | 50유닛이 좁은 길을 겹치거나 튀지 않고 통과, 대역폭 측정값 기록 |
| **4. 전투** | 체력·공격·투사체·사망, 변경분 스탯 복제, 자동 교전 AI | 두 팀 유닛이 교전해 한쪽이 전멸 |
| **5. 시야** | 팀별 시야, 시야 기반 전송 필터, Fog of War 표시 | 상대 시야 밖 유닛 정보가 패킷에 없음을 테스트로 확인 |
| **6. 경제·건설** | 자원 채집, 건물 건설, 유닛 생산 큐 | 일꾼 생산 → 채집 → 병력 생산 흐름 동작 |
| **7. 게임 흐름** | 로비, 매치 시작/종료 조건, 재접속(스냅샷 재전송) | 2인 대전 한 판을 처음부터 끝까지 진행 |
| **8. 이후** | 능력(스킬) 시스템, 봇 AI, 리플레이(명령 로그), 부하 테스트 | — |

## 6. 열린 질문

- 프로젝트 라이선스 결정 (MIT 권장 — 외부 기여와 상용 사용 모두 허용)
- 맵 표현: 타일 격자만으로 충분한지, 내비메시가 필요한지 (2단계에서 결정)
- 유닛 수 목표치 (팀당 100 / 200) — 대역폭 예산과 시야 계산 비용이 이에 따라 달라짐
- 서버 틱 30Hz가 적절한지 (전투 반응성 vs 서버 비용, 4단계에서 측정)
- 스냅샷 주기(15Hz / 20Hz)와 보간 지연 — 반응성과 대역폭의 균형 (3단계에서 측정)
- RVO2를 원본 C# 포팅 그대로 쓸지, 고정 틱·공간 분할에 맞게 다시 작성할지 (3단계 시작 시 결정)
