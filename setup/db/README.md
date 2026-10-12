# FTMS DB 설치 가이드 (PostgreSQL + TimescaleDB)

## 1. 솔루션 안의 DB 관련 자료

경로: `servers/ftms/app-server.ftms/Database/` (zip 안에서 한글 파일명이 `#Uxxxx`로 깨져 보일 수 있음)

| 파일 | 내용 | 판단 |
|---|---|---|
| `1-1 PostgreSQL - ftmsuser 계정 생성.sql` | `tapsuser` 계정, `authdb`·`ftmsdb` 생성, 권한 | ✅ 사용 → `01_create_db_and_user.sql`로 정리 |
| `2-1 시계열 DB 테이블 설치.sql_` | Windows TimescaleDB 설치 메모 (PATH 추가, Setup.exe, `CREATE EXTENSION timescaledb`) | ✅ 참고 |
| `0-0 필요한 것만 모아서...sql` | **최신 통합본**: 이벤트·상태 테이블, 하이퍼테이블, 누적 집계 테이블, 집계/통계 함수 | ✅ 핵심 → `02_ftmsdb_timescale_setup.sql`로 정리. ⚠ 원본에 `delete from process_counts / ftms_event_log / dock_message_events` 가 주석 없이 들어 있음 — **원본 통째 실행 금지** |
| `2-3`, `2-4`, `2-5` `.sql_` | 0-0 이전 단계별 버전 (청크 크기 등 다름, `ScanData` 하이퍼테이블 등 구버전 내용) | ⛔ 구버전 |
| `3-1 자동집계 프로시져 생성.sql_` | `process_counts_aggregate_all` 구버전 (KST 일집계 없음) | ⛔ 구버전 (0-0 사용) |
| `3-2 자동집계 결과 조회 펑션 생성.sql_` | `get_dock_process_statistics_lastest` | 0-0에 동일 포함 |
| `procedure/process_collected_payload_v1~v3.sql` | 수집 처리 함수 구버전 | ⛔ 불필요 (앱이 v4를 자동 생성) |
| `procedure/get_dock_process_statistics_v1~v3.sql` | 통계 함수 구버전 | ⛔ 불필요 |
| `procedure/9_backup_tables.sh`, `9_restore_tables.sh`, `0-1 백업 덤프 복구 삭제.txt` | Docker 컨테이너 `ftms-timescaledb` 기준 pg_dump 백업/복구 | ✅ 운영 백업용 참고 |
| `linux/install_timescaledb_ftms.sh` | Ubuntu: TimescaleDB(PG18) 설치 + 계정/DB 생성 | ✅ Linux 서버용 |
| `NA-3-3 …윈도우 스케쥴러 등록`, `NewFolder/*.ps1, *.bat` | 집계 함수를 NSSM/스케줄러로 1분마다 호출 | ⛔ 불필요 (앱의 DataCollectorService가 1초마다 호출). 사용 시 이중 실행 |
| `DB 마이그레이션 커맨드` | `dotnet ef migrations add` 메모 + "ScanData 릴레이션 없음" 오류 기록 | 참고 (EnsureCreated 순서 문제의 흔적) |
| `DB 연결 문자열 확인` | 연결 문자열 예 | 참고 |
| `auth.db` | 예전 SQLite 인증 DB | ⛔ 미사용 (현재 PostgreSQL) |
| `app-server.ftms/.default.settings/*.json, ftms_event_list.tsv` | 기본 설정·이벤트 마스터 | 앱이 기동 시 DB에 자동 입력 |

기타 자료(솔루션 루트 숨김 폴더 `.readme/`):
- `setup_iis_reverse_proxy/setup_install.pptx` + `rewrite_amd64_ko-KR.msi` — IIS(URL Rewrite+ARR+WebSocket)로 리버스 프록시 구성 가이드 (YARP 대신 IIS 사용 시)
- `인증서 관련 내용 정리 - https 엑세스 사설.pptx` — 사설 인증서(HTTPS) 생성 스크립트
- `scripts_for_window_service/i.bat, u.bat` — app-server Windows 서비스 등록/해제

## 2. 어떤 DB 객체를 누가 만드나

| DB | 객체 | 생성 주체 |
|---|---|---|
| authdb | ASP.NET Identity 테이블, `refresh_tokens`, 기본 Role 7개·기본 계정 | **앱 자동** (EnsureCreated + 시드) |
| ftmsdb | `settings`, `scan_data`, `process_counts`, `ftms_event_list`, `dock_status_change_logs` | **앱 자동** (EF EnsureCreated) |
| ftmsdb | `event_data_store` | **앱 자동** (EventDataStoreService) |
| ftmsdb | `system_config`, `collected_payload`, `rawdata_table`, 함수 `process_collected_payload_v4`, 뷰 `v_payload_performance` | **앱 자동** (CollectedPayloadRepository) |
| ftmsdb | 기본 설정·이벤트 마스터 데이터 | **앱 자동** (`default.settings` 폴더 → DB, 처리 후 파일 이동) |
| ftmsdb | 확장 `timescaledb`, `pgcrypto` | **01 스크립트** (슈퍼유저) |
| ftmsdb | `ftms_event_log`, `dock_message_events` (+하이퍼테이블, 90일 보존) | **02 스크립트** (EF가 생성 제외로 지정) |
| ftmsdb | `process_counts` 하이퍼테이블 변환 + `is_aggregated` 컬럼 | **02 스크립트** |
| ftmsdb | `process_count_per_minute/hour/day_accum`, `process_count_per_day_kst` | **02 스크립트** |
| ftmsdb | 함수 `process_counts_aggregate_all`, `get_dock_process_statistics_lastest` | **02 스크립트** |

## 3. 설치 순서

1. **PostgreSQL 17 설치** (Windows 설치 프로그램, 기본 포트 5432)
2. **TimescaleDB 설치** (PG 17용 Windows zip의 `setup.exe` 실행 → `postgresql.conf` 경로 지정 → 질문에 `y`) → PostgreSQL 서비스 재시작
   - `postgresql.conf`에 `shared_preload_libraries = 'timescaledb'`가 들어갔는지 확인
   - Linux는 `linux/install_timescaledb_ftms.sh`, Docker는 `timescale/timescaledb` 이미지 사용
3. **01 실행** (postgres 계정):
   `psql -U postgres -v app_password="'새비밀번호'" -f 01_create_db_and_user.sql`
4. **app-server.ftms 1회 실행 → 정상 기동 로그 확인 후 종료**
   (EF `EnsureCreated`는 DB에 테이블이 하나라도 있으면 아무것도 만들지 않음 → 반드시 02보다 먼저)
5. **02 실행** (tapsuser): `psql -U tapsuser -d ftmsdb -f 02_ftmsdb_timescale_setup.sql`
6. app-server 재시작 → 도크/에뮬레이터 연결

## 4. 확인 쿼리

```sql
-- ftmsdb
SELECT extname, extversion FROM pg_extension;                                  -- timescaledb, pgcrypto
SELECT hypertable_name FROM timescaledb_information.hypertables ORDER BY 1;     -- 7개
SELECT proname FROM pg_proc WHERE proname IN
 ('process_counts_aggregate_all','get_dock_process_statistics_lastest','process_collected_payload_v4'); -- 3개
SELECT * FROM process_counts_aggregate_all(1000);                               -- 오류 없이 결과 1행
SELECT count(*) FROM process_counts WHERE is_aggregated = false;                -- 운영 중 0 근처 유지
```

## 5. 주의

- **TimescaleDB가 없으면 집계가 조용히 멈춤**: `process_counts_aggregate_all`이 `time_bucket()`(TimescaleDB 함수)을 쓰는데, 오류를 내부에서 잡아 `WARNING`만 남기고 0건 처리로 끝남 → 화면 통계가 비어 보임. PostgreSQL 로그에서 `Error in process_counts_aggregate_all_V2` 확인
- 02의 하이퍼테이블 변환은 데이터가 있어도 되도록 `migrate_data => TRUE`를 넣었지만, 데이터가 많으면 오래 걸리므로 도크 연결 전에 실행 권장
- 원본 `0-0`과 달리 02는 **여러 번 실행해도 안전**(IF NOT EXISTS, 삭제문 제거)
- DB 비밀번호는 원본 스크립트의 `taps12`를 쓰지 말고 바꾼 뒤 `appsettings.json`의 연결 문자열 2개에 동일하게 반영

## 6. 검증 내역

PostgreSQL 16(TimescaleDB 대신 `create_hypertable`/`add_retention_policy`/`time_bucket` 대체 함수)에서 01·02를 실행:
01·02 모두 오류 없음, 02 재실행(멱등) 오류 없음, 표본 50건 → `process_counts_aggregate_all` 50건 처리(분 50/시 12/일 6/KST 6 버킷), `get_dock_process_statistics_lastest` 합계 50 일치.
실제 TimescaleDB 환경에서의 실행은 검증하지 못함.
