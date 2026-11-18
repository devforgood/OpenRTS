# **OpenRTS와 LoL 동기화 기술 비교 및 누락 항목 분석**

이번 분석에서는 단순히 서버와 클라이언트의 위치를 동기화하는 수준이 아니라, **League of Legends(LoL)의 네트워크 동기화 구조를 기준으로 OpenRTS가 보완해야 할 기술**을 살펴보겠습니다.

먼저 중요한 차이가 있습니다.

- **LoL**: 서버 권위(Server Authoritative)를 기반으로 게임 시뮬레이션, 입력 처리, 지연 및 패킷 손실에 대응하는 구조가 중요합니다.
- **RTS**: 다수 유닛의 이동과 전투를 처리하므로 입력 동기화, 결정론적 시뮬레이션, 상태 동기화의 선택이 중요합니다.
- **OpenRTS**: 실제 구현이 서버 권위 모델이라면 LoL의 기술을 그대로 가져오기보다 게임 특성에 맞게 적용해야 합니다.

다만 현재 GitHub에서 지정하신 OpenRTS 저장소의 `docs` 페이지를 직접 열어 문서와 소스 파일을 확보하는 데 실패했습니다. 따라서 아래 내용은 OpenRTS의 구현 여부를 확인했다고 주장하는 코드 리뷰가 아니라, LoL의 기술 기준으로 만든 누락 항목 및 검증 목록입니다. 확인되지 않은 기능을 OpenRTS에 없다고 단정하지 않겠습니다.

## **1. LoL에서 주목해야 할 핵심 기술**

Riot Games가 공개한 공식 기술 문서 중 특히 중요한 것은 2017\~2018년의 *Determinism in League of Legends* 시리즈입니다.

[Chronobreak | Why Did Riot Implement It? | EarlyGame](https://images.openai.com/static-rsc-4/9K75TyM3dz2WndvTq04visyxpllTuxnljpBvaVmeGxy0btfJvBND1BSXBoJ1ughK8znl-a9mUXtFTWaFCVmd4_dpvsAUW29CmbsHJ0uB3S3G-KW5-gSu3nIUQ-RF8xS40hl0Bavajoaq1I2s8atCS8bJVpbJNDfhwOn6P0TzW5w?purpose=inline)

**① Deterministic Simulation**

게임 서버가 동일한 입력을 재생했을 때 동일한 게임 결과를 만들어 내는 기술입니다.

목적은 단순 네트워크 동기화가 아니라 게임 재현, 테스트, 장애 복구입니다.

[Riot – Determinism Introduction](https://www.riotgames.com/en/news/determinism-league-legends-introduction?utm_source=chatgpt.com)

[Determinism in League of Legends: Unified Clock | Riot Games Technology](https://images.openai.com/static-rsc-4/jMA1me-LXjmixBWdjHIODFu8X-cfGCQ45iCCmFNuVhAumvhJGSrvKv-kOt5tAHfKRloAbeuHG45NkIEY3_DE7AY_eiGq7KRMEwXpsL1nFSCwA54tHYwWVJuG58W8fVfVyvaokVfuQaRRz_Iqq4ZK64AkTBktarrXZl5fRW8mReg?purpose=inline)

**② Unified Clock**

서버와 클라이언트의 시간 차이를 측정하고 보정하는 통합 시계 시스템입니다.

밀리초 단위 시간 동기화, 네트워크 지연 측정, 시간 드리프트 보정이 핵심입니다.

[Riot – Unified Clock](https://www.riotgames.com/en/news/determinism-league-legends-unified-clock?utm_source=chatgpt.com)

[저지 당한 '저지 불가'...? 대회 최초 '크로노 브레이크' 발동](https://images.openai.com/static-rsc-4/RPlE_asohJplsDw5ZPBzuoL5KogPBgLWQtEevd6T6KAQ5QuV-rO2EITJs-OO32Rv0WtfKZ01oqPepUeQY3w35hMXg1A91WbWU-fRx45mdEnrCmqL65fEcd8GUXG_iQnsWjnY8drKGnVMmDU61yPGw1C5ojX1zmsghGJ_zeZ3Czw?purpose=inline)

**③ Server Network Recording (SNR)**

서버에 들어온 입력과 게임 설정 등을 기록해 과거 게임을 다시 실행하는 시스템입니다.

Chronobreak의 핵심 기반 기술입니다.

[Riot – Determinism Implementation](https://www.riotgames.com/en/news/determinism-league-legends-implementation?utm_source=chatgpt.com)

[Engineering Wizard with a Gun - State Divergences – Stray Pixels](https://images.openai.com/static-rsc-4/bupQsRpAsWJbTJqqiquhDNZ1np3eX922imfgTR7E0tw0nR-RrRB3ehaDvdx-CoLM9ye8PHVyRljlLrd0DYOLBcBDHw9ZvuHtuLAmxAbOauq01j57WlzI5X-798_9Mu8ZCYZbZ1bZ4G8ecpEbFibcnE1P9WatWIv0OBmc8T5B-PQ?purpose=inline)

**④ Divergence Detection**

동일한 입력으로 실행한 두 게임의 상태를 비교하여 어느 시점부터 결과가 달라졌는지 추적합니다.

단순 Hash 비교를 넘어 계층적 게임 상태 기록을 활용합니다.

[Riot – Fixing Divergences](https://www.riotgames.com/en/news/determinism-league-legends-fixing-divergences?utm_source=chatgpt.com)

여기서 중요한 사실이 하나 있습니다.

**LoL은 전통적인 RTS의 P2P Deterministic Lockstep 방식으로 게임 전체를 동기화하는 게임이 아닙니다.**

Riot의 공식 문서에 따르면 LoL은 서버 게임 로직의 결정론성을 확보해 기록과 재생, 복구 및 테스트에 활용합니다. 서버가 고정 시간 간격으로만 실행되는 구조도 아닙니다. 실제로 목표 FPS에 맞춰 제한하는 가변 프레임 시간 방식을 유지했습니다.&#x20;

[image](https://www.google.com/s2/favicons?domain=https://www.riotgames.com\&sz=32)

Riot Games+1



이 차이를 OpenRTS 설계에 반영해야 합니다.

## **2. OpenRTS에서 추가로 검토해야 할 기술**

아래는 실제 코드 존재 여부를 확인한 결과가 아니라, OpenRTS 문서와 코드에서 반드시 확인해야 할 항목입니다.



우선순위 높음

**A. 시간 동기화 (Unified Clock)**



서버와 클라이언트가 서로 다른 시간을 기준으로 게임을 진행하면 다음과 같은 문제가 발생합니다.

- 이동 시작 및 종료 시점 불일치
- 공격 판정 시점 차이
- 스킬 쿨타임 불일치
- 투사체 도착 시점 불일치
- 지연 보상 계산 오류

LoL의 Unified Clock에서 가져와야 할 것은 단순한 서버 시간 전송이 아닙니다.

| 항목                 | 필요한 기능             |
| ------------------ | ------------------ |
| Clock Offset       | 서버와 클라이언트 시간 차이 계산 |
| RTT                | 왕복 지연 시간 측정        |
| Jitter             | 네트워크 지연 편차 측정      |
| Clock Drift        | 시간 오차 누적 보정        |
| Clock Smoothing    | 갑작스러운 시간 변경 방지     |
| Fixed Tick Mapping | 시간과 서버 Tick 연결     |

예를 들어 다음과 같은 구조를 권장합니다.

```
public struct ClockSync
{
    public long ClientSendTime;
    public long ServerReceiveTime;
    public long ServerSendTime;
    public long ClientReceiveTime;
}
```

NTP 방식으로 다음을 계산합니다.

\\[ RTT=(T_4-T_1)-(T_3-T_2) \\]

\\[ Offset=\frac{(T_2-T_1)+(T_3-T_4)}{2} \\]

단, 이 Offset은 네트워크 경로의 비대칭 지연 때문에 실제 시간 차이와 오차가 있을 수 있습니다.

따라서 여러 샘플 중 지연이 큰 이상치를 제거하고, 필터링하여 시간 오차를 보정해야 합니다.

**OpenRTS 검증 사항:** 서버 시간 동기화와 클라이언트 보정이 별도 시스템으로 구현되어 있는지 확인해야 합니다.



우선순위 높음

**B. Server Network Recording (입력 기록 및 재생)**



개인적으로 OpenRTS에서 가장 중요하게 추가하고 싶은 기술입니다.

단순히 패킷 로그를 저장하는 것과는 다릅니다.

LoL의 SNR은 게임 서버에 들어온 입력을 프레임별 순서대로 기록하고 동일한 입력을 다시 주입해 게임을 재현할 수 있도록 합니다.&#x20;

[image](https://www.google.com/s2/favicons?domain=https://www.riotgames.com\&sz=32)

Riot Games+1



권장 데이터 구조:

```
public struct RecordedInput
{
    public int Tick;
    public int PlayerId;
    public uint Sequence;
    public InputType Type;
    public byte[] Payload;
}
```

서버 처리 과정:

```
Network Receive
      |
      v
Input Validation
      |
      v
Input Recording
      |
      v
Tick Input Queue
      |
      v
World Simulation
      |
      v
State Update
```

재생 과정:

```
Recording File
      |
      v
Recorded Input Reader
      |
      v
Input Injection
      |
      v
World Simulation
      |
      v
State Verification
```

이 구조가 있으면 다음 기능을 만들 수 있습니다.

- 특정 게임의 완전한 재현
- 특정 Tick에서 발생한 버그 재현
- AI 에이전트와 플레이어 행동 테스트
- 패치 전후 게임 결과 비교
- 서버 장애 발생 시 복구 지점 재구성
- 게임 플레이 리플레이

주의할 점은 입력만 기록해서는 충분하지 않을 수 있다는 것입니다. 난수 Seed, 게임 설정, 외부 입력, 비동기 작업 결과 등 게임 결과에 영향을 주는 요소도 통제하거나 기록해야 합니다.



우선순위 높음

**C. Deterministic State Validation**



이것은 SNR과 함께 구현하는 것을 권장합니다.

같은 입력을 두 번 실행했는데 결과가 다르면 어느 부분에서 차이가 발생했는지 찾아야 합니다.

예를 들어:

```
Tick 100
  World Hash: AABBCC
  Unit Hash:  112233

Tick 101
  World Hash: AABBCC
  Unit Hash:  112233

Tick 102
  World Hash: AABBCC
  Unit Hash:  998877  <-- Divergence

Tick 103
  World Hash: FFFFFF
  Unit Hash:  998877
```

단순히 World 전체 Hash만 비교하면 문제 발생 위치를 알기 어렵습니다.

다음과 같이 계층적으로 비교하는 것이 좋습니다.

```
World
 ├── Player
 │    ├── Position
 │    ├── HP
 │    └── State
 │
 ├── Units
 │    ├── UnitId
 │    ├── Position
 │    ├── HP
 │    └── Action
 │
 ├── Projectiles
 │    ├── Position
 │    └── Velocity
 │
 └── Battle
      ├── Score
      └── Timer
```

특히 RTS에서는 유닛별 상태 비교가 중요합니다.

**구현 시 주의사항**

- unordered container 순회 순서 통일
- Random Seed 명시
- 비결정적 부동소수점 연산 통제
- 비동기 작업 결과의 적용 Tick 고정
- Entity ID 생성 규칙 통일
- 상태 Hash 직렬화 순서 통일

Riot도 전체 메모리 영역을 무조건 결정론적으로 만들려고 하지 않았습니다. 게임플레이에 중요한 상태를 중심으로 결정론성을 확보했습니다.&#x20;

[image](https://www.google.com/s2/favicons?domain=https://www.riotgames.com\&sz=32)

Riot Games+1





우선순위 중상

**D. 비동기 작업과 게임 Tick의 분리**



LoL의 결정론 구현에서 상당히 중요한 부분입니다.

Riot은 서버 게임 로직이 메인 스레드에서 효율적으로 실행되고, 다른 스레드의 작업이 게임 상태를 비결정적으로 변경하지 않도록 통제했습니다. 게임 데이터도 미리 로딩하여 게임 진행 중 I/O 경쟁을 줄였습니다.&#x20;

[image](https://www.google.com/s2/favicons?domain=https://www.riotgames.com\&sz=32)

Riot Games



OpenRTS에서도 다음과 같은 상황을 조심해야 합니다.

```
Tick 100
  Unit A 이동 처리
  DB 비동기 응답 대기
  Unit B 이동 처리

Tick 101
  DB 응답 도착
  Unit A 상태 변경
```

DB 응답 시점에 따라 게임 결과가 달라질 수 있습니다.

권장 구조:

```
             Worker Threads
                   |
          Async Result Queue
                   |
                   v
             World Thread
                   |
          Tick Boundary Apply
                   |
                   v
             World State
```

예를 들어 비동기 작업 결과를 곧바로 World에 반영하지 않고, 지정된 Tick 경계에서 처리하도록 합니다.

```
public struct WorldCommand
{
    public long ApplyTick;
    public int EntityId;
    public CommandType Type;
}
```

단, DB 응답처럼 외부 시스템의 결과를 무조건 같은 Tick에 적용할 수는 없습니다. 실제 결과가 도착한 시점에 명령을 큐에 넣고 적용 Tick을 확정한 뒤, 그 명령을 기록해야 재생 시 동일한 결과를 얻을 수 있습니다.

이 부분은 기존의 World 단일 스레드 원칙과도 잘 맞습니다.



우선순위 중상

**E. Snapshot Interpolation 및 Jitter Buffer**



LoL과 같은 MOBA에서 중요한 것은 상대방 유닛이 네트워크 패킷 도착 시점에 순간이동하지 않도록 하는 것입니다.

서버에서 20Hz로 상태를 보낸다고 가정해 보겠습니다.

```
Server Snapshot

Tick 100 ---- Tick 101 ---- Tick 102 ---- Tick 103
   |             |             |             |
   v             v             v             v
 Client receives packets with variable delays
```

패킷이 일정한 간격으로 도착한다는 보장이 없습니다.

따라서 클라이언트는 최근 Snapshot을 버퍼에 보관하고 현재 시간보다 조금 과거의 상태를 렌더링합니다.

\\[ T\_{render}=T\_{server}-InterpolationDelay \\]

두 Snapshot 사이에서는 다음과 같이 보간합니다.

\\[ P(t)=P_0+(P_1-P_0)\alpha \\]

\\[ \alpha=\frac{t-t_0}{t_1-t_0} \\]

권장 구현:

```
public class SnapshotBuffer
{
    public void AddSnapshot(Snapshot snapshot);
    public Snapshot GetBefore(double renderTime);
    public Snapshot GetAfter(double renderTime);
}
```

추가로 필요한 기술:

- Adaptive Interpolation Delay
- Jitter Measurement
- Packet Loss Handling
- Extrapolation 제한
- Snapshot Timestamp
- Entity Spawn/Despawn 처리

특히 Extrapolation은 오래 사용하면 안 됩니다. 이동 경로나 충돌 상황이 바뀌면 실제 서버 상태와 큰 차이가 생기기 때문입니다.

[image](https://www.google.com/s2/favicons?domain=https://mp-docs.dl.it.unity3d.com\&sz=32)

Unity Multiplayer+1



우선순위 중상

**F. Lag Compensation과 Server-side Rewind**



이전 대화에서 질문했던 지연 보상 기술입니다.

여기서 LoL과 FPS를 구분해야 합니다.

FPS의 히트스캔 공격은 과거의 적 위치를 기준으로 명중 여부를 판정하는 Server Rewind가 유용합니다.

하지만 LoL과 같은 MOBA는 일반적으로 모든 공격에 FPS식 Rewind를 적용하는 것이 적절하지 않습니다.

예를 들어 다음과 같은 구분이 필요합니다.

| 공격 종류    | 권장 판정 방식         |
| -------- | ---------------- |
| 일반 근접 공격 | 서버 권위 거리 판정      |
| 투사체 스킬   | 서버 권위 발사 및 이동    |
| 즉시 명중 스킬 | 서버 권위 시점 판정      |
| 범위형 스킬   | 서버 권위 시점 및 범위 판정 |
| 타겟 지정 스킬 | 서버 권위 타겟 유효성 검증  |

RTS에서도 이동 중인 유닛을 대상으로 스킬을 사용하는 경우가 많기 때문에 시간 보정 정책을 명확하게 해야 합니다.

예를 들어:

```
public struct PlayerInput
{
    public uint Sequence;
    public int ClientTick;
    public int TargetEntityId;
    public InputType Type;
}
```

서버에서는 ClientTick을 그대로 신뢰하지 않습니다.

```
Receive Input
     |
     v
Validate Sequence
     |
     v
Validate Client Time
     |
     v
Clamp Rewind Window
     |
     v
Validate Historical State
     |
     v
Execute Skill
```

중요한 점은 과거 상태를 참조하는 것과 과거 게임 상태를 실제로 되돌리는 것은 다르다는 것입니다.

일반적인 스킬 판정은 과거 상태를 읽기 전용으로 조회하고, 실제 결과는 현재 서버 상태에 적용하는 방식이 안전합니다.

최대 보정 시간, 쿨타임, 사거리, 타겟 생존 여부 등의 검증이 필요합니다.

[image](https://www.google.com/s2/favicons?domain=https://mp-docs.dl.it.unity3d.com\&sz=32)

Unity Multiplayer+1



우선순위 중간

**G. Packet Reliability와 State/Event 분리**



서버가 모든 패킷을 동일한 신뢰성으로 보내는 것도 개선 대상입니다.

예를 들어:

| 데이터      | 전달 방식                             | 이유               |
| -------- | --------------------------------- | ---------------- |
| 유닛 위치    | Unreliable                        | 최신 상태가 중요        |
| 유닛 회전    | Unreliable                        | 이전 데이터 불필요       |
| 이동 명령    | Reliable 또는 Sequence 기반 중복 전송     | 명령 누락 방지         |
| 유닛 생성    | Reliable                          | 생성 누락 방지         |
| 유닛 제거    | Reliable                          | 상태 불일치 방지        |
| 스킬 실행 결과 | Reliable                          | 게임 결과 보존         |
| HP 변화    | 설계에 따라 Reliable Event 또는 Snapshot | 최신 상태와 이벤트 의미 구분 |

특히 중요한 것은 State와 Event를 구분하는 것입니다.

- State: 유닛의 현재 위치, HP, 이동 상태
- Event: 유닛 생성, 스킬 발동, 사망, 점수 획득

State는 다음 Snapshot으로 복구할 수 있지만 Event는 누락되면 게임 진행 자체가 달라질 수 있습니다.

UDP를 사용한다면 Reliable 전달, 중복 제거, 순서 보장, 재전송, ACK 등을 직접 설계하거나 검증해야 합니다.

[image](https://www.google.com/s2/favicons?domain=https://playmod.hashnode.dev\&sz=32)

Play Mod+1

## **3. 추가로 반드시 검토할 RTS 전용 기술**

LoL의 기술을 참고하되 OpenRTS는 RTS라는 점에서 다음 항목도 중요합니다.

| 기술                      | 필요한 이유              | 중요도   |
| ----------------------- | ------------------- | ----- |
| Input Sequence / ACK    | 입력 중복 및 누락 방지       | 매우 높음 |
| Command Batching        | 다수 유닛 명령 패킷 효율화     | 매우 높음 |
| Interest Management     | 시야 밖 유닛의 불필요한 전송 제거 | 매우 높음 |
| Fog of War Filtering    | 상대방에게 비공개 정보 노출 방지  | 매우 높음 |
| Delta Compression       | Snapshot 대역폭 절감     | 높음    |
| Entity Lifecycle Sync   | Spawn/Despawn 순서 보장 | 높음    |
| Navigation Determinism  | 클라이언트와 서버의 경로 차이 관리 | 높음    |
| State Checksum          | 비정상적인 상태 차이 감지      | 높음    |
| Replay / Playback       | 버그 재현 및 QA          | 높음    |
| Network Simulation Test | 지연, 손실, Jitter 검증   | 높음    |

특히 RTS에서는 Fog of War를 단순히 화면에서 숨기는 것으로 처리하면 안 됩니다. 서버가 클라이언트별로 전달 가능한 정보를 필터링해야 합니다.

## **4. OpenRTS 적용 우선순위 제안**

현재 프로젝트의 Server Authoritative 모델을 유지한다는 전제에서 다음 순서를 추천합니다.

**Phase 1 — 동기화 기반 강화**

최우선

- Server/Client Clock Synchronization
- Input Sequence / ACK
- Snapshot Timestamp
- Snapshot Interpolation
- Jitter Buffer

**Phase 2 — 결정론 및 디버깅**

- Server Network Recording
- Input Playback
- World State Checksum
- Entity-level Divergence Detection
- Random Seed 관리

**Phase 3 — 지연 보상**

- Historical State Buffer
- Lag Compensation Policy
- Server-side Historical Query
- Skill Validation
- Rewind Window 제한

**Phase 4 — 네트워크 최적화**

- Delta Compression
- Interest Management
- Fog of War Filtering
- Packet Priority
- Adaptive Snapshot Rate

**Phase 5 — 안정성**

- Network Condition Simulator
- Packet Loss / Reordering Test
- Deterministic Replay Test
- Long-duration Desync Test
- Server Performance Monitoring

## **5. 가장 중요하게 생각하는 개선 사항 5가지**

제 판단으로 OpenRTS에서 우선 검토할 기술은 다음과 같습니다.

| 순위 | 기술                                     | 기대 효과            |
| -- | -------------------------------------- | ---------------- |
| 1  | Server Network Recording               | 게임 재현 및 버그 추적    |
| 2  | Unified Clock                          | 시간 기반 동기화 정확도 향상 |
| 3  | Deterministic State Validation         | 장기적인 동기화 오류 발견   |
| 4  | Snapshot Interpolation + Jitter Buffer | 부드러운 유닛 이동       |
| 5  | Input Sequence / ACK                   | 입력 유실 및 중복 방지    |

특히 **SNR + State Validation**은 단순한 네트워크 기능을 넘어 서버 품질을 크게 개선할 수 있는 기술입니다.

게임 서버 개발에서 어려운 문제 중 하나는 특정 환경에서만 발생하는 동기화 버그입니다. 이때 입력을 기록하고 동일한 상황을 재생할 수 있다면 디버깅 난이도가 상당히 낮아집니다.

## **6. 참고할 Riot 공식 기술 문서**

아래 네 문서는 직접 읽어볼 가치가 있습니다.

1. [Determinism in League of Legends: Introduction](https://www.riotgames.com/en/news/determinism-league-legends-introduction?utm_source=chatgpt.com) — Chronobreak와 결정론 도입 배경
2. [Determinism in League of Legends: Implementation](https://www.riotgames.com/en/news/determinism-league-legends-implementation?utm_source=chatgpt.com) — 입력 기록, 비동기 처리, 결정론 구현
3. [Determinism in League of Legends: Unified Clock](https://www.riotgames.com/en/news/determinism-league-legends-unified-clock?utm_source=chatgpt.com) — 시간 동기화, Offset, Drift 보정
4. [Determinism in League of Legends: Fixing Divergences](https://www.riotgames.com/en/news/determinism-league-legends-fixing-divergences?utm_source=chatgpt.com) — 상태 불일치 탐지 및 디버깅

## **7. 실제 OpenRTS 코드 리뷰를 위한 다음 단계**

현재는 GitHub 저장소의 실제 문서와 소스 코드에 접근하지 못했기 때문에, 정확한 누락 여부를 판정하려면 저장소 자료가 필요합니다.

**OpenRTS 저장소를 ZIP으로 첨부해 주시면 다음 기준으로 실제 구현을 대조하겠습니다.**

- 문서에만 존재하고 코드에는 없는 기능
- 코드에는 존재하지만 문서화되지 않은 기능
- 구현은 되어 있지만 LoL 수준에 비해 부족한 기능
- 구조적으로 잘못 구현되어 개선이 필요한 기능
- 새롭게 추가해야 하는 기능

최종 결과는 다음과 같은 형태로 정리하겠습니다.

| 기술               | 현재 구현    | 부족한 부분  | 개선 방법       |
| ---------------- | -------- | ------- | ----------- |
| Clock Sync       | 실제 코드 확인 | 검증 후 작성 | 구체적인 수정안    |
| SNR              | 실제 코드 확인 | 검증 후 작성 | 클래스 및 코드 제안 |
| State Validation | 실제 코드 확인 | 검증 후 작성 | 구현 코드 제안    |
| Interpolation    | 실제 코드 확인 | 검증 후 작성 | 보완 코드 제안    |

가능하면 `docs`뿐 아니라 `Server`, `Client`, `Network`, `Simulation` 관련 소스도 함께 제공해 주세요. 그래야 문서에 적힌 설계와 실제 구현을 구분해서 평가할 수 있습니다.