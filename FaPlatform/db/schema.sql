-- FA Platform PostgreSQL 스키마
-- appsettings.json "Fa:Database:Enabled": true 이면 앱 시작 시 같은 내용을 자동 실행한다 (IF NOT EXISTS).
-- 수동으로 만들 때:
--   CREATE USER fa WITH PASSWORD '...';
--   CREATE DATABASE fa OWNER fa;
--   psql -U fa -d fa -f schema.sql

CREATE TABLE IF NOT EXISTS fa_alarm_history (
    id           uuid PRIMARY KEY,
    code         int          NOT NULL,
    level        varchar(16)  NOT NULL,
    source       varchar(64)  NOT NULL,
    message      text         NOT NULL,
    detail       text,
    recipe       varchar(64),
    occurred_at  timestamp    NOT NULL,
    cleared_at   timestamp,
    cleared_by   varchar(64)
);
CREATE INDEX IF NOT EXISTS ix_fa_alarm_history_occurred ON fa_alarm_history (occurred_at DESC);

CREATE TABLE IF NOT EXISTS fa_cycle_history (
    id           bigserial PRIMARY KEY,
    unit         varchar(64)  NOT NULL,
    cycle_no     int          NOT NULL,
    tact_sec     double precision NOT NULL,
    recipe       varchar(64),
    completed_at timestamp    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_fa_cycle_history_completed ON fa_cycle_history (completed_at DESC);
