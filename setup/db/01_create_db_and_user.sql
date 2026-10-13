-- =============================================================================
-- 01_create_db_and_user.sql   (원본: Database/1-1 PostgreSQL - ftmsuser 계정 생성.sql + 2-1)
--   postgres(슈퍼유저)로 실행:
--     psql -U postgres -v app_password=새비밀번호 -f 01_create_db_and_user.sql
--   사전 조건: TimescaleDB 설치 + postgresql.conf 에 shared_preload_libraries = 'timescaledb' 후 재시작
-- =============================================================================
\set ON_ERROR_STOP on

-- 1) 앱 계정 (appsettings.json ConnectionStrings 의 Username/Password 와 일치해야 함)
SELECT format('CREATE ROLE tapsuser LOGIN PASSWORD %L', :'app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tapsuser') \gexec

-- 2) DB : authdb(로그인/권한, ASP.NET Identity) / ftmsdb(운영 데이터)
SELECT 'CREATE DATABASE authdb OWNER tapsuser'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'authdb') \gexec
SELECT 'CREATE DATABASE ftmsdb OWNER tapsuser'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'ftmsdb') \gexec

-- 3) ftmsdb 확장 (timescaledb 는 슈퍼유저만 생성 가능)
\c ftmsdb
CREATE EXTENSION IF NOT EXISTS timescaledb;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
GRANT ALL ON SCHEMA public TO tapsuser;
GRANT pg_read_server_files TO tapsuser;   -- 원본 스크립트와 동일 (서버측 COPY 파일 읽기)

SELECT extname, extversion FROM pg_extension WHERE extname IN ('timescaledb','pgcrypto');
