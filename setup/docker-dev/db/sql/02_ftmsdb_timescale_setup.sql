-- =============================================================================
-- 02_ftmsdb_timescale_setup.sql
--   원본: servers/ftms/app-server.ftms/Database/0-0 필요한 것만 모아서...sql
--   변경: 데이터 삭제문(delete from ...)·확인용 SELECT 제거, CREATE TABLE/INDEX 에
--         IF NOT EXISTS 추가, process_counts 하이퍼테이블에 migrate_data 추가
--         → 여러 번 실행해도 안전(멱등)
--
--   실행 조건
--     1) 01_create_db_and_user.sql 완료 (timescaledb 확장 생성 포함)
--     2) app-server.ftms 를 빈 ftmsdb 로 1회 실행 → EF 테이블(process_counts 등) 생성 후 종료
--     3) ftmsdb 에 tapsuser 로 접속하여 실행:  psql -U tapsuser -d ftmsdb -f 02_ftmsdb_timescale_setup.sql
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 1. ftms_event_log : 알람/이벤트 이력 (EF 에서 ExcludeFromMigrations → 앱이 생성하지 않음)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS ftms_event_log (
	id BIGINT GENERATED ALWAYS AS IDENTITY,
	occurred_at TIMESTAMPTZ NOT NULL,

	event_code VARCHAR(50) NOT NULL,
	device_id VARCHAR(100) NOT NULL,
	device_name VARCHAR(200),
	event_level VARCHAR(50),
	device_type VARCHAR(100),
	extra_data TEXT,
	event_desc VARCHAR(500),
	event_memo TEXT,

	is_acknowledged BOOLEAN NOT NULL DEFAULT false,
	acknowledged_by VARCHAR(100),
	acknowledged_at TIMESTAMPTZ,
	created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

	PRIMARY KEY (id, occurred_at)
);

SELECT create_hypertable('ftms_event_log',  'occurred_at', chunk_time_interval => INTERVAL '7 day', if_not_exists => TRUE);

CREATE INDEX IF NOT EXISTS idx_ftms_event_log_code_occurred ON ftms_event_log (event_code, occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_ftms_event_log_device_occurred ON ftms_event_log (device_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_ftms_event_log_level_occurred ON ftms_event_log (event_level, occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_ftms_event_log_unacknowledged ON ftms_event_log (is_acknowledged, occurred_at DESC) WHERE is_acknowledged = false;

SELECT add_retention_policy('ftms_event_log', INTERVAL '90 days', if_not_exists => TRUE);

-- ---------------------------------------------------------------------------
-- 2. dock_message_events : EventDataStoreService 상태 영속화 (앱이 생성하지 않음)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS dock_message_events (
	id BIGSERIAL NOT NULL,
	type_name TEXT NOT NULL,
	key_json JSONB NOT NULL,
	payload JSONB NOT NULL,
	version TEXT NULL,
	created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
	PRIMARY KEY (id, created_at)
);

SELECT create_hypertable('dock_message_events',  'created_at', chunk_time_interval => INTERVAL '1 day', if_not_exists => TRUE);

CREATE INDEX IF NOT EXISTS ix_dock_message_events_type_name_key_json_id ON dock_message_events USING btree (type_name, key_json, id);
CREATE INDEX IF NOT EXISTS ix_dock_message_events_type_created ON dock_message_events (type_name, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_dock_message_events_payload_gin ON dock_message_events USING gin (payload);

SELECT add_retention_policy('dock_message_events', INTERVAL '90 days', if_not_exists => TRUE);

-- ---------------------------------------------------------------------------
-- 3. process_counts : 처리수량 원본 (EF 가 생성) → 하이퍼테이블 변환
-- ---------------------------------------------------------------------------
SELECT create_hypertable('process_counts', 'created_at', chunk_time_interval => interval '1 day', if_not_exists => TRUE, migrate_data => TRUE);
ALTER TABLE public.process_counts ADD COLUMN IF NOT EXISTS is_aggregated bool DEFAULT false NULL;
CREATE INDEX IF NOT EXISTS idx_process_counts_dock_vehicle_time ON public.process_counts (dock_id, vehicle_number, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_pc_agg_created_at ON process_counts (is_aggregated, created_at);

-- ---------------------------------------------------------------------------
-- 4. 분/시/일 누적 집계 테이블 (process_counts_aggregate_all 이 채움)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.process_count_per_minute_accum (
	dock_id text NOT NULL,
	vehicle_number text NOT NULL,
	minute_bucket timestamptz NOT NULL,
	scanner_scan_sum int8 DEFAULT 0 NULL,
	scanner_multi_sum int8 DEFAULT 0 NULL,
	dimensioner_read_d_sum int8 DEFAULT 0 NULL,
	dimensioner_read_s_sum int8 DEFAULT 0 NULL,
	dimensioner_noread_sum int8 DEFAULT 0 NULL,
	judge_small_sum int8 DEFAULT 0 NULL,
	judge_medium_sum int8 DEFAULT 0 NULL,
	judge_large_sum int8 DEFAULT 0 NULL,
	judge_irregular_sum int8 DEFAULT 0 NULL,
	sorting_small_auto_sum int8 DEFAULT 0 NULL,
	sorting_small_manu_sum int8 DEFAULT 0 NULL,
	sorting_medium_auto_sum int8 DEFAULT 0 NULL,
	sorting_medium_manu_sum int8 DEFAULT 0 NULL,
	sorting_large_auto_sum int8 DEFAULT 0 NULL,
	sorting_large_manu_sum int8 DEFAULT 0 NULL,
	sorting_large_bypass_sum int8 DEFAULT 0 NULL,
	sorting_irregular_auto_sum int8 DEFAULT 0 NULL,
	sorting_irregular_manu_sum int8 DEFAULT 0 NULL,
	sorting_irregular_bypass_sum int8 DEFAULT 0 NULL,
	sorting_irregular_no_rule_sum int8 DEFAULT 0 NULL,
	sorting_irregular_no_info_sum int8 DEFAULT 0 NULL,
	sorting_irregular_trk_err_sum int8 DEFAULT 0 NULL,
	sorting_irregular_gap_err_sum int8 DEFAULT 0 NULL,
	CONSTRAINT process_count_per_minute_accum_pkey PRIMARY KEY (dock_id, vehicle_number, minute_bucket)
);

CREATE TABLE IF NOT EXISTS public.process_count_per_hour_accum (
	dock_id text NOT NULL,
	vehicle_number text NOT NULL,
	hour_bucket timestamptz NOT NULL,
	scanner_scan_sum int8 DEFAULT 0 NULL,
	scanner_multi_sum int8 DEFAULT 0 NULL,
	dimensioner_read_d_sum int8 DEFAULT 0 NULL,
	dimensioner_read_s_sum int8 DEFAULT 0 NULL,
	dimensioner_noread_sum int8 DEFAULT 0 NULL,
	judge_small_sum int8 DEFAULT 0 NULL,
	judge_medium_sum int8 DEFAULT 0 NULL,
	judge_large_sum int8 DEFAULT 0 NULL,
	judge_irregular_sum int8 DEFAULT 0 NULL,
	sorting_small_auto_sum int8 DEFAULT 0 NULL,
	sorting_small_manu_sum int8 DEFAULT 0 NULL,
	sorting_medium_auto_sum int8 DEFAULT 0 NULL,
	sorting_medium_manu_sum int8 DEFAULT 0 NULL,
	sorting_large_auto_sum int8 DEFAULT 0 NULL,
	sorting_large_manu_sum int8 DEFAULT 0 NULL,
	sorting_large_bypass_sum int8 DEFAULT 0 NULL,
	sorting_irregular_auto_sum int8 DEFAULT 0 NULL,
	sorting_irregular_manu_sum int8 DEFAULT 0 NULL,
	sorting_irregular_bypass_sum int8 DEFAULT 0 NULL,
	sorting_irregular_no_rule_sum int8 DEFAULT 0 NULL,
	sorting_irregular_no_info_sum int8 DEFAULT 0 NULL,
	sorting_irregular_trk_err_sum int8 DEFAULT 0 NULL,
	sorting_irregular_gap_err_sum int8 DEFAULT 0 NULL,
	CONSTRAINT process_count_per_hour_accum_pkey PRIMARY KEY (dock_id, vehicle_number, hour_bucket)
);

CREATE TABLE IF NOT EXISTS public.process_count_per_day_accum (
	dock_id text NOT NULL,
	vehicle_number text NOT NULL,
	day_bucket timestamptz NOT NULL,
	scanner_scan_sum int8 DEFAULT 0 NULL,
	scanner_multi_sum int8 DEFAULT 0 NULL,
	dimensioner_read_d_sum int8 DEFAULT 0 NULL,
	dimensioner_read_s_sum int8 DEFAULT 0 NULL,
	dimensioner_noread_sum int8 DEFAULT 0 NULL,
	judge_small_sum int8 DEFAULT 0 NULL,
	judge_medium_sum int8 DEFAULT 0 NULL,
	judge_large_sum int8 DEFAULT 0 NULL,
	judge_irregular_sum int8 DEFAULT 0 NULL,
	sorting_small_auto_sum int8 DEFAULT 0 NULL,
	sorting_small_manu_sum int8 DEFAULT 0 NULL,
	sorting_medium_auto_sum int8 DEFAULT 0 NULL,
	sorting_medium_manu_sum int8 DEFAULT 0 NULL,
	sorting_large_auto_sum int8 DEFAULT 0 NULL,
	sorting_large_manu_sum int8 DEFAULT 0 NULL,
	sorting_large_bypass_sum int8 DEFAULT 0 NULL,
	sorting_irregular_auto_sum int8 DEFAULT 0 NULL,
	sorting_irregular_manu_sum int8 DEFAULT 0 NULL,
	sorting_irregular_bypass_sum int8 DEFAULT 0 NULL,
	sorting_irregular_no_rule_sum int8 DEFAULT 0 NULL,
	sorting_irregular_no_info_sum int8 DEFAULT 0 NULL,
	sorting_irregular_trk_err_sum int8 DEFAULT 0 NULL,
	sorting_irregular_gap_err_sum int8 DEFAULT 0 NULL,
	CONSTRAINT process_count_per_day_accum_pkey PRIMARY KEY (dock_id, vehicle_number, day_bucket)
);


CREATE TABLE IF NOT EXISTS public.process_count_per_day_kst (
    dock_id text NOT NULL,
    vehicle_number text NOT NULL,
    day_bucket date NOT NULL,   -- ⭐ KST 기준 날짜 (date 권장)
    scanner_scan_sum int8 DEFAULT 0,
    scanner_multi_sum int8 DEFAULT 0,
    dimensioner_read_d_sum int8 DEFAULT 0,
    dimensioner_read_s_sum int8 DEFAULT 0,
    dimensioner_noread_sum int8 DEFAULT 0,
    judge_small_sum int8 DEFAULT 0,
    judge_medium_sum int8 DEFAULT 0,
    judge_large_sum int8 DEFAULT 0,
    judge_irregular_sum int8 DEFAULT 0,
    sorting_small_auto_sum int8 DEFAULT 0,
    sorting_small_manu_sum int8 DEFAULT 0,
    sorting_medium_auto_sum int8 DEFAULT 0,
    sorting_medium_manu_sum int8 DEFAULT 0,
    sorting_large_auto_sum int8 DEFAULT 0,
    sorting_large_manu_sum int8 DEFAULT 0,
    sorting_large_bypass_sum int8 DEFAULT 0,
    sorting_irregular_auto_sum int8 DEFAULT 0,
    sorting_irregular_manu_sum int8 DEFAULT 0,
    sorting_irregular_bypass_sum int8 DEFAULT 0,
    sorting_irregular_no_rule_sum int8 DEFAULT 0,
    sorting_irregular_no_info_sum int8 DEFAULT 0,
    sorting_irregular_trk_err_sum int8 DEFAULT 0,
    sorting_irregular_gap_err_sum int8 DEFAULT 0,
    CONSTRAINT process_count_per_day_kst_pkey
        PRIMARY KEY (dock_id, vehicle_number, day_bucket)
);

SELECT create_hypertable('process_count_per_minute_accum', 'minute_bucket', chunk_time_interval => interval '1 day', if_not_exists => TRUE);
SELECT create_hypertable('process_count_per_hour_accum', 'hour_bucket', chunk_time_interval => interval '7 days', if_not_exists => TRUE);
SELECT create_hypertable('process_count_per_day_accum', 'day_bucket', chunk_time_interval => interval '7 days', if_not_exists => TRUE);
SELECT create_hypertable('process_count_per_day_kst', 'day_bucket', chunk_time_interval => interval '7 days', if_not_exists => TRUE);













CREATE INDEX IF NOT EXISTS idx_process_count_per_minute_accum_dock_vehicle_bucket ON public.process_count_per_minute_accum (dock_id, vehicle_number, minute_bucket DESC);
CREATE INDEX IF NOT EXISTS idx_process_count_per_hour_accum_dock_vehicle_bucket ON public.process_count_per_hour_accum (dock_id, vehicle_number, hour_bucket DESC);
CREATE INDEX IF NOT EXISTS idx_process_count_per_day_accum_dock_vehicle_bucket ON public.process_count_per_day_accum (dock_id, vehicle_number, day_bucket DESC);
CREATE INDEX IF NOT EXISTS idx_process_count_per_day_kst_dock_vehicle_bucket ON public.process_count_per_day_kst (dock_id, vehicle_number, day_bucket DESC);

GRANT ALL ON TABLE process_count_per_minute_accum TO tapsuser;
GRANT ALL ON TABLE process_count_per_hour_accum TO tapsuser;
GRANT ALL ON TABLE process_count_per_day_accum TO tapsuser;
GRANT ALL ON TABLE process_count_per_day_kst TO tapsuser;




-- ---------------------------------------------------------------------------
-- 5. 집계 함수 : DataCollectorService 가 1초마다 SELECT * FROM process_counts_aggregate_all(20000) 호출
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.process_counts_aggregate_all(
    p_batch_size integer DEFAULT 1000
)
RETURNS TABLE(
    processed_count integer,
    minute_buckets integer,
    hour_buckets integer,
    day_buckets integer,
    day_kst_buckets integer,
    duration interval
)
LANGUAGE plpgsql
AS $function$
DECLARE
    -- 업무 기준 하루 시작 시각
    -- KST 11:00 이전 작업량 → 전날로 집계
    workday_offset_hours CONSTANT INTEGER := -2;
    l_processed_count INT := 0;
    l_minute_buckets INT := 0;
    l_hour_buckets INT := 0;
    l_day_buckets INT := 0;
    l_day_kst_buckets INT := 0;
    l_start_time TIMESTAMP;
    l_error_message TEXT;
BEGIN
    l_start_time := clock_timestamp();

    /* ========================================
       1단계: 임시 테이블 생성 및 초기화
       ======================================== */
    -- PK 충돌 방지를 위해 ON COMMIT PRESERVE ROWS 유지
    CREATE TEMP TABLE IF NOT EXISTS temp_aggregate_keys (
        dock_id text,
        vehicle_number text,
        created_at timestamptz,
        PRIMARY KEY (dock_id, vehicle_number, created_at)
    ) ON COMMIT PRESERVE ROWS;
    TRUNCATE temp_aggregate_keys;

    CREATE TEMP TABLE IF NOT EXISTS temp_minute_result (
        dock_id text,
        vehicle_number text,
        minute_bucket timestamptz,
        scanner_scan_sum bigint,
        scanner_multi_sum bigint,
        dimensioner_read_d_sum bigint,
        dimensioner_read_s_sum bigint,
        dimensioner_noread_sum bigint,
        judge_small_sum bigint,
        judge_medium_sum bigint,
        judge_large_sum bigint,
        judge_irregular_sum bigint,
        sorting_small_auto_sum bigint,
        sorting_small_manu_sum bigint,
        sorting_medium_auto_sum bigint,
        sorting_medium_manu_sum bigint,
        sorting_large_auto_sum bigint,
        sorting_large_manu_sum bigint,
        sorting_large_bypass_sum bigint,
        sorting_irregular_auto_sum bigint,
        sorting_irregular_manu_sum bigint,
        sorting_irregular_bypass_sum bigint,
        sorting_irregular_no_rule_sum bigint,
        sorting_irregular_no_info_sum bigint,
        sorting_irregular_trk_err_sum bigint,
        sorting_irregular_gap_err_sum bigint,
        PRIMARY KEY (dock_id, vehicle_number, minute_bucket)
    ) ON COMMIT PRESERVE ROWS;
    TRUNCATE temp_minute_result;

    /* ========================================
       2단계: 원본 데이터 추출 (동시성 제어 적용) 및 분단위 집계
       ======================================== */
    -- FOR UPDATE SKIP LOCKED를 사용하여 여러 세션에서 동시 실행 시 중복 집계 방지
    WITH target_selection AS (
        SELECT dock_id, vehicle_number, created_at
        FROM process_counts
        WHERE is_aggregated = false
        ORDER BY created_at
        LIMIT p_batch_size
        FOR UPDATE SKIP LOCKED
    ),
    inserted_keys AS (
        INSERT INTO temp_aggregate_keys (dock_id, vehicle_number, created_at)
        SELECT dock_id, vehicle_number, created_at FROM target_selection
        RETURNING *
    ),
    minute_aggregates AS (
        SELECT
            pc.dock_id,
            pc.vehicle_number,
            time_bucket('1 minute', pc.created_at) AS minute_bucket,
            SUM(pc.scanner_scan) AS scanner_scan_sum,
            SUM(pc.scanner_multi) AS scanner_multi_sum,
            SUM(pc.dimensioner_read_d) AS dimensioner_read_d_sum,
            SUM(pc.dimensioner_read_s) AS dimensioner_read_s_sum,
            SUM(pc.dimensioner_noread) AS dimensioner_noread_sum,
            SUM(pc.judge_small) AS judge_small_sum,
            SUM(pc.judge_medium) AS judge_medium_sum,
            SUM(pc.judge_large) AS judge_large_sum,
            SUM(pc.judge_irregular) AS judge_irregular_sum,
            SUM(pc.sorting_small_auto) AS sorting_small_auto_sum,
            SUM(pc.sorting_small_manu) AS sorting_small_manu_sum,
            SUM(pc.sorting_medium_auto) AS sorting_medium_auto_sum,
            SUM(pc.sorting_medium_manu) AS sorting_medium_manu_sum,
            SUM(pc.sorting_large_auto) AS sorting_large_auto_sum,
            SUM(pc.sorting_large_manu) AS sorting_large_manu_sum,
            SUM(pc.sorting_large_bypass) AS sorting_large_bypass_sum,
            SUM(pc.sorting_irregular_auto) AS sorting_irregular_auto_sum,
            SUM(pc.sorting_irregular_manu) AS sorting_irregular_manu_sum,
            SUM(pc.sorting_irregular_bypass) AS sorting_irregular_bypass_sum,
            SUM(pc.sorting_irregular_no_rule) AS sorting_irregular_no_rule_sum,
            SUM(pc.sorting_irregular_no_info) AS sorting_irregular_no_info_sum,
            SUM(pc.sorting_irregular_trk_err) AS sorting_irregular_trk_err_sum,
            SUM(pc.sorting_irregular_gap_err) AS sorting_irregular_gap_err_sum
        FROM process_counts pc
        JOIN inserted_keys ik 
          ON pc.dock_id = ik.dock_id 
         AND pc.vehicle_number = ik.vehicle_number 
         AND pc.created_at = ik.created_at
        GROUP BY pc.dock_id, pc.vehicle_number, 3
    )
    INSERT INTO temp_minute_result
    SELECT * FROM minute_aggregates;

    SELECT COUNT(*) INTO l_processed_count FROM temp_aggregate_keys;

    /* ========================================
       3~6단계: 누적 테이블 업데이트 (Upsert)
       ======================================== */
    -- [분단위]
    WITH upsert_min AS (
        INSERT INTO process_count_per_minute_accum AS target
        SELECT * FROM temp_minute_result
        ON CONFLICT (dock_id, vehicle_number, minute_bucket) DO UPDATE SET
            scanner_scan_sum = target.scanner_scan_sum + EXCLUDED.scanner_scan_sum,
            scanner_multi_sum = target.scanner_multi_sum + EXCLUDED.scanner_multi_sum,
            dimensioner_read_d_sum = target.dimensioner_read_d_sum + EXCLUDED.dimensioner_read_d_sum,
            dimensioner_read_s_sum = target.dimensioner_read_s_sum + EXCLUDED.dimensioner_read_s_sum,
            dimensioner_noread_sum = target.dimensioner_noread_sum + EXCLUDED.dimensioner_noread_sum,
            judge_small_sum = target.judge_small_sum + EXCLUDED.judge_small_sum,
            judge_medium_sum = target.judge_medium_sum + EXCLUDED.judge_medium_sum,
            judge_large_sum = target.judge_large_sum + EXCLUDED.judge_large_sum,
            judge_irregular_sum = target.judge_irregular_sum + EXCLUDED.judge_irregular_sum,
            sorting_small_auto_sum = target.sorting_small_auto_sum + EXCLUDED.sorting_small_auto_sum,
            sorting_small_manu_sum = target.sorting_small_manu_sum + EXCLUDED.sorting_small_manu_sum,
            sorting_medium_auto_sum = target.sorting_medium_auto_sum + EXCLUDED.sorting_medium_auto_sum,
            sorting_medium_manu_sum = target.sorting_medium_manu_sum + EXCLUDED.sorting_medium_manu_sum,
            sorting_large_auto_sum = target.sorting_large_auto_sum + EXCLUDED.sorting_large_auto_sum,
            sorting_large_manu_sum = target.sorting_large_manu_sum + EXCLUDED.sorting_large_manu_sum,
            sorting_large_bypass_sum = target.sorting_large_bypass_sum + EXCLUDED.sorting_large_bypass_sum,
            sorting_irregular_auto_sum = target.sorting_irregular_auto_sum + EXCLUDED.sorting_irregular_auto_sum,
            sorting_irregular_manu_sum = target.sorting_irregular_manu_sum + EXCLUDED.sorting_irregular_manu_sum,
            sorting_irregular_bypass_sum = target.sorting_irregular_bypass_sum + EXCLUDED.sorting_irregular_bypass_sum,
            sorting_irregular_no_rule_sum = target.sorting_irregular_no_rule_sum + EXCLUDED.sorting_irregular_no_rule_sum,
            sorting_irregular_no_info_sum = target.sorting_irregular_no_info_sum + EXCLUDED.sorting_irregular_no_info_sum,
            sorting_irregular_trk_err_sum = target.sorting_irregular_trk_err_sum + EXCLUDED.sorting_irregular_trk_err_sum,
            sorting_irregular_gap_err_sum = target.sorting_irregular_gap_err_sum + EXCLUDED.sorting_irregular_gap_err_sum
        RETURNING 1
    ) SELECT COUNT(*) INTO l_minute_buckets FROM upsert_min;

    -- [시간단위 UTC]
    WITH upsert_hour AS (
        INSERT INTO process_count_per_hour_accum AS target (dock_id, vehicle_number, hour_bucket, scanner_scan_sum, scanner_multi_sum, dimensioner_read_d_sum, dimensioner_read_s_sum, dimensioner_noread_sum, judge_small_sum, judge_medium_sum, judge_large_sum, judge_irregular_sum, sorting_small_auto_sum, sorting_small_manu_sum, sorting_medium_auto_sum, sorting_medium_manu_sum, sorting_large_auto_sum, sorting_large_manu_sum, sorting_large_bypass_sum, sorting_irregular_auto_sum, sorting_irregular_manu_sum, sorting_irregular_bypass_sum, sorting_irregular_no_rule_sum, sorting_irregular_no_info_sum, sorting_irregular_trk_err_sum, sorting_irregular_gap_err_sum)
        SELECT dock_id, vehicle_number, date_trunc('hour', minute_bucket), SUM(scanner_scan_sum), SUM(scanner_multi_sum), SUM(dimensioner_read_d_sum), SUM(dimensioner_read_s_sum), SUM(dimensioner_noread_sum), SUM(judge_small_sum), SUM(judge_medium_sum), SUM(judge_large_sum), SUM(judge_irregular_sum), SUM(sorting_small_auto_sum), SUM(sorting_small_manu_sum), SUM(sorting_medium_auto_sum), SUM(sorting_medium_manu_sum), SUM(sorting_large_auto_sum), SUM(sorting_large_manu_sum), SUM(sorting_large_bypass_sum), SUM(sorting_irregular_auto_sum), SUM(sorting_irregular_manu_sum), SUM(sorting_irregular_bypass_sum), SUM(sorting_irregular_no_rule_sum), SUM(sorting_irregular_no_info_sum), SUM(sorting_irregular_trk_err_sum), SUM(sorting_irregular_gap_err_sum)
        FROM temp_minute_result GROUP BY 1, 2, 3
        ON CONFLICT (dock_id, vehicle_number, hour_bucket) DO UPDATE SET
            scanner_scan_sum = target.scanner_scan_sum + EXCLUDED.scanner_scan_sum, -- (중략: 다른 컬럼들도 분단위와 동일한 방식으로 업데이트)
            scanner_multi_sum = target.scanner_multi_sum + EXCLUDED.scanner_multi_sum,
            dimensioner_read_d_sum = target.dimensioner_read_d_sum + EXCLUDED.dimensioner_read_d_sum,
            dimensioner_read_s_sum = target.dimensioner_read_s_sum + EXCLUDED.dimensioner_read_s_sum,
            dimensioner_noread_sum = target.dimensioner_noread_sum + EXCLUDED.dimensioner_noread_sum,
            judge_small_sum = target.judge_small_sum + EXCLUDED.judge_small_sum,
            judge_medium_sum = target.judge_medium_sum + EXCLUDED.judge_medium_sum,
            judge_large_sum = target.judge_large_sum + EXCLUDED.judge_large_sum,
            judge_irregular_sum = target.judge_irregular_sum + EXCLUDED.judge_irregular_sum,
            sorting_small_auto_sum = target.sorting_small_auto_sum + EXCLUDED.sorting_small_auto_sum,
            sorting_small_manu_sum = target.sorting_small_manu_sum + EXCLUDED.sorting_small_manu_sum,
            sorting_medium_auto_sum = target.sorting_medium_auto_sum + EXCLUDED.sorting_medium_auto_sum,
            sorting_medium_manu_sum = target.sorting_medium_manu_sum + EXCLUDED.sorting_medium_manu_sum,
            sorting_large_auto_sum = target.sorting_large_auto_sum + EXCLUDED.sorting_large_auto_sum,
            sorting_large_manu_sum = target.sorting_large_manu_sum + EXCLUDED.sorting_large_manu_sum,
            sorting_large_bypass_sum = target.sorting_large_bypass_sum + EXCLUDED.sorting_large_bypass_sum,
            sorting_irregular_auto_sum = target.sorting_irregular_auto_sum + EXCLUDED.sorting_irregular_auto_sum,
            sorting_irregular_manu_sum = target.sorting_irregular_manu_sum + EXCLUDED.sorting_irregular_manu_sum,
            sorting_irregular_bypass_sum = target.sorting_irregular_bypass_sum + EXCLUDED.sorting_irregular_bypass_sum,
            sorting_irregular_no_rule_sum = target.sorting_irregular_no_rule_sum + EXCLUDED.sorting_irregular_no_rule_sum,
            sorting_irregular_no_info_sum = target.sorting_irregular_no_info_sum + EXCLUDED.sorting_irregular_no_info_sum,
            sorting_irregular_trk_err_sum = target.sorting_irregular_trk_err_sum + EXCLUDED.sorting_irregular_trk_err_sum,
            sorting_irregular_gap_err_sum = target.sorting_irregular_gap_err_sum + EXCLUDED.sorting_irregular_gap_err_sum
        RETURNING 1
    ) SELECT COUNT(*) INTO l_hour_buckets FROM upsert_hour;

    -- [일단위 UTC]
    WITH upsert_day AS (
        INSERT INTO process_count_per_day_accum AS target (dock_id, vehicle_number, day_bucket, scanner_scan_sum, scanner_multi_sum, dimensioner_read_d_sum, dimensioner_read_s_sum, dimensioner_noread_sum, judge_small_sum, judge_medium_sum, judge_large_sum, judge_irregular_sum, sorting_small_auto_sum, sorting_small_manu_sum, sorting_medium_auto_sum, sorting_medium_manu_sum, sorting_large_auto_sum, sorting_large_manu_sum, sorting_large_bypass_sum, sorting_irregular_auto_sum, sorting_irregular_manu_sum, sorting_irregular_bypass_sum, sorting_irregular_no_rule_sum, sorting_irregular_no_info_sum, sorting_irregular_trk_err_sum, sorting_irregular_gap_err_sum)
        SELECT dock_id, vehicle_number, date_trunc('day', minute_bucket), SUM(scanner_scan_sum), SUM(scanner_multi_sum), SUM(dimensioner_read_d_sum), SUM(dimensioner_read_s_sum), SUM(dimensioner_noread_sum), SUM(judge_small_sum), SUM(judge_medium_sum), SUM(judge_large_sum), SUM(judge_irregular_sum), SUM(sorting_small_auto_sum), SUM(sorting_small_manu_sum), SUM(sorting_medium_auto_sum), SUM(sorting_medium_manu_sum), SUM(sorting_large_auto_sum), SUM(sorting_large_manu_sum), SUM(sorting_large_bypass_sum), SUM(sorting_irregular_auto_sum), SUM(sorting_irregular_manu_sum), SUM(sorting_irregular_bypass_sum), SUM(sorting_irregular_no_rule_sum), SUM(sorting_irregular_no_info_sum), SUM(sorting_irregular_trk_err_sum), SUM(sorting_irregular_gap_err_sum)
        FROM temp_minute_result GROUP BY 1, 2, 3
        ON CONFLICT (dock_id, vehicle_number, day_bucket) DO UPDATE SET
            scanner_scan_sum = target.scanner_scan_sum + EXCLUDED.scanner_scan_sum,
            scanner_multi_sum = target.scanner_multi_sum + EXCLUDED.scanner_multi_sum,
            dimensioner_read_d_sum = target.dimensioner_read_d_sum + EXCLUDED.dimensioner_read_d_sum,
            dimensioner_read_s_sum = target.dimensioner_read_s_sum + EXCLUDED.dimensioner_read_s_sum,
            dimensioner_noread_sum = target.dimensioner_noread_sum + EXCLUDED.dimensioner_noread_sum,
            judge_small_sum = target.judge_small_sum + EXCLUDED.judge_small_sum,
            judge_medium_sum = target.judge_medium_sum + EXCLUDED.judge_medium_sum,
            judge_large_sum = target.judge_large_sum + EXCLUDED.judge_large_sum,
            judge_irregular_sum = target.judge_irregular_sum + EXCLUDED.judge_irregular_sum,
            sorting_small_auto_sum = target.sorting_small_auto_sum + EXCLUDED.sorting_small_auto_sum,
            sorting_small_manu_sum = target.sorting_small_manu_sum + EXCLUDED.sorting_small_manu_sum,
            sorting_medium_auto_sum = target.sorting_medium_auto_sum + EXCLUDED.sorting_medium_auto_sum,
            sorting_medium_manu_sum = target.sorting_medium_manu_sum + EXCLUDED.sorting_medium_manu_sum,
            sorting_large_auto_sum = target.sorting_large_auto_sum + EXCLUDED.sorting_large_auto_sum,
            sorting_large_manu_sum = target.sorting_large_manu_sum + EXCLUDED.sorting_large_manu_sum,
            sorting_large_bypass_sum = target.sorting_large_bypass_sum + EXCLUDED.sorting_large_bypass_sum,
            sorting_irregular_auto_sum = target.sorting_irregular_auto_sum + EXCLUDED.sorting_irregular_auto_sum,
            sorting_irregular_manu_sum = target.sorting_irregular_manu_sum + EXCLUDED.sorting_irregular_manu_sum,
            sorting_irregular_bypass_sum = target.sorting_irregular_bypass_sum + EXCLUDED.sorting_irregular_bypass_sum,
            sorting_irregular_no_rule_sum = target.sorting_irregular_no_rule_sum + EXCLUDED.sorting_irregular_no_rule_sum,
            sorting_irregular_no_info_sum = target.sorting_irregular_no_info_sum + EXCLUDED.sorting_irregular_no_info_sum,
            sorting_irregular_trk_err_sum = target.sorting_irregular_trk_err_sum + EXCLUDED.sorting_irregular_trk_err_sum,
            sorting_irregular_gap_err_sum = target.sorting_irregular_gap_err_sum + EXCLUDED.sorting_irregular_gap_err_sum
        RETURNING 1
    ) SELECT COUNT(*) INTO l_day_buckets FROM upsert_day;

    -- [일단위 KST]
    WITH upsert_day_kst AS (
        INSERT INTO process_count_per_day_kst AS target (dock_id, vehicle_number, day_bucket, scanner_scan_sum, scanner_multi_sum, dimensioner_read_d_sum, dimensioner_read_s_sum, dimensioner_noread_sum, judge_small_sum, judge_medium_sum, judge_large_sum, judge_irregular_sum, sorting_small_auto_sum, sorting_small_manu_sum, sorting_medium_auto_sum, sorting_medium_manu_sum, sorting_large_auto_sum, sorting_large_manu_sum, sorting_large_bypass_sum, sorting_irregular_auto_sum, sorting_irregular_manu_sum, sorting_irregular_bypass_sum, sorting_irregular_no_rule_sum, sorting_irregular_no_info_sum, sorting_irregular_trk_err_sum, sorting_irregular_gap_err_sum)
        SELECT 
            dock_id, 
            vehicle_number, 
         -- (minute_bucket AT TIME ZONE 'Asia/Seoul')::date, -- KST 변환 최적화
            (minute_bucket + make_interval(hours => workday_offset_hours))::date,
            SUM(scanner_scan_sum), SUM(scanner_multi_sum), SUM(dimensioner_read_d_sum), SUM(dimensioner_read_s_sum), SUM(dimensioner_noread_sum), SUM(judge_small_sum), SUM(judge_medium_sum), SUM(judge_large_sum), SUM(judge_irregular_sum), SUM(sorting_small_auto_sum), SUM(sorting_small_manu_sum), SUM(sorting_medium_auto_sum), SUM(sorting_medium_manu_sum), SUM(sorting_large_auto_sum), SUM(sorting_large_manu_sum), SUM(sorting_large_bypass_sum), SUM(sorting_irregular_auto_sum), SUM(sorting_irregular_manu_sum), SUM(sorting_irregular_bypass_sum), SUM(sorting_irregular_no_rule_sum), SUM(sorting_irregular_no_info_sum), SUM(sorting_irregular_trk_err_sum), SUM(sorting_irregular_gap_err_sum)
        FROM temp_minute_result GROUP BY 1, 2, 3
        ON CONFLICT (dock_id, vehicle_number, day_bucket) DO UPDATE SET
            scanner_scan_sum = target.scanner_scan_sum + EXCLUDED.scanner_scan_sum,
            scanner_multi_sum = target.scanner_multi_sum + EXCLUDED.scanner_multi_sum,
            dimensioner_read_d_sum = target.dimensioner_read_d_sum + EXCLUDED.dimensioner_read_d_sum,
            dimensioner_read_s_sum = target.dimensioner_read_s_sum + EXCLUDED.dimensioner_read_s_sum,
            dimensioner_noread_sum = target.dimensioner_noread_sum + EXCLUDED.dimensioner_noread_sum,
            judge_small_sum = target.judge_small_sum + EXCLUDED.judge_small_sum,
            judge_medium_sum = target.judge_medium_sum + EXCLUDED.judge_medium_sum,
            judge_large_sum = target.judge_large_sum + EXCLUDED.judge_large_sum,
            judge_irregular_sum = target.judge_irregular_sum + EXCLUDED.judge_irregular_sum,
            sorting_small_auto_sum = target.sorting_small_auto_sum + EXCLUDED.sorting_small_auto_sum,
            sorting_small_manu_sum = target.sorting_small_manu_sum + EXCLUDED.sorting_small_manu_sum,
            sorting_medium_auto_sum = target.sorting_medium_auto_sum + EXCLUDED.sorting_medium_auto_sum,
            sorting_medium_manu_sum = target.sorting_medium_manu_sum + EXCLUDED.sorting_medium_manu_sum,
            sorting_large_auto_sum = target.sorting_large_auto_sum + EXCLUDED.sorting_large_auto_sum,
            sorting_large_manu_sum = target.sorting_large_manu_sum + EXCLUDED.sorting_large_manu_sum,
            sorting_large_bypass_sum = target.sorting_large_bypass_sum + EXCLUDED.sorting_large_bypass_sum,
            sorting_irregular_auto_sum = target.sorting_irregular_auto_sum + EXCLUDED.sorting_irregular_auto_sum,
            sorting_irregular_manu_sum = target.sorting_irregular_manu_sum + EXCLUDED.sorting_irregular_manu_sum,
            sorting_irregular_bypass_sum = target.sorting_irregular_bypass_sum + EXCLUDED.sorting_irregular_bypass_sum,
            sorting_irregular_no_rule_sum = target.sorting_irregular_no_rule_sum + EXCLUDED.sorting_irregular_no_rule_sum,
            sorting_irregular_no_info_sum = target.sorting_irregular_no_info_sum + EXCLUDED.sorting_irregular_no_info_sum,
            sorting_irregular_trk_err_sum = target.sorting_irregular_trk_err_sum + EXCLUDED.sorting_irregular_trk_err_sum,
            sorting_irregular_gap_err_sum = target.sorting_irregular_gap_err_sum + EXCLUDED.sorting_irregular_gap_err_sum
        RETURNING 1
    ) SELECT COUNT(*) INTO l_day_kst_buckets FROM upsert_day_kst;

    /* ========================================
       7단계: 집계 완료 플래그 업데이트 (성능 최적화: JOIN 사용)
       ======================================== */
    UPDATE process_counts pc
    SET is_aggregated = true
    FROM temp_aggregate_keys tak
    WHERE pc.dock_id = tak.dock_id
      AND pc.vehicle_number = tak.vehicle_number
      AND pc.created_at = tak.created_at;

    /* ========================================
       8단계: 결과 반환
       ======================================== */
    RETURN QUERY
    SELECT l_processed_count, l_minute_buckets, l_hour_buckets, l_day_buckets, l_day_kst_buckets, clock_timestamp() - l_start_time;

EXCEPTION
    WHEN OTHERS THEN
        GET STACKED DIAGNOSTICS l_error_message = MESSAGE_TEXT;
        RAISE WARNING 'Error in process_counts_aggregate_all_V2: %', l_error_message;
        RETURN QUERY
        SELECT l_processed_count, l_minute_buckets, l_hour_buckets, l_day_buckets, l_day_kst_buckets, clock_timestamp() - l_start_time;
END;
$function$;

-- ---------------------------------------------------------------------------
-- 6. 통계 조회 함수 : ProcessCountController(/api/ProcessCount/statics) 가 사용
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.get_dock_process_statistics_lastest(
--CREATE OR REPLACE FUNCTION public.get_dock_process_statistics_v3(
    p_dock_ids text[], 
    p_start_time timestamp with time zone, 
    p_end_time timestamp with time zone
)
RETURNS TABLE(
    dock_id text, 
    vehicle_number text, 
    scanner_scan_sum bigint, 
    scanner_multi_sum bigint, 
    dimensioner_read_d_sum bigint, 
    dimensioner_read_s_sum bigint, 
    dimensioner_noread_sum bigint, 
    judge_small_sum bigint, 
    judge_medium_sum bigint, 
    judge_large_sum bigint, 
    judge_irregular_sum bigint, 
    sorting_small_auto_sum bigint, 
    sorting_small_manu_sum bigint, 
    sorting_medium_auto_sum bigint, 
    sorting_medium_manu_sum bigint, 
    sorting_large_auto_sum bigint, 
    sorting_large_manu_sum bigint, 
    sorting_large_bypass_sum bigint, 
    sorting_irregular_auto_sum bigint, 
    sorting_irregular_manu_sum bigint, 
    sorting_irregular_bypass_sum bigint, 
    sorting_irregular_no_rule_sum bigint, 
    sorting_irregular_no_info_sum bigint, 
    sorting_irregular_trk_err_sum bigint, 
    sorting_irregular_gap_err_sum bigint
)
LANGUAGE plpgsql
AS $function$
DECLARE
    -- ========================================
    -- UTC 기준 시간 변수
    -- ========================================
    -- 입력 파라미터는 이미 timestamptz 타입으로 UTC 기준이지만,
    -- 코드 가독성과 의도 명확화를 위해 별도 변수로 관리
    v_start_utc TIMESTAMPTZ;
    v_end_utc TIMESTAMPTZ;
    
    -- ========================================
    -- 시간 범위 계산 변수
    -- ========================================
    v_duration INTERVAL;           -- 전체 조회 기간
    v_duration_hours NUMERIC;      -- 조회 기간 (시간 단위)
    
    -- ========================================
    -- 사용할 집계 테이블 결정 플래그
    -- ========================================
    v_use_day_table BOOLEAN;       -- Day 집계 테이블 사용 여부
    v_use_hour_table BOOLEAN;      -- Hour 집계 테이블 사용 여부
    v_use_minute_table BOOLEAN;    -- Minute 집계 테이블 사용 여부
    
    -- ========================================
    -- 시간 경계 계산 변수
    -- ========================================
    -- 각 시간 단위의 시작/종료 경계를 저장
    v_start_hour TIMESTAMPTZ;      -- 시작 시간이 속한 시간의 시작 (정시)
    v_end_hour TIMESTAMPTZ;        -- 종료 시간이 속한 시간의 시작 (정시)
    v_start_day TIMESTAMPTZ;       -- 시작 시간이 속한 날의 시작 (00:00)
    v_end_day TIMESTAMPTZ;         -- 종료 시간이 속한 날의 시작 (00:00)
    
    -- ========================================
    -- Day 테이블 조회 범위
    -- ========================================
    -- 48시간 초과 시, 시작일 다음날부터 종료일 전날까지의 온전한 날짜만 조회
    v_day_start TIMESTAMPTZ;       -- Day 집계 시작 (시작일 다음날 00:00)
    v_day_end TIMESTAMPTZ;         -- Day 집계 종료 (종료일 00:00)
    
    -- ========================================
    -- Hour 테이블 조회 범위
    -- ========================================
    -- Hour Header: 시작 시간부터 첫 온전한 시간까지
    -- Hour Tail: 마지막 온전한 시간부터 종료 시간까지 (48시간 초과 시에만)
    v_hour_header_start TIMESTAMPTZ;  -- Hour Header 시작
    v_hour_header_end TIMESTAMPTZ;    -- Hour Header 종료
    v_hour_tail_start TIMESTAMPTZ;    -- Hour Tail 시작 (48시간 초과 시에만)
    v_hour_tail_end TIMESTAMPTZ;      -- Hour Tail 종료 (48시간 초과 시에만)
    
    -- ========================================
    -- Minute 테이블 조회 범위
    -- ========================================
    -- Minute Header: 시작 시간부터 첫 정시까지
    -- Minute Tail: 마지막 정시부터 종료 시간까지 (1시간 초과 시에만)
    v_minute_header_start TIMESTAMPTZ;  -- Minute Header 시작
    v_minute_header_end TIMESTAMPTZ;    -- Minute Header 종료
    v_minute_tail_start TIMESTAMPTZ;    -- Minute Tail 시작 (1시간 초과 시에만)
    v_minute_tail_end TIMESTAMPTZ;      -- Minute Tail 종료 (1시간 초과 시에만)
    
    -- ========================================
    -- 필터 관련 변수
    -- ========================================
    v_has_dock_filter BOOLEAN;     -- dock_id 필터 적용 여부
BEGIN
    -- ========================================
    -- 1. 입력 파라미터 유효성 검사
    -- ========================================
    IF p_start_time >= p_end_time THEN
        RAISE EXCEPTION 'Start time must be earlier than end time';
    END IF;

    -- ========================================
    -- 2. UTC 기준 시간 변수 초기화
    -- ========================================
    -- 입력 파라미터(timestamptz)는 이미 내부적으로 UTC로 저장되어 있음
    -- 별도 변환 없이 대입만 수행 (의도 명확화 목적)
    v_start_utc := p_start_time;
    v_end_utc := p_end_time;

    -- ========================================
    -- 3. 필터 및 기간 계산
    -- ========================================
    -- dock_id 필터가 제공되었는지 확인
    v_has_dock_filter := p_dock_ids IS NOT NULL AND array_length(p_dock_ids, 1) > 0;
    
    -- 전체 조회 기간 계산
    v_duration := v_end_utc - v_start_utc;
    v_duration_hours := EXTRACT(EPOCH FROM v_duration) / 3600;
    
    -- ========================================
    -- 4. 시간 경계 계산 (UTC 기준)
    -- ========================================
    -- date_trunc을 사용하여 각 시간 단위의 시작점 계산
    v_start_hour := date_trunc('hour', v_start_utc);  -- 예: 14:30 -> 14:00
    v_end_hour := date_trunc('hour', v_end_utc);      -- 예: 16:45 -> 16:00
    v_start_day := date_trunc('day', v_start_utc);    -- 예: 14:30 -> 00:00
    v_end_day := date_trunc('day', v_end_utc);        -- 예: 16:45 -> 00:00

    -- ============================================
    -- 5. 사용할 집계 테이블 결정
    -- ============================================
    -- 조회 기간에 따라 최적의 집계 테이블 조합 선택
    
    -- --------------------------------------------
    -- 케이스 1: 1시간 미만 (< 1시간)
    -- --------------------------------------------
    -- 사용 테이블: Minute만 사용
    -- 이유: 짧은 기간이므로 Minute 단위 집계만으로 충분
    IF v_duration_hours < 1 THEN
        v_use_day_table := FALSE;
        v_use_hour_table := FALSE;
        v_use_minute_table := TRUE;
        
        -- Minute 범위: 시작 시간부터 종료 시간까지 전체
        v_minute_header_start := v_start_utc;
        v_minute_header_end := v_end_utc;
        
    -- --------------------------------------------
    -- 케이스 2: 1시간 이상 ~ 48시간 이하
    -- --------------------------------------------
    -- 사용 테이블: Hour + Minute
    -- 구조:
    --   [Minute Header] + [Hour 전체] + [Minute Tail]
    --   시작~첫정시    정시들        마지막정시~종료
    ELSIF v_duration_hours <= 48 THEN
        v_use_day_table := FALSE;
        v_use_hour_table := TRUE;
        v_use_minute_table := TRUE;
        
        -- Hour 범위: 첫 정시부터 마지막 정시까지
        -- 예: 14:30 ~ 16:45인 경우
        --     Hour: 15:00 ~ 16:00 (15시, 16시)
        v_hour_header_start := v_start_hour + interval '1 hour';
        v_hour_header_end := v_end_hour;
        v_hour_tail_start := NULL;  -- 48시간 이하에서는 Hour Tail 사용 안함
        v_hour_tail_end := NULL;
        
        -- Minute Header: 시작 시간부터 첫 정시까지
        -- 예: 14:30 ~ 15:00
        v_minute_header_start := v_start_utc;
        v_minute_header_end := LEAST(v_hour_header_start, v_end_utc);
        
        -- Minute Tail: 마지막 정시부터 종료 시간까지
        -- 예: 16:00 ~ 16:45
        v_minute_tail_start := GREATEST(v_end_hour, v_minute_header_end);
        v_minute_tail_end := v_end_utc;
        
    -- --------------------------------------------
    -- 케이스 3: 48시간 초과
    -- --------------------------------------------
    -- 사용 테이블: Day + Hour + Minute
    -- 구조:
    --   [Minute Header] + [Hour Header] + [Day 전체] + [Hour Tail] + [Minute Tail]
    --   시작~첫정시    첫정시~첫날끝    온전한날들    마지막날시작~마지막정시   마지막정시~종료
    ELSE
        v_use_day_table := TRUE;
        v_use_hour_table := TRUE;
        v_use_minute_table := TRUE;
        
        -- Day 범위: 시작일 다음날 00:00부터 종료일 00:00까지
        -- 온전한 24시간 단위 날짜만 포함
        -- 예: 2024-01-15 14:30 ~ 2024-01-18 16:45인 경우
        --     Day: 2024-01-16 00:00 ~ 2024-01-18 00:00 (16일, 17일)
        v_day_start := v_start_day + interval '1 day';
        v_day_end := v_end_day;
        
        -- Hour Header: 시작 시간부터 시작일 종료(다음날 00:00)까지
        -- 예: 2024-01-15 15:00 ~ 2024-01-16 00:00 (15시~23시)
        v_hour_header_start := v_start_hour + interval '1 hour';
        v_hour_header_end := v_start_day + interval '1 day';
        
        -- Hour Tail: 종료일 시작(00:00)부터 종료 시간이 속한 정시까지
        -- 예: 2024-01-18 00:00 ~ 2024-01-18 16:00 (0시~16시)
        v_hour_tail_start := v_end_day;
        v_hour_tail_end := v_end_hour;
        
        -- Minute Header: 시작 시간부터 첫 정시까지
        -- 예: 2024-01-15 14:30 ~ 2024-01-15 15:00
        v_minute_header_start := v_start_utc;
        v_minute_header_end := LEAST(v_hour_header_start, v_end_utc);
        
        -- Minute Tail: 마지막 정시부터 종료 시간까지
        -- 예: 2024-01-18 16:00 ~ 2024-01-18 16:45
        v_minute_tail_start := GREATEST(v_hour_tail_end, v_minute_header_end);
        v_minute_tail_end := v_end_utc;
    END IF;
    
    -- ============================================
    -- 6. 집계 쿼리 실행 및 결과 반환
    -- ============================================
    -- CTE를 사용하여 각 테이블에서 데이터를 UNION ALL로 통합 후
    -- dock_id, vehicle_number별로 최종 집계
    
    RETURN QUERY
    WITH all_aggregations AS (
        -- ----------------------------------------
        -- Day 집계 (48시간 초과일 때만)
        -- ----------------------------------------
        -- 온전한 24시간 단위 날짜의 집계 데이터 조회
        SELECT 
            tbl.dock_id, 
            tbl.vehicle_number, 
            tbl.scanner_scan_sum, 
            tbl.scanner_multi_sum, 
            tbl.dimensioner_read_d_sum, 
            tbl.dimensioner_read_s_sum, 
            tbl.dimensioner_noread_sum, 
            tbl.judge_small_sum, 
            tbl.judge_medium_sum, 
            tbl.judge_large_sum, 
            tbl.judge_irregular_sum, 
            tbl.sorting_small_auto_sum, 
            tbl.sorting_small_manu_sum, 
            tbl.sorting_medium_auto_sum, 
            tbl.sorting_medium_manu_sum, 
            tbl.sorting_large_auto_sum, 
            tbl.sorting_large_manu_sum, 
            tbl.sorting_large_bypass_sum, 
            tbl.sorting_irregular_auto_sum, 
            tbl.sorting_irregular_manu_sum, 
            tbl.sorting_irregular_bypass_sum, 
            tbl.sorting_irregular_no_rule_sum, 
            tbl.sorting_irregular_no_info_sum, 
            tbl.sorting_irregular_trk_err_sum, 
            tbl.sorting_irregular_gap_err_sum
        FROM process_count_per_day_accum AS tbl
        WHERE v_use_day_table                              -- Day 테이블 사용 플래그
          AND (NOT v_has_dock_filter                       -- dock_id 필터가 없거나
               OR EXISTS (                                 -- 필터 패턴과 매칭되는 경우
                   SELECT 1 
                   FROM unnest(p_dock_ids) pattern 
                   WHERE tbl.dock_id LIKE pattern
               ))
          AND v_day_start < v_day_end                      -- 유효한 Day 범위 확인
          AND day_bucket >= v_day_start                    -- Day 범위 필터
          AND day_bucket < v_day_end
        
        UNION ALL
        
        -- ----------------------------------------
        -- Hour Header 집계
        -- ----------------------------------------
        -- 48시간 이하: 첫 정시부터 마지막 정시까지의 전체 시간
        -- 48시간 초과: 시작일의 잔여 시간 (첫 정시 ~ 다음날 00:00)
        SELECT 
            tbl.dock_id, 
            tbl.vehicle_number, 
            tbl.scanner_scan_sum, 
            tbl.scanner_multi_sum, 
            tbl.dimensioner_read_d_sum, 
            tbl.dimensioner_read_s_sum, 
            tbl.dimensioner_noread_sum, 
            tbl.judge_small_sum, 
            tbl.judge_medium_sum, 
            tbl.judge_large_sum, 
            tbl.judge_irregular_sum, 
            tbl.sorting_small_auto_sum, 
            tbl.sorting_small_manu_sum, 
            tbl.sorting_medium_auto_sum, 
            tbl.sorting_medium_manu_sum, 
            tbl.sorting_large_auto_sum, 
            tbl.sorting_large_manu_sum, 
            tbl.sorting_large_bypass_sum, 
            tbl.sorting_irregular_auto_sum, 
            tbl.sorting_irregular_manu_sum, 
            tbl.sorting_irregular_bypass_sum, 
            tbl.sorting_irregular_no_rule_sum, 
            tbl.sorting_irregular_no_info_sum, 
            tbl.sorting_irregular_trk_err_sum, 
            tbl.sorting_irregular_gap_err_sum
        FROM process_count_per_hour_accum AS tbl
        WHERE v_use_hour_table                             -- Hour 테이블 사용 플래그
          AND (NOT v_has_dock_filter                       -- dock_id 필터
               OR EXISTS (
                   SELECT 1 
                   FROM unnest(p_dock_ids) pattern 
                   WHERE tbl.dock_id LIKE pattern
               ))
          AND v_hour_header_start < v_hour_header_end      -- 유효한 Hour Header 범위 확인
          AND hour_bucket >= v_hour_header_start           -- Hour Header 범위 필터
          AND hour_bucket < v_hour_header_end
        
        UNION ALL
        
        -- ----------------------------------------
        -- Hour Tail 집계 (48시간 초과일 때만)
        -- ----------------------------------------
        -- 종료일의 시작 시간 (00:00 ~ 마지막 정시)
        SELECT 
            tbl.dock_id, 
            tbl.vehicle_number, 
            tbl.scanner_scan_sum, 
            tbl.scanner_multi_sum, 
            tbl.dimensioner_read_d_sum, 
            tbl.dimensioner_read_s_sum, 
            tbl.dimensioner_noread_sum, 
            tbl.judge_small_sum, 
            tbl.judge_medium_sum, 
            tbl.judge_large_sum, 
            tbl.judge_irregular_sum, 
            tbl.sorting_small_auto_sum, 
            tbl.sorting_small_manu_sum, 
            tbl.sorting_medium_auto_sum, 
            tbl.sorting_medium_manu_sum, 
            tbl.sorting_large_auto_sum, 
            tbl.sorting_large_manu_sum, 
            tbl.sorting_large_bypass_sum, 
            tbl.sorting_irregular_auto_sum, 
            tbl.sorting_irregular_manu_sum, 
            tbl.sorting_irregular_bypass_sum, 
            tbl.sorting_irregular_no_rule_sum, 
            tbl.sorting_irregular_no_info_sum, 
            tbl.sorting_irregular_trk_err_sum, 
            tbl.sorting_irregular_gap_err_sum
        FROM process_count_per_hour_accum AS tbl
        WHERE v_use_day_table                              -- Day 테이블을 사용할 때만 Hour Tail도 사용
          AND (NOT v_has_dock_filter                       -- dock_id 필터
               OR EXISTS (
                   SELECT 1 
                   FROM unnest(p_dock_ids) pattern 
                   WHERE tbl.dock_id LIKE pattern
               ))
          AND v_hour_tail_start < v_hour_tail_end          -- 유효한 Hour Tail 범위 확인
          AND hour_bucket >= v_hour_tail_start             -- Hour Tail 범위 필터
          AND hour_bucket < v_hour_tail_end
        
        UNION ALL
        
        -- ----------------------------------------
        -- Minute Header 집계
        -- ----------------------------------------
        -- 시작 시간부터 첫 정시까지의 분 단위 데이터
        SELECT 
            tbl.dock_id, 
            tbl.vehicle_number, 
            tbl.scanner_scan_sum, 
            tbl.scanner_multi_sum, 
            tbl.dimensioner_read_d_sum, 
            tbl.dimensioner_read_s_sum, 
            tbl.dimensioner_noread_sum, 
            tbl.judge_small_sum, 
            tbl.judge_medium_sum, 
            tbl.judge_large_sum, 
            tbl.judge_irregular_sum, 
            tbl.sorting_small_auto_sum, 
            tbl.sorting_small_manu_sum, 
            tbl.sorting_medium_auto_sum, 
            tbl.sorting_medium_manu_sum, 
            tbl.sorting_large_auto_sum, 
            tbl.sorting_large_manu_sum, 
            tbl.sorting_large_bypass_sum, 
            tbl.sorting_irregular_auto_sum, 
            tbl.sorting_irregular_manu_sum, 
            tbl.sorting_irregular_bypass_sum, 
            tbl.sorting_irregular_no_rule_sum, 
            tbl.sorting_irregular_no_info_sum, 
            tbl.sorting_irregular_trk_err_sum, 
            tbl.sorting_irregular_gap_err_sum
        FROM process_count_per_minute_accum AS tbl
        WHERE v_use_minute_table                           -- Minute 테이블 사용 플래그
          AND (NOT v_has_dock_filter                       -- dock_id 필터
               OR EXISTS (
                   SELECT 1 
                   FROM unnest(p_dock_ids) pattern 
                   WHERE tbl.dock_id LIKE pattern
               ))
          AND v_minute_header_start < v_minute_header_end  -- 유효한 Minute Header 범위 확인
          AND minute_bucket >= v_minute_header_start       -- Minute Header 범위 필터
          AND minute_bucket < v_minute_header_end
        
        UNION ALL
        
        -- ----------------------------------------
        -- Minute Tail 집계 (1시간 초과일 때만)
        -- ----------------------------------------
        -- 마지막 정시부터 종료 시간까지의 분 단위 데이터
        SELECT 
            tbl.dock_id, 
            tbl.vehicle_number, 
            tbl.scanner_scan_sum, 
            tbl.scanner_multi_sum, 
            tbl.dimensioner_read_d_sum, 
            tbl.dimensioner_read_s_sum, 
            tbl.dimensioner_noread_sum, 
            tbl.judge_small_sum, 
            tbl.judge_medium_sum, 
            tbl.judge_large_sum, 
            tbl.judge_irregular_sum, 
            tbl.sorting_small_auto_sum, 
            tbl.sorting_small_manu_sum, 
            tbl.sorting_medium_auto_sum, 
            tbl.sorting_medium_manu_sum, 
            tbl.sorting_large_auto_sum, 
            tbl.sorting_large_manu_sum, 
            tbl.sorting_large_bypass_sum, 
            tbl.sorting_irregular_auto_sum, 
            tbl.sorting_irregular_manu_sum, 
            tbl.sorting_irregular_bypass_sum, 
            tbl.sorting_irregular_no_rule_sum, 
            tbl.sorting_irregular_no_info_sum, 
            tbl.sorting_irregular_trk_err_sum, 
            tbl.sorting_irregular_gap_err_sum
        FROM process_count_per_minute_accum AS tbl
        WHERE v_duration_hours > 1                         -- 1시간 초과일 때만 Tail 사용
          AND (NOT v_has_dock_filter                       -- dock_id 필터
               OR EXISTS (
                   SELECT 1 
                   FROM unnest(p_dock_ids) pattern 
                   WHERE tbl.dock_id LIKE pattern
               ))
          AND v_minute_tail_start < v_minute_tail_end      -- 유효한 Minute Tail 범위 확인
          AND minute_bucket >= v_minute_tail_start         -- Minute Tail 범위 필터
          AND minute_bucket < v_minute_tail_end
    )
    -- ----------------------------------------
    -- 최종 집계
    -- ----------------------------------------
    -- 모든 테이블에서 가져온 데이터를 dock_id, vehicle_number별로 합산
    SELECT 
        agg.dock_id,
        agg.vehicle_number,
        SUM(agg.scanner_scan_sum)::BIGINT,
        SUM(agg.scanner_multi_sum)::BIGINT,
        SUM(agg.dimensioner_read_d_sum)::BIGINT,
        SUM(agg.dimensioner_read_s_sum)::BIGINT,
        SUM(agg.dimensioner_noread_sum)::BIGINT,
        SUM(agg.judge_small_sum)::BIGINT,
        SUM(agg.judge_medium_sum)::BIGINT,
        SUM(agg.judge_large_sum)::BIGINT,
        SUM(agg.judge_irregular_sum)::BIGINT,
        SUM(agg.sorting_small_auto_sum)::BIGINT,
        SUM(agg.sorting_small_manu_sum)::BIGINT,
        SUM(agg.sorting_medium_auto_sum)::BIGINT,
        SUM(agg.sorting_medium_manu_sum)::BIGINT,
        SUM(agg.sorting_large_auto_sum)::BIGINT,
        SUM(agg.sorting_large_manu_sum)::BIGINT,
        SUM(agg.sorting_large_bypass_sum)::BIGINT,
        SUM(agg.sorting_irregular_auto_sum)::BIGINT,
        SUM(agg.sorting_irregular_manu_sum)::BIGINT,
        SUM(agg.sorting_irregular_bypass_sum)::BIGINT,
        SUM(agg.sorting_irregular_no_rule_sum)::BIGINT,
        SUM(agg.sorting_irregular_no_info_sum)::BIGINT,
        SUM(agg.sorting_irregular_trk_err_sum)::BIGINT,
        SUM(agg.sorting_irregular_gap_err_sum)::BIGINT
    FROM all_aggregations AS agg
    GROUP BY agg.dock_id, agg.vehicle_number
    ORDER BY agg.dock_id, agg.vehicle_number;

END;
$function$;

-- ---------------------------------------------------------------------------
-- 7. 확인
-- ---------------------------------------------------------------------------
SELECT hypertable_name FROM timescaledb_information.hypertables ORDER BY 1;
SELECT proname FROM pg_proc WHERE proname IN ('process_counts_aggregate_all','get_dock_process_statistics_lastest','process_collected_payload_v4');
