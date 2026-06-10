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
- [ ] **공유 ResourceDictionary 추출** (S, P3)
  - What: FormatToggleStyle/DeleteButtonStyle 등이 StickerWindow/PomodoroWindow에 중복 — 공유 사전으로
  - Context: 포모도로 v1은 의도적 복제 (창 2개까지는 허용). 3번째 창 생기면 필수
- [ ] **DESIGN.md 작성** (S, P3)
  - What: /design-consultation 실행해 디자인 토큰 문서화
  - Why: 현재 토큰이 StickerWindow.xaml에 암묵적으로만 존재 — 리뷰마다 재추출 중
- [ ] **push 서식 annotation 지원 (rich_text bold/underline)** (M, P2)
  - What: push가 plain text 대신 run 구조(굵게/밑줄)를 rich_text annotation으로 전송
  - Why: 현재 push는 서식을 벗기므로, 서식 있는 스티커가 pull을 거치면 로컬 서식도
    사라지는 비대칭 (검증 리뷰 F2). 가져오기 경고로 동의는 받지만 근본 해결은 push 개선
  - Context: SaveBodyContent가 UI 스레드에서 문서를 이미 순회함 — 그 시점에 NoteLine
    구조를 직렬화해 두면 백그라운드 push가 annotation 포함 블록을 만들 수 있음
  - Depends on: 양방향 v1 실사용 확인
- [ ] **글로벌 hotkey로 스티커 생성** (M, P2)
  - What: 시스템 전역 단축키 → 새 스티커
  - Why: CEO 리뷰 지적 — capture 도구의 최고 레버리지 기능이 두 계획 연속 제외됨
