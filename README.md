# Noticker

Windows 바탕화면 스티커 메모 앱. Notion DB와 자동 동기화됩니다.

## 사용법

배포된 `Noticker.exe`를 실행하세요. 설치 과정 없음. 자세한 사용법은 `GUIDE.txt`를 참고하세요.

## 개발 환경

**요구사항**: .NET 8 SDK, Windows

```bash
# 실행 (개발용)
dotnet run

# 단일 exe로 빌드 (배포용)
dotnet publish -c Release -r win-x64 --self-contained
```

publish 결과물은 `bin\Release\net8.0-windows\win-x64\publish\` 아래에 생성됩니다.  
`Noticker.exe`와 `GUIDE.txt`를 함께 배포하세요.

## 기술 스택

- WPF (.NET 8)
- SQLite (Microsoft.Data.Sqlite)
- Notion REST API v1
