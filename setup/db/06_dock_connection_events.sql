-- =============================================================================
-- 06_dock_connection_events.sql            [도크 끊김 이력 패치 - setup/patches/dock-offline]
--
--   도크 통신 끊김/복구 이벤트 코드를 이벤트 마스터(ftms_event_list)에 등록
--   (앱이 ftms_event_log 에 기록하는 코드: LogisticsSystemStateManager.EventCodeDock*)
--   실행 위치 : DBeaver  연결 = tapsuser / DB = ftmsdb,  Alt+X
--   여러 번 실행해도 안전 (이미 있으면 건너뜀)
--   등록 후 app-server 재시작 → 이벤트 마스터 캐시에 반영
-- =============================================================================

INSERT INTO ftms_event_list (event_code, event_level, device_type, sub_code, event_name, event_desc, is_active)
VALUES
    ('E0000.0050', 'Warning', 'System', 65536,  'DockDisconnected', '도크 통신 끊김 (TCP 연결 종료)', true),
    ('E0000.0051', 'Info',    'System', 131072, 'DockReconnected',  '도크 통신 복구 (heartbeat 재수신)', true)
ON CONFLICT (event_code) DO NOTHING;

-- 확인
SELECT event_code, event_level, device_type, sub_code, event_name, event_desc
FROM ftms_event_list
WHERE event_code IN ('E0000.0050', 'E0000.0051');

-- 기록된 끊김/복구 이력 확인 (에뮬레이터 종료·재시작 후)
SELECT occurred_at, event_code, device_id, event_level, event_desc, extra_data
FROM ftms_event_log
WHERE event_code IN ('E0000.0050', 'E0000.0051')
ORDER BY occurred_at DESC
LIMIT 20;
