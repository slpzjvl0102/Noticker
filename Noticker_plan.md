# Sticky-Notion PRD

Notion 연동 스티커메모 데스크탑 앱 기획 문서. v1 기준.

## 1. 프로젝트 개요

Windows 데스크탑 위에 떠 있는 자체 스티커메모 앱. 떠오른 아이디어를 즉시 입력하면 자동으로 Notion DB에 단방향 sync되는 capture 도구.

핵심 가치는 사고의 흐름을 깨지 않고 빠르게 텍스트를 흘려보내는 inbox 역할 + Notion 워크스페이스로 자동 수렴.

본인 사용 목적, vibe coding으로 진행.

## 2. 목표 / 제외

### 🎯 목표

- 빠른 텍스트 capture와 자동 Notion sync
- 재부팅 후에도 스티커 상태(위치, 크기, 내용) 그대로 복원
- 듀얼 모니터 환경에서도 robust한 위치 관리
- 단순함 우선의 미니멀 UI

### 🚫 제외 (v1에서 구현하지 않음)

- 양방향 동기화 (Notion → 스티커 방향 일체 없음)
- 멀티 사용자 / 클라우드 동기화 (본인 단일 디바이스 전제)
- 리치 텍스트 / 이미지 / 첨부파일 (plain text + 줄바꿈만 지원)
- 모바일 / Mac / Linux (Windows 전용)
- 다중 색상 팔레트 / 카테고리별 색상 매핑 (무채색 흑/백만 사용)
- 글로벌 hotkey (트레이 아이콘과 스티커 + 버튼만 사용)
- 검색 / 필터 / 정렬 등 관리 기능 (capture에 집중)

## 3. 기술 스택

| 영역 | 선택 |
|------|------|
| 프레임워크 | WPF + .NET 8 (C#) |
| 로컬 저장소 | SQLite (`Microsoft.Data.Sqlite`) |
| HTTP 클라이언트 | `HttpClient` |
| Notion API | REST API 직접 호출 |
| Credential 보안 | Windows DPAPI (`ProtectedData` class) |
| 자동 시작 | Registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |
| 시스템 트레이 | `System.Windows.Forms.NotifyIcon` (WPF에서 참조 가능) |

비용 측면: Notion API는 무료. Internal Integration도 Notion 무료 플랜에서 사용 가능. Rate limit 평균 3 req/sec 초과 시 throttling만 적용되며 과금 없음.

## 4. 기능 명세

### 🔐 4.1 인증 / 연결 설정

- 사용자가 Notion에서 Internal Integration을 생성하고 token 발급 (Notion 무료 플랜에서도 가능)
- 사용자가 대상 DB를 integration에게 직접 connect (Notion UI에서 수행)
- 앱 설정 창에서 token 입력 → DPAPI로 암호화 후 로컬 저장
- 대상 DB ID 입력 또는 검색 (Notion API search endpoint 활용)
- Category property 이름 지정 (DB의 select property 중 선택)
- 연결 테스트 버튼: API 호출하여 DB 접근 가능 여부 확인

### 📝 4.2 스티커 생성 / 편집 / 삭제

#### 생성 트리거

- 시스템 트레이 아이콘 좌클릭 → 새 스티커 생성
- 시스템 트레이 우클릭 메뉴 → "새 스티커"
- 기존 스티커의 `+` 버튼

#### 초기 상태

- 위치: 현재 active monitor의 화면 중앙
- 크기: 250x300px (기본값)
- 색상: 기본 (상단 바 = 검정, 본문 = 흰색)
- 제목 / 본문: 빈 상태로 시작, 제목 input에 즉시 focus

#### 편집

- 제목: 상단 바 영역의 텍스트 input
- 본문: 본문 영역의 multi-line TextBox (plain text + 줄바꿈)
- 위치 변경: 상단 바 드래그
- 크기 변경: 우하단 resize handle

#### 삭제

- 우상단 X 버튼 클릭 → 확인 dialog 표시
  - dialog 메시지: "이 스티커를 삭제할까요? Notion에 동기화된 내용은 그대로 유지됩니다"
- 확인 시 로컬 DB에서 삭제
- Notion page는 절대 삭제하지 않음

### 🔄 4.3 Notion 동기화

#### 기본 정책

- 단방향: 스티커 → Notion
- Debounced auto-save: 변경 발생 후 1.5초간 추가 변경 없으면 sync 실행

#### 데이터 매핑

| 스티커 필드 | Notion 매핑 |
|-------------|-------------|
| `title` | Page의 `title` property |
| `body` | Page body (줄바꿈 단위로 `paragraph` block 배열) |
| `category` | DB의 select property. null이면 호출에서 제외 |
| 작성일 | 매핑하지 않음 (Notion이 자동으로 `created_time` 부여) |

#### Sync 동작

- `notion_page_id`가 null인 스티커 → POST `/v1/pages` (신규 생성), 응답의 page ID를 로컬에 저장
- `notion_page_id`가 있는 스티커 → PATCH 호출 두 번 (page properties 업데이트 + body blocks 교체)
  - Body 교체: 기존 children blocks 모두 archive 후 신규 paragraph blocks append
- 빈 스티커(제목/본문 모두 비어있음)는 sync하지 않음 (로컬에만 보관)

#### 오프라인 / 실패 처리

- 모든 변경은 로컬 DB에 먼저 저장 (single source of truth)
- API 호출 실패 시 `sync_state = 'pending'`으로 마킹
- 재시도 트리거:
  - 앱 시작 시
  - 1분마다 주기적 재시도
  - 사용자가 "수동 sync" 클릭 시

#### Notion page 삭제 감지 → 스티커 자동 재생성

- PATCH 호출이 404 응답 → page가 archive/삭제됨
- 새 page를 생성하고 `notion_page_id` 갱신 → 다시 sync
- 사용자에게는 별도 알림 없이 자동 처리

### 🪟 4.4 위치 및 창 관리

#### 저장 정보

- 스티커별로 `monitor_device_name`, `position_x`, `position_y`, `width`, `height` 저장
- 좌표 단위: 픽셀, 해당 모니터 working area 기준 상대 좌표

#### 저장 시점

- 드래그 종료(`Window.LocationChanged` 이벤트의 마지막 발생) 시점에 저장
- Resize 종료 시점에 크기 저장

#### 부팅 시 복원 로직

```
1. DB에서 모든 스티커 로드
2. 각 스티커마다:
   a. 현재 연결된 모니터 중 동일 device_name 찾기
   b. 있으면: 해당 모니터의 (position_x, position_y)에 배치
   c. 없으면: primary monitor의 (position_x, position_y)에 배치
   d. 배치 후 화면 영역 검증 → 일부라도 영역 밖이면 안쪽으로 clamp
```

#### Clamp 정책

- 스티커의 어느 한 부분이라도 모니터 영역 밖이면 안쪽으로 밀어 넣기
- 우상단 X 버튼이 항상 화면 안에 보이도록 보장 (안 그러면 삭제 불가능)

#### 창 동작

- 일반 윈도우 (always-on-top 아님)
- TaskBar에 표시 안 함 (시스템 트레이로 통합)
- Alt+Tab에 표시 안 함 (옵션, 단순화 위해 표시해도 무방)

### 🔌 4.5 시스템 트레이

#### 트레이 아이콘

항상 표시. 앱이 살아있는 한 유지.

#### 좌클릭

- 새 스티커 생성

#### 우클릭 메뉴

- 새 스티커
- 모든 스티커 표시 (숨겨진 스티커도 화면 위로 가져오기)
- 수동 sync (pending 항목 재시도)
- 설정 열기
- ─────
- 종료

### ⚙️ 4.6 자동 시작

- 설정 창에 "Windows 시작 시 자동 실행" 토글 제공
- 활성화 시 Registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`에 앱 실행 경로 등록
- 비활성화 시 해당 키 제거
- 부팅 직후 실행 시 모든 스티커 자동 복원

### 🎨 4.7 색상 및 외관

- 색상: 무채색 흑/백만 사용
- 영역 분할: 상단 바 + 본문 영역
- 기본 상태: 상단 바 = 검정, 본문 = 흰색
- Swap 상태: 상단 바 = 흰색, 본문 = 검정
- Swap은 전역 설정 (모든 스티커 일괄 적용)
- 설정 창에서 swap 토글 변경 가능

### 🏷️ 4.8 카테고리 옵션 관리

- 앱 시작 시 1회 Notion API 호출하여 DB의 select property 옵션 목록 fetch → 로컬 캐시
- 설정 창에 "옵션 새로고침" 버튼 제공
- 스티커의 카테고리 dropdown은 캐시된 목록에서 선택
- 카테고리 미선택 가능 (null 허용)

## 5. 데이터 모델

### 5.1 로컬 DB 스키마

#### stickers 테이블

```sql
CREATE TABLE stickers (
    id                   TEXT PRIMARY KEY,         -- GUID
    notion_page_id       TEXT,                     -- nullable, 미동기화면 null
    title                TEXT NOT NULL DEFAULT '',
    body                 TEXT NOT NULL DEFAULT '',
    category             TEXT,                     -- nullable
    monitor_device_name  TEXT NOT NULL,
    position_x           INTEGER NOT NULL,
    position_y           INTEGER NOT NULL,
    width                INTEGER NOT NULL,
    height               INTEGER NOT NULL,
    sync_state           TEXT NOT NULL DEFAULT 'pending',  -- synced / pending / failed
    last_synced_at       TEXT,                     -- ISO 8601
    created_at           TEXT NOT NULL,
    updated_at           TEXT NOT NULL
);
```

#### settings 테이블 (key-value)

```sql
CREATE TABLE settings (
    key    TEXT PRIMARY KEY,
    value  TEXT NOT NULL
);
```

저장 키 목록:

- `notion_token` (DPAPI 암호화 후 base64)
- `target_db_id`
- `category_property_name`
- `color_swapped` (boolean)
- `autostart_enabled` (boolean)
- `category_options_cache` (JSON array, 옵션 이름 목록)

### 5.2 Notion API 요청 예시

#### 페이지 생성 (POST /v1/pages)

```json
{
  "parent": { "database_id": "..." },
  "properties": {
    "title": {
      "title": [{ "text": { "content": "스티커 제목" } }]
    },
    "Category": {
      "select": { "name": "아이디어" }
    }
  },
  "children": [
    {
      "type": "paragraph",
      "paragraph": {
        "rich_text": [{ "text": { "content": "본문 첫 줄" } }]
      }
    }
  ]
}
```

## 6. 주요 시나리오 / 엣지 케이스

| 시나리오 | 처리 |
|----------|------|
| 네트워크 없는 상태에서 스티커 작성 | 로컬 저장 + `sync_state = 'pending'`. 복구 시 자동 재시도 |
| Notion에서 page를 archive | 다음 sync 시 404 → 새 page 자동 생성, page_id 갱신 |
| Notion에서 카테고리 변경 | 스티커에 반영 안 됨 (단방향 정책) |
| Notion에서 다른 DB로 page 이동 | page_id로 추적되어 update 계속 동작. 무시 |
| 외부 모니터 분리 | 부팅 시 primary monitor로 fallback + clamp |
| 모니터 해상도 변경 | clamp 적용 (영역 밖 부분만 안으로 밀어넣기) |
| DB 변경 (설정에서 target_db_id 교체) | 기존 스티커는 옛 DB와 매핑 유지, 신규만 새 DB로 |
| 빈 스티커 (제목/본문 모두 비어있음) | sync 안 함, 로컬에만 존재 |
| Token 무효화 (revoke) | API 401 응답 → 설정 창에 경고 표시, 모든 sync 일시 중지 |

## 7. 향후 확장 후보 (v1 범위 외)

- 다중 색상 팔레트 / 카테고리별 색 매핑
- 글로벌 hotkey로 스티커 생성
- 검색 기능 (로컬 스티커 전체 검색)
- 양방향 동기화
- 알림 / 리마인더
- 마크다운 / 리치 텍스트 지원
- 이미지 첨부
- 멀티 디바이스 동기화 (Notion이 source of truth)

## 8. 다음 단계 후보

- 모듈 구조 / 아키텍처 다이어그램 작성 (UI Layer, Sync Layer, Repository Layer, Notion Client 분리 등)
- 화면 와이어프레임 스케치 (설정 창, 스티커 UI, 트레이 메뉴)
- Notion API 호출 시퀀스 다이어그램 (생성, 업데이트, 재생성 시나리오)
- 바로 vibe coding 시작

## GSTACK REVIEW REPORT

| Review | Trigger | Why | Runs | Status | Findings |
|--------|---------|-----|------|--------|----------|
| CEO Review | `/plan-ceo-review` | Scope & strategy | 1 | CLEAR | SELECTIVE EXPANSION mode, Ctrl+B/U cherry-pick accepted |
| Codex Review | `/codex review` | Independent 2nd opinion | 0 | — | — |
| Eng Review | `/plan-eng-review` | Architecture & tests (required) | 2 | CLEAR (CODE) | 2 bugs fixed: RTF crash-on-load → try-catch+fallback; Body \\r\\n regression → TrimEnd |
| Design Review | `/plan-design-review` | UI/UX gaps | 1 | CLEAR (FULL) | score: 5/10 → 8/10, 5 decisions |
| DX Review | `/plan-devex-review` | Developer experience gaps | 0 | — | — |

- **UNRESOLVED:** 0 decisions outstanding
- **VERDICT:** CEO + ENG (×2) + DESIGN CLEARED — ready to ship. Eng review required gate satisfied.
