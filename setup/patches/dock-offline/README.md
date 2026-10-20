# 도크 통신 끊김 표시 + 끊김 이력 (보고서 B17)

원본 zip 기준 작성, **컴파일 미검증**(이 환경에 .NET SDK 없음). 사용자 PC 소스가 원본과 다를 수 있으므로 `files/` 는 비교용.

| 파일 | 내용 |
|---|---|
| `app-shared.ftms/Models/Messages/HeartBeatReportMessage.cs` | `ReceivedAt`·`Connected`·`DisconnectedAt` 추가. `ToJson(staleMs)` → root `[3]` 끊김 판정 시간, 도크 배열 `[3]` 수신시각·`[4]` 연결여부 |
| `app-server.ftms/Controllers/DockController.cs` | `TcpServerFinal:TimeoutSeconds` × 1000 을 `ToJson` 에 전달 |
| `app-server.ftms/Managers/LogisticsSystemStateManager.cs` | 수신 시 서버 시각·Connected 기록, 끊김→복구 감지, `MarkDockDisconnectedAsync`, `ftms_event_log` 기록(`E0000.0050` 끊김 / `E0000.0051` 복구) |
| `app-server.ftms/TcpServer/TcpServerFinalService.cs` | 연결 종료 시 DockId·endpoint 로 끊김 표시 |
| `DxfBlazorViewer/Pages/Viewer.razor.cs` | `ElementState.Offline`(진회색), 서버가 보낸 판정 시간으로 끊김/무수신/응답 누락 도크 표시, 상태를 한 번도 못 받은 도크(미접속) 기본값 Offline, 범례에 "미접속" 추가 |
| `app-server.ftms/.default.settings/ftms_event_list.tsv` | 신규 설치용 이벤트 코드 2줄 추가 |
| `../../db/06_dock_connection_events.sql` | 기존 DB 의 `ftms_event_list` 에 이벤트 코드 2개 등록 |

- `dock-offline.patch` : 원본 대비 diff (CRLF 차이로 `git apply` 가 안 될 수 있음 → 직접 반영 권장)
