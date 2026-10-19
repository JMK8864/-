-- =============================================================================
-- 01_create_user_and_db.sql                [가이드 7단계 - ①]
--
--   실행 위치 : DBeaver  연결 = postgres(관리자) / DB = postgres
--   실행 방법 : 한 줄씩 Ctrl+Enter   (Auto-Commit 켜짐 확인)
--               CREATE DATABASE 는 트랜잭션 안에서 실행할 수 없으므로 한 줄씩 실행
--   수정할 곳 : '앱비밀번호' → 실제 tapsuser 비밀번호 (영문/숫자/_ 권장)
--               이 값은 8단계 appsettings.json 연결 문자열에 똑같이 넣어야 함
-- =============================================================================

CREATE USER tapsuser WITH PASSWORD '앱비밀번호';

CREATE DATABASE authdb OWNER tapsuser;

CREATE DATABASE ftmsdb OWNER tapsuser;

-- (선택) ProcessCountRepository 의 pg_read_file() 경로용. 원본 1-1 스크립트와 동일
GRANT pg_read_server_files TO tapsuser;

-- 확인 : authdb, ftmsdb 의 owner 가 tapsuser 여야 함
SELECT datname, pg_get_userbyid(datdba) AS owner
FROM pg_database
WHERE datname IN ('authdb', 'ftmsdb');

-- 비밀번호를 바꿀 때
-- ALTER USER tapsuser WITH PASSWORD '새비밀번호';
