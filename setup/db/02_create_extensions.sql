-- =============================================================================
-- 02_create_extensions.sql                 [가이드 7단계 - ②]
--
--   실행 위치 : DBeaver  연결 = postgres(관리자) / DB = ftmsdb   ← 반드시 ftmsdb
--               (확장 설치는 관리자 권한 필요 → tapsuser 로 실행하면 permission denied)
--   실행 방법 : Alt+X
-- =============================================================================

CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- 확인 : current_database = ftmsdb, 목록에 timescaledb / pgcrypto
SELECT current_database(), extname, extversion FROM pg_extension ORDER BY extname;
