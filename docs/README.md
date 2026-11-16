# OpenRTS 개발 문서

코드를 고치거나 기능을 더하는 사람을 위한 문서입니다. 프로젝트 소개·로드맵·측정값은 루트 [README](../README.md)에 있고, 여기서는 **코드가 어떻게 짜여 있고, 바꿀 때 무엇을 지켜야 하는지**를 다룹니다.

| 문서 | 내용 | 먼저 읽을 사람 |
| --- | --- | --- |
| [development.md](development.md) | 개발 환경, 빌드·실행, 코드 규칙, 자주 하는 변경의 체크리스트 | 모두 (처음 한 번) |
| [architecture.md](architecture.md) | 전체 구조, 서버 틱 처리 순서, 모듈 경계, 데이터 흐름 | 모두 |
| [protocol.md](protocol.md) | 메시지 목록, 바이트 형식, 스냅샷 델타, 버전 규칙 | 네트워크·클라이언트 |
| [simulation.md](simulation.md) | 이동(길찾기·ORCA·대형·재경로), 전투·AI, 시야, 경제 | 서버 게임 로직 |
| [abilities.md](abilities.md) | 능력(스킬): 데이터, 시전 흐름, 전달 방식, 상태 효과, AI 사용, 스크립트, 추가 방법 | 게임 로직·클라이언트 |
| [client.md](client.md) | Unity 클라이언트 구성, 보간, 표시, 입력 | 클라이언트 |
| [testing.md](testing.md) | 테스트 종류, 리포트, 실제 클라이언트 자동 확인, 디버깅 도구 | 모두 (변경 전후) |

추천 읽는 순서: development → architecture → (맡은 쪽) → testing.

## 용어

| 용어 | 뜻 |
| --- | --- |
| 틱 (tick) | 서버 시뮬레이션 한 단계. 30Hz (`SimConstants.TickRate`) |
| 스냅샷 | 한 틱의 유닛 상태. 15Hz 로 보냄 |
| baseline / 델타 | 클라이언트가 확인 응답(ack)한 스냅샷 / 그 대비 바뀐 것만 담은 스냅샷 |
| 플레이어 번호 (PlayerId, Owner) | 서버가 접속 순서로 주는 1~254. 0 은 서버 봇 |
| 팀 (Team) | 0 = 중립(전투 안 함), 1·2 = 전투 팀. 플레이어는 인원이 적은 팀에 들어감 |
| 종류 (UnitKind) | 근접·원거리·일꾼·영웅(움직임), 본진·병영·광맥(정적) |
| 능력 (Ability) | 유닛 종류별로 가진 스킬. 데이터(`AbilityDefinition`) + 선택적 서버 스크립트 |
| 정적 유닛 (static) | 건물·광맥. 움직이지 않고 발판(footprint)이 길찾기·충돌에서 벽이 됨 |
| 엔진 층 / 행동 층 | 명령을 실행하는 전투 엔진(`CombatSystem`) / 명령을 정하는 AI(`BattleAi`) |
| 가장자리 거리 (edge distance) | 두 유닛 표면 사이 거리. 사거리·획득 거리의 기준 |
