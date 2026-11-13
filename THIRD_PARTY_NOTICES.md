# Third-Party Notices

이 프로젝트에 포함되거나 의존하는 외부 구성요소입니다.

| 구성요소 | 버전 | 라이선스 | 사용 위치 | 출처 |
| --- | --- | --- | --- | --- |
| LiteNetLib | 2.1.5 | MIT | 서버: NuGet `LiteNetLib` / 클라이언트: `Client/Assets/Plugins/LiteNetLib/LiteNetLib.dll` (NuGet 패키지의 netstandard2.1 빌드를 그대로 복사) | https://github.com/RevenantX/LiteNetLib (commit `aa6d03e5`) |

## LiteNetLib

전문: [Client/Assets/Plugins/LiteNetLib/LICENSE.txt](Client/Assets/Plugins/LiteNetLib/LICENSE.txt)

```text
MIT License

Copyright (c) 2025 Ruslan Pyrch
```

서버와 클라이언트의 LiteNetLib 버전은 같게 유지합니다. 서버 NuGet 버전을 올리면 Unity 쪽 DLL도 같은 패키지에서 다시 복사합니다.
