#!/bin/bash
# DB 컨테이너 최초 기동 시(데이터 볼륨이 비어 있을 때) 1회만 실행됩니다.
#   - tapsuser 계정, authdb / ftmsdb 생성, timescaledb·pgcrypto 확장
#   비밀번호는 .env 의 FTMS_DB_PASSWORD
# 주의: 이 파일은 LF 줄바꿈이어야 합니다 (CRLF 이면 실행 오류).
set -e
echo "[ftms-init] create tapsuser / authdb / ftmsdb"
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
     -v app_password="${FTMS_DB_PASSWORD}" \
     -f /ftms-sql/01_create_db_and_user.sql
echo "[ftms-init] done"
