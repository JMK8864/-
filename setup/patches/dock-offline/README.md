# 도크 통신 끊김 표시 수정 (보고서 B17)

에뮬레이터/도크가 끊겨도 화면이 마지막 상태를 계속 표시하던 문제 수정.
적용 순서와 이유: 대화의 1~5단계 안내 참고. 원본 zip 기준으로 작성, **컴파일 미검증**(이 환경에 .NET SDK 없음).

| 단계 | 파일 | 내용 |
|---|---|---|
| 1 | `servers/ftms/app-shared.ftms/Models/Messages/HeartBeatReportMessage.cs` | `ReceivedAt`, `Connected`, `DisconnectedAt` 추가, `ToJson` 도크 배열에 `[3]` 수신시각, `[4]` 연결여부 추가 |
| 2 | `servers/ftms/app-server.ftms/Managers/LogisticsSystemStateManager.cs` | 수신 시 서버 시각·Connected=true 기록, `MarkDockDisconnectedAsync` 추가 |
| 3 | `servers/ftms/app-server.ftms/TcpServer/TcpServerFinalService.cs` | 연결 종료 시 DockId 찾아 끊김 표시 |
| 4 | `servers/ftms/DxfBlazorViewer/Pages/Viewer.razor.cs` | `ElementState.Offline`(진회색), 끊김/10초 무수신/응답 누락 도크를 Offline 표시 |

- `dock-offline.patch` : 원본 대비 diff (참고용, 줄바꿈 CRLF 차이로 `git apply` 가 안 될 수 있음 → 직접 반영 권장)
- `files/` : 원본에 수정만 반영한 전체 파일 (사용자 PC 소스가 원본과 다르면 덮어쓰지 말고 비교용으로 사용)
