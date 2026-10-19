-- =============================================================================
-- 05_clear_test_data.sql                   [테스트 환경 전용 - 운영 DB 실행 금지]
--
--   에뮬레이터로 넣은 테스트 데이터를 모두 지움 (테이블 구조·함수·설정·계정은 유지)
--   실행 위치 : DBeaver  연결 = tapsuser / DB = ftmsdb
--   실행 전   : VS 에서 app-server 중지 (Shift+F5), 에뮬레이터 종료
--   실행 방법 : Alt+X
-- =============================================================================

TRUNCATE TABLE process_counts;
TRUNCATE TABLE process_count_per_minute_accum;
TRUNCATE TABLE process_count_per_hour_accum;
TRUNCATE TABLE process_count_per_day_accum;
TRUNCATE TABLE process_count_per_day_kst;
TRUNCATE TABLE collected_payload;
TRUNCATE TABLE rawdata_table;
TRUNCATE TABLE ftms_event_log;
TRUNCATE TABLE dock_message_events;

-- 확인 : 모두 0
SELECT 'process_counts' AS t, count(*) FROM process_counts
UNION ALL SELECT 'collected_payload', count(*) FROM collected_payload
UNION ALL SELECT 'ftms_event_log',    count(*) FROM ftms_event_log;
