# 아키텍처

## 1. 한눈에

```text
┌──────────── Unity 클라이언트 (표현 전용) ─────────────┐        ┌──────────── .NET 11 서버 (권위) ───────────────┐
│ RtsInput / CommandPanel ── 명령 ──────────────────────┼──UDP──▶│ GameServer (LiteNetLib, 세션·검증·전송)        │
│ GameClient (접속, 지연·손실 흉내, 수신 분배)          │        │   │                                            │
│   SnapshotReceiver → SnapshotInterpolator             │◀──UDP──┼── 스냅샷(팀별 필터·델타), 이벤트, 지표, 경제  │
│ UnitViewManager · CombatEffects · FogOfWarView ·      │        │ World (시뮬레이션 상태, Step)                   │
│ GhostView · MapView (그리기)                          │        │   NavGrid · OrcaSolver · CombatSystem ·        │
└───────────────────────────────────────────────────────┘        │   BattleAi · EconomySystem · VisionGrid        │
                     ▲                                            └───────────────────────────────────────────────┘
                     └──────── Shared (netstandard2.1, 양쪽이 같은 소스를 컴파일) ────────┘
                               프로토콜·직렬화·보간·맵 정의·유닛 수치·시야 격자
```

- **서버 권위**: 판정은 서버만 합니다. 클라이언트는 명령을 보내고, 받은 상태를 그립니다(락스텝이 아님 — 이유는 루트 README 4.1).
- **Shared**: 서버와 Unity 가 **같은 소스**를 컴파일합니다. 서버는 `ProjectReference`, Unity 는 로컬 패키지(`Client/Packages/manifest.json` → `file:../../Shared`). Unity 쪽 컴파일러 때문에 **C# 9 / netstandard2.1** 로 고정입니다.
- **같은 함수로 같은 결과**: 맵 벽(`MapLayouts`)·유닛 수치(`UnitArchetypes`)·시야(`VisionGrid`)는 Shared 에 있어서, 서버가 따로 보내지 않아도 클라이언트가 같은 값을 계산합니다(설정의 맵 종류만 전파).

## 2. 폴더와 책임

| 위치 | 책임 | 의존 |
| --- | --- | --- |
| `Shared/Runtime/Serialization` | 리틀 엔디언 바이너리 `PacketWriter` / `PacketReader`(잘린 패킷은 `PacketFormatException`) | 없음 |
| `Shared/Runtime/Protocol` | 메시지 정의(`Messages.cs`), 스냅샷 형식·델타·히스토리(`Snapshot.cs`), 설정·지표(`SimSettings.cs`) | Serialization, Combat |
| `Shared/Runtime/Interpolation` | 조각 조립(`SnapshotAssembler`), 델타 복원(`SnapshotReceiver`), 보간(`SnapshotInterpolator`) | Protocol |
| `Shared/Runtime/Map` | 맵 종류별 벽 사각형(`MapLayouts`), `WallRect` 기하 | 없음 |
| `Shared/Runtime/Combat` | 유닛 종류·수치(`UnitArchetypes`), 경제 규칙(`EconomyRules`), 시야 격자(`VisionGrid`) | Map |
| `Shared/Runtime/Abilities` | 능력 데이터·목록·종류별 보유(`AbilityDefinition`, `AbilityCatalog`) | Combat |
| `Server/src/OpenRTS.Server/Net` | `GameServer`: 접속·세션·명령 수신·전송(팀별 필터·델타·대역폭 측정) | Simulation, Shared |
| `Server/src/OpenRTS.Server/Simulation` | `World`(상태·Step·명령), `Formation`, `Scenarios`, `SpatialGrid` | Shared |
| `.../Simulation/Navigation` | `NavGrid`: clearance 격자, A\*, 경로 단순화, 선분-벽 판정 | Shared.Map |
| `.../Simulation/Avoidance` | `OrcaSolver`: RVO2 에서 가져온 ORCA (Apache-2.0, 머리말에 변경 기록) | 없음 |
| `.../Simulation/Combat` | `CombatSystem`(엔진 층), `BattleAi`(행동 층), `UnitOrder` | World |
| `.../Simulation/Economy` | `EconomySystem`: 자원·채집·건설·생산·기지 배치 | World |
| `.../Simulation/Abilities` | `AbilitySystem`: 시전·전달·상태 효과·AI 사용, 스크립트 등록부 | World |
| `Server/src/OpenRTS.Server/GameLoop.cs`, `Program.cs` | 고정 틱 루프, 명령줄 옵션 | — |
| `Client/Assets/Scripts/Net` | `GameClient`: 접속, 지연·손실 흉내, 메시지 분배, 명령 전송 | Shared |
| `Client/Assets/Scripts/View` | 그리기: 유닛·건물, 전투 연출, 안개, 유령, 벽 | Net |
| `Client/Assets/Scripts/Controls` | 선택·명령 입력, 명령 패널 | Net, View |
| `Client/Assets/Scripts/Debugging` | HUD, F2 설정 패널, 명령줄 자동 확인 4종 | 전부 |
| `Client/Assets/Scripts/ClientBootstrap.cs` | 씬과 무관하게 모든 객체를 코드로 만들고 연결 | 전부 |

규칙: `Simulation` 은 `Net` 을 모릅니다(네트워크 없이 단위 테스트). Shared 는 `UnityEngine` 도 서버 라이브러리도 참조하지 않습니다(`noEngineReferences`).

## 3. 서버 틱

`GameLoop` 가 30Hz 로 `GameServer.Tick(tick)` 을 부릅니다. 밀린 틱은 한 번에 최대 5개까지만 따라잡고 나머지는 버립니다. LiteNetLib 이벤트는 `PollEvents` 안에서만 발생하므로 **모든 처리가 한 스레드**입니다(잠금 없음).

### 3.1 `GameServer.Tick`

```text
1. PollEvents          접속·해제, 메시지 수신 → 명령은 곧바로 World.Command / ApplySettings
2. World.Step(dt, tick)
3. 전투 이벤트 모으기   World.Combat.Events (+ 이벤트 위치) → 대기 목록
4. tick % 2 == 0        스냅샷 전송(팀별) + 전투 이벤트 전송(팀별 필터)       15Hz
5. tick % 15 == 0       지표(SimStats) 전송 (Fog of War 면 팀별로 가림)         2Hz
   (스냅샷 주기에 능력 이벤트도 팀별 필터로 전송)
6. tick % 6 == 0        플레이어별 상태(PlayerState: 자원·대기열·능력 재사용 대기) 5Hz
```

### 3.2 `World.Step` 순서

판단을 이동보다 먼저 둡니다(참고 서버는 반대여서 명령이 1틱 늦었음).

```text
EnsureNavigation       건물이 바뀌었으면 길찾기 격자·ORCA 장애물 재구성
UpdateBots             중립 봇 왕복 (충돌 확인 시나리오)
Combat.BeginTick       전투 유닛 공간 격자
Ai.Update              행동 층: 전투 봇 명령 결정 (0.25초마다, 유닛별 시점 분산)
Combat.UpdateUnits     엔진 층: 대상 확인·획득·리쉬·추적·선딜·타격/발사
EconomySystem.Update   채집·건설·생산
AbilitySystem.Update   재사용 대기·상태 효과 시간, 시전(접근·선딜·발동·정신 집중), 지연 범위
UpdateWaypoints        보이는 다음 경유점으로 건너뛰기
선호 속도 + ORCA       유닛·벽 회피 (정적 유닛 제외, 돌진 중이면 회피 없이 직진), 유닛별 속도(둔화·기절)
적분                   속도 제한, 위치 갱신, 맵 경계
Separate × 2           남은 겹침 풀기 (회피 몫 비율로)
PushOutOfWalls         벽·건물 발판에서 밀어내기 (최종 안전장치)
UpdateArrival          도착·막힘 판정, 막힘 재경로 (A* 예산 안에서)
Combat.UpdateProjectiles  이번 틱 위치로 명중 판정
AbilitySystem.UpdateAfterMovement  돌진 도착, 능력 투사체 이동·명중
사망 제거              체력 0 유닛 제거 (정적이면 길찾기 재구성 예약)
UpdateBattle           Battle 승패, 재시작 타이머
UpdateVision           팀별 시야 (전송 필터·명령 검증이 쓴다)
```

각 단계 시간은 `World.LastProfile`(`StepProfile`)에 남습니다. 서버 1틱이 튀면 이걸로 원인을 찾습니다(`BattleReport` 가 최악 틱의 단계별 시간을 출력).

## 4. 데이터 흐름

### 4.1 명령 (C→S)

```text
RtsInput/CommandPanel ─▶ GameClient.SendCommand ─(지연 흉내)─▶ UnitCommand (reliable ordered)
─▶ GameServer: 세션의 PlayerId 확인 ─▶ World.Command(owner, cmd)
   ├ owner 소유 유닛만 고름 (남의 ID 는 무시)
   ├ Train / Build → EconomySystem (비용·자리·시야 검증)
   ├ 정적 유닛 제외, 하던 건설 취소(환불)·공격 취소
   ├ Stop / Attack(보이는 적만) / Gather
   └ Move / AttackMove → Formation.Assign → 유닛별 A*
```

### 4.2 상태 (S→C)

```text
World.CaptureState ─▶ 팀마다: CanSee 로 거르고, 안 보이는 공격 대상 ID 가림 ─▶ ID 순 정렬
─▶ 피어마다: 확인 응답한 틱이 그 팀 히스토리에 있으면 Diff(델타), 없으면 전체
─▶ Plan(패킷 크기에 맞춰 조각) ─▶ unreliable 전송 ─▶ 팀 히스토리에 저장
클라이언트: SnapshotReceiver(조각 조립 → baseline 에 적용) ─▶ SnapshotAck 전송
          ─▶ SnapshotInterpolator(위치) + GameClient.Latest(체력·종류·플래그)
```

### 4.3 공개 범위

| 정보 | 받는 사람 |
| --- | --- |
| 내 팀·중립 유닛 | 항상 |
| 적 유닛 | 그 팀 시야(벽 가림 포함) 안일 때만 (Fog of War 켬) |
| 전투 이벤트 | 공격자나 대상 위치가 보일 때만, 안 보이는 쪽 ID 는 0 |
| 자원·생산 대기열·능력 재사용 대기 | 그 플레이어만 |
| 능력 이벤트 | 시전자나 지점이 보일 때만, 안 보이는 ID 는 0 |
| 지표 | 상대 팀 생존 수·사망·투사체 수는 `Unknown`. 겹침 지표는 디버그용으로 전체 기준 |
| 설정 | 모두 |

새 메시지를 추가할 때 이 표에서 어디에 해당하는지 먼저 정합니다. 시야 밖 정보가 새면 맵핵이 됩니다.

## 5. 설계 결정 요약

| 결정 | 이유 | 문서 |
| --- | --- | --- |
| 서버 권위 + 스냅샷 보간 | RVO2 로 속도가 매 틱 바뀌어 경로 전송 방식이 안 맞음, 맵핵 방지 | 루트 README 4.1·4.2 |
| 30Hz 틱 / 15Hz 스냅샷 | 반응성과 대역폭의 절충. 보간 지연 약 130ms | protocol.md |
| RVO2(ORCA) + 분리 보정 + 벽 밀어내기 | ORCA 만으로는 밀집 시 겹침이 남음 | simulation.md |
| 균일 격자 + A\* (내비메시 아님) | 벽이 축 정렬 사각형뿐, 반지름별 통행을 clearance 로 쉽게 표현 | simulation.md |
| 필드 묶음 델타 | 멈춘 유닛·체력만 바뀐 유닛을 싸게 | protocol.md |
| 시야를 Shared 에서 계산 | 클라이언트가 시야 데이터 없이 같은 안개를 그림 | simulation.md |
| 엔진 층 / 행동 층 분리 | 참고 서버 구조. AI 는 명령만 바꾸고 실행은 공용 | simulation.md |
| 정적 유닛 = 길찾기 벽 | 건물을 ORCA 이웃(같은 반지름)으로 다룰 수 없음 | simulation.md |
