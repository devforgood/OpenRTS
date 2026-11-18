확인했어. 이번에는 단순히 README의 기능 목록만 본 것이 아니라, OpenRTS의 아키텍처 설명과 능력 시스템 문서까지 확인해서 앞서 설명한 기술들과 대조했어.



먼저 결론부터 말하면, OpenRTS에는 우리가 이야기한 네트워크 동기화 기술 중 상당수가 이미 구현되어 있어. 특히 Snapshot Interpolation, 서버 시간 추정, Delta Snapshot, UDP 통신, 스킬 지연 보상은 실제 구현 범위에 포함되어 있어.



다만 Client Prediction, Server Reconciliation, Server Rewind, Deterministic Replay는 우리가 앞서 설명한 형태로 구현되어 있지 않아.

OpenRTS 네트워크 기술 구현 현황

구현 확인

다수 구현



GitHub README 및 docs/abilities.md 기준

기술	상태

Server Authoritative	구현

Fixed Tick	구현

Unified Clock 계열	구현

Snapshot Interpolation	구현

Adaptive Interpolation Delay	구현

Delta Snapshot	구현

Client Prediction (이동)	미구현

Server Reconciliation	미구현

Lag Compensation (스킬 선딜)	구현

Server Rewind	미구현

Deterministic Simulation	불필요한 설계

Server Network Recording	미구현

UDP Reliable Command	부분 구현

Server Allocation	미구현





출처: OpenRTS README · OpenRTS 능력 시스템 문서.



1\. Server Authoritative

구현 완료

OpenRTS는 명확하게 Server Authoritative 상태 동기화 방식을 채택하고 있어.



README의 4.1절에 이 설계가 명시되어 있어.



서버가 이동, 충돌, 전투를 계산하고 클라이언트는 서버 상태를 전달받아 화면에 표현하는 구조야.



실제 서버 처리 순서도 정의되어 있어.

1\. Network Receive

2\. Command Apply

3\. AI Update

4\. Movement

5\. Combat

6\. Economy

7\. Vision

8\. Snapshot Send





특히 AI 판단을 이동보다 먼저 수행해서 1 Tick 지연을 줄이도록 설계한 점이 좋아.



내가 평가하는 부분: 서버 권한의 경계가 명확해. 클라이언트가 유닛의 이동 결과나 공격 결과를 결정하지 않아. 



devforgood/OpenRTS · GitHub+1



2\. Unified Clock

구현 완료

이 부분은 네가 앞서 질문했던 LoL Unified Clock과 직접적으로 관련 있어.



OpenRTS에는 다음 구현이 명시되어 있어.

\- 서버 Snapshot에 Server Tick 포함

\- 클라이언트의 서버 시계 추정

\- 점진적 시간 보정

\- 적응형 렌더링 지연

\- Snapshot을 기준으로 서버 시간 계산

특히 README의 539행에 클라이언트 보간 기능으로 서버 시계 추정(점진 보정)이 명시되어 있어.



또한 650\~651행에서는 기존 참고 서버의 RTT 측정 및 시간 동기화 문제를 개선한 설계가 설명되어 있어. 



devforgood/OpenRTS · GitHub+1



다만 앞서 내가 설명한 NTP 4 Timestamp 방식과 OpenRTS가 완전히 동일한 알고리즘을 쓴다고 단정할 수는 없어.



현재 확인 가능한 구현은 서버 Tick 기반 시간 추정과 점진 보정이야.



3\. Snapshot Interpolation

구현 완료

이건 OpenRTS에서 상당히 잘 구현된 부분이야.



현재 구조는 다음과 같아.

OpenRTS Snapshot 흐름

Server Simulation



30Hz Fixed Tick





Snapshot Generation



15Hz Snapshot





Client Snapshot Buffer



Snapshot 수신 및 조립





Interpolation



약 2개 Snapshot만큼 과거 시점 재생





특히 중요한 특징이 있어.

적응형 Interpolation Delay

고정적으로 100ms를 지연시키는 것이 아니라 네트워크 상태에 따라 보간 지연을 조절해.



또한 Snapshot이 도착하지 않을 경우 최대 0.25초까지 외삽하는 기능도 명시되어 있어.



README에 기록된 테스트에서는 다음 환경에서도 이동 동기화 자동 테스트를 통과했어.

네트워크 환경	결과

로컬	PASS

RTT 150ms / 손실 10%	PASS

RTT 300ms / 손실 25%	PASS



단, 이 테스트는 유닛이 목표 지점까지 튀지 않고 도착하는지 확인하는 것이지 모든 네트워크 조건에서 완벽한 동기화를 보장한다는 의미는 아니야. 



devforgood/OpenRTS · GitHub+1



4\. Client Prediction / Server Reconciliation

이동 예측 미구현

여기는 중요한 차이가 있어.



앞서 내가 설명했던 Client Prediction은 클라이언트가 서버 응답을 기다리지 않고 자신의 유닛을 실제로 이동시키는 방식이었지.



그런데 OpenRTS는 이 방식을 사용하지 않아.



README 4.6절에 다음 설계가 명시되어 있어.

\- 클라이언트는 표현 전용

\- 실제 유닛 이동은 서버 응답 후 시작

\- 명령을 보낼 때 클릭 마커나 선택 유닛 방향 등은 즉시 표시

\- 실제 이동 위치는 서버 Snapshot을 통해 보간

즉 다음과 같은 구조야.

Client                    Server

&#x20; |                         |

&#x20; | Move Command            |

&#x20; |------------------------>|

&#x20; |                         |

&#x20; | Show Click Marker       |

&#x20; |                         |

&#x20; |                         | Validate

&#x20; |                         | Simulate Movement

&#x20; |                         |

&#x20; |<------------------------|

&#x20; | Snapshot                |

&#x20; |                         |

&#x20; | Interpolation           |





여기서 구분해야 하는 것이 있어.

기능	OpenRTS

명령 즉시 시각적 피드백	구현

실제 이동 Prediction	미구현

서버 위치로 Reconciliation	해당 이동 Prediction 구조가 없어 불필요

상대 유닛 Interpolation	구현



이것은 반드시 개선해야 하는 결함은 아니야.



RTS는 FPS보다 이동 명령의 반응성에 대한 허용 범위가 크고, 특히 ORCA 충돌 회피를 서버에서 계산하기 때문에 클라이언트에서 같은 이동을 예측하면 결과가 어긋날 가능성이 높아.



현재 구조를 유지하는 것도 충분히 합리적이야. 



devforgood/OpenRTS · GitHub+1



5\. Lag Compensation

스킬 선딜 보상 구현

이 부분은 상당히 흥미로워.



OpenRTS에는 단순히 RTT를 측정하는 수준을 넘어, 실제 스킬 시전 시 지연 보상을 적용하는 기능이 구현되어 있어.



다만 우리가 앞서 설명했던 Server Rewind와는 다른 방식이야.

5-1. 구현된 방식: Windup Credit

스킬을 사용할 때 다음과 같은 상황이라고 해보자.

스킬 선딜: 300ms

명령 지연: 150ms





일반적인 방식이라면 서버가 명령을 수신한 후 300ms를 기다렸다가 스킬을 발동해.



하지만 OpenRTS는 명령이 서버에 도착하기까지 걸린 지연을 고려해서 선딜 시간을 줄여.



문서에 공개된 공식은 다음과 같아.

\\\[

Credit=\\min(Delay,\\ CastTime\\times0.5,\\ 0.1s)

\\]



\\\[

RemainingWindup=CastTime-Credit

\\]



예를 들어:

\- CastTime = 300ms

\- CommandDelay = 150ms

이면,

\\\[

Credit=\\min(150,150,100)=100ms

\\]



따라서:

\\\[

RemainingWindup=300-100=200ms

\\]



서버는 선딜을 200ms만 처리하면 돼.

5-2. 실제 C# 구현 구조

OpenRTS 문서에서 확인되는 관련 위치는 다음과 같아.

\- GameServer.CommandDelay

\- UnitCommand.ClientTick

\- AbilitySystem.WindupCredit

\- AbilitySystem의 Windup 처리

개념적으로는 다음과 같은 구조야.



public static float CalculateWindupCredit(

&#x20;   float commandDelay,

&#x20;   float castTime)

{

&#x20;   return MathF.Min(

&#x20;       MathF.Min(commandDelay, castTime \* 0.5f),

&#x20;       0.1f);

}



public static float CalculateRemainingWindup(

&#x20;   float commandDelay,

&#x20;   float castTime)

{

&#x20;   float credit = CalculateWindupCredit(

&#x20;       commandDelay,

&#x20;       castTime);



&#x20;   return castTime - credit;

}







이 코드는 문서에 공개된 공식을 재현한 예시야. 실제 저장소의 함수 구현을 그대로 복사한 것은 아니야.

5-3. 중요한 보안 제한

OpenRTS는 클라이언트가 과거의 ClientTick을 조작해서 지나치게 많은 선딜 보상을 받지 못하도록 제한해.

\- 미래 Tick은 지연 0으로 처리

\- 최대 측정 지연 1초

\- 선딜 보상 최대 선딜의 50%

\- 선딜 보상 최대 100ms

\- 이미 이동을 시작한 후 접근해서 사용하는 스킬은 보상하지 않음

\- AI가 사용하는 스킬은 보상하지 않음

이 부분은 좋은 설계야.

5-4. 실제 테스트 결과

README의 능력 시스템 문서에는 다음 결과가 공개되어 있어.

왕복 지연	보상	투사체가 보이는 시간

150ms	끔	505ms

150ms	켬	400\~411ms

300ms + 손실 10%	끔	696ms

300ms + 손실 10%	켬	621ms



추가로 내 유닛의 발사 이벤트와 범위 예고를 수신 즉시 재생하는 기능도 있어.



150ms 환경에서 투사체가 화면에 보이는 시간이 505ms에서 약 301\~310ms로 줄었다고 보고하고 있어.



이건 상당히 좋은 개선이야. 



GitHub+1



5-5. 아직 없는 것: Server Rewind

현재 OpenRTS의 Lag Compensation은 스킬 선딜 시간을 줄이는 것이야.



다음 기능은 확인되지 않았어.

\- 과거 유닛 위치 저장

\- 공격 시점의 과거 상태 조회

\- 과거 상태에서 충돌 재판정

\- 과거 상태의 공격 결과를 현재 상태에 적용

따라서 FPS에서 사용하는 Server Rewind 방식은 아직 구현되어 있지 않다고 판단하는 것이 맞아.



다만 현재 RTS의 스킬 시스템에서 모든 공격을 Rewind해야 하는 것은 아니야. 지금처럼 서버의 현재 시뮬레이션 상태에서 판정하면서 시각적 반응을 앞당기는 방법도 충분히 좋은 선택이야.



6\. Deterministic Simulation

완전한 결정론 불필요

여기서 OpenRTS의 설계 방향이 명확하게 드러나.



README 4.1절에서 Lockstep과 Server Authoritative를 비교하면서 다음을 설명하고 있어.

\- Lockstep은 모든 클라이언트가 비트 단위로 동일한 결과를 만들어야 함

\- Server Authoritative에서는 서버만 게임 시뮬레이션을 수행

\- 클라이언트는 서버의 상태를 받아서 표현

\- 따라서 클라이언트 간 완전한 Determinism이 필요하지 않음

특히 OpenRTS는 Unity의 Float와 서버의 시뮬레이션을 독립적으로 운영하는 방향을 선택했어. 



devforgood/OpenRTS · GitHub



내가 앞서 설명한 결정론적 난수 생성이나 Fixed Point 연산은 현재 OpenRTS의 동기화 모델에서 필수 사항은 아니야.



다만 서버에서 동일한 상황을 재현할 수 있도록 서버 시뮬레이션을 결정론적으로 만드는 것은 별개의 가치가 있어.

7\. Server Network Recording / Replay

미구현

이 기능은 아직 확인되지 않아.



현재는 게임 상태 Snapshot과 입력을 통한 과거 게임 재현 시스템이 구현 범위에 포함되어 있지 않아.



README의 로드맵 14단계에도 리플레이(명령 로그)가 향후 작업으로 기재되어 있어.



앞으로 구현한다면 다음 구조를 추천해.

Tick 1000

&#x20; Snapshot



Tick 1001

&#x20; Player 1 : Move



Tick 1002

&#x20; Player 2 : Attack



Tick 1003

&#x20; Player 1 : Cast Skill



Tick 1100

&#x20; Snapshot





이 구조는 게임 리플레이뿐 아니라 다음 용도로도 유용해.

\- 네트워크 버그 재현

\- 전투 결과 분석

\- 서버 크래시 분석

\- 비정상 명령 추적

\- 게임 밸런스 분석



devforgood/OpenRTS · GitHub

8\. UDP Reliability

일부 구현

OpenRTS는 LiteNetLib 2.1.5를 사용하고 있어.



UDP 기반이며 LiteNetLib이 제공하는 신뢰성 및 비신뢰성 전송 방식을 활용해.



여기서 주목할 점은 Snapshot ACK야.

Snapshot ACK 구조

Client                         Server

&#x20; |                              |

&#x20; | Snapshot Tick 100            |

&#x20; |<-----------------------------|

&#x20; |                              |

&#x20; | SnapshotAck(100)             |

&#x20; |----------------------------->|

&#x20; |                              |

&#x20; | Delta Snapshot               |

&#x20; | Baseline = 100               |

&#x20; |<-----------------------------|





클라이언트가 마지막으로 정상 수신한 Snapshot을 서버에 알려주면 서버는 해당 Snapshot을 기준으로 변경된 유닛만 전송해.



기준점이 없거나 너무 오래되면 전체 Snapshot을 전송해.



이것은 일반적인 UDP ACK와는 구분해야 해.

기능	OpenRTS

LiteNetLib 신뢰성 채널	사용

비신뢰성 채널	사용

Snapshot ACK	구현

Snapshot Delta Baseline	구현

모든 패킷에 대한 별도 ACK Bits 시스템	구현 여부 확인 안 됨

Reliable Command의 자체 재전송 큐	확인되지 않음



특히 이전에 이야기했던 Sequence + Ack + AckBits를 모든 패킷에 적용하는 커스텀 프로토콜을 별도로 구현해야 한다고 볼 필요는 없어. LiteNetLib의 신뢰성 채널이 제공하는 기능과 중복될 수 있기 때문이야. 



devforgood/OpenRTS · GitHub+1





9\. Server Allocation

미구현

현재 OpenRTS는 단일 게임 서버를 실행하는 구조야.



다음과 같은 게임 서버 운영 시스템은 아직 구현 범위에 없어.

\- 게임 매치 생성 및 서버 할당

\- CPU 부하 기반 서버 선택

\- 서버 Auto Scaling

\- 게임 서버 장애 감지 및 재배치

\- 매치메이킹과 서버 연결 관리

이 부분은 네트워크 동기화 기술보다는 별도의 게임 서비스 운영 아키텍처로 보는 게 맞아.



10\. 추가로 발견한 OpenRTS의 좋은 설계

위에서 이야기한 기술 외에도 내가 주목한 부분이 있어.

10-1. Delta Snapshot

구현 완료

OpenRTS는 클라이언트가 마지막으로 수신한 Snapshot을 기준으로 변경된 유닛만 전송해.



특히 필드 단위 Delta를 사용하고 있어.

필드	크기

공통 Entity ID + 필드 표시	3 bytes

Position	4 bytes

Info	3 bytes

Health	1 byte

Action	3 bytes



300개 유닛을 대상으로 한 30초 측정 결과도 README에 공개되어 있어.

300개 유닛 Snapshot 대역폭

README에 기록된 테스트 결과, KB/s







전체 전송



Delta 전송





020406080정지 유닛배회원형 교차





























특히 정지한 유닛이 많을 때 효과가 상당히 커.



devforgood/OpenRTS · GitHub

10-2. Fog of War와 정보 보안

구현 완료

이것도 RTS에서는 매우 중요해.



클라이언트에서 적 유닛을 화면에 숨기는 것만으로는 맵핵을 방지할 수 없어.



OpenRTS는 서버에서 팀별 시야를 계산하고, 시야 밖 적 유닛은 아예 Snapshot에 넣지 않아.



또한 공격 명령을 서버에서 검증해서 보이지 않는 적을 대상으로 공격하는 명령을 거부해.



단순한 렌더링 문제가 아니라 네트워크 데이터 자체를 제한하는 방식이지. 



devforgood/OpenRTS · GitHub





11\. 최종 평가 및 개선 권장 사항

내가 보기에는 OpenRTS의 네트워크 동기화 구조는 상당히 합리적으로 구성되어 있어.



특히 현재 구조에서 굳이 Client Prediction이나 Server Rewind를 추가하는 것보다는 먼저 기존 동기화 기술의 안정성을 높이는 게 좋아.

개선 우선순위

현재 구현 유지

1\. Snapshot Interpolation



이미 적응형 지연과 외삽까지 구현되어 있어. 네트워크 품질 테스트를 확대하는 것을 추천해.





현재 구현 유지

2\. Delta Snapshot / Snapshot ACK



이미 구현되어 있고, 300개 유닛 테스트도 있어. 향후 1,000개 이상으로 확장 테스트하면 좋겠어.





개선 추천

3\. Clock Synchronization



시간 추정의 오차, Drift, Jitter를 계측하고 동기화 안정성을 검증하는 테스트를 추가하면 좋아.





개선 추천

4\. Lag Compensation



현재 스킬 선딜 보상은 좋은 접근이야. 다양한 스킬 유형과 명령 지연에서 판정의 공정성을 검증하면 좋겠어.





향후 추가

5\. Server Recording / Replay



게임 입력 기록과 Snapshot을 결합해 네트워크 버그 재현 시스템을 구축하는 것을 추천해.





필요시 추가

6\. Client Prediction



이동 명령의 체감 지연이 문제가 되는 경우에만 선택적으로 검토해.





12\. 확인한 주요 소스 코드 위치

실제 저장소에서 확인할 때는 아래 경로부터 보는 것을 추천해.

기능	소스 경로

서버 시뮬레이션	Server/src/OpenRTS.Server/Simulation/

Snapshot 생성	Server/src/OpenRTS.Server/ 내 Replication 관련 코드

Snapshot 복원	Shared/Runtime/Interpolation/

클라이언트 보간	Shared/Runtime/Interpolation/

스킬 지연 보상	Server/.../Simulation/Abilities/AbilitySystem.cs

스킬 명령 지연 측정	GameServer.CommandDelay

스킬 네트워크 처리	Shared/Runtime/Protocol/

Fog of War	Shared/Runtime/Combat/VisionGrid.cs



참고 자료:

\- OpenRTS GitHub

\- Ability 시스템 상세 문서

\- 전체 아키텍처 및 구현 현황

위 경로는 저장소 문서에서 확인한 위치야. 이번 검토에서는 모든 .cs 파일을 개별적으로 열어 코드 라인 단위 검증까지 수행한 것은 아니므로, 실제 코드 수준의 추가 수정 여부는 별도 확인이 필요해.



내 의견

현재 OpenRTS에서 가장 잘 선택한 부분은 Client Prediction을 무리하게 적용하지 않고 Server Snapshot + Interpolation을 선택했다는 것이야.



특히 네가 사용하는 구조와도 잘 맞아.

\- 서버가 유닛 이동을 최종 결정

\- RVO2가 충돌 회피를 담당

\- 서버 Snapshot이 실제 위치를 전달

\- 클라이언트가 과거 Snapshot을 보간

\- 스킬은 즉시 시각적 반응을 보여 주되 최종 판정은 서버가 담당

이런 구조라면 굳이 모든 유닛을 클라이언트에서 재시뮬레이션할 필요가 없어.



다음으로 가장 깊게 분석할 가치가 있는 것은 Snapshot Interpolation + Adaptive Render Delay야.



특히 네 프로젝트의 서버가 30Hz이고 Snapshot이 15Hz인 구조에서 RTT 50ms, 100ms, 200ms, 300ms일 때 보간 지연을 어떻게 결정하고, 패킷 손실이 발생했을 때 어떻게 외삽하는지 살펴보면 좋겠어.

