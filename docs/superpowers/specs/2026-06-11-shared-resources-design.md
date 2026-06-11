# 공유 ResourceDictionary 추출 + DESIGN.md — 설계

날짜: 2026-06-11 · 브랜치: main · 상태: 승인됨 (사용자 D1–D4 결정 반영)

## 배경

- TODOS.md (M, P3): 창마다 스타일 사본이 흩어져 있음. 트리거 조건("3번째 창")은
  OnboardingWindow가 SettingsWindow 스타일을 복제하면서 충족됨.
- 인벤토리 조사 결과 (TODOS 메모와 다른 실측):
  - **진짜 중복**: `ActionButtonStyle`/`PrimaryButtonStyle`/`FieldLabelStyle`
    (Onboarding↔Settings), `PasswordBox` 암시적 스타일 (동일 두 창),
    `ListViewItem` 암시적 스타일 (NotionImport↔NoteList)
  - **이미 분기된 복제**: Settings의 `PrimaryButtonStyle`에 disabled 트리거 누락
    (Onboarding 판에는 있음) — 복제 비용의 실증
  - **중복 아님 (TODOS 메모 정정)**: `FormatToggleStyle`/`DeleteButtonStyle`은
    StickerWindow 전용
  - **불가침 영역**: StickerWindow/PomodoroWindow/NoteListWindow의 색은
    ViewModel 바인딩(노션 카테고리 색 등 런타임 데이터) — 정적 토큰 대상 아님
  - 매직 hex ~10종이 라이트 테마 창들(Onboarding/Settings/Import/NoteList 정적부)에 반복
- DESIGN.md (S, P3)는 같은 탐색 산출물이라 이번 라운드에 묶음 (D2).

## 범위 (사용자 결정)

| 결정 | 선택 |
|---|---|
| D1 공통화 범위 | 중복 제거 + 라이트 팔레트 토큰 (테마 바인딩 계열 불가침) |
| D2 DESIGN.md | 이번 라운드에 같이 작성 |
| D3 토큰 이름 | 시맨틱 (역할 기반) |

비범위: StickerWindow/PomodoroWindow 스타일 변경, 다크 테마 추가, 단일 사용
색·스타일의 토큰화, 간격(margin/padding) 토큰화.

## 1. 파일 구조

- **`Styles/SharedStyles.xaml`** (신규, ResourceDictionary 1개): 팔레트 토큰 +
  공유 스타일. 규모가 작아(토큰 ~11, 스타일 ~5) 파일 분리 안 함.
- **`App.xaml`**: `Application.Resources`를 `ResourceDictionary`로 감싸고
  `MergedDictionaries`에 `Styles/SharedStyles.xaml` 병합. 기존
  `BoolToVisibilityConverter`는 같은 ResourceDictionary 안에 유지
  (MergedDictionaries와 직접 리소스는 공존 가능).
- **`DESIGN.md`** (신규, 저장소 루트 — TODOS.md와 같은 위치).
- csproj 변경 불필요 (xaml은 Page로 자동 포함).

## 2. 팔레트 토큰 (SolidColorBrush)

| 토큰 키 | 값 | 역할 |
|---|---|---|
| `SurfaceBrush` | #FAFAFA | 라이트 창 배경 |
| `InkBrush` | #1A1A1A | 헤더 띠·주 텍스트·Primary 버튼 bg (모노크롬 "잉크") |
| `InkHoverBrush` | #333333 | Primary 버튼 hover·보조 본문 텍스트 |
| `TextMutedBrush` | #666666 | 필드 라벨 |
| `TextSectionBrush` | #777777 | 섹션 제목 |
| `BorderStrongBrush` | #CCCCCC | 외곽선 버튼 테두리 |
| `BorderInputBrush` | #DDDDDD | 입력 밑줄 |
| `BorderHoverBrush` | #999999 | 외곽선 버튼 hover 테두리 |
| `ControlHoverBrush` | #F0F0F0 | 보조 버튼 hover bg |
| `ControlPressedBrush` | #E4E4E4 | 보조 버튼 pressed bg |
| `DangerBrush` | #C0392B | 에러 텍스트 (hotkey 인라인 에러 등) |

규칙:
- **토큰화 기준 = 2개 이상 파일에서 반복되는 값.** 단일 사용 색(#AAAAAA,
  #50808080 등)은 인라인 유지.
- **픽셀 변화 0 불변식** — 값을 바꾸지 않고 이름만 부여. Brush는
  `PresentationOptions:Freeze="True"` 없이 기본 정의 (기존과 동일 동작이
  우선; freeze 최적화는 비범위).
- 같은 hex(#1A1A1A, #333333)가 복수 역할을 가질 때도 토큰은 위 표의 1개만
  정의 — 역할 분리는 실제 색 분기 필요가 생길 때 (YAGNI).

## 3. 공유 스타일

`SharedStyles.xaml`로 이동 (내부 hex는 토큰 참조로 교체):

| 스타일 키 | 채택 판 | 비고 |
|---|---|---|
| `ActionButtonStyle` (Button) | Onboarding 판 (Margin 없음) | Settings 판에만 있던 `Margin="0,0,8,0"`은 Settings의 해당 버튼 인스턴스 속성으로 이동 — 픽셀 보존 |
| `PrimaryButtonStyle` (Button) | Onboarding 판 (disabled 트리거 포함) | **유일한 의도적 동작 변화**: Settings 저장 버튼이 disabled 시각 처리를 얻음 (분기 버그 수정 방향) |
| `FieldLabelStyle` (TextBlock) | 동일 (두 판 일치) | |
| `LinePasswordBoxStyle` (PasswordBox) | 공통분 (Margin 제외) | 두 판은 Margin만 다름 (Onboarding 0,0,0,8 / Settings 0,0,0,16) — Margin은 각 창 래퍼가 보유 |
| `PlainListViewItemStyle` (ListViewItem) | 공통 setter 5개 | NoteList 판은 추가로 Template 오버라이드 보유 — BasedOn 래퍼에 Template setter 유지 |

**암시적 스타일을 App 전역으로 올리지 않는다** (App 수준 암시적 TargetType
스타일은 모든 창에 적용돼 StickerWindow 등을 오염). 대신 각 창에 한 줄 래퍼만
남긴다:

```xml
<Style TargetType="PasswordBox" BasedOn="{StaticResource LinePasswordBoxStyle}"/>
```

단일 사용 스타일(`SectionHeadingStyle`, `TitleBoxStyle`, `StickerComboBoxStyle`,
`FormatToggleStyle`, `DeleteButtonStyle`, `PomodoroButtonStyle`,
`PomodoroToggleStyle`, `ResizeHandleTemplate`, Settings의 암시적
TextBox/CheckBox, Onboarding의 암시적 ComboBox)은 **창에 그대로 둔다**.
단, 라이트 테마 창 안의 단일 사용 스타일이 팔레트 hex를 쓰면 그 hex만 토큰
참조로 교체 (예: `SectionHeadingStyle`의 #777777 → `TextSectionBrush`).

## 4. 창별 영향

| 창 | 변경 |
|---|---|
| OnboardingWindow | 중복 스타일 3개 삭제→공유 참조, PasswordBox 래퍼화, 본문 hex→토큰 |
| SettingsWindow | 동일 + ActionButton Margin 인스턴스 이동, hex→토큰 (#C0392B 포함) |
| NotionImportWindow | ListViewItem 래퍼화. 정적 hex 중 팔레트 표 일치분만 토큰 |
| NoteListWindow | ListViewItem 래퍼화. 바인딩 색은 불변 |
| StickerWindow / PomodoroWindow | **불변** (커밋 diff에 나타나면 스펙 위반) |

## 5. 검증

XAML은 단위 테스트 대상이 아니므로:

1. `dotnet build Noticker.csproj` 0 에러 + 기존 272 테스트 회귀 통과
2. **리소스 키 오타는 창을 열 때 런타임 XamlParseException** — 영향받는 창
   4개(온보딩·설정·노트 목록·가져오기)를 실제로 열어 확인하는 수동 QA 필수.
   온보딩은 설정 창의 "연결 다시 설정"으로 열 수 있음
3. 변경 전후 스크린샷 비교로 픽셀 보존 확인 (예외: Settings 저장 버튼
   disabled — 단, 저장 버튼은 비활성화되는 경로가 현재 없어 시각 차이는
   관찰되지 않을 수 있음)
4. 스티커/포모도로 창은 diff 부재로 검증 (열어볼 필요 없음)

## 6. DESIGN.md 내용

저장소 루트 `DESIGN.md`:

1. **팔레트 토큰 표** — §2 표 + 사용처 (SharedStyles.xaml과 1:1)
2. **공유 스타일 목록** — 키·용도·사용 창
3. **정적/동적 경계** — 라이트 테마 창(정적 토큰) vs
   스티커·포모도로·노트목록 카드(ViewModel 바인딩, 노션 카테고리 색 런타임
   의존)의 구분과 이유. 이 경계를 넘는 토큰화 금지 경고
4. **새 창 추가 규칙** — SharedStyles 참조 먼저, 새 hex 도입 시 토큰 추가
   검토, 암시적 스타일은 창 범위 래퍼로
