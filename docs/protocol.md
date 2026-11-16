# 프로토콜

현재 버전: **9** (`SimConstants.ProtocolVersion`). 전송은 LiteNetLib 2.1.5 UDP, 접속 키 `"OpenRTS"`(보안 수단 아님), 기본 포트 7777.

## 1. 공통 규칙

- 모든 메시지의 **첫 바이트가 `MessageType`** 입니다. 이후는 리틀 엔디언 고정 크기 필드.
- 직렬화는 `PacketWriter`(버퍼 재사용, `Reset` 후 다시 씀) / `PacketReader`(struct, `ref` 로 넘김). 남은 바이트보다 많이 읽으면 `PacketFormatException` → 서버는 그 피어를 끊고, 클라이언트는 경고 후 무시.
- 실수형은 IEEE 754 float, 좌표는 스냅샷에서만 16비트로 양자화합니다.
- **형식이나 값의 의미를 바꾸면 `ProtocolVersion` 을 올립니다.** 버전이 다르면 서버가 `ClientHello` 단계에서 끊습니다. 하위 호환은 두지 않습니다(서버·클라이언트를 같이 배포).
- 한 unreliable 패킷은 **1023바이트 이하**로 맞춥니다(LiteNetLib 의 MTU 탐색 전 한도). 서버는 피어마다 `GetMaxSinglePacketSize(Unreliable)` 로 실제 한도를 씁니다.

## 2. 메시지 목록

| 값 | 이름 | 방향 | 전송 | 주기 | 정의 |
| --- | --- | --- | --- | --- | --- |
| 1 | ClientHello | C→S | reliable ordered | 접속 직후 1회 | `ClientHello` |
| 2 | ServerWelcome | S→C | reliable ordered | 입장 시 | `ServerWelcome` |
| 3 | UnitCommand | C→S | reliable ordered | 명령마다 | `UnitCommand` |
| 4 | Snapshot | S→C | unreliable | 15Hz (2틱마다) | `SnapshotMessage` |
| 5 | SimSettings | S→C | reliable ordered | 입장 시·변경 시 | `SimSettings` |
| 6 | SetSimSettings | C→S | reliable ordered | 디버그 패널 | `SimSettings` (같은 형식) |
| 7 | ResetScenario | C→S | reliable ordered | 디버그 패널 | 타입 바이트만 |
| 8 | SimStats | S→C | unreliable | 2Hz | `SimStats` |
| 9 | SnapshotAck | C→S | unreliable | 스냅샷 복원마다 | `SnapshotAck` |
| 10 | CombatEvents | S→C | unreliable | 스냅샷과 같이 | `CombatEventsMessage` |
| 11 | PlayerState | S→C | unreliable | 5Hz | `PlayerStateMessage` |
| 12 | AbilityEvents | S→C | unreliable | 스냅샷과 같이 | `AbilityEventsMessage` |

6·7 은 서버가 `--no-debug-commands` 로 떠 있으면 무시합니다.

## 3. 접속 순서

```text
C: Connect(key) ─▶ S: AcceptIfKey
C: ClientHello { int ProtocolVersion }
S: 버전 다르면 Disconnect
   빈 플레이어 번호(1~254) 배정, World.AddPlayer → 팀 배정·유닛 생성
S: ServerWelcome { byte PlayerId, uint ServerTick, int TickRate, int SnapshotRate }
S: SimSettings
… 이후 스냅샷·지표·경제 상태가 주기적으로 옴. 클라이언트는 스냅샷마다 SnapshotAck.
접속 해제: World.RemovePlayer → 그 플레이어 유닛·건물 제거
```

## 4. 형식

### UnitCommand

```text
byte  type = 3
byte  CommandType      0 Move, 1 AttackMove, 2 Attack, 3 Stop, 4 Gather, 5 Build, 6 Train, 7 Cast
float target.x, target.y   (Move/AttackMove/Build 지점, Gather 의 비일꾼 이동 지점)
u16   targetUnit       (Attack 대상, Gather 의 광맥·건설 중 건물, Cast 의 유닛 대상)
u32   clientTick       (보낼 때 클라이언트가 추정한 서버 틱. 서버가 명령 지연을 재서 능력 선딜 보상에 씀 — abilities.md 9.2)
byte  UnitKind         (Build·Train 의 종류)
byte  AbilityId        (Cast 의 능력)
byte  count (≤255)
u16 × count  unitIds
```

모르는 `CommandType` 은 `PacketFormatException`. 유닛이 255개를 넘으면 `GameClient.SendCommand` 가 나눠 보냅니다. 서버는 **소유권·비용·자리·시야를 모두 다시 확인**합니다(클라이언트 판단은 미리보기일 뿐).

### Snapshot

```text
헤더 (13바이트)
byte type = 4
u32  tick
u32  baseTick       0 = 전체 스냅샷, 그 밖 = 이 틱 대비 델타
byte part, partCount    (조각 번호 / 전체 조각 수, partCount ≤ 255)
u16  count
항목 × count
u16  id
byte fields         SnapshotFields 비트: 1 Position, 2 Info, 4 Health, 8 Action, 0x80 Removed
[Position] i16 x, i16 y          4
[Info]     byte owner, team, kind 3
[Health]   byte health (1~255)    1
[Action]   u16 attackTarget, byte flags   3
```

- 좌표 양자화: `q = round(v / MapHalfSize × 32767)`, 범위 ±50 → 해상도 약 0.0015. `short.MinValue` 는 쓰지 않음.
- 체력: `ceil(비율 × 255)`, 살아 있으면 최소 1. 광맥은 남은 양 비율, 건설 중 건물은 진행도만큼의 체력 비율.
- `UnitFlags`: 1 WindingUp(선딜 중), 2 Carrying(자원 운반), 4 UnderConstruction, 8 Producing, 16 Stunned, 32 Casting(능력 선딜·정신 집중·돌진), 64 Slowed, 128 Shielded.
- 항목 크기: 전체 14, 위치만 7, 체력만 4, 행동만 6, 제거 3.
- 조각 나누기: `SnapshotMessage.Plan(entries, maxPacketSize, parts)` 가 바이트 기준으로 자릅니다. **항목이 없어도 빈 조각 하나는 보냅니다**(클라이언트가 그 틱을 확인 응답해야 다음 델타 기준이 생김).

#### 델타 규칙

| 상황 | 항목 |
| --- | --- |
| baseline 에 없던 유닛 | 모든 필드 (`All`) |
| 값이 바뀐 유닛 | 바뀐 묶음만 |
| 그대로인 유닛 | 없음 |
| baseline 에 있었는데 사라진 유닛 (죽음, 시야 밖) | `Removed` |

- 서버: 팀마다 "그 팀에게 보낸 상태"를 `SnapshotHistory`(64개 ≈ 4.3초)에 보관합니다. 피어의 마지막 ack 틱이 히스토리에 있으면 그걸 baseline 으로 씁니다. 같은 (팀, baseline) 조합의 델타는 한 번만 계산합니다.
- 클라이언트: `SnapshotReceiver` 가 조각을 다 모은 뒤, 델타면 자기 히스토리의 baseline 에 `SnapshotDelta.Apply` 합니다. baseline 이 없으면 버리고 `MissingBaseline` 을 셉니다(서버는 ack 를 못 받았으니 계속 옛 baseline 이나 전체를 보내므로 곧 회복).
- 비교는 **양자화된 값**으로 합니다. 그래서 서버가 본 "바뀜"과 클라이언트가 복원한 값이 정확히 같습니다(통합 테스트가 확인).
- 틱 0 은 baseline 이 될 수 없습니다(`NoBaseline` 과 겹침).

#### 조각 조립

`SnapshotAssembler`: 한 틱의 조각이 모두 모여야 완성. 더 새 틱의 조각이 오면 덜 모인 이전 틱은 버립니다(`IncompleteDropped`). 이미 완성한 틱 이하의 조각, partCount·baseTick 이 다른 조각은 무시.

### SnapshotAck

`byte type=9, u32 tick` — 클라이언트가 완성·복원한 틱. 서버는 더 큰 값만 받아들이고, 현재 틱보다 큰 값은 무시.

### CombatEvents

```text
byte type = 10
u16  count (패킷당 ≤110)
반복: u32 tick, byte type (0 MeleeHit, 1 ProjectileLaunch, 2 Death), u16 attacker, u16 target
```

연출용입니다. 잃어도 체력·사망은 스냅샷이 권위. 클라이언트는 `tick × TickDelta ≤ 렌더 시각`이 될 때 재생합니다(보간 지연과 맞춤). 안 보이는 쪽 ID 는 0.

### PlayerState

```text
byte type = 11
i32  ore
byte queueCount
반복: u16 buildingId, byte itemCount, byte × itemCount (UnitKind), byte progress(0~255, 첫 항목)
byte cooldownCount (≤150)
반복: u16 unitId, byte AbilityId, u16 remaining (0.1초 단위, 올림)
```

그 플레이어에게만 보냅니다. 대기열이 빈 건물, 준비된 능력은 없음.

### AbilityEvents

```text
byte type = 12
u16  count (패킷당 ≤70)
반복: u32 tick, byte AbilityEventType, u16 caster, byte AbilityId, u16 target, i16 x, i16 y (좌표는 스냅샷과 같은 양자화)
```

`AbilityEventType`: 0 CastStart, 1 CastCancel(시전이 끊김 또는 **시전 명령 거부**), 2 ProjectileLaunch(지점 = 비행 끝), 3 ProjectileEnd(지점 = 끝난 위치), 4 AreaTelegraph, 5 AreaHit, 6 DashStart(지점 = 도착), 7 ChannelStart, 8 Hit(target = 맞은 유닛).
팀마다 시전자 위치나 지점이 보일 때만 보내고, 안 보이는 시전자·대상 ID 는 0. 렌더 시각에 재생합니다(CombatEvents 와 같음).

### SimSettings (5, 6)

```text
byte type, u16 botCount, byte scenario, byte map, byte playerUnits,
float unitRadius, float unitSpeed, byte avoidance, float neighborDist, byte maxNeighbors,
float timeHorizon, byte stopWhenBlocked, byte separation, byte repathWhenBlocked,
byte deltaSnapshots, byte fogOfWar, byte lagCompensation
```

서버는 받은 값을 반드시 `Clamped()` 로 범위를 자른 뒤 적용하고, 적용한 값을 모두에게 다시 보냅니다.

### SimStats

```text
byte type=8, u16 unitCount, u16 overlapPairs, float minSeparationRatio, u16 movingUnits,
u16 blockedStops, float stepMs, u16 repaths, u16 wallPenetrations, float snapshotBytesPerSec,
u16 team1Alive, u16 team2Alive, u16 deaths, u16 projectiles, byte winner, float commandDelayMs,
float deltaRatio
```

`ushort.MaxValue`(`SimStats.Unknown`) 는 "안개 때문에 모름"입니다.

## 5. 대역폭 감각

| 상황 | 대략 |
| --- | --- |
| 움직이는 유닛 1개 | 7바이트 × 15Hz ≈ 105 B/s |
| 멈춘 유닛 | 0 (바뀔 때만) |
| 움직이는 유닛 300 | 약 30 KB/s |

측정은 `BandwidthReport` 테스트와 HUD 의 `snapshot KB/s`. 움직이는 유닛을 더 줄이려면 위치 변화량 비트 패킹이 필요합니다(남은 과제).

## 6. 메시지 추가 체크리스트

1. `MessageType` 에 값 추가(기존 값 재사용 금지), 주석에 방향·전송 방식·주기.
2. `Messages.cs` 에 `Write`/`Read`. 길이가 가변이면 상한을 두고 패킷 1023바이트를 넘지 않게.
3. `ProtocolVersion` 올리기.
4. 공개 범위 정하기(architecture.md 4.3). 팀별·플레이어별이면 `SendToJoined` 대신 피어 루프.
5. `MessageTests` 에 왕복·잘린 패킷 테스트, 필요하면 `GameServerIntegrationTests` 의 `TestClient` 에 수신 처리.
6. `GameClient.Handle` 에 분기.
7. 이 문서 표 갱신.
