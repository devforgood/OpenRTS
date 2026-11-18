리그 오브 레전드(LoL)의 네트워크 동기화 기술을 조사해 볼게.

특히 앞서 이야기한 **Server Authoritative, Client Prediction, Interpolation, Lag Compensation** 관점에서 살펴보는 게 좋겠어.

LoL은 일반적인 RTS와 달리 5대5 MOBA 게임이기 때문에 다음 항목이 중요해.

1. 클라이언트 입력을 서버가 어떻게 처리하는가?
2. 챔피언 이동을 어떻게 동기화하는가?
3. 스킬 발사 및 적중 판정을 어떻게 처리하는가?
4. 네트워크 지연과 패킷 손실을 어떻게 보정하는가?
5. 서버와 클라이언트의 게임 시뮬레이션을 어떻게 일치시키는가?

## **1. 가장 중요한 발견: LoL은 Lockstep 방식이 아니다**

Riot의 공식 기술 문서에서 상당히 중요한 내용을 확인했어.

Riot 엔지니어가 2017년 발표한 *Determinism in League of Legends: Unified Clock*에 따르면:

- LoL은 일반적인 RTS나 격투 게임에서 사용하는 동기화된 P2P Lockstep 네트워크 구조를 사용하지 않는다.
- 서버와 클라이언트의 시간 동기화를 별도로 관리한다.
- 서버의 일시적인 성능 저하가 발생하면 게임 시뮬레이션을 느리게 하는 대신 처리 지연이 증가하는 방식을 선택했다.
- 서버와 클라이언트의 시간 차이를 보정하는 Unified Clock을 개발했다.&#x20;

  [image](https://www.google.com/s2/favicons?domain=https://www.riotgames.com\&sz=32)

  Riot Games



이 부분은 네가 개발 중인 RTS에도 상당히 중요한 참고 자료야.

### **LoL과 일반적인 Lockstep 비교**

| 항목       | Lockstep RTS       | LoL                |
| -------- | ------------------ | ------------------ |
| 동기화 기준   | 동일한 Tick의 입력       | 서버와 클라이언트의 시간      |
| 입력 처리    | 입력을 모아서 동기화        | 네트워크를 통해 입력 전달     |
| 게임 시뮬레이션 | 참여자 간 결정론적 동기화     | 서버 중심의 게임 진행       |
| 시간 관리    | 공통 Simulation Tick | Unified Clock      |
| 한쪽 처리 지연 | 전체 진행에 영향 가능       | 서버 처리 지연 및 네트워크 보정 |

여기서 주의할 점은 LoL의 모든 내부 동기화 구현이 공개된 것은 아니라는 거야. 표는 Riot이 공개한 구조를 바탕으로 한 개념적 비교야.

## **2. LoL의 핵심 기술: Unified Clock**

가장 추천하는 기술 자료

[Determinism in League of Legends: Unified Clock | Riot Games Technology](https://images.openai.com/static-rsc-4/jMA1me-LXjmixBWdjHIODFu8X-cfGCQ45iCCmFNuVhAumvhJGSrvKv-kOt5tAHfKRloAbeuHG45NkIEY3_DE7AY_eiGq7KRMEwXpsL1nFSCwA54tHYwWVJuG58W8fVfVyvaokVfuQaRRz_Iqq4ZK64AkTBktarrXZl5fRW8mReg?purpose=inline)

**Determinism in League of Legends: Unified Clock**

Riot Games · 2017년 10월 10일

[Riot 공식 기술 문서 읽기](https://www.riotgames.com/en/news/determinism-league-legends-unified-clock?utm_source=chatgpt.com)

이 문서에서 가장 주목할 부분은 클라이언트와 서버의 시간을 맞추는 알고리즘이야.

### **2-1. 4개의 Timestamp를 이용한 시간 동기화**

클라이언트와 서버가 시간을 측정하는 방식은 다음과 같아.

**Clock Synchronization**

1. **T1: Client Send**

   클라이언트가 시간 동기화 요청을 전송
2. **T2: Server Receive**

   서버가 요청을 수신한 시간 기록
3. **T3: Server Send**

   서버가 응답을 보내는 시간 기록
4. **T4: Client Receive**

   클라이언트가 응답을 수신한 시간 기록

이 네 가지 시간을 이용해서 네트워크 지연과 클라이언트-서버 시간 차이를 추정할 수 있어.

네트워크 왕복 지연(RTT)은 다음과 같이 계산할 수 있어.

\\[ RTT=(T4-T1)-(T3-T2) \\]

서버 시간에서 클라이언트 시간을 뺀 Offset은 다음과 같이 계산해.

\\[ Offset=\frac{(T2-T1)+(T3-T4)}{2} \\]

예를 들어 다음과 같다고 해보자.

| Timestamp | 시간     |
| --------- | ------ |
| T1        | 1000ms |
| T2        | 1055ms |
| T3        | 1057ms |
| T4        | 1112ms |

계산하면:

\\[ RTT=(1112-1000)-(1057-1055) \\]

\\[ RTT=110ms \\]

\\[ Offset=\frac{55+(-55)}{2}=0ms \\]

즉, RTT는 110ms이고 서버와 클라이언트의 시간 차이는 0ms로 추정돼.

### **2-2. 시간 동기화에서 더 중요한 부분**

Riot의 구현은 단순히 Offset을 한 번 계산하고 끝내지 않아.

공식 문서에서 설명하는 특징은 다음과 같아.

1. 초기에는 동기화 요청을 연속적으로 보내 빠르게 Offset을 추정한다.
2. 네트워크 지연의 평균과 표준편차를 계산한다.
3. 통계적으로 벗어난 측정값은 Outlier로 제외한다.
4. 안정화된 이후에는 동기화 주기를 늘린다.
5. 시간 오차와 Drift를 필터링한다.
6. 클라이언트 시간을 서버 시간에 순간적으로 맞추지 않고 점진적으로 보정한다.

특히 마지막 부분이 중요해.

서버 시간이 100초인데 클라이언트가 99.9초라고 해서 클라이언트 시간을 갑자기 100초로 변경하면 화면이나 게임 오브젝트의 움직임에 문제가 생길 수 있어.

Riot은 프레임별 시간 진행 속도를 조금씩 빠르게 하거나 느리게 하면서 차이를 줄이는 방법을 사용한다고 설명하고 있어. 당시 구현에서는 최대 1.3배까지 시간 진행 속도를 조정했어.&#x20;

[image](https://www.google.com/s2/favicons?domain=https://www.riotgames.com\&sz=32)

Riot Games



이 기술은 네가 C# 서버와 Unity 클라이언트를 구현할 때도 참고할 만해.

## **3. LoL의 Deterministic Simulation과 Chronobreak**

[Chronobreak | Why Did Riot Implement It? | EarlyGame](https://images.openai.com/static-rsc-4/9K75TyM3dz2WndvTq04visyxpllTuxnljpBvaVmeGxy0btfJvBND1BSXBoJ1ughK8znl-a9mUXtFTWaFCVmd4_dpvsAUW29CmbsHJ0uB3S3G-KW5-gSu3nIUQ-RF8xS40hl0Bavajoaq1I2s8atCS8bJVpbJNDfhwOn6P0TzW5w?purpose=inline)

**Determinism in League of Legends: Introduction**

Riot Games · 2017년 6월 20일

[Riot 공식 기술 문서 읽기](https://www.riotgames.com/en/news/determinism-league-legends-introduction?utm_source=chatgpt.com)

LoL의 네트워크 동기화 기술을 공부한다면 이 자료도 반드시 읽어보는 게 좋아.

Riot은 e스포츠 경기에서 버그가 발생했을 때 특정 시점으로 게임을 복구할 수 있도록 Project Chronobreak를 개발했어.

핵심은 다음과 같아.

**Server Network Recording (SNR)**

**1. 게임 진행**

서버가 게임 입력, 설정 및 필요한 데이터를 기록

**2. 게임 서버 재실행**

기록된 입력을 이용해 서버를 다시 실행

**3. 과거 상태 복구**

문제가 발생하기 이전의 게임 상태를 재구성

여기서 중요한 기술적 포인트는 서버의 Determinism이야.

동일한 입력과 동일한 설정으로 시뮬레이션을 다시 실행했을 때 동일한 결과를 얻어야 해.

Riot은 이를 위해 게임 서버의 시간, 난수, 네트워크 입력 처리 등을 포함한 여러 시스템을 수정했어.&#x20;

[image](https://www.google.com/s2/favicons?domain=https://www.riotgames.com\&sz=32)

Riot Games+2



**주의:** Chronobreak는 일반 플레이 중 공격 적중을 보정하는 Lag Compensation 기능이 아니야. 경기 복구를 위한 기록 및 재실행 기술이야. 둘 다 과거 상태를 다룬다는 점에서 관련은 있지만 목적은 달라.

## **4. Riot Direct: 네트워크 지연 자체를 줄이는 기술**

[Leveling Up Networking for A Multi-Game Future | Riot Games Technology](https://images.openai.com/static-rsc-4/e4wOoq71ieOPu6IVUrHLSjXmRmUmt3_h7iiMNEu_uJM47jJ4ijf8Vs6ylv9EtvSfKC72SFma7gW_S0vrOJ_8eEyhE_zWLUEoUCOBOis61TOvngtJ645Yv80f0_BPI774QjiSrmZmQ70FI1P5G_Zv-nAwPYp4K7qS99TBDLOOy3o?purpose=inline)

**Fixing the Internet for Real Time Applications**

Riot Games · 2015\~2016

[Part I: 문제 분석](https://www.riotgames.com/en/news/fixing-internet-real-time-applications-part-i?utm_source=chatgpt.com)[Part II: Riot Direct 구현](https://www.riotgames.com/en/news/fixing-internet-real-time-applications-part-ii?utm_source=chatgpt.com)

이 자료는 게임 내부 동기화보다는 네트워크 인프라에 관한 내용이야.

Riot이 발견한 문제는 일반 인터넷 경로가 실시간 게임에 적합하지 않을 수 있다는 점이었어.

예를 들어:

```
Client
  |
  v
ISP
  |
  v
Internet Backbone A
  |
  v
Internet Backbone B
  |
  v
Riot Game Server
```

중간에 불필요한 라우터가 많으면 지연이나 패킷 손실이 발생할 수 있어.

Riot Direct는 자체 네트워크 인프라와 ISP 간 직접 연결 등을 통해 더 효율적인 경로를 제공하려는 프로젝트야.

즉, 지연 보상을 구현하는 것만큼이나 **지연 자체를 줄이는 것이 중요하다**는 사례야.&#x20;

[image](https://www.google.com/s2/favicons?domain=https://www.riotgames.com\&sz=32)

Riot Games+1



## **5. LoL은 UDP를 사용한다**

Riot의 2021년 공식 기술 문서에서는 LoL과 VALORANT처럼 빠른 반응이 필요한 게임은 UDP를 사용하고, 상대적으로 느린 카드 게임 Legends of Runeterra는 TCP를 사용한다고 설명해.

[LoL Esports | Riot Games Tech Blog:  Artificial Latency for Remote Competitors](https://images.openai.com/static-rsc-4/e-UT-U4GqJxNxt4mq5lmEwuXRhEMNyclctDSrnrogXDM2Nv5ryS1cuSric4qcE6PoBQMw4cFHEh0KZ71yQI8mi_dEvpcXFXDVItxQanKQIc2Cj8BsX04RH2Eor34pcxPIYHu4wJsq2QwtxxvRLyUSDF-lF1WGIH3jXejYivFZWk?purpose=inline)

**Leveling Up Networking for a Multi-Game Future**

Riot Games · 2021년 6월 29일

[공식 기술 문서 읽기](https://technology.riotgames.com/news/leveling-networking-multi-game-future?utm_source=chatgpt.com)

LoL에서 UDP를 선택한 이유는 실시간 게임에서 오래된 패킷의 재전송을 기다리는 것보다 최신 상태를 빠르게 전달하는 것이 중요하기 때문이야.

다만 UDP를 사용한다고 해서 신뢰성이 필요 없는 것은 아니야.

예를 들어:

- 이동 입력은 최신 입력으로 처리할 수 있는 경우가 있음
- 중요한 명령은 별도 확인 및 중복 방지 필요
- 게임 상태 Snapshot은 손실되어도 다음 Snapshot으로 회복 가능
- 패킷 순서와 시퀀스 번호 관리 필요

[image](https://www.google.com/s2/favicons?domain=https://technology.riotgames.com\&sz=32)

Riot Games Technology+1

## **6. 2025년 LoL 게임 서버 최적화 자료**

이건 최신 자료 중에서 특히 추천해.

[Engineering Esports: The Tech That Powers Worlds | Riot Games](https://images.openai.com/static-rsc-4/6ueG1H1KafGseiFPhEw3CRnGiuzErmv2MXbsCAQj__CERNzH3CSoqGIMvzuvmtaEjoi-qQomGRLI9P33WeF53kr3vspYRV_W2-tBFzLaz-4SVH7tliY0KkBUhT7ucw81lbyHzGYNCZqpDzyBOJspvcPVjy5Sl2d57QhVm_DBD3E?purpose=inline)

**Improving Performance by Streamlining League's Server Selection**

Riot Games · 2025년 1월 16일

[공식 기술 문서 읽기](https://www.riotgames.com/en/news/tech-blog-cpu-usage?utm_source=chatgpt.com)

Riot은 게임 서버에 게임을 배정하는 방식을 개선했어.

기존에는 게임 서버가 직접 대기열에서 게임을 가져가는 Pull 방식이었지만, 새로운 구조에서는 Orchestrator가 게임 서버를 선택하는 방식으로 변경했어.

특히 다음과 같은 성과를 공개했어.

| 항목                  | 결과             |
| ------------------- | -------------- |
| Auto Scaling CPU 목표 | 40% → 50%      |
| 하드웨어 비용             | 20% 절감         |
| 게임 서버 배정            | Round Robin 기반 |

게임 서버별 CPU 편차를 줄여 전투 중 프레임 저하와 게임 품질 악화를 방지하는 것이 목적이야.&#x20;

[image](https://www.google.com/s2/favicons?domain=https://www.riotgames.com\&sz=32)

Riot Games



이건 네가 관심을 갖고 있는 서버 Tick 처리 성능과도 직접적으로 연결되는 주제야.

## **7. LoL에서 배울 수 있는 기술을 RTS에 적용한다면?**

지금까지 조사한 자료를 기준으로 내가 추천하는 기술은 다음과 같아.

| 기술                       | LoL에서 참고할 점 | RTS 적용 |
| ------------------------ | ----------- | ------ |
| Unified Clock            | 서버 시간 동기화   | 적극 추천  |
| Determinism              | 동일 입력 재실행   | 적극 추천  |
| Server Network Recording | 게임 상태 재현    | 적극 추천  |
| UDP                      | 실시간 데이터 전달  | 적극 추천  |
| Riot Direct              | 네트워크 경로 최적화 | 인프라 참고 |
| Server Allocation        | CPU 부하 분산   | 인프라 참고 |

### **내가 가장 주목하는 부분**

사실 LoL 기술 자료 중 네 프로젝트에 가장 큰 도움이 될 부분은 Unified Clock과 Determinism이야.

특히 네가 C# 서버를 개발하고 있고 Server Authoritative 구조를 선택했다면 다음 구조를 추천해.

## **추천 RTS 네트워크 아키텍처**

**Unity Client**

- Local Prediction
- Snapshot Interpolation
- Client Clock Synchronization
- Input Sequence

UDP Transport

**C# Authoritative Server**

- Unified Simulation Clock
- Input Validation
- Tick-based Simulation
- Deterministic Game Logic
- Snapshot Generation
- Historical State Buffer
- Server Network Recording

## **8. 추가로 읽어볼 공식 자료 순서**

추천하는 학습 순서는 다음과 같아.

1. **Unified Clock**

   서버-클라이언트 시간 동기화 및 시간 보정

   [기술 문서](https://www.riotgames.com/en/news/determinism-league-legends-unified-clock?utm_source=chatgpt.com)
2. **Determinism Introduction**

   서버 기록과 Chronobreak 구조

   [기술 문서](https://www.riotgames.com/en/news/determinism-league-legends-introduction?utm_source=chatgpt.com)
3. **Determinism Implementation**

   실제 게임 코드를 결정론적으로 만드는 방법

   [기술 문서](https://www.riotgames.com/en/news/determinism-league-legends-implementation?utm_source=chatgpt.com)
4. **Fixing Divergences**

   결정론적 재실행에서 결과가 달라지는 문제 추적

   [기술 문서](https://www.riotgames.com/en/news/determinism-league-legends-fixing-divergences?utm_source=chatgpt.com)
5. **Leveling Up Networking**

   UDP와 네트워크 인프라 설계

   [기술 문서](https://technology.riotgames.com/news/leveling-networking-multi-game-future?utm_source=chatgpt.com)

**마지막으로 한 가지 짚고 넘어갈 점이 있어.**

Riot이 공개한 자료만으로는 LoL의 실제 챔피언 이동 Prediction 알고리즘, Snapshot 전송 주기, 스킬별 Server Rewind 정책, 공격 판정의 정확한 지연 보상 알고리즘까지 확인할 수는 없어. 이런 세부 구현은 공개 자료와 추정 영역을 구분해야 해.

내 생각에는 다음 주제로 더 깊게 들어가는 것이 좋아.

**LoL의 Unified Clock을 실제 C# 서버와 Unity 클라이언트로 구현한다면 어떻게 설계할 것인가?**

이걸 이해하면 단순히 RTT를 측정하는 수준을 넘어, 클라이언트가 서버의 현재 게임 시간을 추정하고 스킬 및 이동 판정을 일관되게 처리하는 구조까지 설계할 수 있어.


-----

좋아. 이번에는 LoL의 네트워크 기술을 단순한 개념 설명이 아니라 **실제 C# 게임 서버와 Unity 클라이언트에서 구현할 수 있는 수준**으로 설명할게.

네가 개발하는 RTS를 기준으로 다음 환경을 가정하겠어.

- 서버: C# Dedicated Server
- 클라이언트: Unity 6
- 네트워크: UDP
- 권한 모델: Server Authoritative
- 서버 시뮬레이션: Tick 기반
- 게임 구조: 다수의 유닛이 실시간으로 이동하고 전투하는 RTS

먼저 한 가지 구분할게.

아래 코드는 Riot이 공개한 설계 원칙을 바탕으로 작성한 구현 예시야. LoL 내부의 실제 소스 코드나 비공개 알고리즘을 재현한 것은 아니야.

## **전체 기술 구성**

| 기술                       | 해결하는 문제         | 구현 위치           |
| ------------------------ | --------------- | --------------- |
| Unified Clock            | 서버와 클라이언트 시간 차이 | Client / Server |
| Deterministic Simulation | 동일 입력에 대한 결과 일치 | Server          |
| Client Prediction        | 내 유닛의 이동 지연     | Client          |
| Server Reconciliation    | 예측과 서버 결과의 차이   | Client          |
| Snapshot Interpolation   | 다른 유닛의 부드러운 이동  | Client          |
| Server Network Recording | 게임 상태 재현 및 디버깅  | Server          |
| UDP Reliability          | 패킷 손실 및 순서 문제   | Network Layer   |
| Server Allocation        | 게임 서버 부하 분산     | Infrastructure  |

# **1. Unified Clock: 서버와 클라이언트 시간 동기화**

이 기술은 전체 네트워크 동기화의 기초야.

가령 서버가 30Hz로 게임을 진행한다고 하자.

\\[ TickInterval = \frac{1}{30} = 33.333ms \\]

서버의 현재 Tick이 1000이라면 서버 시뮬레이션 시간은 다음과 같아.

\\[ ServerTime = \frac{1000}{30} = 33.333s \\]

그런데 클라이언트의 로컬 시간은 서버 시간과 일치하지 않을 수 있어.

예를 들어:

```
Server Time : 33.333 sec
Client Time : 33.280 sec

Time Offset : 53 ms
```

클라이언트가 서버 시간을 추정하지 못하면 다음 문제가 발생해.

- 공격 명령의 발생 시점이 달라짐
- Snapshot 보간 시점이 달라짐
- 과거 상태 조회 시점이 달라짐
- 클라이언트와 서버의 Tick 계산이 달라짐

## **1-1. NTP 방식의 시간 측정**

먼저 4개의 Timestamp를 사용해 RTT와 Clock Offset을 추정하자.

\\[ RTT=(T4-T1)-(T3-T2) \\]

\\[ Offset=\frac{(T2-T1)+(T3-T4)}{2} \\]

단위는 모두 밀리초라고 가정해.

C# 구현은 다음과 같아.

```

public struct ClockMeasurement
{
    public double RttMs;
    public double OffsetMs;
}

public static class ClockSynchronizer
{
    public static ClockMeasurement Calculate(
        double t1,
        double t2,
        double t3,
        double t4)
    {
        double rtt = (t4 - t1) - (t3 - t2);

        double offset =
            ((t2 - t1) + (t3 - t4)) * 0.5;

        return new ClockMeasurement
        {
            RttMs = rtt,
            OffsetMs = offset
        };
    }
}

```

예를 들어:

```
T1 = 1000
T2 = 1055
T3 = 1057
T4 = 1112
```

결과:

```
RTT    = 110ms
Offset = 0ms
```

이 값은 클라이언트와 서버의 시계가 같은 기준에 있다고 추정할 수 있다는 의미야.

주의할 점은 Offset이 실제 시간 차이를 정확히 알아내는 값은 아니라는 거야. 네트워크 경로의 상하행 지연이 비대칭이면 오차가 발생해.

## **1-2. 서버 시간 추정기**

실제 게임에서는 매번 시간을 강제로 변경하지 않고, Offset을 추정하고 점진적으로 보정하는 방식을 사용하는 게 좋아.

클라이언트에서 다음 클래스를 사용할 수 있어.

```

public sealed class ServerClock
{
    private double _offsetMs;
    private double _targetOffsetMs;

    private double _lastLocalTimeMs;
    private double _estimatedServerTimeMs;

    private bool _initialized;

    public void AddMeasurement(
        ClockMeasurement measurement)
    {
        // 지연이 지나치게 큰 측정값은 제외
        if (measurement.RttMs < 0 ||
            measurement.RttMs > 300)
        {
            return;
        }

        if (!_initialized)
        {
            _offsetMs = measurement.OffsetMs;
            _targetOffsetMs = measurement.OffsetMs;
            _initialized = true;
            return;
        }

        // 간단한 EWMA 필터
        const double alpha = 0.1;

        _targetOffsetMs +=
            alpha * (measurement.OffsetMs -
                     _targetOffsetMs);
    }

    public double Update(double localTimeMs)
    {
        if (!_initialized)
            return localTimeMs;

        double deltaMs =
            Math.Max(0, localTimeMs - _lastLocalTimeMs);

        _lastLocalTimeMs = localTimeMs;

        // 시간 오차를 점진적으로 보정
        const double maxCorrectionMs = 1.0;

        double error =
            _targetOffsetMs - _offsetMs;

        double correction = Math.Clamp(
            error,
            -maxCorrectionMs,
            maxCorrectionMs);

        _offsetMs += correction;

        double candidate =
            localTimeMs + _offsetMs;

        if (_estimatedServerTimeMs == 0)
        {
            _estimatedServerTimeMs = candidate;
        }
        else
        {
            // 서버 시간은 역행하지 않도록 처리
            _estimatedServerTimeMs = Math.Max(
                _estimatedServerTimeMs,
                candidate);
        }

        return _estimatedServerTimeMs;
    }
}

```

여기서는 설명을 위해 단순한 EWMA 필터를 사용했어.

실제 서비스에서는 다음도 고려해야 해.

- RTT가 가장 낮은 샘플을 우선적으로 활용
- 네트워크 지연의 편차 추적
- 시계 Drift 추정
- 재접속 후 초기 동기화
- 시간 보정 속도 제한

또한 위 코드는 게임 시간의 단순 추정 예시야. LoL의 Unified Clock 구현을 그대로 옮긴 코드는 아니야.

## **1-3. Server Time을 Tick으로 변환**

서버 시간을 기준으로 Tick을 계산하는 함수야.

```

public static class SimulationClock
{
    public const int TickRate = 30;

    public const double TickIntervalMs =
        1000.0 / TickRate;

    public static uint TimeToTick(
        double serverTimeMs)
    {
        return (uint)Math.Floor(
            serverTimeMs / TickIntervalMs);
    }

    public static double TickToTimeMs(uint tick)
    {
        return tick * TickIntervalMs;
    }
}

```

이제 클라이언트는 서버 시간을 추정해서 현재 서버 Tick을 계산할 수 있어.

**중요:** 이 Tick은 클라이언트가 게임 상태를 확정하는 권한을 의미하지 않아. 서버가 보내는 Tick과 Snapshot을 기준으로 클라이언트가 시간을 추정하는 용도야.

# **2. Deterministic Simulation: 동일한 입력으로 동일한 결과 만들기**

LoL의 Chronobreak와 관련된 핵심 기술이야.

예를 들어 서버에서 다음과 같은 시뮬레이션이 진행되었다고 가정하자.

```
Tick 100 : Move Right
Tick 101 : Move Right
Tick 102 : Move Right
Tick 103 : Attack
Tick 104 : Move Left
```

동일한 초기 상태와 동일한 입력을 다시 넣으면 동일한 결과가 나와야 해.

이것을 결정론적 시뮬레이션이라고 해.

## **2-1. 결정론을 깨뜨리는 대표적인 요소**

C#에서 특히 주의해야 하는 부분이야.

| 요소            | 문제                    |
| ------------- | --------------------- |
| DateTime.Now  | 실행 시점마다 다름            |
| Random.Shared | 난수 상태가 다름             |
| Dictionary 순회 | 순서에 의존하면 결과가 달라질 수 있음 |
| float 연산      | 플랫폼 및 연산 순서에 따른 차이    |
| Task.Run      | 실행 순서가 달라질 수 있음       |
| 외부 DB 조회      | 조회 결과 및 타이밍이 달라질 수 있음 |

예를 들어 다음 코드는 결정론적이지 않아.

```
float deltaTime =
    (float)Stopwatch.GetElapsedTime(
        lastTimestamp).TotalSeconds;

position += velocity * deltaTime;
```

실제 경과 시간에 따라 계산 결과가 달라지기 때문이지.

서버에서는 고정 Tick을 사용하는 것이 좋아.

## **2-2. Fixed Tick Simulation**

```

public sealed class GameWorld
{
    public const int TickRate = 30;

    private const float FixedDeltaTime =
        1.0f / TickRate;

    private uint _currentTick;

    public void UpdateTick()
    {
        ProcessCommands(_currentTick);

        SimulateUnits(FixedDeltaTime);

        ResolveCombat();

        _currentTick++;
    }
}

```

실제 서버 실행 루프는 다음처럼 구성할 수 있어.

```

public sealed class GameLoop
{
    private readonly GameWorld _world;

    private const double TickIntervalMs =
        1000.0 / 30.0;

    public GameLoop(GameWorld world)
    {
        _world = world;
    }

    public void Run(CancellationToken token)
    {
        var timer = Stopwatch.StartNew();

        double nextTickMs = timer.Elapsed.TotalMilliseconds;

        while (!token.IsCancellationRequested)
        {
            double nowMs = timer.Elapsed.TotalMilliseconds;

            if (nowMs >= nextTickMs)
            {
                _world.UpdateTick();
                nextTickMs += TickIntervalMs;
            }
            else
            {
                Thread.SpinWait(50);
            }
        }
    }
}

```

이 코드는 구조 설명용이야. 운영 환경에서는 Busy Waiting을 줄이고, Tick 지연이 발생했을 때 누적된 시간을 어떻게 처리할지도 설계해야 해.

## **2-3. 결정론적인 난수 생성**

전투에서 치명타 확률이나 랜덤 스킬 효과를 처리한다고 해보자.

`Random.Shared`를 사용하면 재실행 시 결과가 달라질 수 있어.

따라서 명시적인 Seed와 상태를 사용하는 PRNG를 만들 수 있어.

```

public struct DeterministicRandom
{
    private uint _state;

    public DeterministicRandom(uint seed)
    {
        _state = seed == 0 ? 1u : seed;
    }

    public uint Next()
    {
        uint x = _state;

        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;

        _state = x;

        return x;
    }

    public int Next(int max)
    {
        return (int)(Next() % (uint)max);
    }
}

```

서버가 난수 상태까지 기록하면 동일한 전투 결과를 재현할 수 있어.

다만 `% max` 방식은 약간의 분포 편향이 있어. 실제 확률 판정에는 Rejection Sampling 등을 사용하는 게 더 적합해.

## **3. Client Prediction: 내 유닛의 이동을 미리 처리하기**

이제 클라이언트에서 이동 예측을 구현해 보자.

서버 Tick이 30Hz이고 RTT가 100ms라면 클라이언트가 서버 응답을 기다리는 동안 상당한 지연을 느끼게 돼.

그래서 다음과 같이 처리해.

```
Client                         Server
  |                              |
  | Input #100                   |
  |----------------------------->|
  |                              |
  | Local Prediction             |
  | Position = 101               |
  |                              |
  | Input #101                   |
  |----------------------------->|
  |                              |
  | Local Prediction             |
  | Position = 102               |
  |                              |
  |<-----------------------------|
  | Snapshot                     |
  | LastProcessedInput = 100     |
  | ServerPosition = 101         |
```

여기서 클라이언트는 서버가 Input #100까지 처리했다는 사실을 알게 돼.

Input #101은 아직 확정되지 않았으므로 재실행 대상이야.

### **3-1. 입력 데이터 구조**

```

public struct PlayerInput
{
    public uint Sequence;
    public uint ClientTick;

    public float MoveX;
    public float MoveY;
}

public struct PlayerState
{
    public Vector3 Position;
    public Vector3 Velocity;
}

public struct PlayerSnapshot
{
    public uint ServerTick;
    public uint LastProcessedInput;

    public PlayerState State;
}

```

여기서 `Sequence`는 패킷 번호와는 다른 개념이야.

- Packet Sequence: 네트워크 패킷의 순서
- Input Sequence: 게임 입력의 순서

하나의 패킷에 여러 입력을 넣을 수 있기 때문에 둘을 분리하는 것이 좋아.

### **3-2. 클라이언트 Prediction 구현**

```

public sealed class ClientPrediction
{
    private const float DeltaTime = 1.0f / 30.0f;

    private readonly List<PlayerInput> _pendingInputs = new();

    private uint _nextSequence;

    public PlayerState State;

    public PlayerInput CreateInput(
        float moveX,
        float moveY,
        uint clientTick)
    {
        var input = new PlayerInput
        {
            Sequence = ++_nextSequence,
            ClientTick = clientTick,
            MoveX = moveX,
            MoveY = moveY
        };

        _pendingInputs.Add(input);

        Simulate(ref State, input);

        return input;
    }

    private static void Simulate(
        ref PlayerState state,
        PlayerInput input)
    {
        Vector3 direction = new(
            input.MoveX, 0, input.MoveY);

        if (direction.sqrMagnitude > 1.0f)
            direction.Normalize();

        const float speed = 5.0f;

        state.Position +=
            direction * speed * DeltaTime;
    }

    public void Reconcile(PlayerSnapshot snapshot)
    {
        State = snapshot.State;

        _pendingInputs.RemoveAll(
            input => input.Sequence <=
                     snapshot.LastProcessedInput);

        foreach (var input in _pendingInputs)
        {
            Simulate(ref State, input);
        }
    }
}

```

핵심은 다음 세 가지야.

1. 입력을 생성한다.
2. 로컬에서 먼저 시뮬레이션한다.
3. 서버가 처리했다고 확인한 입력을 제거하고 나머지를 재실행한다.

실제 프로젝트에서는 `RemoveAll`보다 고정 크기 Ring Buffer를 사용하면 메모리 할당을 줄일 수 있어.

또한 서버에서 처리한 마지막 Sequence보다 큰 입력을 무조건 재실행하는 것만으로는 부족할 수 있어. 서버가 입력을 거부하거나 누락한 경우에 대한 처리도 필요해.

## **4. Server Reconciliation: 서버와 클라이언트의 오차 보정**

이제 가장 중요한 문제가 남아 있어.

클라이언트와 서버의 이동 결과가 다르면 어떻게 할까?

예를 들어:

```
Client Predicted Position = 105
Server Position           = 103

Error = 2
```

단순히 서버 위치로 즉시 이동하면 캐릭터가 순간이동하는 것처럼 보여.

그래서 두 가지 보정을 구분하는 것이 좋아.

### **4-1. Simulation State와 Render State 분리**

이 방식은 적극 추천해.

```
Simulation State
    |
    | Server Reconciliation
    |
    v
Authoritative Position

Render State
    |
    | Smooth Correction
    |
    v
Visual Position
```

즉, 실제 게임 계산에 사용하는 위치와 화면에 표시하는 위치를 분리하는 거야.

```

public sealed class PositionSmoother
{
    public Vector3 SimulationPosition;
    public Vector3 RenderPosition;

    private Vector3 _visualError;

    public void ApplyCorrection(
        Vector3 authoritativePosition)
    {
        _visualError +=
            RenderPosition - authoritativePosition;

        SimulationPosition =
            authoritativePosition;
    }

    public void UpdateRender(float deltaTime)
    {
        const float smoothTime = 0.1f;

        float factor = 1.0f -
            Mathf.Exp(-deltaTime / smoothTime);

        _visualError *= 1.0f - factor;

        RenderPosition =
            SimulationPosition + _visualError;
    }
}

```

이렇게 하면 서버가 확정한 위치는 즉시 반영하면서 화면에서는 자연스럽게 보정할 수 있어.

주의할 점은 RenderPosition을 실제 충돌 판정이나 이동 시뮬레이션에 사용하면 안 된다는 거야.

### **4-2. 오차가 큰 경우**

예를 들어 서버 위치와 클라이언트 위치의 차이가 5m 이상이라면 부드럽게 보정하는 것이 오히려 부자연스러울 수 있어.

다음처럼 정책을 나눌 수 있어.

| 오차         | 처리               |
| ---------- | ---------------- |
| 0.05m 이하   | 무시 또는 미세 보정      |
| 0.05\~0.5m | 부드럽게 보정          |
| 0.5\~2m    | 빠른 보정            |
| 2m 이상      | 서버 위치로 강제 동기화 검토 |

이 값은 게임의 이동 속도와 충돌 반경에 따라 조정해야 해.

특히 RTS는 유닛이 많으므로 모든 유닛에 Prediction을 적용하기보다 플레이어가 직접 조작하는 유닛이나 선택된 유닛 위주로 적용하는 편이 효율적일 수 있어.

## **5. Snapshot Interpolation: 상대방 유닛의 움직임 보간**

이제 상대방 유닛을 처리해 보자.

상대방 유닛은 클라이언트에서 직접 시뮬레이션하지 않고 서버가 보낸 Snapshot을 기준으로 화면에 표현할 수 있어.

예를 들어 서버가 10Hz로 Snapshot을 보낸다고 가정하자.

```
Snapshot A
ServerTime = 1000ms
Position   = (10, 0, 10)

Snapshot B
ServerTime = 1100ms
Position   = (20, 0, 10)
```

현재 렌더링할 시점이 1050ms라면:

\\[ \alpha=\frac{1050-1000}{1100-1000}=0.5 \\]

\\[ Position=A+(B-A)\alpha \\]

결과는:

```
Position = (15, 0, 10)
```

### **5-1. Snapshot 데이터 구조**

```

public struct EntitySnapshot
{
    public uint EntityId;
    public uint ServerTick;

    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Velocity;
}

```

실제 RTS에서는 전체 유닛을 매번 전송하기보다 변경된 상태만 전송하는 Delta Snapshot 구조도 고려해야 해.

### **5-2. Snapshot Buffer 구현**

```

public sealed class SnapshotBuffer
{
    private readonly List<EntitySnapshot> _snapshots = new();

    public void Add(EntitySnapshot snapshot)
    {
        int index = _snapshots.BinarySearch(
            snapshot,
            Comparer<EntitySnapshot>.Create(
                (a, b) => a.ServerTick.CompareTo(b.ServerTick)));

        if (index < 0)
            index = ~index;

        _snapshots.Insert(index, snapshot);

        // 최대 32개 보관
        while (_snapshots.Count > 32)
            _snapshots.RemoveAt(0);
    }

    public bool TrySample(
        double renderTick,
        out Vector3 position)
    {
        position = default;

        for (int i = 0; i < _snapshots.Count - 1; i++)
        {
            var a = _snapshots[i];
            var b = _snapshots[i + 1];

            if (renderTick < a.ServerTick ||
                renderTick > b.ServerTick)
                continue;

            double duration =
                b.ServerTick - a.ServerTick;

            float alpha = duration <= 0
                ? 0
                : (float)(
                    (renderTick - a.ServerTick) /
                    duration);

            position = Vector3.Lerp(
                a.Position,
                b.Position,
                alpha);

            return true;
        }

        return false;
    }
}

```

이 코드는 이해하기 위한 기본형이야. 실제 구현에서는 다음을 추가하는 게 좋아.

- Snapshot 중복 제거
- 오래된 Snapshot 폐기
- 패킷 순서 역전 처리
- 버퍼 부족 시 보간 중단
- 최신 Snapshot 이후의 Extrapolation 정책
- Entity별 Snapshot 관리

### **5-3. Interpolation Delay**

여기서 중요한 것은 렌더링 시점을 서버 현재 시간보다 조금 과거로 잡는다는 점이야.

예를 들어:

```
Estimated Server Time = 2000ms
Interpolation Delay   = 100ms

Render Time           = 1900ms
```

이렇게 하면 과거와 현재 사이에 두 개의 Snapshot이 존재할 가능성이 높아져.

반면 보간 지연이 너무 작으면 다음 Snapshot이 도착하지 않아 화면이 멈출 수 있어.

RTS라면 처음에는 100\~150ms 정도를 시험해 볼 수 있지만, 실제 값은 Snapshot 전송 주기, RTT, Jitter에 따라 결정해야 해.

## **6. Server Rewind: 과거 상태를 이용한 공격 판정**

이제 처음 질문했던 Lag Compensation으로 돌아가 보자.

LoL과 같은 MOBA에서 원거리 스킬을 생각해 보자.

A가 화면에서 B를 조준했는데 서버가 공격을 처리할 때는 B가 이미 이동했을 수 있어.

다만 중요한 점이 있어.

**LoL이 모든 스킬에 Server Rewind를 적용한다고 공개적으로 확인된 것은 아니야.** 아래는 RTS/MOBA에서 적용할 수 있는 일반적인 구현 방식이야.

### **6-1. Historical State Buffer**

서버는 매 Tick마다 과거 상태를 저장해.

```

public struct HistoricalUnitState
{
    public uint EntityId;

    public Vector3 Position;
    public float CollisionRadius;
}

public sealed class WorldHistory
{
    private readonly HistoricalUnitState[][] _history;

    private readonly int _capacity;

    public WorldHistory(int capacity, int maxUnits)
    {
        _capacity = capacity;

        _history = new HistoricalUnitState[capacity][];

        for (int i = 0; i < capacity; i++)
            _history[i] = new HistoricalUnitState[maxUnits];
    }

    public void Save(
        uint tick,
        ReadOnlySpan<HistoricalUnitState> states)
    {
        int index = (int)(tick % _capacity);

        states.CopyTo(_history[index]);
    }

    public ReadOnlySpan<HistoricalUnitState> Get(
        uint tick)
    {
        int index = (int)(tick % _capacity);

        return _history[index];
    }
}

```

위 코드는 개념적인 Ring Buffer야. 실제 운영 코드에서는 각 Tick의 유닛 개수, Entity ID 매핑, Tick 태그를 별도로 저장해야 해. 그래야 Ring Buffer가 덮어쓴 오래된 상태를 잘못 조회하지 않아.

### **6-2. 과거 시점 계산**

예를 들어:

```
Server Time = 10000ms
RTT         = 100ms
One-way     = 50ms
```

클라이언트가 공격을 시작한 시점은 서버 현재 시점보다 대략 50ms 이전으로 추정할 수 있어.

\\[ RewindTime=ServerTime-\frac{RTT}{2} \\]

30Hz라면:

\\[ RewindTicks=50/33.333=1.5 \\]

약 1\~2 Tick 이전 상태를 조회하는 거야.

하지만 RTT/2를 무조건 사용하는 것은 올바른 해법이 아니야.

실제로는 다음을 함께 고려해야 해.

- 클라이언트의 입력 생성 시점
- 클라이언트와 서버의 Clock Offset
- 입력 패킷의 전송 지연
- 클라이언트의 Snapshot Interpolation Delay
- 서버의 수신 및 처리 시간

특히 클라이언트가 이미 과거 Snapshot을 보고 공격했다면 보간 지연까지 고려해야 해.

### **6-3. 과거 위치에서 충돌 판정**

예를 들어 원형 충돌체끼리 충돌을 검사한다고 하자.

```

public static bool CheckHit(
    Vector3 attackerPosition,
    Vector3 targetPosition,
    float attackRange,
    float targetRadius)
{
    float distanceSq = Vector3.SqrMagnitude(
        targetPosition - attackerPosition);

    float totalRange =
        attackRange + targetRadius;

    return distanceSq <= totalRange * totalRange;
}

```

과거 위치를 넣으면 과거 시점의 공격 판정을 수행할 수 있어.

하지만 이 방식은 근접 공격처럼 단순한 경우에 적합해.

투사체, 부채꼴 스킬, 벽에 막히는 스킬 등은 별도의 충돌 기하 검사가 필요해.

### **6-4. 서버가 반드시 검증해야 하는 항목**

```
1. 공격자가 실제로 존재하는가?
2. 공격 가능한 상태인가?
3. 공격 쿨다운이 끝났는가?
4. 공격 사거리가 유효한가?
5. 공격 시점에 대상이 유효했는가?
6. 최대 Rewind 범위를 초과하지 않았는가?
7. 이미 처리한 공격 명령은 아닌가?
```

특히 서버는 과거 상태를 이용해 적중 여부를 판단하더라도 현재 게임 상태에 공격 결과를 적용할 때 대상이 이미 사망했거나 삭제되었는지 다시 확인해야 해.

## **7. Server Network Recording: 게임 상태 기록 및 재현**

이 기술은 네가 서버 프레임워크를 개발할 때 특히 추천하고 싶어.

일반적인 로그는 다음과 같아.

```
Player 1001 moved
Player 1001 attacked
Unit 2001 died
```

하지만 이 정도로는 게임 상태를 정확하게 재현하기 어려워.

Chronobreak와 같은 시스템을 구현하려면 단순 로그가 아니라 **게임을 재실행할 수 있는 입력 기록**이 필요해.

### **7-1. Tick 단위 입력 기록**

```

public enum CommandType : byte
{
    Move,
    Attack,
    Stop,
    CastSkill
}

public struct RecordedCommand
{
    public uint Tick;
    public uint PlayerId;
    public uint Sequence;

    public CommandType Type;

    public uint TargetEntityId;
    public Vector3 Position;
}

public sealed class TickRecording
{
    public uint Tick;

    public List<RecordedCommand> Commands = new();
}

```

기록은 다음과 같은 구조가 돼.

```
Tick 100
  Player 1 : Move Unit 100 -> (10, 20)
  Player 2 : Move Unit 200 -> (30, 40)

Tick 101
  Player 1 : Attack Unit 200
  Player 2 : Move Unit 200 -> (31, 40)

Tick 102
  Player 1 : Cast Skill 1
```

이것만으로도 입력 순서와 게임 진행을 분석할 수 있어.

### **7-2. Snapshot + Command Log**

더 효율적인 방법은 일정 Tick마다 전체 상태를 저장하고, 그 이후의 입력을 기록하는 거야.

**게임 재현 구조**

**Snapshot #1000**

전체 게임 상태 저장

**Command Log**

Tick 1001 \~ 1099 입력 기록

**Snapshot #1100**

다시 전체 게임 상태 저장

예를 들어 100 Tick마다 Snapshot을 저장하면:

- Tick 1050의 상태를 복원하려면
- Snapshot 1000을 읽고
- Tick 1001\~1050의 입력을 재실행하면 돼.

### **7-3. State Hash 검증**

재실행 결과가 동일한지 검증하려면 상태 Hash를 비교하면 좋아.

```
public static ulong CalculateStateHash(
    IReadOnlyList<UnitState> units)
{
    ulong hash = 14695981039346656037UL;

    foreach (var unit in units.OrderBy(
                 x => x.EntityId))
    {
        hash ^= unit.EntityId;
        hash *= 1099511628211UL;

        hash ^= (uint)BitConverter.SingleToInt32Bits(
            unit.Position.X);
        hash *= 1099511628211UL;

        hash ^= (uint)BitConverter.SingleToInt32Bits(
            unit.Position.Y);
        hash *= 1099511628211UL;
    }

    return hash;
}
```

여기서는 예시로 FNV-1a 계열의 Hash를 사용했어.

실제 검증에서는 다음을 포함해야 해.

- 모든 유닛의 위치와 속도
- 체력 및 상태 이상
- 쿨다운
- AI 상태
- RNG 상태
- 투사체 상태
- 게임 진행 상태

또한 Float를 사용할 때는 비트 단위 Hash가 달라질 수 있으므로 결정론적 시뮬레이션이 보장되는 환경에서 사용해야 해.

**이 기술의 가장 큰 장점은 버그가 발생한 게임을 개발 PC에서 재현할 수 있다는 점이야.**

네가 고민하는 서버의 비동기 처리나 유닛 간 상태 불일치 문제도 기록과 재생 시스템을 갖추면 추적하기 쉬워져.

## **8. UDP Reliability: 패킷 손실 및 순서 문제 처리**

UDP는 데이터를 전달하는 것만 보장하고 패킷이 도착한다는 사실까지 보장하지 않아.

따라서 게임 프로토콜에서 직접 필요한 신뢰성 기능을 구현해야 해.

여기서 중요한 건 모든 데이터를 신뢰성 있게 전송할 필요는 없다는 거야.

### **8-1. 패킷 종류 분리**

RTS에서는 대략 다음과 같이 분류할 수 있어.

| 데이터            | 전송 정책         |
| -------------- | ------------- |
| 유닛 위치 Snapshot | 최신 데이터 우선     |
| 이동 상태          | 최신 데이터 우선     |
| 공격 명령          | 신뢰성 보장        |
| 스킬 사용 명령       | 신뢰성 보장        |
| 게임 시작 및 종료     | 신뢰성 보장        |
| 상태 동기화 전체 데이터  | 필요에 따라 신뢰성 보장 |

예를 들어 이동 Snapshot 100번이 손실됐더라도 101번이 도착했다면 100번을 다시 받을 필요가 없는 경우가 많아.

반면 스킬 사용 명령이 손실되면 게임 결과가 달라질 수 있어.

### **8-2. Packet Header**

다음과 같은 Header를 설계할 수 있어.

```

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PacketHeader
{
    public ushort Magic;
    public byte Version;
    public byte Flags;

    public uint Sequence;
    public uint Ack;
    public uint AckBits;

    public uint ServerTick;
}

```

각 필드의 의미는 다음과 같아.

| 필드         | 역할                 |
| ---------- | ------------------ |
| Sequence   | 내가 보낸 패킷 번호        |
| Ack        | 상대방에게 받은 마지막 패킷 번호 |
| AckBits    | 이전 32개 패킷의 수신 여부   |
| ServerTick | 해당 패킷의 게임 Tick     |

### **8-3. ACK Bits**

예를 들어 서버가 다음 패킷을 수신했다고 해보자.

```
Received Packet: 100

Ack = 100

AckBits = 0000 0000 0000 0000
```

이후 98, 99번 패킷도 받았다면:

```
Ack = 100

AckBits = ... 0011
```

이런 방식으로 최근 패킷의 수신 상태를 알려줄 수 있어.

C#에서는 다음과 같이 처리할 수 있어.

```

public sealed class PacketAckTracker
{
    public uint LatestSequence { get; private set; }

    public uint AckBits { get; private set; }

    private bool _initialized;

    public void OnReceive(uint sequence)
    {
        if (!_initialized)
        {
            LatestSequence = sequence;
            AckBits = 0;
            _initialized = true;
            return;
        }

        int delta = unchecked(
            (int)(sequence - LatestSequence));

        if (delta > 0)
        {
            if (delta >= 33)
            {
                AckBits = 0;
            }
            else
            {
                AckBits = (AckBits << delta) |
                    (1u << (delta - 1));
            }

            LatestSequence = sequence;
        }
        else
        {
            uint distance =
                LatestSequence - sequence;

            if (distance >= 1 && distance <= 32)
            {
                AckBits |= 1u << ((int)distance - 1);
            }
        }
    }
}

```

이 코드는 Sequence가 32비트 순환하는 상황까지 고려한 기본형이야. 실제로는 오래된 패킷인지 판단하고, 중복 패킷을 무시하는 처리도 추가해야 해.

### **8-4. Reliable Command 처리**

이동 명령과 공격 명령을 같은 방식으로 전송하지 않는 것이 좋아.

예를 들어 공격 명령을 전송할 때:

```
Client
  |
  | Attack Command #501
  |
  v
Server
  |
  | Command Accepted #501
  |
  v
Client
```

ACK를 받지 못했다면 재전송해.

하지만 서버는 중복 명령이 들어와도 공격을 두 번 실행하면 안 돼.

```

public sealed class CommandProcessor
{
    private readonly Dictionary<uint, uint>
        _lastProcessedSequence = new();

    public bool TryProcess(
        uint playerId,
        uint sequence,
        Action execute)
    {
        if (_lastProcessedSequence.TryGetValue(
                playerId, out uint last))
        {
            if (sequence <= last)
                return false;
        }

        execute();

        _lastProcessedSequence[playerId] =
            sequence;

        return true;
    }
}

```

이 코드는 순서가 보장된 명령 스트림의 단순한 예시야.

실제 UDP에서는 502번이 먼저 도착하고 501번이 나중에 도착할 수 있으므로, 모든 명령을 무조건 마지막 Sequence만으로 처리하면 안 돼.

명령 스트림별로 순서 보장, 재정렬 버퍼, 중복 제거 정책을 정해야 해.

예를 들어:

- 이동 명령: 이전 명령을 대체할 수 있음
- 공격 명령: 순서대로 처리해야 함
- 스킬 명령: 순서 및 자원 검증 필요
- 즉시 중단 명령: 이전 명령보다 우선 처리할 수 있음

이런 식으로 명령의 의미에 따라 정책을 달리하는 것이 좋아.

## **9. Server Allocation: 게임 서버 부하 분산**

이 부분은 LoL의 네트워크 동기화보다는 게임 서버 운영 기술에 해당해.

게임을 시작할 때 어느 서버에 배치할 것인지 결정하는 문제야.

예를 들어:

```
Matchmaking
    |
    v
Game Orchestrator
    |
    +---- Game Server A (CPU 30%)
    |
    +---- Game Server B (CPU 70%)
    |
    +---- Game Server C (CPU 45%)
```

단순 Round Robin보다 실제 부하를 고려하는 방식이 더 적합할 수 있어.

### **9-1. 서버 상태 정보**

```

public sealed class GameServerInfo
{
    public string ServerId = "";

    public int ActiveMatches;

    public float CpuUsage;

    public int ActivePlayers;

    public bool IsHealthy;
}

```

### **9-2. 서버 선택**

```

public static GameServerInfo? SelectServer(
    IReadOnlyList<GameServerInfo> servers)
{
    return servers
        .Where(x => x.IsHealthy)
        .Where(x => x.CpuUsage < 0.7f)
        .OrderBy(x => x.CpuUsage)
        .ThenBy(x => x.ActiveMatches)
        .FirstOrDefault();
}

```

실제 서비스에서는 CPU 사용률만으로 판단하지 않는 것이 좋아.

RTS는 유닛 수와 AI 계산량에 따라 CPU 부하가 크게 달라질 수 있기 때문이야.

다음 지표를 같이 사용하는 것을 추천해.

- Tick 처리 시간
- Tick Overrun 횟수
- 유닛 수
- 활성 투사체 수
- AI 업데이트 비용
- 네트워크 송수신량
- 메모리 사용량

특히 서버의 CPU 사용률이 50%여도 Tick 처리 시간이 33ms를 넘어간다면 이미 문제가 발생한 상태일 수 있어.

# **10. 실제 RTS 프로젝트에 적용할 구현 우선순위**

지금까지 설명한 기술을 한꺼번에 구현하기보다는 단계별로 적용하는 것이 좋아.

내가 추천하는 순서는 다음과 같아.

## **Phase 1. 기본 네트워크 동기화**

최우선

- Fixed Tick Simulation
- Server Clock Synchronization
- Input Sequence
- UDP Packet Header
- Server Authoritative Movement\
  ## **Phase 2. 화면 반응성**
- Client Prediction
- Server Reconciliation
- Snapshot Interpolation
- Interpolation Buffer\
  ## **Phase 3. 안정성**
- Reliable Command
- ACK / ACK Bits
- Packet Reordering
- Duplicate Command Filtering
- Network Jitter 대응\
  ## **Phase 4. 고급 기능**
- Historical State Buffer
- Server Rewind
- Deterministic Random
- State Hash
- Server Network Recording\
  ## **Phase 5. 운영**
- Tick Time Monitoring
- Server Allocation
- Auto Scaling
- Network Metrics

## **11. 구현 시 주의할 점: Determinism과 C#**

한 가지 기술적으로 더 짚고 넘어가자.

C#에서 `float`를 사용하고 Fixed Tick을 적용했다고 해서 완전한 Determinism이 보장되는 것은 아니야.

동일한 실행 환경에서도 연산 순서가 달라지면 부동소수점 결과가 달라질 수 있어.

따라서 목적에 따라 선택해야 해.

| 방식                  | 특징               | 추천 용도          |
| ------------------- | ---------------- | -------------- |
| float + Fixed Tick  | 구현이 간단           | 일반적인 서버 권위형 게임 |
| double + Fixed Tick | 정밀도 향상           | 시간 및 계산 보조     |
| Fixed Point         | 결정론 확보에 유리       | 결정론적 재실행       |
| Integer 기반 계산       | 플랫폼 간 일관성 확보에 유리 | RTS 시뮬레이션      |

네가 원하는 것이 서버에서만 게임 시뮬레이션을 수행하는 구조라면 모든 클라이언트까지 완전한 Determinism을 강제할 필요는 없어.

서버에서 기록한 입력을 동일한 서버 실행 환경에서 재현하는 것부터 시작하는 게 현실적이야.

## **12. 내가 추천하는 최종 아키텍처**

지금까지의 내용을 종합하면 다음 구조를 추천해.

**Unity Client**

**Network Layer**

- UDP Transport
- Packet Sequence / ACK
- Reliable Command

**Synchronization Layer**

- Server Clock
- Input Queue
- Snapshot Buffer

**Presentation Layer**

- Local Prediction
- Reconciliation
- Remote Interpolation

**C# Dedicated Server**

**Network Layer**

- UDP Transport
- Packet Validation
- Reliable Command

**Command Layer**

- Input Sequence
- Command Scheduling
- Command Validation

**Simulation Layer**

- Fixed Tick
- World Simulation
- Combat / Collision
- Deterministic Random

**Synchronization Layer**

- Snapshot Generation
- Historical State Buffer
- Server Rewind

**Recording Layer**

- Command Recording
- Periodic Snapshot
- State Hash
- Replay

### **개인적으로 가장 추천하는 다음 구현**

네 RTS 프로젝트에서는 다음 세 가지를 먼저 완성하는 것이 가장 가치 있다고 생각해.

1. **Unified Clock**: 서버와 클라이언트가 동일한 시간 기준을 갖도록 한다.
2. **Snapshot Interpolation**: 다른 플레이어의 유닛이 부드럽게 이동하도록 한다.
3. **Client Prediction + Reconciliation**: 내가 조작하는 유닛의 반응성을 확보한다.

그다음에 Server Rewind가 실제로 필요한 스킬이 있는지 판단하면 돼.

특히 RTS에서는 모든 공격에 과거 판정을 적용하는 것보다 서버의 현재 시뮬레이션 시점을 기준으로 공격을 처리하는 편이 더 단순하고 일관적일 수 있어.

**중요한 설계 원칙은 클라이언트는 반응성을 담당하고, 서버는 게임 규칙과 결과를 담당하도록 역할을 분리하는 것이야.**