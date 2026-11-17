# Unity 클라이언트

Unity 6000.6.0f1, Built-in 렌더 파이프라인, 옛 Input Manager(`activeInputHandler 0`). 스크립트는 `Client/Assets/Scripts`, 어셈블리 `OpenRTS.Client`(→ `OpenRTS.Shared` 참조).

클라이언트는 **판정을 하지 않습니다.** 명령을 보내고, 서버 상태를 보간해 그립니다. 판정이 필요해 보이는 것(건설 미리보기 색 등)은 미리보기일 뿐이고, 서버가 다시 검증합니다.

## 1. 부팅

`ClientBootstrap.Boot` 가 `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` 로 **어떤 씬이든** 실행되어 카메라·조명·바닥과 아래 컴포넌트를 하나의 `OpenRTS` 게임오브젝트에 붙이고 서로 연결합니다. 씬에는 아무것도 배치하지 않습니다(이미 `GameClient` 가 있으면 건너뜀).

| 컴포넌트 | 폴더 | 역할 |
| --- | --- | --- |
| `GameClient` | Net | 접속, 지연·손실 흉내, 메시지 분배, 명령 전송 |
| `MapView` | View | 설정의 맵 종류로 벽 상자 생성 |
| `UnitViewManager` | View | 보간 위치로 유닛·건물·광맥 그리기 |
| `CombatEffects` | View | 투사체·타격·사망 연출 |
| `AbilityEffects` | View | 능력 연출 (투사체·범위 예고·폭발·적중) |
| `CastPrediction` | Controls | 시전 즉시 표시: 자세·시전 막대·조준 표시·재사용 대기 예측, 확인·거부 처리 (abilities.md 9.1) |
| `FogOfWarView` | View | 안개 막 |
| `GhostView` | View | 안개 속 마지막 위치 표시 |
| `RtsInput` | Controls | 선택·명령·건설 배치·카메라 |
| `CommandPanel` | Controls | 왼쪽 아래 자원·버튼·대기열 |
| `NetDebugHud` | Debugging | 왼쪽 위 상태 표시, 지연·손실 조절 키 |
| `SimSettingsPanel` | Debugging | F2 서버 설정 창 |
| `*AutoTest` | Debugging | 명령줄로 켤 때만 (testing.md) |

## 2. `GameClient`

- 명령줄: `-host`, `-port`, `-latency <왕복 ms>`, `-loss <%>`.
- **지연·손실 흉내**: LiteNetLib 내장 시뮬레이션은 DEBUG 빌드 전용이라 직접 구현했습니다. 송신·수신을 각각 왕복 지연의 절반만큼 큐에 잡아 두고, 수신한 unreliable 패킷을 확률로 버립니다. HUD 에서 `[` `]` / `-` `=` 로 실행 중에 바꿉니다.
- 수신 분배(`Handle`):

  | 메시지 | 처리 |
  | --- | --- |
  | ServerWelcome | `PlayerId`, 최소 보간 지연 = 2 / 스냅샷 주기 |
  | Snapshot | `SnapshotReceiver` 로 복원 → `SnapshotAck` 전송 → 보간기에 추가, 최신이면 `Latest`·`LatestRaw`·`MyUnits`·`MyTeam` 갱신 |
  | SimSettings | `Settings`, `SettingsChanged` 이벤트 |
  | SimStats | `Stats` |
  | CombatEvents | 대기 목록 → 렌더 시각이 되면 `DueCombatEvents` 로 |
  | AbilityEvents | 같은 방식으로 `DueAbilityEvents`. 단 내 유닛의 발사·예고는 받은 즉시 (`-delayed-own-events` 로 끔, abilities.md 9.4) |
  | PlayerState | `Ore`, `Queues`, `Cooldowns`(그 사이 로컬로 줄임) |

- 명령: `SendCommand(type, unitIds, target, targetUnit[, kind])`, 능력은 `SendCast(unitIds, ability, point, targetUnit)`. 지금 추정한 서버 틱(`ServerClock / TickDelta`)을 `ClientTick` 으로 싣고, 255개씩 나눠 보냅니다.
- 상태 조회 도우미: `IsMine(id)`, `IsEnemy(id)`(중립·같은 팀 제외).

## 3. 보간 (`SnapshotInterpolator`, Shared)

- 스냅샷을 서버 틱 시각(`tick × TickDelta`)으로 버퍼에 쌓고, **렌더 시각 = 추정 서버 시계 − 보간 지연**에서 앞뒤 두 스냅샷 사이를 선형 보간합니다.
- 서버 시계 추정: 스냅샷이 올 때마다 오차의 10% 씩 따라감(`ClockCorrectionGain`). 1초 넘게 어긋나면 바로 맞춤.
- **적응형 지연**: 스냅샷이 올 때 "이번에 필요했던 지연"을 기록하고, 최근 3초 중 최악 + 1틱을 목표로 삼아 빨리 늘리고(0.25/s) 천천히 줄입니다(0.05/s). 최소 2/스냅샷 주기(≈133ms), 최대 0.5초.
- 끊기면 마지막 속도로 최대 0.25초 외삽. 늦게 온(순서가 뒤집힌) 스냅샷은 버림.
- 위치만 보간합니다. 체력·종류·플래그·공격 대상은 `GameClient.Latest`(최신 스냅샷) 값을 씁니다.
- 새로 나타난 유닛은 이전 스냅샷에 없으므로 보간 없이 그 자리에 나타납니다(시야에 다시 들어온 적이 미끄러지지 않음).

## 4. 그리기

### `UnitViewManager`

- 좌표: 서버 (x, y) → Unity (x, 0, y) (`ToWorld`).
- 움직이는 유닛: 루트 크기 = 지름(2r), 몸은 캡슐. 근접은 짧은 네모 코, 원거리는 긴 총신, 일꾼은 작은 몸·짧은 도구(자원을 들면 등 위 수정). 공격 대상이 있으면 그쪽, 아니면 진행 방향을 봅니다.
- 정적 유닛: 루트 크기 1, 몸 상자를 발판 크기로. 건설 중이면 진행도만큼 낮게, 광맥은 남은 양만큼 작게.
- 색은 **내 팀 기준**: 내 것 초록, 같은 팀 파랑, 적 빨강, 중립 회색, 광맥 하늘색. 맞으면 0.12초 하얗게(`Flash`), 공격 선딜 중이면 코가 노랗게, 능력 시전 중이면 보라.
- 상태: 기절 = 머리 위 도는 노란 상자, 보호막 = 하늘색 고리, 둔화 = 몸이 푸르스름. 영웅은 머리 위 금색 관.
- 체력 막대: 전투 팀 유닛만, 항상 카메라를 향함.
- ID 가 재사용되거나 소유자·팀·종류가 바뀌면 뷰를 새로 만듭니다.
- F1: 보간 전 최신 스냅샷 위치(노란 구).

### `CombatEffects`

`DueCombatEvents` 를 꺼내 근접 타격은 대상 번쩍, 발사는 공격자 → 대상(따라감) 구, 사망은 그 자리에 1.2초 원판. 사망은 `GhostView.NotifyDeath` 에도 알립니다. 공격자 ID 가 0(안 보임)이면 발사 연출은 건너뜁니다.

### `FogOfWarView`

- 0.1초마다 `VisionGrid` 를 내 팀 유닛의 보간 위치와 맵 벽으로 계산합니다.
- 결과를 100×100 텍스처에 씁니다: 보임은 알파 0, 가 봄 130, 처음 215.
- 이 텍스처를 바닥 바로 위 쿼드(`Sprites/Default` 셰이더 — 빌드에 항상 포함)에 씌웁니다.
- 서버 설정의 Fog of War 가 꺼져 있거나 내 팀이 없으면 숨깁니다.
- 막은 표시일 뿐입니다. 적은 서버가 아예 보내지 않습니다.

### `GhostView`

보이던 적이 스냅샷에서 빠지면 0.35초 기다렸다가(그 사이 사망 이벤트가 오면 취소) 마지막 위치에 어두운 원판을 남깁니다. 그 자리가 다시 보이면(유닛이 거기 없다는 뜻) 지웁니다. 같은 ID 가 다시 보이면 즉시 지웁니다.

## 5. 입력 (`RtsInput`)

| 입력 | 동작 |
| --- | --- |
| 좌클릭 / 드래그 | 내 유닛 선택 / 박스 선택(움직이는 유닛만, 건물은 클릭). Shift 추가·토글 |
| Ctrl+A | 내 것 전체 |
| Ctrl+1~9 / 1~9 | 부대 지정 / 선택 (Shift: 추가) |
| 우클릭 | 적 → Attack, 광맥·짓다 만 내 건물 → Gather, 땅 → Move. Ctrl → AttackMove |
| H | Stop |
| B | 병영 배치 모드: 0.5 칸 맞춤 미리보기(초록·빨강), 좌클릭 놓기(Shift 유지), 우클릭·Esc 취소 |
| Z / X / C | 선택한 본진 → 일꾼(Z)·영웅(C), 병영 → 근접(Z) / 원거리(X) |
| Q W E R D F | 선택한 유닛의 능력 (abilities.md 8절). 대상이 있으면 대상 고르기 모드 |
| 화살표, 휠 | 카메라 (WASD 는 능력 키와 겹쳐 쓰지 않음) |

- 처음 내 유닛이 보이면 전부 선택합니다.
- 디버그 창·명령 패널 위의 클릭은 선택으로 치지 않습니다: 각 창이 `Area`(GUI 좌표)를 공개하고, `IsOverDebugWindow` 가 이를 확인합니다. **새 GUI 창을 만들면 여기에 추가**하세요.
- 클릭으로 집기(`PickUnit`): 화면 거리 기준. 정적 유닛은 발판 크기로 집습니다.
- 명령을 내리면 바로 표시가 납니다(초록 이동, 빨강 공격, 하늘색 작업). 서버 응답 전 피드백입니다.
- 선택(`UnitSelection`)은 매 프레임 내 것이 아니게 된(사라진) ID 를 정리합니다.

## 6. 화면 UI

모두 IMGUI(`OnGUI`)로 만든 임시 UI입니다. 정식 UI(uGUI/UI Toolkit)는 아직 없습니다.

- `NetDebugHud`(왼쪽 위): 상태, 플레이어·팀, RTT·명령 지연, 보간 지연, 스냅샷·델타 수, 대역폭, 조작 안내. 아래쪽 가운데에 팀별 생존 수와 승패(모르는 값은 `?`).
- `SimSettingsPanel`(F2, 오른쪽): 서버 설정 편집 → Apply 하면 서버에 요청, 서버가 적용한 값이 돌아와 덮어씀. 프리셋, 서버 지표.
- `CommandPanel`(왼쪽 아래): 자원, 선택 구성, 선택에 맞는 버튼(자원 부족이면 비활성), 선택한 건물의 대기열.

## 7. 빌드

- 메뉴 **OpenRTS → Build Windows Player** 또는 배치 모드:

  ```bash
  Unity.exe -batchmode -quit -projectPath Client -executeMethod OpenRTS.Client.Editor.BuildScript.BuildWindows -logFile build.log
  ```

  출력 `Client/Builds/Windows/OpenRTS.exe`(백그라운드 실행 허용, 1280×720 창). **에디터가 프로젝트를 열고 있으면 배치 빌드는 실패**합니다.
- 메뉴 **OpenRTS → Create Main Scene**: 빈 `Main.unity` 생성(빌드 설정 등록).
