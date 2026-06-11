# Noticker 디자인 토큰

스타일 정의의 단일 소스는 `Styles/SharedStyles.xaml` (App.xaml에서 병합).
이 문서는 그 내용의 사람용 설명이다. **둘이 어긋나면 XAML이 정답** — 수정 시
이 문서도 같이 갱신할 것.

## 정적 / 동적 경계 (가장 중요한 규칙)

| 영역 | 색의 출처 | 토큰 사용 |
|---|---|---|
| 라이트 테마 창 (온보딩·설정·가져오기·노트 목록의 고정 부분) | 아래 정적 토큰 | O |
| 스티커·포모도로 창, 노트 목록 카드 색 | ViewModel 바인딩 — 노션 카테고리 색 등 **런타임 데이터** | **X — 토큰화 금지** |

스티커 계열의 `{Binding TitleBackground}` 류를 정적 토큰으로 바꾸면 테마
전환·카테고리 색이 깨진다. 이 경계를 넘는 리팩토링은 하지 않는다.

## 팔레트 토큰

모노크롬(잉크 온 페이퍼) 디자인. 검정 `InkBrush`가 헤더 띠·주 텍스트·주 버튼을
겸하고, 회색 계열이 위계를 만든다.

| 토큰 | 값 | 역할 | 주 사용처 |
|---|---|---|---|
| `SurfaceBrush` | #FAFAFA | 라이트 창 배경 | Onboarding/Settings Window |
| `InkBrush` | #1A1A1A | 잉크 — 헤더 띠, 주 텍스트, Primary 버튼 bg, 입력 글자색 | 헤더 Grid, PrimaryButtonStyle, LinePasswordBoxStyle |
| `InkHoverBrush` | #333333 | Primary 버튼 hover, 보조 본문 텍스트 | PrimaryButtonStyle 트리거, ActionButtonStyle Foreground, CheckBox |
| `TextMutedBrush` | #666666 | 필드 라벨 | FieldLabelStyle |
| `TextSectionBrush` | #777777 | 섹션 제목, 설명 문단 | SectionHeadingStyle, 온보딩 설명 |
| `BorderStrongBrush` | #CCCCCC | 외곽선 버튼 테두리, 비활성 점 표시 | ActionButtonStyle, 온보딩 단계 점 |
| `BorderInputBrush` | #DDDDDD | 입력 밑줄 | LinePasswordBoxStyle, Settings TextBox |
| `BorderHoverBrush` | #999999 | 외곽선 버튼 hover 테두리 | ActionButtonStyle 트리거 |
| `ControlHoverBrush` | #F0F0F0 | 보조 버튼 hover bg | ActionButtonStyle 트리거 |
| `ControlPressedBrush` | #E4E4E4 | 보조 버튼 pressed bg | ActionButtonStyle 트리거 |
| `DangerBrush` | #C0392B | 에러 텍스트 | Settings hotkey 인라인 에러 |

토큰화 기준: **2개 이상 파일에서 반복되는 값만.** 단일 사용 색(#555555,
#EEEEEE, #AAAAAA, #50808080, Primary pressed #000000 등)은 사용처에 인라인.

## 공유 스타일

| 키 | TargetType | 용도 | 사용 창 |
|---|---|---|---|
| `ActionButtonStyle` | Button | 외곽선 보조 버튼 (12,6 패딩, 둥근 3px) | Onboarding, Settings |
| `PrimaryButtonStyle` | Button | 검정 채움 주 버튼 (24,8 패딩) | Onboarding, Settings |
| `FieldLabelStyle` | TextBlock | 11px 회색 필드 라벨 | Onboarding, Settings |
| `LinePasswordBoxStyle` | PasswordBox | 밑줄 입력 (Margin 없음 — 창 래퍼가 보유) | Onboarding(8), Settings(16) |
| `PlainListViewItemStyle` | ListViewItem | 시스템 크롬 없는 리스트 행 | NotionImport, NoteList(+Template 오버라이드) |

Margin 규칙: 공유 스타일은 Margin을 갖지 않는다 — 배치는 창/인스턴스 책임.

## 새 창 추가 시 규칙

1. 라이트 테마 창이면 `SurfaceBrush` 배경 + `InkBrush` 헤더 띠로 시작.
2. 버튼·라벨·입력은 공유 스타일 참조 먼저 — 복사 금지.
3. 새 hex가 필요하면: 2개 이상 파일에서 쓰게 될 값인지 따져 토큰 추가
   (SharedStyles.xaml + 이 문서 동시 갱신), 단일 사용이면 인라인.
4. 암시적(TargetType-only) 스타일은 App 전역에 올리지 않는다 — 모든 창에
   적용돼 스티커 창을 오염시킨다. 창 안에서 `BasedOn` 래퍼로 적용할 것.
