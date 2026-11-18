OpenRTS 실제 코드 분석 결과
ZIP 분석 완료
이번에는 첨부한 OpenRTS.zip을 직접 열어 확인했습니다.

분석 범위는 다음과 같습니다.
- docs/ 전체 11개 문서
- Server/src/OpenRTS.Server/Net/GameServer.cs
- Server/src/OpenRTS.Server/Simulation/Abilities/AbilitySystem.cs
- Server/src/OpenRTS.Server/GameLoop.cs
- Shared/Runtime/Protocol/
- Shared/Runtime/Interpolation/
- Client/Assets/Scripts/Net/GameClient.cs
- 관련 서버 단위·통합 테스트
문서뿐 아니라 실제 구현 경로도 대조했습니다.

먼저 결론부터 말씀드리면 OpenRTS는 이미 상당히 괜찮은 Server Authoritative 동기화 구조를 갖추고 있습니다. 이전 답변에서 제안했던 Snapshot Interpolation, Delta Snapshot, Fog of War, Lag Compensation 등이 실제로 구현되어 있습니다.

반대로 LoL의 기술을 기준으로 봤을 때 아직 보완할 만한 부분도 명확합니다.
1. 실제 구현 현황
기술	코드 확인 결과	평가
Server Authoritative	구현	양호
Fixed Tick Simulation	30Hz 구현	양호
Snapshot Replication	15Hz 구현	양호
Snapshot Interpolation	구현	양호
Adaptive Interpolation Delay	구현	양호
Snapshot Delta	필드 단위 구현	양호
Snapshot ACK	구현	양호
Snapshot Fragmentation	구현	양호
Fog of War Filtering	서버 측 구현	양호
Lag Compensation	스킬 선딜 보상 구현	양호
Client Prediction	시전 연출·쿨다운 예측만 구현	부분 구현
Unified Clock	스냅샷 기반 시계 추정	부분 구현
Server Network Recording	구현 확인 안 됨	미구현
Deterministic Replay	구현 확인 안 됨	미구현
State Divergence Detection	구현 확인 안 됨	미구현
Server-side Rewind	구현 확인 안 됨	미구현
Command Sequence ID	별도 구현 확인 안 됨	보완 검토

여기서 미구현은 첨부된 소스에서 해당 기능을 찾지 못했다는 의미입니다.

2. 이미 잘 구현된 부분
2-1. Snapshot Interpolation
구현 확인
파일:

Shared/Runtime/Interpolation/SnapshotInterpolator.cs

현재 구조는 다음과 같습니다.
서버
Tick 100

Tick 102

Tick 104


30Hz 시뮬레이션, 15Hz Snapshot 전송

클라이언트
- Snapshot Buffer
- ServerClock 추정
- Adaptive Interpolation Delay
- 두 Snapshot 사이 위치 보간
- 최대 0.25초 외삽

렌더링은 최신 서버 상태가 아닌 과거 시점의 상태를 표현합니다.

실제 코드에서 확인한 주요 값입니다.
설정	값
최소 보간 지연	약 133ms
최대 보간 지연	500ms
지연 측정 구간	3초
지연 증가 속도	0.25초/초
지연 감소 속도	0.05초/초
최대 외삽	250ms
시계 보정 계수	0.1
시계 강제 재설정 기준	1초

특히 적응형 지연은 최근 3초 동안 필요한 보간 지연의 최악값에 서버 Tick 하나를 더해 목표값을 설정합니다.

이것은 단순한 선형 보간보다 실제 네트워크 상황에 대응하기 좋은 구조입니다.

개선 의견: 현재 구조를 교체할 필요는 없습니다. 다만 RTT와 Snapshot 도착 간격의 변동을 구분해서 측정하는 것이 좋습니다.

현재 구현은 RTT가 아닌 Snapshot 도착 간격을 중심으로 보간 지연을 조절합니다. 두 값은 서로 다른 의미이므로 별도 지표로 관리하는 것을 추천합니다.
2-2. Delta Snapshot + Snapshot ACK
구현 확인
파일:
- Shared/Runtime/Protocol/Snapshot.cs
- Shared/Runtime/Interpolation/SnapshotReceiver.cs
- Server/src/OpenRTS.Server/Net/GameServer.cs
이 부분은 설계가 상당히 좋습니다.

일반적인 전체 Snapshot 대신 클라이언트가 ACK한 Snapshot을 기준으로 변경된 필드만 전송합니다.

예를 들어:
Snapshot 100
Unit 1: Position + Info + Health + Action
Unit 2: Position + Info + Health + Action

Client ACK 100

Snapshot 102
Unit 1: Position
Unit 2: Health


추가로 다음 문제까지 처리하고 있습니다.
- Snapshot Fragmentation
- Missing Baseline
- Incomplete Fragment
- 중복 및 오래된 Snapshot
- 양자화된 좌표 기준 Delta 비교
- 팀별 Snapshot History
64개 Snapshot을 보관하므로 약 4.3초의 히스토리를 유지합니다.

여기서 중요한 것은 Snapshot ACK가 일반적인 UDP 패킷 ACK와 다르다는 점입니다. 이것은 Delta를 복원할 기준 상태를 확인하는 애플리케이션 레벨 ACK입니다.
2-3. Fog of War Filtering
구현 확인
파일:
- Shared/Runtime/Combat/VisionGrid.cs
- Server/src/OpenRTS.Server/Net/GameServer.cs
이것은 RTS에서 특히 중요합니다.

서버가 적 유닛의 위치를 전송하고 클라이언트에서 숨기는 방식이 아닙니다.

서버에서 팀별 시야를 계산한 다음, 보이지 않는 적을 Snapshot에서 제외합니다.

추가로 공격 명령 검증에서도 시야를 확인합니다.

즉, 단순 렌더링 보안이 아니라 정보 자체를 서버에서 필터링하는 구조입니다.
2-4. Lag Compensation
구현 확인
파일:

Server/src/OpenRTS.Server/Simulation/Abilities/AbilitySystem.cs

이 부분은 이전에 이야기했던 레이턴시 지연 보상과 직접 연결됩니다.

현재 구현은 과거 위치로 되돌리는 Server Rewind가 아니라 명령 전달 지연을 스킬 선딜에서 일부 차감하는 방식입니다.

실제 공식:
\[
Credit=\min(Delay,\ CastTime\times0.5,\ 0.1)
\]

즉 다음 세 가지 제한이 있습니다.
- 실제 명령 지연보다 많이 보상하지 않음
- 선딜 시간의 최대 50%
- 최대 100ms
또한 접근 이동 후 사거리 안에 들어온 경우에는 처음부터 지연 보상을 주지 않습니다. AI 시전에도 적용하지 않습니다.

이것은 클라이언트가 과거 Tick을 조작해서 무제한으로 선딜을 줄이는 것을 방지하는 좋은 설계입니다.

3. LoL 기준으로 발견한 주요 누락 및 보완 사항
여기부터가 이번 분석의 핵심입니다.
3-1. Unified Clock — 가장 먼저 개선할 부분
부분 구현 · 개선 권장
현재 구현:

Shared/Runtime/Interpolation/SnapshotInterpolator.cs

현재 시계 보정 코드는 다음과 같습니다.
private void SyncClock(double snapshotTime)
{
    var error = snapshotTime - ServerClock;

    if (!_hasClock || Math.Abs(error) > ClockResetThreshold)
    {
        ServerClock = snapshotTime;
        _hasClock = true;
        _gaps.Clear();
        return;
    }

    ServerClock += error * ClockCorrectionGain;
}


현재 방식은 다음과 같습니다.
1. Snapshot의 서버 Tick을 시간으로 변환
2. 클라이언트 추정 시계와 비교
3. 차이의 10%를 보정
4. 오차가 1초 이상이면 즉시 재설정
이것은 기본적인 시계 추정 및 보정으로는 적절합니다.

하지만 LoL의 Unified Clock과 비교하면 부족한 부분이 있습니다.
기능	OpenRTS	LoL 공개 구현
서버 시간 추정	Snapshot 기반	별도 Clock Sync
4-Timestamp 측정	확인 안 됨	구현
Offset 추정	단순 오차 보정	통계적 추정
RTT 샘플 이상치 제거	확인 안 됨	구현
Clock Drift 필터링	확인 안 됨	구현
초기 빠른 동기화	확인 안 됨	구현
시간 Scale 제어	없음	지원
Pause 동기화	없음	지원
복수 Clock 인스턴스	없음	지원

Riot은 초기 접속 시 연속적인 시간 동기화 요청을 보내고, 충분한 샘플을 얻은 후 동기화 빈도를 낮춥니다. 네트워크 지연의 평균과 표준편차를 이용해 이상치도 제거합니다. 

Riot Games

OpenRTS에 추천하는 개선

현재 Snapshot 기반 시계는 유지하고 별도의 ClockSynchronizer를 추가하는 것이 좋겠습니다.
ClockSynchronizer
 ├── SendSyncRequest()
 ├── ReceiveSyncResponse()
 ├── CalculateRtt()
 ├── CalculateOffset()
 ├── FilterOutliers()
 ├── EstimateDrift()
 └── SmoothClock()
          |
          v
SnapshotInterpolator


다만 모든 게임에 LoL 수준의 시간 시스템이 필요한 것은 아닙니다.

OpenRTS에서는 우선 다음 세 가지가 중요합니다.
- Clock Offset
- RTT 및 Jitter 측정
- 안정적인 Clock Smoothing
Pause, Time Scale, 복수 Clock은 실제 필요성이 생길 때 추가하면 됩니다.
3-2. Server Network Recording (SNR)
구현 확인 안 됨 · 강력 추천
LoL의 Chronobreak 기술에서 가장 가치 있는 부분 중 하나입니다.

공식 문서:
Riot Games – Determinism in League of Legends: ImplementationRiot의 SNR은 네트워크 패킷만 저장하는 단순 로깅이 아닙니다.

서버에 들어온 플레이어 입력을 프레임별 순서로 기록하고, 게임 결과에 영향을 미치는 외부 입력도 기록해 재생합니다.

OpenRTS에는 다음과 같은 입력이 있습니다.
UnitCommand
 ├── Move
 ├── Attack
 ├── AttackMove
 ├── Stop
 ├── Gather
 ├── Build
 ├── Train
 └── Cast


현재 GameServer.OnReceive()에서는 명령을 받으면 바로 다음과 같이 처리합니다.
case MessageType.UnitCommand:
    var command = UnitCommand.Read(ref r);
    var delaySeconds = CommandDelay(command.ClientTick);
    World.Command(session.PlayerId, command, delaySeconds);
    break;


즉, 명령을 즉시 World에 반영합니다.

하지만 이 입력을 순서대로 기록하고 나중에 동일한 순서로 재생하는 시스템은 확인되지 않았습니다.

권장 구조:
Network Receive
      |
      v
Input Validation
      |
      v
SNR Recording
      |
      v
World.Command
      |
      v
World.Step
      |
      v
State Checksum


기록할 데이터는 다음과 같습니다.
데이터	기록 이유
서버 Tick	재생 시점
입력 수신 순서	명령 처리 순서
PlayerId	명령 주체
UnitCommand	실제 명령
ClientTick	지연 보상 재현
난수 Seed	난수 결과 재현
SimSettings	게임 환경 재현
연결/해제 이벤트	플레이어 상태 재현
외부 입력 결과	비동기 결과 재현

특히 OpenRTS는 현재 GameServer.OnReceive()에서 명령을 바로 처리하므로 입력 수신 순서가 게임 결과에 영향을 줄 수 있습니다.

따라서 단순히 명령을 Tick별로 저장하는 것보다 실제 서버 수신 순서와 World 반영 순서를 모두 기록하는 것이 중요합니다.
3-3. Deterministic State Validation
구현 확인 안 됨 · 추천
LoL의 SNR과 반드시 함께 검토할 기술입니다.

Riot은 동일한 게임을 다시 재생했을 때 최초로 상태가 달라지는 지점을 찾는 시스템을 구축했습니다.
Riot Games – Fixing DivergencesOpenRTS에도 상태 비교 시스템이 있으면 좋겠습니다.

현재는 Snapshot을 통해 클라이언트에 상태를 전달하지만, 서버 시뮬레이션 자체가 동일한 입력에서 동일한 결과를 만드는지 검증하는 별도 시스템은 확인되지 않았습니다.

권장 구조:
Tick 100
  World Hash
  Unit Hash
  Projectile Hash

Tick 101
  World Hash
  Unit Hash
  Projectile Hash

Tick 102
  World Hash
  Unit Hash       <-- 최초 불일치
  Projectile Hash


다만 여기서 중요한 설계 원칙이 있습니다.

Server Authoritative에서는 클라이언트와 서버의 게임 상태가 비트 단위로 동일할 필요가 없습니다.

OpenRTS의 Unity 클라이언트는 표현 전용이고, 서버가 최종 판정을 합니다.

따라서 클라이언트와 서버의 Hash를 비교하는 것보다 서버에서 동일한 입력 기록을 두 번 재생해 결과를 비교하는 것이 LoL의 방식에 더 가깝습니다.

또한 모든 메모리 상태를 비교할 필요도 없습니다.

다음 게임플레이 상태만 대상으로 삼는 것을 추천합니다.
- Entity Position
- HP / Shield
- Unit Order
- Attack Target
- Ability State
- Projectile State
- Cooldown
- Economy State
- Random State
Hash는 Entity ID 순서로 직렬화한 뒤 계산해야 합니다.

특히 부동소수점 값의 NaN, 초기화되지 않은 값, 컨테이너 순회 순서 등을 주의해야 합니다.
3-4. Lag Compensation — 현재 구현의 정확한 한계
부분 구현 · 정책 검토 필요
이 부분은 앞선 대화와도 연결됩니다.

현재 OpenRTS는 다음을 구현하고 있습니다.
- 명령 전달 지연 측정
- 스킬 선딜 단축
- 클라이언트 즉시 시전 연출
- 서버 시전 거부 시 예측 취소
- 본인 투사체 및 범위 예고 즉시 표시
특히 CastPrediction.cs와 AbilitySystem.cs를 함께 사용하는 구조는 합리적입니다.

그런데 중요한 차이가 하나 있습니다.

현재는 과거의 게임 상태를 조회하지 않습니다.

예를 들어 다음 상황입니다.
Client                         Server

Enemy Position A
     |
     | Attack Command
     |------------------------>
     |                         Enemy Position B
     |                         (이미 이동함)
     |                         |
     |                         v
     |                    Current State Validation
     |                         |
     |<------------------------|
     |                     Attack Result


클라이언트가 A 위치에서 적을 선택했지만, 서버에 명령이 도착했을 때는 적이 B 위치에 있을 수 있습니다.

현재 지연 보상은 선딜을 줄여 주지만, A 위치에서의 과거 사거리나 충돌 판정을 복원하지는 않습니다.

이것은 현재 서버 권위 설계의 의도적인 선택으로 볼 수도 있습니다.

RTS에서는 FPS와 같은 무조건적인 Server Rewind를 권장하지 않습니다.

다만 향후 다음 기능을 추가한다면 Historical State Buffer를 고려할 수 있습니다.
HistoricalStateBuffer
 ├── Tick 100
 │    ├── Unit A Position
 │    └── Unit B Position
 ├── Tick 101
 │    ├── Unit A Position
 │    └── Unit B Position
 └── Tick 102
      ├── Unit A Position
      └── Unit B Position


그리고 다음과 같이 사용합니다.
Receive Command
      |
      v
Validate Client Time
      |
      v
Clamp Historical Tick
      |
      v
Query Historical State
      |
      v
Validate Target / Range
      |
      v
Apply Result to Current World


단, 이 기능을 적용하기 전에 다음을 결정해야 합니다.
- 이동 명령에도 과거 상태를 적용할 것인가?
- 근접 공격만 과거 상태를 참조할 것인가?
- 투사체 발사 시점은 과거로 돌릴 것인가?
- 시야 검증은 과거 시점과 현재 시점 중 어느 것을 기준으로 할 것인가?
제 의견은 현재의 선딜 보상을 유지하고, 과거 상태 판정은 실제 플레이에서 공정성 문제가 확인될 때 추가하는 것입니다.
3-5. Command Sequence ID와 중복 방지
보완 검토
현재 UnitCommand의 필드는 다음과 같습니다.
public struct UnitCommand
{
    public CommandType Type;
    public Vector2 Target;
    public int TargetUnit;
    public uint ClientTick;
    public UnitKind Kind;
    public AbilityId Ability;
    public int[] UnitIds;
}


여기에는 별도의 Command Sequence ID가 없습니다.

다만 명령 전송은 DeliveryMethod.ReliableOrdered를 사용하고 있습니다.

따라서 LiteNetLib의 신뢰성·순서 보장 기능이 정상적으로 작동하는 상황에서 별도의 재전송 및 ACK 시스템을 중복 구현할 필요는 없습니다.

그렇지만 다음 기능은 별도로 검토할 가치가 있습니다.
- 명령 중복 실행 방지
- 명령별 고유 ID
- 명령 처리 결과 추적
- 재접속 시 명령 상태 확인
- 디버깅용 명령 순서 추적
특히 다음 상황을 고려해야 합니다.
Command 100 : Move
Command 101 : Attack
Command 102 : Cast


서버에서 어떤 명령이 언제 처리되었는지 추적할 수 있으면 SNR 구현에도 도움이 됩니다.

추천 필드:
public struct UnitCommand
{
    public uint Sequence;
    public uint ClientTick;

    // Existing fields...
}


단, Sequence를 추가한다면 재접속, 명령 분할, 중복 제거 정책까지 함께 설계해야 합니다.

현재 OpenRTS의 구조에서는 필수적인 네트워크 안정성 개선이라기보다는 운영과 디버깅을 위한 개선에 가깝습니다.

3-6. Network Simulation 테스트의 부족
개선 권장
파일:

Client/Assets/Scripts/Net/GameClient.cs

현재 지연과 손실을 인위적으로 발생시키는 기능이 있습니다.
-latency 150
-loss 10


확인한 구현은 다음과 같습니다.
- 송신 지연: RTT의 절반
- 수신 지연: RTT의 절반
- Unreliable 패킷 손실
- 실행 중 지연 및 손실 변경
기본적인 테스트에는 충분합니다.

그러나 LoL 수준의 네트워크 검증을 목표로 한다면 다음이 부족합니다.
테스트 항목	현재 상태
고정 RTT	구현
패킷 손실	구현
가변 Jitter	미확인
패킷 재정렬	미확인
연속 패킷 손실	미확인
Burst Loss	미확인
순간적인 RTT 급증	미확인
업로드·다운로드 비대칭 지연	미확인
장시간 네트워크 불안정	미확인

특히 현재의 손실 시뮬레이션은 수신한 Unreliable 패킷을 확률적으로 버리는 방식입니다.

실제 인터넷 환경에서는 패킷 손실이 연속적으로 발생하는 경우도 있습니다.

따라서 다음과 같은 테스트를 추가하는 것을 권장합니다.
추천 네트워크 테스트 시나리오
Scenario A — 정상 환경

RTT 30~50ms / Loss 0%


Scenario B — 일반적인 불안정 환경

RTT 100ms / Jitter ±30ms / Loss 2%


Scenario C — 열악한 환경

RTT 250ms / Jitter ±80ms / Loss 5%


Scenario D — 순간적인 장애

RTT 50ms → 500ms / Burst Loss 10~20%


Scenario E — 비대칭 네트워크

Upload 30ms / Download 150ms


각 테스트에서 다음을 측정해야 합니다.
- Snapshot 보간 지연
- 외삽 발생 시간
- Snapshot 누락 및 복구
- Missing Baseline
- 명령 전달 지연
- 유닛 화면 위치 오차
- 이벤트 누락
- 서버 Tick 처리 시간

4. LoL과 OpenRTS의 근본적인 차이
여기서 한 가지 중요한 부분을 짚고 넘어가겠습니다.

LoL의 기술을 모두 OpenRTS에 적용하는 것이 정답은 아닙니다.
구분	LoL	OpenRTS
게임 동기화	서버 권위	서버 권위
서버 시뮬레이션	프레임 시간 기반	30Hz 고정 Tick
클라이언트 상태	서버 상태 기반 표현	서버 Snapshot 기반 표현
시간 시스템	Unified Clock	Snapshot 기반 추정 시계
결정론	게임플레이 상태 중심	별도 검증 시스템 미확인
입력 기록	SNR	미구현 확인
상태 불일치 탐지	Replay 비교	미구현 확인
상태 전송	서버 네트워크 복제	15Hz Delta Snapshot
이동 보정	게임 특성에 따른 처리	보간 및 최대 250ms 외삽
지연 보상	다양한 시간 기반 보정	스킬 선딜 보상
과거 상태 조회	공개 자료만으로 상세 구현 확인 불가	미구현 확인

특히 LoL의 Unified Clock은 단순히 네트워크 패킷의 RTT를 측정하는 기술이 아닙니다.

게임 전체의 시간 API를 통합하여 모든 게임 시스템이 일관된 시간 기준을 사용하도록 하는 것이 핵심입니다. 

Riot Games+1

그리고 LoL의 결정론 기술은 P2P Lockstep을 위한 것이 아닙니다.

서버 게임플레이 상태를 재현하고 Chronobreak 및 디버깅에 활용하기 위한 것입니다. 

Riot Games+1

따라서 OpenRTS에서도 클라이언트까지 완전한 Deterministic Simulation을 적용할 필요는 없습니다.

5. 최종 개선 우선순위
실제 구현 상태를 기준으로 다시 정리했습니다.
P0 — 가장 먼저
높은 우선순위

Server Network Recording + Replay
- 명령 수신 순서 기록
- Tick별 입력 재생
- 난수 Seed 및 외부 입력 기록
- 특정 게임 재현
- 버그 발생 시점 추적


P1 — 시간 시스템
권장

Unified Clock 개선
- 4-Timestamp Clock Sync
- RTT / Offset / Jitter 분리
- Outlier Filtering
- Clock Drift 보정
- 시계 동기화 자동 테스트


P2 — 디버깅 시스템

Deterministic State Validation
- World Checksum
- Entity별 Checksum
- 최초 Divergence 탐지
- Replay 비교


P3 — 네트워크 안정성

Network Simulation 강화
- Jitter
- Burst Loss
- Packet Reordering
- 비대칭 지연
- 장시간 연결 테스트


P4 — 게임플레이 개선

선택적 Historical State Query
- Historical State Buffer
- 과거 시점 타겟 검증
- 스킬 종류별 지연 보상 정책
- Server Rewind 필요성 검증


6. 제가 추가로 권장하는 구현 아키텍처
OpenRTS의 기존 구조를 크게 바꾸지 않고 LoL의 좋은 부분을 도입한다면 다음과 같이 설계하겠습니다.
                   GameServer
                       |
          +------------+------------+
          |            |            |
          v            v            v
     InputRecorder  ClockSync   NetworkMetrics
          |            |            |
          v            v            v
      InputLog     ServerClock   RTT/Jitter
          |
          v
      World.Command
          |
          v
       World.Step
          |
          +------------------+
          |                  |
          v                  v
     StateHasher        SnapshotBuilder
          |                  |
          v                  v
    StateCheckpoint    Delta Snapshot
          |
          v
     ReplayVerifier
          |
          v
    Divergence Report


여기서 기존의 다음 구조는 유지합니다.
- World 단일 스레드 시뮬레이션
- GameServer의 네트워크 처리
- SnapshotReceiver
- SnapshotInterpolator
- AbilitySystem
새 기능은 기존 시스템을 대체하기보다 관찰·기록·검증 기능으로 추가하는 것이 좋습니다.

7. 최종 판단
제일 중요한 것은 OpenRTS의 현재 동기화 방식이 잘못된 것이 아니라는 점입니다.

오히려 다음 조합은 RTS에 적합합니다.

Server Authoritative + 30Hz Simulation + 15Hz Delta Snapshot + Adaptive Interpolation + Fog of War Filtering

여기에 스킬 지연 보상과 클라이언트 시전 예측까지 갖추고 있습니다.

제가 가장 높게 평가하는 부분은 Delta Snapshot과 Snapshot ACK 설계입니다. 특히 팀별 상태를 기준으로 델타를 계산하고, 클라이언트가 복원한 상태만 ACK하는 점이 좋습니다.

반대로 현재 가장 큰 기술적 공백은 SNR과 Replay입니다.

이 기능이 없으면 특정 네트워크 환경에서만 발생하는 전투·이동 오류를 재현하고 분석하기가 어려워집니다.

제 추천은 Server Rewind부터 추가하는 것이 아니라, SNR → Replay → State Checksum 순서로 개발하는 것입니다.

LoL의 Chronobreak 기술을 OpenRTS에 도입한다는 관점에서도 가장 가치가 높은 작업입니다.

참고할 Riot 공식 자료:
- Determinism Implementation
- Unified Clock
- Fixing Divergences
※ 이번 분석은 첨부 ZIP의 문서와 소스 정적 검토입니다. 실제 서버 실행, Unity 플레이어 실행, 전체 테스트 통과 여부까지 확인한 것은 아닙니다.