# 온보딩 위저드 + 노트 목록 카드형 정리 — 설계

날짜: 2026-06-11 · 브랜치: main · 상태: 승인됨 (사용자 D3–D7 결정 반영)

## 배경

현재 초기 설정은 SettingsWindow에 Notion API 토큰, DB ID(또는 URL), Category
속성 이름을 직접 붙여넣는 방식이다. 노트 목록(NoteListWindow)은 기본 ListView에
파란 시스템 하이라이트 hover, 줄마다 상시 노출되는 "삭제" 버튼을 쓴다.

## 범위 (사용자 결정)

| 결정 | 선택 |
|---|---|
| D3 로그인 방식 | OAuth 아님 — 토큰 1회 입력 + DB 목록 API 드롭다운 |
| D4 설정 UI 형태 | 첫 실행 온보딩 창 신설 (위저드) |
| D5 노트 목록 범위 | 외관 정리만 (그룹핑·정렬 등 구조 변경 없음) |
| 목업 | 온보딩: A 단계형 위저드, **완료 점 없음 (2단계 점만)** / 목록: B 카드형 |
| D6 설정 창 정리 | 노션 입력란 3개 → 요약 + [연결 다시 설정] 버튼 교체 포함 |

비범위: OAuth, 날짜 그룹핑/정렬/고정, 100블록 초과 페이지 처리 변경.

## 1. 온보딩 위저드 (`Windows/OnboardingWindow.xaml(.cs)` 신설)

스타일: SettingsWindow와 동일한 라이트 고정 (검정 #1A1A1A 헤더, #FAFAFA 배경,
밑줄형 입력). 점 표시는 `●○` 2단계만 — 완료 단계 점 없음.

### 1단계 — 노션과 연결
- PasswordBox 토큰 입력 + "↗ 노션에서 토큰 발급받기" 링크
  (`https://www.notion.so/my-integrations` 브라우저 오픈)
- [다음] 클릭 → `GET /users/me`로 후보 토큰 즉석 검증.
  실패: 빨간 인라인 메시지(권한/네트워크 구분), 2단계 진입 차단.

### 2단계 — DB 선택
- 진입 시 `POST /v1/search` (filter `object=database`, page_size 100,
  has_more 페이지네이션 전부 순회)로 통합에 공유된 DB 목록 로드 → 이름 드롭다운
- 결과 0개: "노션에서 DB 페이지에 통합을 연결한 뒤 새로고침하세요" 안내 +
  [새로고침] 버튼 (링크로 노션 열기 포함)
- DB 선택 시 `GET /databases/{id}`의 properties 중 **select 타입만** 카테고리
  드롭다운에 나열. "Category"가 있으면 자동 선택, select 속성이 없으면
  "(카테고리 없음)" 항목만 표시하고 진행 허용. "(카테고리 없음)" 선택 시
  CategoryPropertyName은 기본값 "Category"를 유지하고 완료 시 카테고리 옵션
  갱신을 건너뜀 — 속성이 없을 때 옵션이 비는 현행 동작과 동일
- [시작하기] → 저장 + 배선:
  1. `AppSettings`/`SettingsRepository`에 token, TargetDbId(드롭다운 선택 id),
     CategoryPropertyName 저장
  2. 토큰이 바뀌었으면 `App.InvalidateBotUserId()` 호출 (기존 규칙 그대로)
  3. 카테고리 옵션/색 갱신 (SettingsWindow RefreshCat 로직 재사용)
- [← 이전]으로 1단계 복귀 가능 (입력 보존)

### 트리거
- `App.xaml.cs:77` `if (!IsConfigured) OpenSettings()` → `OpenOnboarding()`으로 교체
- 창 닫기(X)로 미완료 종료 허용 — 다음 실행 시 다시 뜸 (IsConfigured 불변)

### NotionClient 추가 메서드
저장 전 후보 토큰으로 호출해야 하므로 **토큰을 명시 인자로 받는** 형태:
- `ValidateTokenAsync(string token, CancellationToken)` → 성공/오류 메시지
- `SearchDatabasesAsync(string token, CancellationToken)` → `(string Id, string Title)[]`
- `GetSelectPropertiesAsync(string token, string dbId, CancellationToken)` → `string[]`

기존 `_settings` 기반 메서드는 변경하지 않는다. JSON 파싱은 단위 테스트 추가
(빈 결과, 제목 없는 DB `(제목 없음)` 폴백, 페이지네이션, select 속성 0개).

### SettingsWindow 정리 (D6-A)
- TOKEN / DB ID / CATEGORY 입력 3개 + [연결 테스트] 제거
- "노션 연결" 섹션: `연결됨: {DB 제목}` 요약 (미설정 시 `연결 안 됨`) +
  [연결 다시 설정] 버튼 → OnboardingWindow 오픈. 위저드 완료 시 요약 갱신
- DB 제목은 온보딩 완료 시 settings에 `notion_db_title`로 캐시 (요약 표시용)
- [옵션 새로고침](카테고리)은 유지 — 연결과 무관한 기존 기능

## 2. 노트 목록 카드형 (`Windows/NoteListWindow.xaml(.cs)` 수정)

동작(클릭→스티커 열기, 검색 필터, 삭제 확인 다이얼로그, 빈 상태 메시지)은 불변.
외관만 교체:

- **카드 행**: ItemTemplate을 둥근 카드로 — CornerRadius 5, 좌우 margin 8,
  카드 간격 5, padding 11×9. hover 시 테두리 진해짐 + 옅은 그림자(살짝 떠오름).
  파란 SystemColors.HighlightBrush hover 제거
- **삭제**: 상시 "삭제" 버튼 제거 → hover한 카드에만 회색 `✕` (템플릿
  IsMouseOver 트리거). Tag/Click 핸들러와 확인 다이얼로그는 기존 그대로
- **숨김**: `· 숨김` 텍스트 → 제목 옆 알약 배지 (radius 8, 9px, 연회색 배경)
- **검색창**: 박스형 → 밑줄(line-style) 입력
- **가져오기**: 기본 버튼 → 조용한 외곽선 버튼 `↓ Notion에서 가져오기`
- **테마**: 기존 `ApplyTheme`/`ApplyRowColors` 코드비하인드 경로를 카드 색으로 확장
  - 라이트: 바탕 #FAFAFA, 카드 #FFFFFF, 테두리 #ECECEC, hover 테두리 #D5D5D5
  - 다크: 바탕 #333333, 카드 #3C3C3C, 테두리 #505050, hover 테두리 #6A6A6A
  - 배지: 라이트 #F0F0F0/#888 · 다크 #4A4A4A/#AAA

## 오류 처리

- 온보딩 모든 API 호출: 인라인 상태 텍스트(회색 "확인 중…" → 초록/빨강 결과),
  버튼 재시도 가능. 예외는 기존 `SyncLog`에 기록
- 401(토큰 무효)·네트워크 오류 메시지 구분
- 위저드 진행 중 토큰 검증 성공 후 2단계 API가 실패해도 1단계로 안 돌려보냄 —
  2단계 안에서 재시도

## 테스트 / 검증

1. NotionClient 신규 파싱 단위 테스트 (`dotnet test Noticker.Tests/Noticker.Tests.csproj`)
2. 기존 210개 테스트 통과 유지
3. 수동 QA (사용자 직접, 선호 워크플로): 첫 실행 온보딩 전체 흐름, 설정 창
   [연결 다시 설정], 노트 목록 라이트/다크 양쪽 확인
4. 빌드 절차: 빌드 전 `taskkill //IM Noticker.exe //F`, 검증 후 재실행

## 수용 리스크

- DB 300개+ 워크스페이스는 드롭다운이 길어짐 — 검색 필터는 비범위 (개인용 앱)
- `/v1/search`는 통합에 공유된 DB만 반환 — 0개 안내문으로 대응
- 삭제 ✕는 hover 전 비노출 — 데스크톱 전용이라 수용
