# TODOS

## 포모도로 (2026-06-10 /autoplan 리뷰에서 연기)

- [ ] **E4 — "오늘 N세션" 카운터** (S, P3)
  - What: 위젯에 오늘 완료한 집중 세션 수 표시
  - Why: 가벼운 성취감. 단, v1 프리미스 "통계 없음"과 충돌해 연기
  - Context: settings에 날짜+카운트 1키면 충분. 프리미스 재논의 후 진행
- [ ] **E5 — Notion 세션 로그 sync** (M, P3)
  - What: 세션 완료를 Notion DB에 기록
  - Why: 기존 sync 파이프라인 재사용 가능한 차별화 포인트
  - Context: `SessionEndedEventArgs` 구독자 추가만으로 가능하도록 설계됨. NotionClient 재사용
  - Depends on: 포모도로 v1 출시 + 실사용 확인
- [ ] **E6 — 스티커-태스크 연결** (L, P2)
  - What: 특정 스티커를 태스크로 삼아 포모도로 실행, 집중 시간 기록
  - Why: 일반 타이머 대비 유일한 moat — Noticker의 capture+sync 자산 활용. 12개월 이상향의 핵심
  - Context: CEO 리뷰가 "차별화는 이것뿐"이라 지적. PomodoroService 이벤트에 TaskId 필드 추가로 시작
  - Depends on: E5 (로그 인프라)
- [x] **공유 ResourceDictionary 추출** (S, P3) — 2026-06-11 출하
  - 구현: Styles/SharedStyles.xaml (토큰 11 + 공유 스타일 5, App.xaml 병합). 실측 결과
    중복은 Onboarding↔Settings(버튼/라벨/PasswordBox)와 Import↔NoteList(ListViewItem)였고
    FormatToggle/DeleteButton은 창 전용이라 유지. 테마 바인딩 계열(스티커/포모도로) 불가침.
    스펙: docs/superpowers/specs/2026-06-11-shared-resources-design.md
- [x] **DESIGN.md 작성** (S, P3) — 2026-06-11 출하 (위 항목과 묶음)
  - 구현: 루트 DESIGN.md — 팔레트 토큰 표, 공유 스타일 목록, 정적/동적 경계, 새 창 규칙
- [x] **push 서식 annotation 지원 (rich_text bold/underline)** (M, P2) — 2026-06-11 출하
  - 구현: body_runs 컬럼(v5) + NoteLineSerializer/NoteLineExtractor + BuildAnnotatedBlocks
    (plain 폴백 포함). 가져오기 경고는 italic/strikethrough/code/색으로 축소.
    스펙: docs/superpowers/specs/2026-06-11-push-annotations-and-sync-dot-design.md
- [x] **글로벌 hotkey로 스티커 생성** (M, P2) — 2026-06-11 출하
  - 구현: HotkeyPresets(프리셋 4종 매핑) + HotkeyManager(메시지 전용 HwndSource +
    RegisterHotKey, MOD_NOREPEAT). 기본 Ctrl+Alt+N, 설정 콤보로 변경(실패 시 인라인
    에러+이전 값 복원), 시작 실패는 트레이 풍선. 생성 즉시 본문 포커스.
    스펙: docs/superpowers/specs/2026-06-11-global-hotkey-design.md
