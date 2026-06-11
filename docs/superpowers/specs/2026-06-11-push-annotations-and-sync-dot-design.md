# push 서식 annotation + 동기화 표시등 정리 — 설계

날짜: 2026-06-11 · 브랜치: main · 상태: 승인됨 (사용자 D9–D10 결정 반영)

## 배경

- **push 비대칭**: pull/가져오기는 Notion의 굵게/밑줄을 스티커 RTF로 합성하지만
  (`NotionBlockConverter.NoteRun` → `RtfComposer`), push는 plain text만 보낸다
  (`NotionClient.BuildParagraphBlocks` — `text.content`만 구성). 스티커의 굵게/밑줄이
  Notion에 안 올라가고, push가 본문 블록을 전체 교체하므로 Notion 쪽 서식도 한 번의
  push로 벗겨진다. 이 때문에 가져오기 창이 서식 있는 페이지에 경고를 띄운다.
- **표시등**: 상태별 색은 이미 존재(초록=synced, 주황=pending, 빨강=failed,
  진한 주황=conflict). 문제는 빈 메모가 push 대상이 아니라서(`SyncQueue.cs:91`)
  기본 상태 "pending"의 주황이 영원히 유지되는 것 — 온보딩 직후 자동 생성되는
  빈 메모가 정확히 이 케이스.

## 범위 (사용자 결정)

| 결정 | 선택 |
|---|---|
| D9 빈 스티커 표시등 | 회색 + "빈 메모 — 동기화 대상 아님" 툴팁 |
| D10 push 서식 설계 | body_runs 별도 컬럼 + plain Body 유지 + 폴백 + 경고 축소 |

비범위: italic/strikethrough/code 지원(스티커 UI에 없음), 표시등 색 체계 개편.

## 1. push 서식 annotation

### 데이터 모델
- `Sticker.BodyRuns` (string? — JSON, 기본 null) 추가.
- DB: `stickers` 테이블에 `body_runs TEXT` 컬럼 — 기존 마이그레이션 패턴
  (`PRAGMA user_version`, 다음 버전 5) 그대로.
- JSON 구조 (줄 단위 배열):
  ```json
  [{"Kind":"paragraph","Runs":[{"T":"plain ","B":false,"U":false},{"T":"bold","B":true,"U":false}]},
   {"Kind":"bullet","Runs":[{"T":"항목","B":false,"U":true}]},
   {"Kind":"numbered","Runs":[{"T":"첫째","B":false,"U":false}]}]
  ```
  `Kind`: paragraph | bullet | numbered. `Runs`의 텍스트에 마커(•, "N. ")는 포함하지
  않는다 — 마커는 Kind가 표현. 직렬화/역직렬화는 새 `Sync/NoteLineSerializer`(정적,
  순수 함수)가 담당해 HTTP·WPF 없이 단위 테스트한다. 파싱 실패는 null 반환(폴백 유도).

### 쓰기 경로 — `StickerWindow.SaveBodyContent`
- 이미 UI 스레드에서 문서 블록을 순회한다. 같은 순회에서 각 Paragraph의 Inline 중
  `Run`을 `NoteRun(Text, Bold, Underline)`으로 추출:
  - Bold: `FontWeight == FontWeights.Bold`, Underline: `TextDecorations`에 Underline 포함
  - 인접 run이 동일 서식이면 병합, 빈 텍스트 run은 제외
  - 리스트 항목은 기존 마커 제거 로직 이후의 텍스트 기준 — 단 run 분해는 Inline 단위로
    하되 WPF가 주입한 마커 문자는 plain 경로와 같은 규칙으로 첫 run에서 제거
- 결과를 `NoteLineSerializer.Serialize`로 JSON화해 `_sticker.BodyRuns`에 저장.
  plain `Body`/`BodyRtf` 저장은 기존 그대로 (검색·노트 목록·레거시 호환).

### push 경로 — `NotionClient`
- `CreatePageAsync`/`UpdatePageAsync`가 쓰는 `BuildParagraphBlocks(s.Body)`를
  `BuildBodyBlocks(Sticker s)`로 감싼다:
  - `s.BodyRuns` 역직렬화 성공 → run 단위 블록 구성: 각 NoteLine이 Kind에 따라
    paragraph/bulleted_list_item/numbered_list_item, `rich_text`는 run별
    `{ text: { content }, annotations: { bold, underline } }` — **annotations 객체는
    bold·underline 둘 다 false면 생략** (Notion 기본값과 동일, payload 절약)
  - 2000자 청크 분할(`SplitIntoChunks`)은 run 단위로 적용 (청크가 run 서식 상속)
  - `BodyRuns`가 null이거나 역직렬화 실패 → **기존 plain 경로 폴백**
    (기존 스티커는 다음 편집에서 BodyRuns가 생기는 순간부터 서식 push)
- 빈 줄 처리(rich_text 빈 배열)는 기존 규칙 유지.

### pull/가져오기 일관성
- `PullService`가 원격 본문을 적용할 때, 이미 갖고 있는 NoteLine(NoteRun 줄들)을
  `NoteLineSerializer`로 직렬화해 `BodyRuns`도 함께 저장.
- `NotionImportWindow`(가져오기)도 동일 — 가져온 스티커의 `BodyRuns`를 변환 결과로 채움.
- 효과: pull 직후 conflict-경로 push가 일어나도 서식이 보존된다.

### 가져오기 경고 축소
- `NotionBlockConverter.HasAnnotations` → `HasUnsupportedAnnotations`로 의미 변경:
  **italic / strikethrough / code + 기본값 아닌 color** 검사 (bold/underline은 이제
  왕복 가능하지만 color는 어디서도 왕복하지 않으므로 경고 유지).
- `NotionImportWindow`의 경고 다이얼로그 문구도 "굵게/밑줄 외 서식(기울임 등)은
  유지되지 않습니다" 취지로 갱신.

## 2. 동기화 표시등 — 빈 메모 회색

`StickerWindow.SyncDotColor` / `SyncTooltip`에 분기 추가 (스키마 변경 없음):

- 조건: `_sticker.NotionPageId is null && string.IsNullOrEmpty(Title) && string.IsNullOrEmpty(Body)`
  — `SyncQueue.ProcessAsync`의 skip 조건과 동일 기준 (Title/Body 모두 빈 경우만)
- 색: 회색 `#9CA3AF`, 툴팁: "빈 메모 — 동기화 대상 아님"
- 내용 입력 → SaveContent가 pending 설정 + 표시등 갱신(기존 경로) → 주황 →
  push 성공 후 초록. 기존 상태 전환은 변경 없음.

## 오류 처리

- BodyRuns 역직렬화 실패: 조용히 plain 폴백 (예외 전파 금지) — 손상된 JSON이
  push를 막으면 안 된다. `SyncLog`에 1줄 기록.
- run 추출 실패 가능성(예상 밖 Inline 타입): Run 외 Inline은 텍스트만 취해
  서식 없음으로 처리 — plain과 동일한 본문이 보장되도록.

## 테스트 / 검증

1. `NoteLineSerializer` 왕복(한국어/이모지 포함), 손상 JSON → null
2. runs → 블록 JSON: annotations 생략 규칙, bullet/numbered Kind 매핑,
   2000자 초과 run 청크 분할, 빈 줄
3. `SaveBodyContent` run 추출: STA 테스트 (`PullApplyStaRepro`의 RunSta 패턴) —
   굵게/밑줄/혼합/리스트 마커 제거
4. `HasUnsupportedAnnotations`: bold/underline만 → false, italic 포함 → true
5. 표시등: SyncDotColor 분기 — 빈+미연결=회색, 내용 있으면 기존 상태색
   (StickerWindow 분리 가능 로직이면 단위 테스트, 아니면 수동 QA)
6. 기존 222개 테스트 통과 유지, 수동 QA: 스티커 굵게/밑줄 → Notion 반영 확인,
   Notion 서식 페이지 가져오기 → 편집 → push 후 굵게/밑줄 보존 확인

## 수용 리스크

- 기존 스티커(BodyRuns null)는 다음 편집 전까지 plain push — 의도된 점진 적용
- italic 등 미지원 서식은 여전히 push 시 소실 — 경고 다이얼로그가 계속 안내
- Body(plain)와 BodyRuns가 이론상 어긋날 수 있는 경로는 SaveBodyContent가 항상
  둘을 같은 순회에서 갱신하므로 없음 (pull/가져오기도 쌍으로 저장)
