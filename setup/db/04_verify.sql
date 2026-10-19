-- =============================================================================
-- 04_verify.sql                            [가이드 9·11단계 확인용]
--
--   실행 위치 : DBeaver  연결 = tapsuser / DB = ftmsdb
--   실행 방법 : 확인하고 싶은 쿼리에 커서 → Ctrl+Enter
-- =============================================================================

-- [9단계] 하이퍼테이블 : 9개
--   collected_payload, dock_message_events, ftms_event_log,
--   process_count_per_day_accum, process_count_per_day_kst,
--   process_count_per_hour_accum, process_count_per_minute_accum,
--   process_counts, rawdata_table
--   (collected_payload, rawdata_table 은 app-server 가 기동 시 직접 생성)
SELECT hypertable_name FROM timescaledb_information.hypertables ORDER BY 1;

-- [9단계] 함수 : 3개
--   get_dock_process_statistics_lastest, process_collected_payload_v4(앱 생성), process_counts_aggregate_all
SELECT proname FROM pg_proc
WHERE proname IN ('process_counts_aggregate_all',
                  'get_dock_process_statistics_lastest',
                  'process_collected_payload_v4')
ORDER BY 1;

-- [9단계] 집계 함수 호출 테스트 : 오류 없이 0행 이상
SELECT * FROM process_counts_aggregate_all(10);

-- [11단계] 원본 데이터 : 에뮬레이터 실행 중 다시 실행하면 count 증가
SELECT count(*), max(created_at) FROM process_counts;

-- [11단계] 집계 진행 : done 증가, pending 작게 유지
SELECT count(*) FILTER (WHERE is_aggregated)     AS done,
       count(*) FILTER (WHERE NOT is_aggregated) AS pending
FROM process_counts;

-- [11단계] 분 단위 집계 결과
SELECT dock_id, vehicle_number, minute_bucket
FROM process_count_per_minute_accum
ORDER BY minute_bucket DESC
LIMIT 5;

-- [11단계] 알람 이력
SELECT count(*), max(occurred_at) FROM ftms_event_log;
