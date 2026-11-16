# 테스트와 확인

세 겹으로 확인합니다.

| 층 | 무엇 | 실행 |
| --- | --- | --- |
| 서버 단위·통합 테스트 | 시뮬레이션 규칙, 직렬화, 실제 UDP 접속 | `cd Server && dotnet test` |
| 리포트 테스트 | 시나리오별 수치(겹침, 대역폭, 전투 시간, 틱 비용) 출력 | `dotnet test --filter <이름> --logger "console;verbosity=detailed"` |
| 실제 클라이언트 자동 확인 | 빌드한 Unity 플레이어 + 실제 서버 | `OpenRTS.exe -batchmode -nographics -autotest...` |

변경한 뒤에는 최소한 `dotnet test` 전체와, 클라이언트를 건드렸으면 아래 "클라이언트 스크립트 컴파일 확인"을 합니다. 프로토콜·동기화를 건드렸으면 자동 확인까지 돌립니다.

## 1. 서버 테스트 (`Server/tests/OpenRTS.Server.Tests`, xUnit)

| 파일 | 내용 |
| --- | --- |
| `MessageTests` | 메시지 왕복, 잘린·잘못된 패킷, 스냅샷 조각 계획(1023바이트 이하), 필드 묶음 델타 차분·복원, 수신기(baseline 없음) |
| `SnapshotInterpolatorTests` | 보간·외삽·시계 추정·적응형 지연 |
| `GameLoopTests` | 고정 틱, 밀린 틱 상한 |
| `WorldTests` | 이동 속도·도착, 맵 경계, 정면 교차, 시나리오별 최소 간격, 막힘 |
| `NavigationTests` | 직선 경로, 통로, 큰 유닛, 무작위 50경로 벽 회피, 선분-사각형 거리, 벽 ORCA, Blocks 우회, **Gate 50유닛 통과(3단계 완료 기준)**, 재경로 |
| `CombatTests` | 근접 처치 시간, 투사체 비행 시간, 대상 사망 시 투사체 소멸, 이동 중 적 무시, 벽 돌아 추적, 리쉬, 도움 요청, 우선순위, 중립 제외, 정지로 선딜 취소, 소유권, **Battle 전멸·재시작(4단계)** |
| `VisionTests` | 격자 판정, 벽 가림, 적·아군·중립, 안개 끔, 안개 속 공격 명령 거부 |
| `AbilityTests` | 선딜 지연 보상(상한·걸어갈 때·AI·설정 끔), 거부된 시전 명령의 CastCancel, 보유 목록, 직선 투사체(첫 적·벽·터널링), 범위+둔화, 예고 범위 회피, 사거리 접근, 돌진(벽 앞 정지·기절), 기절(시전 취소·재사용 대기 없음·이동 정지), 보호막, 정신 집중(회복·끊김), 명령(한 명/모두·소유권), 유닛 대상 검증(안개·적아군), 스크립트 훅, **전투 봇이 능력을 씀** |
| `EconomyTests` | 시작 기지, 채집·반납, 생산·비용, 건설 검증·환불, **전체 흐름(6단계)**, 광맥 고갈, 건물 파괴 |
| `GameServerIntegrationTests` | 실제 UDP 소켓으로 서버 + `TestClient`: 입장·이동, 대형, 소유권, 해제, 버전 거부, 450유닛 분할 스냅샷, 델타 상태 일치, 네트워크 전투, **시야 밖 적이 어떤 패킷에도 없음(5단계)**, Skirmish 생산과 자원 비공개 |
| `CollisionReportTests` | 리포트 3종 (아래) |

### 테스트 작성 요령

- `World` 는 네트워크 없이 바로 만들고 `Step(SimConstants.TickDelta)` 를 반복합니다. 테스트마다 있는 `RunUntil(condition, maxSeconds)` 패턴을 쓰세요(시간 초과 시 실패 메시지).
- **시야는 Step 끝에 계산됩니다.** 생성 직후 시야가 필요한 명령(공격·건설)을 테스트하려면 먼저 한 번 `Step` 하세요.
- 길찾기 격자 재구성도 다음 Step 시작에 일어납니다.
- 반격하지 않는 표적은 `HoldFire = true`, 서로 쫓지 않게 하려면 `Order = UnitOrder.Move`.
- 통합 테스트의 `TestClient` 는 Unity 클라이언트와 같은 `SnapshotReceiver` 를 쓰고 ack 를 보냅니다. 받은 조각 원본은 `RawEntries`(필터 검증용).
- 서버가 실제로 보낸 상태는 `GameServer.TryGetSentSnapshot(team, tick, out state)` 로 비교합니다.
- 타이밍 의존 기대값은 범위로(`InRange`) 쓰고, 근거(공식)를 주석에 적습니다.

## 2. 리포트

판정은 최소한(NaN·승패 없음 등)이고 수치를 출력합니다. 결과는 루트 README 표에 옮겨 적습니다.

| 테스트 | 출력 |
| --- | --- |
| `CollisionReport` | 시나리오 × 회피 설정별 최소 간격, 겹침 쌍, 벽 침범, 막힘, 재경로, 왕복 수, 1틱 평균 (30초) |
| `BandwidthReport` | 전체 vs 델타 스냅샷 바이트/초 (기준 = 2스냅샷 전 ack 가정), 시야 켬/끔 |
| `BattleReport` | 맵·인원별 전멸까지 시간, 생존, 최소 간격, 1틱 평균/최대, **최악 틱의 단계별 시간**(`StepProfile`), 미룬 경로 수 |

1틱 최대값은 PC 부하에 따라 몇 ms 흔들립니다. 첫 케이스는 JIT 때문에 높게 나옵니다.

## 3. 실제 클라이언트 자동 확인

먼저 플레이어를 빌드하고(client.md 7) 서버를 띄웁니다.

```bash
dotnet run --project Server/src/OpenRTS.Server -- --bots 0
Client/Builds/Windows/OpenRTS.exe -batchmode -nographics -logFile out.log <옵션>
```

| 옵션 | 확인 | 통과 조건 |
| --- | --- | --- |
| `-autotest` | 이동 동기화 | 내 유닛 하나를 8 이동 → 화면 위치 오차 < 0.01, 화면 최대 속도 < 1.5배 |
| `-autotest-collision <봇> <시나리오> [맵]` | 설정 전파·충돌 | 서버 적용 확인 후 12초간 최소 간격 ≥ 0.8, 벽 침범 0, 화면 유닛 수 |
| `-autotest-battle [봇] [맵]` | 전투·시야·능력 | 시작 1초 뒤 받은 적 0(안개 켬), 한쪽 전멸 후 화면에서도 진 팀 0(내 팀이 지면 안개 때문에 이긴 팀도 안 보이는 게 정상), 이벤트·투사체·체력 감소, 능력 이벤트 수신, 내 영웅 Shock Bolt 시전이 재사용 대기로 확인됨(기회가 있었으면) |
| `-autotest-economy [맵]` | 경제 흐름 | 채집 → 일꾼 생산 → 병영 건설 → 근접 생산, 단계별 시간 기록 |
| `-autotest-cast [on\|off]` | 능력 지연 감추기 | 영웅 Shock Bolt 4회: 시전 자세가 0.1초 안에 보임, 서버 확인·투사체 보임 시간 기록(이벤트 손실 라운드는 제외, 2회 이상 측정), 재사용 대기 중 강제 시전 → 서버 거부 → 예측이 제한 시간 안에 거둬짐. on/off = 서버 선딜 지연 보상 |

- 로그에 `AUTOTEST PASS/FAIL: ...` 한 줄, 종료 코드 0/1.
- `-latency 150 -loss 10` 처럼 지연·손실과 함께 돌릴 수 있습니다.
- **자동 확인은 서버 설정을 바꿉니다.** 앞 확인이 남긴 상태(봇 수, 시나리오)가 다음 확인에 영향을 줄 수 있으니, 이동 확인은 봇 0 상태에서, 순서를 바꿀 땐 서버를 다시 띄우세요. 경제 확인은 스스로 시나리오를 다시 시작합니다.
- 충돌 확인은 내 유닛을 1개로 줄입니다(무작위 위치의 무리가 봇 경로에 끼면 결과가 흔들림).
- 두 클라이언트를 동시에 띄우면 서로 다른 팀이 됩니다.

## 4. 클라이언트 스크립트 컴파일 확인 (에디터 없이)

Unity 에디터를 열지 않고 클라이언트 C# 를 빠르게 확인하는 방법입니다. Unity 의 관리 DLL 을 참조하는 임시 SDK 프로젝트를 저장소 **밖**에 만듭니다.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <Nullable>disable</Nullable>
    <DefineConstants>UNITY_5_3_OR_NEWER;UNITY_6000_0_OR_NEWER;ENABLE_LEGACY_INPUT_MANAGER</DefineConstants>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="(저장소)\Client\Assets\Scripts\**\*.cs" />
    <ProjectReference Include="(저장소)\Shared\OpenRTS.Shared.csproj" />
    <!-- (Unity 설치)\Editor\Data\Managed\UnityEngine\UnityEngine*.dll 을 Private=false 로 모두 참조 -->
    <!-- LiteNetLib: (저장소)\Client\Assets\Plugins\LiteNetLib\LiteNetLib.dll -->
  </ItemGroup>
</Project>
```

`dotnet build --no-incremental` 이 경고 0·오류 0 이면 됩니다. 에디터 전용 코드(`Assets/Editor`)는 대상이 아닙니다. 최종 확인은 Unity 빌드로 합니다.

## 5. 디버깅 도구

| 도구 | 쓰임 |
| --- | --- |
| F1 | 보간 전 최신 스냅샷 위치 — 보간·외삽 문제 확인 |
| F2 패널 지표 | 최소 간격, 겹침 쌍, 막힘·재경로, 벽 침범, 서버 1틱, 대역폭, 조각 손실, baseline 없음 |
| HUD | RTT, 명령 지연, 보간 지연(목표), 외삽 여부, 델타 수 |
| `[` `]` `-` `=` | 실행 중 지연·손실 바꾸기 |
| F2 의 fog / delta / avoidance 등 토글 | 기능을 끄고 비교 |
| `World.LastProfile` | 서버 1틱 단계별 시간 |
| 서버 콘솔 로그 | 접속·입장(팀)·설정 변경·잘못된 패킷 |
