# setup — SCADA 테스트 환경 설치·설정 자료

모든 파일은 [docs/02_SCADA-테스트환경-구축가이드.md](../docs/02_SCADA-테스트환경-구축가이드.md)의 단계에서 **하나씩** 사용합니다.
여러 명령을 묶은 실행 스크립트는 포함하지 않습니다.

| 파일 | 단계 | 사용 위치 / 방법 |
|---|---|---|
| `wsl/.wslconfig` | 3, 13 | Windows `%UserProfile%\.wslconfig` 로 복사 → `wsl --shutdown` |
| `db/01_create_user_and_db.sql` | 7-2 | DBeaver, postgres@**postgres**, 한 줄씩 Ctrl+Enter (`'앱비밀번호'` 수정) |
| `db/02_create_extensions.sql` | 7-3 | DBeaver, postgres@**ftmsdb**, Alt+X |
| `app-config/app-server.appsettings.ConnectionStrings.json` | 8-1 | `servers/ftms/app-server.ftms/appsettings.json` 의 ConnectionStrings 교체 |
| `db/03_ftmsdb_timescale_setup.sql` | 9-3 | DBeaver, tapsuser@ftmsdb, Alt+X (8단계 이후, "빈 줄 구분자 = Never") |
| `db/04_verify.sql` | 9-5, 11-3 | DBeaver, tapsuser@ftmsdb, 쿼리별 Ctrl+Enter |
| `app-config/app-server.launchSettings.json` | 12-2 | `servers/ftms/app-server.ftms/Properties/launchSettings.json` (0.0.0.0:8121) |
| `app-config/DxfBlazorViewer.launchSettings.json` | 12-2 | `servers/ftms/DxfBlazorViewer/Properties/launchSettings.json` (0.0.0.0:8122) |
| `nginx/default.conf` | 12-4 | WSL `~/ftms-nginx/default.conf` — **미러 모드**용 (127.0.0.1, listen 80) |
| `nginx/default.nat.conf` | 부록 A | WSL `~/ftms-nginx/default.conf` — NAT 모드용 (winhost) |
| `db/05_clear_test_data.sql` | 14-3 | 테스트 데이터 삭제 (테스트 환경 전용) |
| `db/06_dock_connection_events.sql` | 패치 | 도크 끊김/복구 이벤트 코드 등록 (`patches/dock-offline`) |
| `patches/dock-offline/` | 패치 | 도크 통신 끊김 표시·이력 수정 (보고서 B17) |

## 컨테이너

| 이름 | 이미지 | 실행 (WSL, 가이드 단계) |
|---|---|---|
| ftms-db | `timescale/timescaledb:latest-pg17` | 6-1 — `--network host`(미러) / `-p 5432:5432`(NAT, 부록 A), 볼륨 `ftms_db_data` |
| ftms-nginx | `nginx:latest` | 12-5 — `--network host`, `~/ftms-nginx/default.conf` 마운트 |

## Windows → WSL 파일 복사 예

WSL 에서 Windows 드라이브는 `/mnt/<드라이브>/` 로 보입니다.
```bash
cp "/mnt/d/받은폴더/setup/nginx/default.conf" ~/ftms-nginx/default.conf
```
