# 개발 가이드

## 1. 준비

| 도구 | 버전 | 비고 |
| --- | --- | --- |
| .NET SDK | 11.0.100 이상 (같은 기능 밴드) | `global.json` 고정, `rollForward: latestFeature` |
| Unity | 6000.6.0f1 | Unity Hub 로 `Client` 폴더 열기 |
| IDE | Visual Studio / Rider / VS Code | 서버는 `Server/OpenRTS.slnx` |

```bash
cd Server
dotnet build
dotnet test                                           # 전체 (리포트 포함 약 1분)
dotnet test --filter "FullyQualifiedName!~CollisionReport"   # 리포트 빼고 (약 25초)
dotnet run --project src/OpenRTS.Server -- --help      # 옵션
```

Unity: `Assets/Scenes/Main.unity` 를 열고 Play. 서버가 먼저 떠 있어야 합니다(`127.0.0.1:7777`).

### 서버 명령줄

```text
--port <n>  --bots <n>  --scenario wander|converge|circleswap|crossing|pass|battle|skirmish
--map open|gate|blocks  --player-units <n>  --radius <r>  --speed <v>
--no-avoidance  --no-repath  --no-delta  --no-debug-commands
```

설정은 실행 중 F2 패널로 바꿀 수 있습니다(`--no-debug-commands` 면 거부).

## 2. 저장소 규칙

- **빌드 출력은 `artifacts/`**(`Directory.Build.props` 의 `UseArtifactsOutput`). `Shared/` 안에 `bin`·`obj` 가 생기면 Unity 가 그 안의 생성 코드까지 컴파일하므로 절대 두지 마세요.
- **`.meta` 파일은 커밋합니다**(Shared·Client 모두). Unity 가 새 파일에 만들어 주고, GUID 가 바뀌면 참조가 깨집니다. 새 `.cs` 를 추가했으면 Unity(또는 배치 빌드)를 한 번 돌려 `.meta` 를 만든 뒤 함께 커밋하세요.
- 무시하는 것: `artifacts/`, `Client/Library|Temp|Logs|UserSettings|Builds`, Unity 가 만드는 `Client/*.sln`·`*.csproj`.

## 3. 코드 규칙

### Shared

- **C# 9, netstandard2.1**. Unity 컴파일러가 받는 범위입니다. 파일 범위 네임스페이스·컬렉션 식·`required` 같은 C# 10 이후 문법은 `dotnet build` 가 먼저 막아 줍니다. `record`·`init` 은 C# 9 이지만 netstandard2.1 에 `IsExternalInit` 이 없어 쓰지 않습니다.
- 참조 형식에 nullable 주석(`?`)을 쓰지 마세요. `OpenRTS.Shared.csproj` 는 nullable 이 켜져 있어 null 을 넘기면 경고가 나고, Unity 쪽은 nullable 문맥이 꺼져 있어 `?` 에 경고가 납니다. 빈 값은 `Array.Empty<T>()` 처럼 실제 값으로 넘깁니다.
- `UnityEngine`·서버 전용 라이브러리 참조 금지(asmdef `noEngineReferences: true`). 벡터는 `System.Numerics.Vector2`.
- 서버와 클라이언트가 **같은 결과를 내야 하는 계산**(맵 벽, 유닛 수치, 시야)은 Shared 에 두고 양쪽이 같은 함수를 부릅니다.

### 서버

- C# 최신(net11.0), nullable 켬, 경고 0 유지.
- 시뮬레이션은 **한 스레드**에서 돕니다(LiteNetLib `PollEvents` 포함). 잠금을 넣지 마세요. 병렬화하려면 Step 안의 순수 계산 단계(예: ORCA 속도 계산)만.
- 틱마다 할당을 줄입니다: 목록 버퍼를 필드로 두고 `Clear` 해서 재사용하는 기존 패턴을 따르세요.
- 클라이언트가 보낸 값은 믿지 않습니다: 소유권, 비용, 위치, 시야, 범위(`SimSettings.Clamped`)를 서버에서 다시 확인.
- 자동으로 경로를 요청하는 코드는 `TryMoveUnitBudgeted` + 재요청 타이머(simulation.md 2.6).

### 클라이언트

- 판정 코드를 넣지 않습니다. 필요한 정보는 서버가 보내게 하세요.
- 새 GUI 창은 `Area` 를 공개하고 `RtsInput.IsOverDebugWindow` 에 추가(클릭 관통 방지).
- 투명 셰이더가 필요하면 빌드에 항상 포함되는 `Sprites/Default` 를 쓰세요(Standard 의 Fade 변형은 빌드에서 빠질 수 있음).

### 공통

- 주석·문서는 한국어. 식별자·커밋 메시지는 영어.
- 공개 상수에는 의미와 단위(초, 유닛, 바이트)를 `<summary>` 로 적습니다. 숫자를 바꾸면 루트 README 와 docs 의 표도 같이 고칩니다.
- 측정값을 문서에 적을 때는 어떤 테스트·명령으로 쟀는지 함께 적습니다.

## 4. 라이선스 규칙 (중요)

참고 대상인 LeagueSandbox GameServer 는 **AGPL-3.0** 입니다.

1. 참고 서버 코드는 **복사하지 않습니다.** 구조와 알고리즘을 이해한 뒤 보지 않고 새로 작성합니다. 클래스·메서드 이름, 주석, 코드 구조를 그대로 옮기지 않습니다.
2. 외부 코드·라이브러리는 MIT / BSD / Apache-2.0 / Boost 같은 허용적 라이선스만 쓰고, 가져오기 **전에** 라이선스를 확인합니다.
3. 가져오면 `THIRD_PARTY_NOTICES.md` 에 출처·버전(커밋)·라이선스·사용 위치를 적고, 수정했으면 파일 머리말에 변경 사항을 남깁니다(예: `OrcaSolver.cs`).
4. 원작 게임(LoL)의 데이터·이름·에셋은 쓰지 않습니다.

현재 외부 구성요소: LiteNetLib 2.1.5 (MIT), RVO2 Library C# 일부 (Apache-2.0). 검토했지만 안 쓴 것도 `THIRD_PARTY_NOTICES.md` 에 있습니다. 서버·클라이언트의 LiteNetLib 버전은 같게 유지합니다(NuGet 버전을 올리면 `Client/Assets/Plugins/LiteNetLib` 의 DLL 도 같은 패키지에서 복사).

## 5. 변경 체크리스트

### 프로토콜을 바꿀 때

- [ ] `SimConstants.ProtocolVersion` 올림
- [ ] 공개 범위(팀·플레이어 필터) 정함 — architecture.md 4.3
- [ ] 패킷 1023바이트 한도 확인 / 나눠 보내기
- [ ] `MessageTests` 왕복 + 잘린 패킷, 필요하면 통합 테스트의 `TestClient`
- [ ] `GameClient.Handle`, docs/protocol.md 표

### 시뮬레이션 규칙을 바꿀 때

- [ ] 해당 테스트 파일에 동작 테스트(`RunUntil`)
- [ ] 리포트로 수치 변화 확인(`CollisionReport` / `BattleReport` 의 1틱 비용)
- [ ] 루트 README 의 표·설명 갱신
- [ ] Step 순서에 넣을 자리 확인(판단은 이동 전, 명중은 이동 후)

### 클라이언트를 바꿀 때

- [ ] 에디터 없이 컴파일 확인(testing.md 4) 또는 Unity 에서 컴파일 에러 없음
- [ ] 새 파일의 `.meta` 생성 후 커밋
- [ ] 관련 자동 확인 1회 (플레이어 빌드 필요)

### 커밋 전

- [ ] `dotnet build` 경고 0, `dotnet test` 전체 통과
- [ ] 실행 중인 서버가 DLL 을 잡고 있으면 빌드가 실패합니다(MSB3027) — 서버를 먼저 끄세요
- [ ] 문서 갱신
