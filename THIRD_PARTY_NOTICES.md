# Third-Party Notices

이 프로젝트에 포함되거나 의존하는 외부 구성요소입니다.

| 구성요소 | 버전 | 라이선스 | 사용 위치 | 출처 |
| --- | --- | --- | --- | --- |
| LiteNetLib | 2.1.5 | MIT | 서버: NuGet `LiteNetLib` / 클라이언트: `Client/Assets/Plugins/LiteNetLib/LiteNetLib.dll` (NuGet 패키지의 netstandard2.1 빌드를 그대로 복사) | <https://github.com/RevenantX/LiteNetLib> (commit `aa6d03e5`) |
| RVO2 Library C# | main (commit `de124a27`) | Apache-2.0 | `Server/src/OpenRTS.Server/Simulation/Avoidance/OrcaSolver.cs` — Agent.cs 의 ORCA 선 생성(장애물·에이전트)과 선형계획, Obstacle.cs 의 꼭짓점 구조, Simulator.AddObstacle 의 다각형 구성을 수정해 사용 | <https://github.com/snape/RVO2-CS> |

## RVO2 Library (C# port)

전문: [Server/src/OpenRTS.Server/Simulation/Avoidance/LICENSE-RVO2.txt](Server/src/OpenRTS.Server/Simulation/Avoidance/LICENSE-RVO2.txt) (Apache License 2.0)

```text
SPDX-FileCopyrightText: 2008 University of North Carolina at Chapel Hill
SPDX-License-Identifier: Apache-2.0
Jur van den Berg, Stephen J. Guy, Jamie Snape, Ming C. Lin, Dinesh Manocha
```

원본 저장소에는 NOTICE 파일이 없습니다. 수정 사항은 `OrcaSolver.cs` 머리말에 적어 두었습니다:
Simulator·KdTree·스레드 제거(장애물 이웃은 같은 규칙의 선형 탐색), 상태를 인자로 전달, `System.Numerics.Vector2` 사용, 버퍼 재사용,
같은 위치·속도일 때 0 나눗셈 방지, 이웃별 회피 몫(원본은 항상 0.5).
공간 분할(`SpatialGrid`), 길찾기(`NavGrid`), 대형(`Formation`), 분리 보정, 벽 밀어내기, 막힘 감지·재경로, 시나리오는 이 프로젝트에서 새로 작성한 코드입니다.

## 검토했지만 쓰지 않은 것

| 구성요소 | 라이선스 | 이유 |
| --- | --- | --- |
| RoyT.AStar 3.0.2 | MIT | 반지름별 통행·셀 추가 비용이 필요해 직접 작성 (`NavGrid`) |
| OptimizedPriorityQueue 5.1.0 | MIT | .NET 내장 `PriorityQueue` 로 충분 |


## LiteNetLib

전문: [Client/Assets/Plugins/LiteNetLib/LICENSE.txt](Client/Assets/Plugins/LiteNetLib/LICENSE.txt)

```text
MIT License

Copyright (c) 2025 Ruslan Pyrch
```

서버와 클라이언트의 LiteNetLib 버전은 같게 유지합니다. 서버 NuGet 버전을 올리면 Unity 쪽 DLL도 같은 패키지에서 다시 복사합니다.
