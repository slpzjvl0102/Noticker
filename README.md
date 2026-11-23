# Noticker

Windows 바탕화면 스티커 메모 앱. Notion DB와 자동 동기화됩니다.

## 사용법

[최신 Release](https://github.com/slpzjvl0102/Noticker/releases/latest)에서 `Noticker-win-Setup.exe`를 받아 한 번 설치하세요.
이후에는 앱이 시작 1분 뒤와 6시간마다 새 버전을 확인해 받아 두고, 트레이 메뉴의 **재시작하여 업데이트**를 누르거나 다음 실행 때 자동 적용됩니다.
설치 위치는 `%LOCALAPPDATA%\Noticker`, 데이터(`%APPDATA%\Noticker\noticker.db`)는 업데이트·재설치와 무관하게 유지됩니다.
자세한 사용법은 `GUIDE.txt`를 참고하세요.

> 코드 서명이 없어 처음 설치할 때 SmartScreen 경고가 나옵니다 (추가 정보 → 실행). 업데이트 때는 나오지 않습니다.

## 개발 환경

**요구사항**: .NET 8 SDK, Windows

```bash
# 실행 (개발용)
dotnet run

# 단일 exe로 빌드 (배포용)
dotnet publish -c Release -r win-x64 --self-contained
```

publish 결과물은 `bin\Release\net8.0-windows\win-x64\publish\` 아래에 생성됩니다.

## 새 버전 배포

1. `Noticker.csproj`의 `<Version>`을 올립니다 (예: `2026.11.23` → `2026.11.30`, 항상 이전보다 커야 함).
2. main에 merge한 뒤 GitHub → **Actions → Release → Run workflow**를 누릅니다.
3. 테스트 → publish → Velopack 패키지 → Release `v<Version>` 생성까지 자동으로 진행됩니다. 설치된 앱은 이 Release를 보고 업데이트합니다.

## 기술 스택

- WPF (.NET 8)
- SQLite (Microsoft.Data.Sqlite)
- Notion REST API v1
