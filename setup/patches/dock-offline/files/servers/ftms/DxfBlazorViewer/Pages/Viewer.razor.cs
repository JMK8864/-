using DxfBlazorViewer.Client.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SkiaSharp;
using SkiaSharp.Views.Blazor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;


namespace DxfBlazorViewer.Client.Pages
{

    public partial class Viewer : ComponentBase, IAsyncDisposable
    {
        // ─────────────────────────────────────────────────────────────
        // DI (Razor의 @inject IJSRuntime JS, @inject DxfServerAdapter ServerAdapter 는 그대로 사용)
        // ─────────────────────────────────────────────────────────────
        [Inject] private HttpClient Http { get; set; } = default!;

        [Inject] private NavigationManager Nav { get; set; } = default!;

        // Alarm 페이지 라우트(프로젝트에서 다르면 여기만 수정)
        [Parameter] public string AlarmPageUrl { get; set; } = "/alarms";

        // ✅ Stats 같은 페이지에서 도크 목록만 필요할 때 true
        // - true면 EventLog/ProcessCount live 폴링 및 관련 JS 초기화(휠/리사이즈 등)를 시작하지 않음
        [Parameter] public bool DocksOnly { get; set; } = false;


        // 1초 자동 새로고침
        private CancellationTokenSource? _alarmPollCts;
        private readonly TimeSpan _alarmPollInterval = TimeSpan.FromSeconds(1);
        private int _alarmPollBusy = 0;

        // 새 알람 강조
        private long _alarmMaxIdSeen = 0;
        private readonly Dictionary<long, DateTimeOffset> _alarmHighlightUntil = new();
        private static readonly TimeSpan AlarmHighlightDuration = TimeSpan.FromSeconds(3);

        // 마지막 갱신 시각(로컬)
        private DateTimeOffset _alarmLastUpdatedLocal = DateTimeOffset.MinValue;

        // UI 표시용: LIVE / PAUSED
        private bool _alarmAutoActive => !_alarmCollapsed && _alarmPage == 1;


        // API 베이스 경로(동일 오리진 기본)
        //[Parameter] public string LaneLabelApiBase { get; set; } = "http://192.168.0.41:8121/api/setting";
        [Parameter] public string LaneLabelApiBase { get; set; } = "/api/setting";

        // Dock Status API (동일 오리진 프록시 기준)
        [Parameter] public string DockStatusApi { get; set; } = "/api/Dock/status";

        // EventLog API (최근 알람)
        [Parameter] public string EventLogApi { get; set; } = "/api/EventLog";

        private const int AlarmPageSize = 5;

        private int _alarmPage = 1;

        // ✅ fast search는 totalCount/totalPages가 없음
        private bool _alarmHasPrev = false;
        private bool _alarmHasNext = false;

        // (선택) 조회 기간(최근 N시간) — 필요에 맞게 조정
        [Parameter] public int AlarmLookbackHours { get; set; } = 72; // 최근 3일

        private static bool IsErrorOrWorse(string? level)
        {
            var s = (level ?? "").Trim().ToLowerInvariant();
            return s == "error" || s == "critical";
        }

        private List<EventLogItemDto> _alarmLogs = new();
        private bool _alarmLoading = false;
        private string? _alarmError = null;
        private bool _alarmCollapsed = false;

        private CancellationTokenSource? _alarmCts;


        // Dock status 폴링
        private CancellationTokenSource? _dockStatusPollCts;
        private readonly TimeSpan _dockStatusInterval = TimeSpan.FromSeconds(1);
        private int _dockStatusBusy = 0;


        [Parameter] public string? FocusDockId { get; set; }
        private string? _lastFocusDockIdNorm;

        protected override void OnParametersSet()
        {
            var norm = string.IsNullOrWhiteSpace(FocusDockId) ? null : DockLabelNormalization.CanonLabel(FocusDockId);

            if (!string.Equals(norm, _lastFocusDockIdNorm, StringComparison.OrdinalIgnoreCase))
            {
                _lastFocusDockIdNorm = norm;

                if (UseDockStatusDiagram)
                {
                    _diagramCanvasHeightCssPx = 0;     // 도크 개수 변동 → 높이 재계산
                    _invalidateAfterRender = true;     // CSS 반영 후 다시 그리기
                    _ = InvokeAsync(StateHasChanged);
                }
            }
        }

        private sealed class ApiEnvelope<T>
        {
            public bool Success { get; set; }
            public int StatusCode { get; set; }
            public string? Message { get; set; }
            public T? Data { get; set; }
        }

        private sealed class EventLogFastPageDto
        {
            public int Page { get; set; }
            public int PageSize { get; set; }
            public bool HasPreviousPage { get; set; }
            public bool HasNextPage { get; set; }
            public List<EventLogItemDto> Logs { get; set; } = new();
        }

        private sealed class EventLogFastSearchRequest
        {
            public string? EventCode { get; set; }
            public string? DeviceId { get; set; }
            public string? EventLevel { get; set; }
            public string? DeviceType { get; set; }
            public bool IsAcknowledged { get; set; } = false;

            public DateTimeOffset OccurredFrom { get; set; }
            public DateTimeOffset OccurredTo { get; set; }

            public int Page { get; set; } = 1;
            public int PageSize { get; set; } = AlarmPageSize;
        }

        private sealed class EventLogItemDto
        {
            public long Id { get; set; }
            public DateTimeOffset? OccurredAt { get; set; }
            public string? EventCode { get; set; }   // ✅ 추가
            public string? DeviceId { get; set; }
            public string? EventLevel { get; set; }
            public string? DeviceType { get; set; }
            public string? EventDesc { get; set; }
        }

        private static string DashIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? "-" : s.Trim();

        private static string FmtOccurredAt(DateTimeOffset? ts)
            => ts.HasValue ? ts.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "-";

        private static string LevelCss(string? level)
        {
            var s = (level ?? "").Trim().ToLowerInvariant();
            return s switch
            {
                "critical" => "critical",
                "error" => "error",
                "warning" => "warning",
                "info" => "info",
                _ => ""
            };
        }

        private async Task LoadEventLogsAsync(int page, bool silent = false)
        {
            page = Math.Max(1, page);

            _alarmCts?.Cancel();
            _alarmCts?.Dispose();
            _alarmCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ct = _alarmCts.Token;

            if (!silent) _alarmLoading = true;
            _alarmError = null;

            try
            {
                // ✅ 조회 범위: 최근 N시간
                // 서버는 Z(UTC)로 주는 예시를 쓰고 있으니 DateTimeOffset.UtcNow 기반으로 맞춤
                var toUtc = DateTimeOffset.UtcNow;
                var fromUtc = toUtc.AddHours(-Math.Max(1, AlarmLookbackHours));

                // ✅ deviceId: FocusDockId가 있으면 그걸 우선 사용 (없으면 null로 전체 조회 시도)
                // (서버가 null을 허용하지 않으면 여기 기본값을 "B22" 같은 값으로 강제하면 됩니다)
                var deviceId = string.IsNullOrWhiteSpace(_lastFocusDockIdNorm) ? null : _lastFocusDockIdNorm;

                var req = new EventLogFastSearchRequest
                {
                    EventCode = null,
                    DeviceId = deviceId,
                    EventLevel = "Error",
                    DeviceType = null,
                    IsAcknowledged = false,
                    OccurredFrom = fromUtc,
                    OccurredTo = toUtc,
                    Page = page,
                    PageSize = AlarmPageSize
                };

                // ✅ POST /api/EventLog/search/fast
                // EventLogApi가 기존 "/api/EventLog" 라면, 아래처럼 suffix만 붙여 사용
                var url = $"{EventLogApi}/search/fast";

                using var httpResp = await Http.PostAsJsonAsync(url, req, ct);
                httpResp.EnsureSuccessStatusCode();

                var resp = await httpResp.Content.ReadFromJsonAsync<ApiEnvelope<EventLogFastPageDto>>(cancellationToken: ct);

                if (resp?.Data == null)
                {
                    _alarmError = "EventLog fast API 응답이 비어있습니다.";
                    _alarmLogs = new();
                    _alarmHasPrev = false;
                    _alarmHasNext = false;
                    _alarmPage = page;
                    return;
                }

                if (!resp.Success)
                {
                    _alarmError = resp.Message ?? "EventLog fast API 오류";
                    _alarmLogs = new();
                    _alarmHasPrev = false;
                    _alarmHasNext = false;
                    _alarmPage = page;
                    return;
                }

                var data = resp.Data;

                _alarmPage = data.Page <= 0 ? page : data.Page;
                _alarmHasPrev = data.HasPreviousPage;
                _alarmHasNext = data.HasNextPage;
                _alarmLogs = (data.Logs ?? new())
                             .Where(x => IsErrorOrWorse(x.EventLevel))
                             .ToList();

                // ✅ 새 알람 강조는 "페이지 1"에서만
                if (_alarmPage == 1)
                    MarkNewAlarms(_alarmLogs);

                _alarmLastUpdatedLocal = DateTimeOffset.Now;
                PruneHighlights();
            }
            catch (OperationCanceledException)
            {
                // 취소 무시
            }
            catch (Exception ex)
            {
                _alarmError = ex.Message;
                _alarmLogs = new();
                _alarmHasPrev = false;
                _alarmHasNext = false;
            }
            finally
            {
                if (!silent) _alarmLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }

        private void MarkNewAlarms(List<EventLogItemDto> logs)
        {
            if (logs == null || logs.Count == 0) return;

            var now = DateTimeOffset.Now;
            var max = logs.Max(x => x.Id);

            // 최초 1회는 기준만 세팅(강조 없음)
            if (_alarmMaxIdSeen == 0)
            {
                _alarmMaxIdSeen = max;
                return;
            }

            if (max > _alarmMaxIdSeen)
            {
                foreach (var it in logs.Where(x => x.Id > _alarmMaxIdSeen))
                    _alarmHighlightUntil[it.Id] = now.Add(AlarmHighlightDuration);

                _alarmMaxIdSeen = max;
            }
        }

        private void PruneHighlights()
        {
            if (_alarmHighlightUntil.Count == 0) return;

            var now = DateTimeOffset.Now;
            var expired = _alarmHighlightUntil
                .Where(kv => kv.Value <= now)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var id in expired)
                _alarmHighlightUntil.Remove(id);
        }

        private string RowCss(EventLogItemDto log)
        {
            var cls = "clickable";
            if (_alarmHighlightUntil.TryGetValue(log.Id, out var until) && until > DateTimeOffset.Now)
                cls += " new";
            return cls;
        }

        private string FmtAlarmLastUpdated()
        {
            if (_alarmLastUpdatedLocal == DateTimeOffset.MinValue) return "-";
            return _alarmLastUpdatedLocal.ToString("HH:mm:ss");
        }

        private void OpenAlarmFromRow(EventLogItemDto log)
        {
            var deviceId = (log.DeviceId ?? "").Trim();
            var level = (log.EventLevel ?? "").Trim();

            // deviceId / eventLevel 자동 적용 (Alarm 쪽에서 쿼리 파싱 필요: 아래 3) 참고)
            var qs = $"deviceId={Uri.EscapeDataString(deviceId)}&eventLevel={Uri.EscapeDataString(level)}&onlyUnack=true";
            Nav.NavigateTo($"{AlarmPageUrl}?{qs}");
        }

        private void StartAlarmPolling()
        {
            StopAlarmPolling();

            _alarmPollCts = new CancellationTokenSource();
            var ct = _alarmPollCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(_alarmPollInterval, ct);

                        // ✅ 페이지 1에서만 자동 갱신 (이전 기록 보기 방해 방지)
                        if (_alarmCollapsed || _alarmPage != 1) continue;

                        if (Interlocked.Exchange(ref _alarmPollBusy, 1) == 1) continue;

                        try
                        {
                            await InvokeAsync(() => LoadEventLogsAsync(1, silent: true));
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _alarmPollBusy, 0);
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, ct);
        }

        private void StopAlarmPolling()
        {
            try { _alarmPollCts?.Cancel(); } catch { }
            try { _alarmPollCts?.Dispose(); } catch { }
            _alarmPollCts = null;
        }


        // UI 이벤트 핸들러
        private Task RefreshAlarmLogs() => LoadEventLogsAsync(_alarmPage);
        private Task PrevAlarmPage()
            => (_alarmHasPrev && _alarmPage > 1) ? LoadEventLogsAsync(_alarmPage - 1) : Task.CompletedTask;

        private Task NextAlarmPage()
            => _alarmHasNext ? LoadEventLogsAsync(_alarmPage + 1) : Task.CompletedTask;
        private Task GotoAlarmPage(int page) => LoadEventLogsAsync(page);

        private void ToggleAlarmCollapsed()
        {
            _alarmCollapsed = !_alarmCollapsed;
        }



        private IJSObjectReference? _wheelModule, _dprModule, _dprHandle;
        private DotNetObjectReference<Viewer>? _selfRef;
        private ElementReference _host;

        // Skia 텍스트(범례/그룹 헤더 등) 한글 표시용 타입페이스
        // - 브라우저(WebAssembly) 환경에서는 기본 타입페이스로 한글이 깨질 수 있어,
        //   wwwroot/fonts의 CJOnlyOne 폰트를 우선 로드하여 사용
        private SKTypeface? _koreanTypeface;

        // ─────────────────────────────────────────────────────────────
        // 통계 폴링
        // ─────────────────────────────────────────────────────────────
        private CancellationTokenSource? _statsPollCts;
        private readonly TimeSpan _statsInterval = TimeSpan.FromSeconds(2);
        private int _statsBusy = 0; // 0: idle, 1: in-flight
        private CancellationTokenSource? _colApplyDebounceCts;
        // 슬라이더 드래그 중에도 값이 튀지 않게: step=1로 받고, 내부에서 8단위로 스냅
        private async Task OnColDefaultSliderInput(ChangeEventArgs e)
        {
            if (e?.Value is null) return;
            if (!int.TryParse(e.Value.ToString(), out var raw)) return;

            var snapped = SnapTo9(raw);

            // UI 값 갱신
            if (_colDefault != snapped)
            {
                _colDefault = snapped;

                // "기본"만 쓴다고 하셨으니, 내부 로직 충돌 방지용으로 B/C/D/E도 같이 맞춰줌
                _colB = _colDefault;
                _colC = _colDefault;
                _colD = _colDefault;
                _colE = _colDefault;
            }

            // 드래그 중 Apply를 매번 때리면 무거울 수 있어 debounce로 처리
            _colApplyDebounceCts?.Cancel();
            var cts = new CancellationTokenSource();
            _colApplyDebounceCts = cts;

            try
            {
                await Task.Delay(120, cts.Token); // 드래그 중 잠깐 멈췄을 때 적용
                await ApplyDiagramColumnsAsync(save: true);
            }
            catch (TaskCanceledException) { }
        }

        private static int SnapTo9(int v)
        {
            // 기존 UI가 1~50 이었으니 그 범위는 유지하되,
            // 8 단위로 스냅 (끝값 50은 예외적으로 허용)
            if (v < 9) v = 9;
            if (v >= 49) return 50;

            var k = (int)Math.Round(v / 9.0, MidpointRounding.AwayFromZero);
            var snapped = k * 9;

            if (snapped < 9) snapped = 9;
            if (snapped > 48) snapped = 48;
            return snapped;
        }
        public void StopProcessPolling() => StopStatsPolling();
        public void StartProcessPolling() => StartStatsPolling();

        private void StartStatsPolling()
        {
            StopStatsPolling();
            _statsPollCts = new CancellationTokenSource();
            var ct = _statsPollCts.Token;

            _ = Task.Run(async () =>
            {
                var timer = new PeriodicTimer(_statsInterval);
                try
                {
                    while (await timer.WaitForNextTickAsync(ct))
                    {
                        if (_renderer.IsInteracting) continue;
                        if (Interlocked.Exchange(ref _statsBusy, 1) == 1) continue;

                        try { await InvokeAsync(LoadJudgeAndApplyAsync); }
                        catch (OperationCanceledException) { /* normal */ }
                        catch { /* optional log */ }
                        finally { Volatile.Write(ref _statsBusy, 0); }
                    }
                }
                catch (OperationCanceledException) { /* normal exit */ }
            }, ct);
        }

        private void StopStatsPolling()
        {
            try { _statsPollCts?.Cancel(); } catch { }
            try { _statsPollCts?.Dispose(); } catch { }
            _statsPollCts = null;
        }

        private void StartDockStatusPolling()
        {
            StopDockStatusPolling();
            _dockStatusPollCts = new CancellationTokenSource();
            var ct = _dockStatusPollCts.Token;

            _ = Task.Run(async () =>
            {
                var timer = new PeriodicTimer(_dockStatusInterval);
                try
                {
                    while (await timer.WaitForNextTickAsync(ct))
                    {
                        if (_renderer.IsInteracting) continue;
                        if (Interlocked.Exchange(ref _dockStatusBusy, 1) == 1) continue;

                        try
                        {
                            // NOTE: 상대경로(프록시) 기준. 필요 시 DockStatusApi를 절대주소로 바꿔도 됨.
                            var json = await Http.GetStringAsync(DockStatusApi, ct);

                            await InvokeAsync(() =>
                            {
                                ApplyDockStatusJson(json);   // ✅ 아래 4)에서 배열 포맷까지 처리하도록 확장
                                _canvas?.Invalidate();
                            });
                        }
                        catch (OperationCanceledException) { }
                        catch { /* optional log */ }
                        finally { Volatile.Write(ref _dockStatusBusy, 0); }
                    }
                }
                catch (OperationCanceledException) { }
            }, ct);
        }

        private void StopDockStatusPolling()
        {
            try { _dockStatusPollCts?.Cancel(); } catch { }
            try { _dockStatusPollCts?.Dispose(); } catch { }
            _dockStatusPollCts = null;
        }


        // ===== Dock Status Diagram (도식) 모드 =====
        private bool _useDockStatusDiagram;
        private DockDiagramView _dockDiagramView = DockDiagramView.Full;
        private readonly DockStatusDiagramRenderer _dockDiagramRenderer = new();

        // ===== Mock Dock Status DTO =====
        private sealed class DockStatusBundleDto
        {
            public DateTimeOffset? Timestamp { get; set; }
            public List<DockStatusDockDto> Docks { get; set; } = new();
        }

        private sealed class DockStatusDockDto
        {
            public string DockId { get; set; } = "";
            public string? Base { get; set; }
            public DockStatusDevicesDto? Devices { get; set; }
            public Dictionary<string, string>? Conveyors { get; set; }
        }

        private sealed class DockStatusDevicesDto
        {
            public string? ReadSystem { get; set; }
            public string? Plc { get; set; }
            public string? Encoder { get; set; }
            public string? EncoderLabel { get; set; }
        }

        // ===== 상태 저장소(도식용) =====
        // (이미 _dockRunStateByDock, _dockConvStateByDock 등이 있다면 "추가분만" 넣으세요)
        private readonly Dictionary<string, ElementState> _dockStationStateByDock = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ElementState> _dockPlcStateByDock = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ElementState> _dockEncoderStateByDock = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _dockEncoderLabelByDock = new(StringComparer.OrdinalIgnoreCase);

        private static bool TryParseState(string? s, out ElementState st)
        {
            st = ElementState.Stop;
            if (string.IsNullOrWhiteSpace(s)) return false;
            return Enum.TryParse(s.Trim(), ignoreCase: true, out st);
        }

        private void ApplyDockStatusJson(string json)
        {
            // ✅ NEW: /api/Dock/status (배열 기반) 포맷 지원
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    ApplyDockStatusCompactFrame(root);
                    return;
                }
            }
            catch
            {
                // 기존 로직으로 폴백
            }
            var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var bundle = JsonSerializer.Deserialize<DockStatusBundleDto>(json, opt);
            if (bundle?.Docks == null) return;

            foreach (var d in bundle.Docks)
            {
                if (string.IsNullOrWhiteSpace(d.DockId)) continue;

                // 도크 라벨 정규화(프로젝트에 이미 CanonLabel 쓰고 있으면 그걸로)
                var dockId = DockLabelNormalization.CanonLabel(d.DockId);

                // base 상태(원하면 도크 전체 상태로 사용)
                if (TryParseState(d.Base, out var baseSt))
                    _dockRunStateByDock[dockId] = baseSt;

                // devices
                if (d.Devices != null)
                {
                    if (TryParseState(d.Devices.ReadSystem, out var stStation)) _dockStationStateByDock[dockId] = stStation;
                    if (TryParseState(d.Devices.Plc, out var stPlc)) _dockPlcStateByDock[dockId] = stPlc;
                    if (TryParseState(d.Devices.Encoder, out var stEnc)) _dockEncoderStateByDock[dockId] = stEnc;

                    if (!string.IsNullOrWhiteSpace(d.Devices.EncoderLabel))
                        _dockEncoderLabelByDock[dockId] = d.Devices.EncoderLabel!.Trim();
                }

                // conveyors
                if (d.Conveyors != null)
                {
                    foreach (var kv in d.Conveyors)
                    {
                        var convName = kv.Key?.Trim();
                        if (string.IsNullOrWhiteSpace(convName)) continue;

                        if (TryParseState(kv.Value, out var convSt))
                            SetConvState(dockId, convName, convSt);
                    }
                }
            }
        }

        private bool _mockDockStatusEnabled;
        private CancellationTokenSource? _mockDockStatusCts;
        private int _mockStep;

        private void StartMockDockStatusPolling()
        {
            StopMockDockStatusPolling();
            _mockDockStatusCts = new CancellationTokenSource();
            var ct = _mockDockStatusCts.Token;

            _ = Task.Run(async () =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                try
                {
                    while (await timer.WaitForNextTickAsync(ct))
                    {
                        var json = GenerateMockDockStatusJson(_mockStep++);
                        await InvokeAsync(() =>
                        {
                            ApplyDockStatusJson(json);
                            _canvas?.Invalidate();   // 도식 재렌더
                        });
                    }
                }
                catch (OperationCanceledException) { /* normal */ }
            }, ct);
        }

        private void StopMockDockStatusPolling()
        {
            try { _mockDockStatusCts?.Cancel(); } catch { }
            try { _mockDockStatusCts?.Dispose(); } catch { }
            _mockDockStatusCts = null;
        }

        private static string GenerateMockDockStatusJson(int step)
        {
            // 4상태 순환
            string[] cycle = { "Stop", "Run", "Error", "Emergency" };

            string St(int offset) => cycle[(step + offset) % cycle.Length];

            var obj = new
            {
                timestamp = DateTimeOffset.UtcNow,
                docks = new[]
                {
            new {
                dockId="B01",
                @base=St(0),
                devices = new { readSystem=St(0), plc=St(1), encoder=St(2), encoderLabel="E-1" },
                conveyors = new Dictionary<string,string> {
                    ["MAIN_TOP"]=St(0), ["MAIN_MID"]=St(1), ["MAIN_BOT"]=St(2), ["EXT"]=St(3),
                    ["M01"]=St(0), ["M02"]=St(1), ["M03"]=St(2), ["M04"]=St(3),
                    ["M05"]=St(0), ["M06"]=St(1), ["M07"]=St(2),
                }
            },
            new {
                dockId="B02",
                @base=St(1),
                devices = new { readSystem=St(1), plc=St(2), encoder=St(3), encoderLabel="E-2" },
                conveyors = new Dictionary<string,string> {
                    ["MAIN_TOP"]=St(1), ["MAIN_MID"]=St(2), ["MAIN_BOT"]=St(3), ["EXT"]=St(0)
                }
            }
        }
            };

            return JsonSerializer.Serialize(obj);
        }

        private void OnMockDockStatusToggled()
        {
            ToggleMockDockStatus(_mockDockStatusEnabled); // 내부에서 Start/Stop 호출
        }

        private void OnDockDiagramToggled()
        {
            if (UseDockStatusDiagram)
            {
                // 도식 모드 ON: 통계 폴링은 멈추고, (mock이면 mock / 아니면 실API) 폴링
                StopStatsPolling();

                if (_mockDockStatusEnabled)
                {
                    StopDockStatusPolling();
                    StartMockDockStatusPolling();
                }
                else
                {
                    StopMockDockStatusPolling();
                    StartDockStatusPolling();
                }
            }
            else
            {
                // 도식 모드 OFF: dock-status/mock 폴링 정리 후 통계 폴링 재개
                StopDockStatusPolling();

                if (_mockDockStatusEnabled)
                {
                    _mockDockStatusEnabled = false;
                    StopMockDockStatusPolling();
                }

                StartStatsPolling();
            }

            _canvas?.Invalidate();
        }


        private void ToggleMockDockStatus(bool on)
        {
            _mockDockStatusEnabled = on;

            if (on)
            {
                StopStatsPolling();
                StopDockStatusPolling();
                StartMockDockStatusPolling();
            }
            else
            {
                StopMockDockStatusPolling();

                if (UseDockStatusDiagram)
                {
                    StopStatsPolling();
                    StartDockStatusPolling();
                }
                else
                {
                    StopDockStatusPolling();
                    StartStatsPolling();
                }
            }

            _canvas?.Invalidate();
        }



        private readonly record struct DiagramLegendItem(string Text, string Color, bool Dot = false);

        // 설비/요소 범례 (기존 DrawLegend의 의미를 HTML로 옮김)
        private static readonly IReadOnlyList<DiagramLegendItem> _diagramLegendSystem = new List<DiagramLegendItem>
        {
            new("판독시스템(체적기/변위센서/트리거/SMS)", "#56C873", Dot: true),
            new("판넬(PLC)",                         "#F2F2F2"),
            new("엔코더(ENC)",                       "#56C873"),
            new("컨베이어",                          "#F2F2F2"),
        };

        // 상태 범례
        private static readonly IReadOnlyList<DiagramLegendItem> _diagramLegendState = new List<DiagramLegendItem>
        {
            new("정지", "#D8D8D8"),
            new("운전", "#56C873"),
            new("에러", "#FFD200"),
            new("비상", "#FF2A2A"),
        };

        // Razor에서 그대로 접근해서 쓰면 됩니다(코드비하인드 private여도 OK)
        private IReadOnlyList<DiagramLegendItem> DiagramLegendSystem => _diagramLegendSystem;
        private IReadOnlyList<DiagramLegendItem> DiagramLegendState => _diagramLegendState;

        // 도식 모드 전환 전 DXF(전체 도크) 뷰 상태를 저장했다가 복원
        private (double zoom, double panX, double panY)? _savedDxfView;

        // 모드 전환/레이아웃 변경 직후에는 DOM/CSS가 먼저 반영된 다음 invalidate가 필요함
        private bool _invalidateAfterRender;

        // 도식 모드에서 내용 높이에 맞춰 캔버스 높이를 키워 스크롤되게 함(CSS px 단위)
        private double _diagramCanvasHeightCssPx = 0;
        private string CanvasHeightCss => UseDockStatusDiagram && _diagramCanvasHeightCssPx > 0
            ? $"{_diagramCanvasHeightCssPx:0}px"
            : "100%";

        // 도크별 "RUN/STOP" 같은 상태를 임시로 추정(현재 ProcessCount 기반)
        // (나중에 PLC/상태 API가 생기면 이 맵만 바꿔 끼우면 됨)
        private readonly Dictionary<string, long> _lastTotalByDock = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ElementState> _dockRunStateByDock = new(StringComparer.OrdinalIgnoreCase);

        private static bool TryGetArrayItem(JsonElement arr, int index, out JsonElement value)
        {
            value = default;
            if (arr.ValueKind != JsonValueKind.Array) return false;

            int i = 0;
            foreach (var e in arr.EnumerateArray())
            {
                if (i == index) { value = e; return true; }
                i++;
            }
            return false;
        }

        private static ElementState StateFromBitBytes(JsonElement bytes)
        {
            // bytes: [0/1/...] 형태
            if (bytes.ValueKind != JsonValueKind.Array) return ElementState.Error;

            bool any = false, all0 = true, all1 = true;

            foreach (var b in bytes.EnumerateArray())
            {
                any = true;
                int v;
                try { v = b.GetInt32(); }
                catch { return ElementState.Error; }

                if (v != 0) all0 = false;
                if (v != 1) all1 = false;
            }

            if (!any) return ElementState.Error;
            if (all1) return ElementState.Run;
            if (all0) return ElementState.Stop;
            return ElementState.Error;
        }

        private static ElementState StateFromSingle(int v)
        {
            if (v == 1) return ElementState.Run;
            if (v == 0) return ElementState.Stop;
            return ElementState.Error;
        }

        private static ElementState StateFromConveyorByte(byte v)
        {
            if (v == 0) return ElementState.Stop;
            if (v == 1) return ElementState.Run;
            return ElementState.Error;
        }


        private static ElementState CombineReadSystem(ElementState scanner, ElementState dim, ElementState sensor)
        {
            if (scanner == ElementState.Run && dim == ElementState.Run && sensor == ElementState.Run) return ElementState.Run;
            if (scanner == ElementState.Stop && dim == ElementState.Stop && sensor == ElementState.Stop) return ElementState.Stop;
            return ElementState.Error;
        }

        // conveyor status bytes는 HEX 문자열을 byte[]로 만든 값(0~255)이라고 가정
        // 비트 순서: LSB-first(기본) = bit0(M01) ... bit6(M07) bit7(신축)
        private const bool ConveyorBitsMsbFirst = false; // 필요하면 true로 바꿔서 MSB-first로 전환

        private static bool TryReadByteList(JsonElement arr, out List<byte> bytes)
        {
            bytes = new List<byte>();
            if (arr.ValueKind != JsonValueKind.Array) return false;

            foreach (var e in arr.EnumerateArray())
            {
                int v;
                try { v = e.GetInt32(); }
                catch { return false; }

                if (v < 0 || v > 255) return false;
                bytes.Add((byte)v);
            }
            return true;
        }

        private static int? TryGetBit(IReadOnlyList<byte> bytes, int bitIndex, bool msbFirst)
        {
            if (bitIndex < 0) return null;

            int byteIndex = bitIndex / 8;
            if (byteIndex >= bytes.Count) return null;

            int bitInByte = bitIndex % 8;
            int shift = msbFirst ? (7 - bitInByte) : bitInByte;

            return (bytes[byteIndex] >> shift) & 0x01; // 항상 0 or 1
        }


        // [도크 끊김 판정 - 원인 3] 서버 수신 시각이 이 시간보다 오래되면 통신 끊김으로 표시
        //   서버 TCP 무수신 타임아웃(TcpServerFinal:TimeoutSeconds = 10초)과 맞춤
        private const long DockStaleMs = 10_000;

        // dockEl = ["B22", [장치들], 파라미터, 서버수신시각(ms), 연결여부]
        private static bool IsDockOffline(JsonElement dockEl, long serverNowMs)
        {
            // [4] 연결 여부: 서버가 TCP 끊김을 감지했으면 false → 즉시 끊김
            if (TryGetArrayItem(dockEl, 4, out var connEl) && connEl.ValueKind == JsonValueKind.False)
                return true;

            // [3] 서버 수신 시각. 없으면(이전 서버 버전) 장치별 lastSeenAt 중 가장 최근 값 사용
            long lastMs = 0;
            if (TryGetArrayItem(dockEl, 3, out var recvEl) && recvEl.ValueKind == JsonValueKind.Number && recvEl.TryGetInt64(out var recvMs))
            {
                lastMs = recvMs;
            }
            else if (TryGetArrayItem(dockEl, 1, out var devsEl) && devsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in devsEl.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.Array &&
                        TryGetArrayItem(t, 0, out var lsEl) && lsEl.ValueKind == JsonValueKind.Number &&
                        lsEl.TryGetInt64(out var ls) && ls > lastMs)
                        lastMs = ls;
                }
            }

            // 시각 정보가 전혀 없으면 판단 불가 → 끊김으로 표시
            return lastMs == 0 || (serverNowMs - lastMs) > DockStaleMs;
        }

        private void MarkDockOffline(string dockId, IEnumerable<string> convKeys)
        {
            _dockRunStateByDock[dockId] = ElementState.Offline;
            _dockStationStateByDock[dockId] = ElementState.Offline;
            _dockEncoderStateByDock[dockId] = ElementState.Offline;
            _dockPlcStateByDock[dockId] = ElementState.Offline;
            foreach (var k in convKeys)
                SetConvState(dockId, k, ElementState.Offline);
        }

        private void ApplyDockStatusCompactFrame(JsonElement root)
        {
            // root = [ts, iso, docks]
            if (!TryGetArrayItem(root, 2, out var docksEl) || docksEl.ValueKind != JsonValueKind.Array)
                return;

            // [도크 끊김 판정 - 원인 3] root[0] = 서버 현재 시각(ms). 브라우저 PC 시계 대신 서버 시각으로 비교
            long serverNowMs = (TryGetArrayItem(root, 0, out var nowEl) && nowEl.ValueKind == JsonValueKind.Number && nowEl.TryGetInt64(out var nowMs))
                ? nowMs
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // [도크 끊김 판정 - 원인 3] 이번 응답에 들어 있는 도크 목록 (응답에서 빠진 도크 처리용)
            var seenDocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // conveyor 순서: [M01..M07, 신축]
            string[] convKeys =
            {
                DockConvName.M01, DockConvName.M02, DockConvName.M03, DockConvName.M04,
                DockConvName.M05, DockConvName.M06, DockConvName.M07, DockConvName.ShinChuk
            };

            foreach (var dockEl in docksEl.EnumerateArray())
            {
                if (dockEl.ValueKind != JsonValueKind.Array) continue;

                // dockId
                if (!TryGetArrayItem(dockEl, 0, out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                var dockIdRaw = idEl.GetString();
                if (string.IsNullOrWhiteSpace(dockIdRaw)) continue;

                var dockId = DockLabelNormalization.CanonLabel(dockIdRaw);
                seenDocks.Add(dockId);

                // [도크 끊김 판정 - 원인 3] 끊긴 도크는 마지막 상태 대신 Offline 으로 표시하고 다음 도크로
                if (IsDockOffline(dockEl, serverNowMs))
                {
                    MarkDockOffline(dockId, convKeys);
                    continue;
                }

                // devices: [scanner, dimensioner, sensor, conveyor]
                if (!TryGetArrayItem(dockEl, 1, out var devicesEl) || devicesEl.ValueKind != JsonValueKind.Array) continue;

                ElementState scannerSt = ElementState.Error;
                ElementState dimSt = ElementState.Error;
                ElementState sensorSt = ElementState.Error;

                // scanner tuple: [lastSeenAt, [..status bytes..]]
                if (TryGetArrayItem(devicesEl, 0, out var scannerTuple) &&
                    TryGetArrayItem(scannerTuple, 1, out var scannerBytes))
                    scannerSt = StateFromBitBytes(scannerBytes);

                // dimensioner tuple
                if (TryGetArrayItem(devicesEl, 1, out var dimTuple) &&
                    TryGetArrayItem(dimTuple, 1, out var dimBytes))
                    dimSt = StateFromBitBytes(dimBytes);

                // sensor tuple
                if (TryGetArrayItem(devicesEl, 2, out var sensorTuple) &&
                    TryGetArrayItem(sensorTuple, 1, out var sensorBytes))
                    sensorSt = StateFromBitBytes(sensorBytes);

                // 판독시스템(readSystem) 결정
                var readSystem = CombineReadSystem(scannerSt, dimSt, sensorSt);

                // ✅ 요청: base는 별도 정의가 없으므로 판독시스템 상태를 base로 사용
                _dockRunStateByDock[dockId] = readSystem;

                // ✅ 요청: readSystem/plc/encoder 표시는 도식 요소 업데이트가 목적
                // encoder는 이 API에 없으므로 동일 상태로 미러(원하면 여기만 나중에 분리 가능)
                _dockStationStateByDock[dockId] = readSystem;
                _dockEncoderStateByDock[dockId] = readSystem;

                _dockPlcStateByDock[dockId] = ElementState.Error; // conveyor 파싱 성공 시 아래에서 갱신

                // conveyor tuple: [lastSeenAt, [b0,b1,...]]  (b0..는 0~255 byte 값)
                if (TryGetArrayItem(devicesEl, 3, out var convTuple) &&
                    TryGetArrayItem(convTuple, 1, out var convBytesEl) &&
                    convBytesEl.ValueKind == JsonValueKind.Array)
                {
                    if (!TryReadByteList(convBytesEl, out var bytes))
                    {
                        // 파싱 실패면 전부 Error + PLC도 Error
                        for (int i = 0; i < convKeys.Length; i++)
                            SetConvState(dockId, convKeys[i], ElementState.Error);

                        _dockPlcStateByDock[dockId] = ElementState.Error;
                    }
                    else
                    {
                        int run = 0, stop = 0, err = 0;

                        // ✅ 요청사항:
                        // - 정상은 8개지만, 부족하면 앞에서부터 M01..에 매핑
                        // - 없는 M06/M07/신축은 0(비가동)으로 간주
                        for (int i = 0; i < convKeys.Length; i++)
                        {
                            byte v = (i < bytes.Count) ? bytes[i] : (byte)0;

                            ElementState st = StateFromConveyorByte(v); // 0=Stop, 1=Run, else=Error
                            SetConvState(dockId, convKeys[i], st);

                            if (st == ElementState.Run) run++;
                            else if (st == ElementState.Stop) stop++;
                            else err++;
                        }

                        // ✅ PLC 판넬 상태 결정(컨베이어 8개 집계)
                        // - 하나라도 Error면 Error
                        // - 전부 Run이면 Run
                        // - 전부 Stop이면 Stop
                        // - 그 외(0/1 혼합)는 Error
                        ElementState plc;
                        if (err > 0) plc = ElementState.Error;
                        else if (run == convKeys.Length) plc = ElementState.Run;
                        else if (stop == convKeys.Length) plc = ElementState.Stop;
                        else plc = ElementState.Error;

                        _dockPlcStateByDock[dockId] = plc;
                    }
                }
                else
                {
                    // conveyor 자체가 없으면 상태를 알 수 없으니 PLC는 Error 유지(위에서 기본 Error로 세팅됨)
                }

                // meta: ["B60","",ts] → encoderLabel 반영(있으면)
                if (TryGetArrayItem(dockEl, 2, out var metaEl) && metaEl.ValueKind == JsonValueKind.Array)
                {
                    if (TryGetArrayItem(metaEl, 1, out var labelEl) && labelEl.ValueKind == JsonValueKind.String)
                    {
                        var label = labelEl.GetString();
                        if (!string.IsNullOrWhiteSpace(label))
                            _dockEncoderLabelByDock[dockId] = label!;
                    }
                }
            }

            // [도크 끊김 판정 - 원인 3] 이전에 표시했지만 이번 응답에 없는 도크 (서버 재시작 등) → Offline
            foreach (var knownDock in _dockRunStateByDock.Keys.ToList())
            {
                if (!seenDocks.Contains(knownDock))
                    MarkDockOffline(knownDock, convKeys);
            }
        }


        private bool UseDockStatusDiagram
        {
            get => _useDockStatusDiagram;
            set
            {
                if (_useDockStatusDiagram == value) return;

                // ON으로 들어갈 때 현재 DXF 뷰 상태 저장
                if (value)
                    _savedDxfView = (_zoom, _panX, _panY);

                _useDockStatusDiagram = value;

                // 도식/일반 모드 전환 시 캔버스 높이 재계산 + 휠 동작 전환
                _diagramCanvasHeightCssPx = 0;
                _ = UpdateWheelBlockerAsync();

                // OFF로 나올 때 저장해둔 DXF 뷰 상태 복원
                if (!value && _savedDxfView is not null)
                {
                    var s = _savedDxfView.Value;
                    _zoom = s.zoom;
                    _panX = s.panX;
                    _panY = s.panY;
                    _savedDxfView = null;
                    Fit();
                }

                // ✅ @key로 DOM 교체하지 말고, OFF일 때 스크롤만 JS로 리셋
                if (!value)
                    _pendingResetDiagramScroll = true;

                // CSS/DOM(@key로 스크롤 영역 교체)이 먼저 적용된 다음 invalidate
                _invalidateAfterRender = true;
                _ = InvokeAsync(StateHasChanged);
            }
        }

        private bool DockConnectionsOnly
        {
            get => _dockDiagramView == DockDiagramView.ConnectionsOnly;
            set
            {
                var next = value ? DockDiagramView.ConnectionsOnly : DockDiagramView.Full;
                if (_dockDiagramView == next) return;
                _dockDiagramView = next;

                // 레이아웃이 바뀌면 캔버스 높이 재계산
                _diagramCanvasHeightCssPx = 0;

                _invalidateAfterRender = true;
                _ = InvokeAsync(StateHasChanged);
            }
        }

        private async Task UpdateWheelBlockerAsync()
        {
            if (_wheelModule is null) return;
            try
            {
                if (UseDockStatusDiagram)
                {
                    await _wheelModule.InvokeVoidAsync("unblockWheel", _host);
                    await CjOnlyOneSkiaFont.EnsureLoadedAsync(Http); // ★ API용 HttpClient 말고, 앱 기본 HttpClient
                    _dockDiagramRenderer?.ApplyTypefaces(CjOnlyOneSkiaFont.Regular, CjOnlyOneSkiaFont.Bold);
                    _canvas?.Invalidate();
                }

                else
                    await _wheelModule.InvokeVoidAsync("blockWheel", _host);
            }
            catch
            {
                // ignore
            }
        }

        private static class CjOnlyOneSkiaFont
        {
            private static readonly SemaphoreSlim _lock = new(1, 1);
            private static bool _loaded;

            // SKTypeface는 내부적으로 폰트 데이터를 참조할 수 있어서, SKData도 같이 잡고 있는게 안전합니다.
            private static SKData? _regularData;
            private static SKData? _boldData;

            public static SKTypeface? Regular { get; private set; }
            public static SKTypeface? Bold { get; private set; }

            public static async Task EnsureLoadedAsync(HttpClient http)
            {
                if (_loaded) return;

                await _lock.WaitAsync();
                try
                {
                    if (_loaded) return;

                    // scada.css / index.html 에서 쓰는 경로와 동일하게 맞추세요.
                    // (프로젝트에 woff2가 있으면 woff2만 두셔도 됩니다.)
                    var regBytes = await TryGetAsync(http,
                        "fonts/CJONLYONENEWbodyRegular.woff2",
                        "fonts/CJONLYONENEWbodyRegular.woff",
                        "fonts/CJOnlyOne-Regular.ttf");

                    var boldBytes = await TryGetAsync(http,
                        "fonts/CJONLYONENEWtitleBold.woff2",
                        "fonts/CJONLYONENEWtitleBold.woff",
                        "fonts/CJOnlyOne-Bold.ttf");

                    if (regBytes is not null)
                    {
                        _regularData = SKData.CreateCopy(regBytes);
                        Regular = SKTypeface.FromData(_regularData);
                    }

                    if (boldBytes is not null)
                    {
                        _boldData = SKData.CreateCopy(boldBytes);
                        Bold = SKTypeface.FromData(_boldData);
                    }

                    // 둘 중 하나라도 로드되면 적용 가능
                    _loaded = (Regular is not null) || (Bold is not null);
                }
                catch
                {
                    // 폰트 로드 실패 시: 그냥 기본 폰트로 렌더링(깨져보일 수는 있음)
                    _loaded = true; // 무한 재시도 방지
                }
                finally
                {
                    _lock.Release();
                }
            }

            private static async Task<byte[]?> TryGetAsync(HttpClient http, params string[] urls)
            {
                foreach (var u in urls)
                {
                    try { return await http.GetByteArrayAsync(u); }
                    catch { /* ignore */ }
                }
                return null;
            }
        }

        // ===============================
        // Dock diagram: Group Columns UI + localStorage
        // ===============================
        private bool _showDiagramColEditor = false;

        private int _colB = 36;
        private int _colC = 36;
        private int _colD = 36;
        private int _colE = 36;
        private int _colDefault = 36;

        private bool _diagramColsLoaded = false;
        private const string DiagramColsStoreKey = "viewer:dockDiagramCols:v1";
        private bool _pendingResetDiagramScroll;


        private sealed class DiagramColsConfigDto
        {
            public int DefaultCols { get; set; } = 9;
            public Dictionary<string, int> GroupCols { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private async Task SaveDiagramColumnsAsync()
        {
            var dto = new DiagramColsConfigDto
            {
                DefaultCols = Math.Max(1, _colDefault),
                GroupCols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["B"] = Math.Max(1, _colB),
                    ["C"] = Math.Max(1, _colC),
                    ["D"] = Math.Max(1, _colD),
                    ["E"] = Math.Max(1, _colE),
                }
            };

            var json = JsonSerializer.Serialize(dto);
            await JS.InvokeVoidAsync("localStorage.setItem", DiagramColsStoreKey, json);
        }

        private async Task LoadDiagramColumnsFromLocalStorageAsync()
        {
            if (_diagramColsLoaded) return;
            _diagramColsLoaded = true;

            try
            {
                var json = await JS.InvokeAsync<string?>("localStorage.getItem", DiagramColsStoreKey);
                if (string.IsNullOrWhiteSpace(json)) return;

                var dto = JsonSerializer.Deserialize<DiagramColsConfigDto>(json);
                if (dto is null) return;

                _colDefault = Math.Max(1, dto.DefaultCols);

                int Get(string k, int fallback)
                    => dto.GroupCols.TryGetValue(k, out var v) ? Math.Max(1, v) : fallback;

                _colB = Get("B", _colB);
                _colC = Get("C", _colC);
                _colD = Get("D", _colD);
                _colE = Get("E", _colE);

                // 로드된 값 즉시 적용 (저장은 하지 않음)
                await ApplyDiagramColumnsAsync(save: false);
            }
            catch
            {
                // ignore
            }
        }

        private async Task ApplyDiagramColumnsAsync(bool save = true)
        {
            _dockDiagramRenderer.DefaultCols = Math.Max(1, _colDefault);

            _dockDiagramRenderer.SetGroupColumns(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["B"] = Math.Max(1, _colB),
                ["C"] = Math.Max(1, _colC),
                ["D"] = Math.Max(1, _colD),
                ["E"] = Math.Max(1, _colE),
            });

            if (save)
                await SaveDiagramColumnsAsync();

            // 레이아웃/높이 재계산 + CSS 반영 후 invalidate
            _diagramCanvasHeightCssPx = 0;
            _invalidateAfterRender = true;

            await InvokeAsync(StateHasChanged);
        }

        private async Task EnsureKoreanTypefaceAsync()
        {
            if (_koreanTypeface is not null) return;

            // 1) wwwroot/fonts의 CJOnlyOne 폰트를 우선 로드(한글 지원)
            //    - woff2를 Skia가 읽지 못하는 환경도 있어, 실패 시 family-name 매칭으로 폴백
            try
            {
                // 우선 woff2
                var bytes = await Http.GetByteArrayAsync("/fonts/CJOnlyOne-Regular.woff2");
                using (var data = SKData.CreateCopy(bytes))
                    _koreanTypeface = SKTypeface.FromData(data);

                // Skia 빌드에 따라 woff2 파싱이 안 되는 경우가 있어 woff도 추가 시도
                if (_koreanTypeface is null)
                {
                    var bytesWoff = await Http.GetByteArrayAsync("/fonts/CJONLYONENEWbodyRegular.woff");
                    using var dataWoff = SKData.CreateCopy(bytesWoff);
                    _koreanTypeface = SKTypeface.FromData(dataWoff);
                }
            }
            catch
            {
                // ignore
            }

            // 2) 폴백: 브라우저/OS에 있는 폰트 패밀리로 매칭 시도
            if (_koreanTypeface is null)
            {
                foreach (var fam in new[] { "CJ OnlyOne", "CJOnlyOne", "Malgun Gothic", "Apple SD Gothic Neo", "Noto Sans KR", "Noto Sans CJK KR" })
                {
                    try
                    {
                        var tf = SKTypeface.FromFamilyName(fam);
                        if (tf is not null)
                        {
                            _koreanTypeface = tf;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (_koreanTypeface is not null)
            {
                _dockDiagramRenderer.SetTypeface(_koreanTypeface);
                _canvas?.Invalidate();
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 렌더링/데이터
        // ─────────────────────────────────────────────────────────────
        private SKCanvasView? _canvas;
        private readonly DxfSkiaRendererFast _renderer = new();

        // 뷰 상태
        private bool _firstPaint = true;
        private double _zoom = 1.0;
        private double _panX = 0, _panY = 0;
        private double _time;
        private bool _dragging;
        private bool _drawPending;
        private double _dpr = 1.0;
        private (double x, double y) _last;
        private int _viewW, _viewH;
        private SKRect _sceneBounds = SKRect.Empty;
        private bool _pendingFit;

        private ElementReference _diagramScroll;
        private IJSObjectReference? _resizeModule;
        private IJSObjectReference? _resizeHandle;
        private double _lastScrollW, _lastScrollH;


        // DPR 반영 실제 픽셀 크기
        private float _canvasPixelWidth, _canvasPixelHeight;

        // 도크 재중앙 기준(월드 좌표)
        private SKPoint? _anchorWorld;
        private SKRect? _anchorDockRectWorld;

        // 애니메이션
        private CancellationTokenSource? _animCts;
        private bool _animating;

        // 기타
        private string _stat = "";
        private string _sceneId = ""; // lane label 저장 키용
        private (double x, double y) _down;

        // 줌 한계
        private const double MIN_ZOOM = 1e-3;
        private const double MAX_ZOOM = 1e3;

        // 외부 파라미터/이벤트
        [Parameter] public EventCallback<IReadOnlyList<string>> DocksChanged { get; set; }
        [Parameter] public EventCallback<DockInfo> DockInfoChanged { get; set; }
        [Parameter] public string? Endpoint { get; set; }
        [Parameter] public string? ProcessCountEndpoint { get; set; }
        [Parameter] public string[]? JudgeRoutes { get; set; }

        // 프로세스 카운트 & judge 맵
        private List<ProcessCount> _counts = new();
        private readonly Dictionary<string, DxfSkiaRendererFast.JudgeData> _judgeMap = new();
        private bool _labelsOpen;
        private string[] _dockIdsForModal = Array.Empty<string>();
        private Dictionary<string, string> _laneLabelsForModal = new();
        private List<LaneWithLinksDto> _laneEntriesForModal = new();
        // 도크 표시 라벨 오버라이드 (dock.label / localStorage)
        private readonly Dictionary<string, string> _dockLabelOverrides = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _dockIdMap = new(StringComparer.OrdinalIgnoreCase); // DOCK ID -> DOCK LABEL



        // 키보드 팔레트 (0은 원본 색)
        private static readonly SKColor[] _palette = new[]
        {
        SKColors.White, SKColors.Red, SKColors.Yellow, SKColors.Lime, SKColors.Cyan,
        SKColors.Blue, SKColors.Magenta, new SKColor(255,128,0), new SKColor(128,255,0), new SKColor(255,0,128)
    };

        private CancellationTokenSource _loadCts = new();

        // 도크별 컨베이어 상태(이름 기반)
        private readonly Dictionary<string, Dictionary<string, ElementState>> _dockConvStateByDock
            = new(StringComparer.OrdinalIgnoreCase);

        private ElementState GetConvState(string dockKey, string convName, ElementState @default = ElementState.Stop)
        {
            if (_dockConvStateByDock.TryGetValue(dockKey, out var map) &&
                map.TryGetValue(convName, out var st))
                return st;

            return @default;
        }

        // (선택) 외부에서 쉽게 업데이트할 수 있게 헬퍼
        private void SetConvState(string dockKey, string convName, ElementState state)
        {
            if (!_dockConvStateByDock.TryGetValue(dockKey, out var map))
            {
                map = new Dictionary<string, ElementState>(StringComparer.OrdinalIgnoreCase);
                _dockConvStateByDock[dockKey] = map;
            }
            map[convName] = state;
        }


        // 도식(타일) 정렬용 DOCK ID(예: DOCK1, DOCK2 ...)를 구한다.
        // - 우선순위: dock.id.map(서버/로컬) 역매핑 → B/C/D/E 라벨 표준 역변환
        // - dockKey 는 CanonLabel("B01" 같은 형태) 기준
        private string ResolveSortDockIdFromDockKey(string dockKey)
        {
            if (string.IsNullOrWhiteSpace(dockKey)) return dockKey;

            var canonKey = DockLabelNormalization.CanonLabel(dockKey);

            // 1) dock.id.map: (DOCK ID -> DOCK LABEL) 형태라고 가정하고,
            //    값(label)이 현재 dockKey와 같으면 그 키(DOCK ID)를 사용
            if (_dockIdMap.Count > 0)
            {
                foreach (var kv in _dockIdMap)
                {
                    var id = (kv.Key ?? "").Trim();
                    var label = (kv.Value ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(label)) continue;

                    // label 쪽은 "B1"/"b01"/"DOCK1" 등 다양한 입력이 올 수 있어 CanonLabel로 통일
                    var canonLabel = DockLabelNormalization.CanonLabel(label);
                    if (string.Equals(canonLabel, canonKey, StringComparison.OrdinalIgnoreCase))
                        return id.ToUpperInvariant();
                }
            }

            // 2) 표준 역변환: "B01" -> "DOCK1" (범위 밖이면 그냥 dockKey 유지)
            if (DockLabelUtil.TryParseIndexFromLabel(canonKey, out var idx) && idx > 0)
                return $"DOCK{idx}";

            // 3) 이미 DOCKn 형태면 그대로
            if (canonKey.StartsWith("DOCK", StringComparison.OrdinalIgnoreCase))
                return canonKey.ToUpperInvariant();

            return canonKey;
        }

        private DockDiagramModel BuildDockDiagramModel(string dockName)
        {
            // dockKey: 상태 조회/매핑 기준(항상 CanonLabel 형태로 통일)
            var dockKey = DockLabelNormalization.CanonLabel(dockName);

            // sortDockId: 도식 정렬 기준(요청: DockId가 DOCK1, DOCK2 ... 인 순서)
            var sortDockId = ResolveSortDockIdFromDockKey(dockKey);

            var baseSt = _dockRunStateByDock.TryGetValue(dockKey, out var b) ? b : ElementState.Stop;

            var station = _dockStationStateByDock.TryGetValue(dockKey, out var s) ? s : baseSt;
            var plc = _dockPlcStateByDock.TryGetValue(dockKey, out var p) ? p : baseSt;
            var encoder = _dockEncoderStateByDock.TryGetValue(dockKey, out var e) ? e : baseSt;

            var encLabel = _dockEncoderLabelByDock.TryGetValue(dockKey, out var lbl) ? lbl : "ENC";

            return new DockDiagramModel
            {
                // ✅ DockId: 정렬용 ID(DOCK1..)
                DockId = sortDockId,

                // ✅ DockKey: 상태/라벨 조회용 CanonLabel(B01..)
                DockKey = dockKey,

                // ✅ 명칭 매핑 반영
                Conv1_MainTop = GetConvState(dockKey, DockConvName.ShinChuk), // "신축"
                Conv2_MainMid = GetConvState(dockKey, DockConvName.M01),      // "M01"
                Conv3_MainBot = GetConvState(dockKey, DockConvName.M02),      // "M02"
                Conv4_CenterDown = GetConvState(dockKey, DockConvName.M03),   // "M03"

                // 오른쪽 가지
                Conv7_RightDiag = GetConvState(dockKey, DockConvName.M04),    // "M04"
                Conv8_RightDown = GetConvState(dockKey, DockConvName.M05),    // "M05"

                // 왼쪽 가지
                Conv5_LeftDiag = GetConvState(dockKey, DockConvName.M06),     // "M06"
                Conv6_LeftDown = GetConvState(dockKey, DockConvName.M07),     // "M07"

                // 장치들
                ReadSystem = station,
                Plc = plc,
                Encoder = encoder,
                EncoderLabel = encLabel,
            };
        }





        // ─────────────────────────────────────────────────────────────
        // DockInfo DTO (부모 콜백용)
        // ─────────────────────────────────────────────────────────────
        public sealed class DockInfo
        {
            public required string Name { get; init; }
            public required int Index { get; init; }
            public required float Left { get; init; }
            public required float Top { get; init; }
            public required float Right { get; init; }
            public required float Bottom { get; init; }
            public required float Cx { get; init; }
            public required float Cy { get; init; }
            public int InsertCount { get; init; }
            public int HeaderCount { get; init; }
            public int LaneCount { get; init; }
            public int TboxCount { get; init; }
        }

        // 가로 레인 라벨 저장/불러오기용 DTO
        public sealed class LaneLabelConfigDto
        {
            public string? SceneId { get; set; }
            public Dictionary<string, string> Labels { get; set; } = new();
        }

        // 도크 라벨 저장/불러오기용 DTO (dock.label)
        public sealed class DockLabelConfigDto
        {
            public string? SceneId { get; set; }
            public Dictionary<string, string> Labels { get; set; } = new();
        }

        // DOCK ID 매핑 저장/불러오기용 DTO (dock.id.map)
        public sealed class DockIdMapConfigDto
        {
            public string? SceneId { get; set; }
            public Dictionary<string, string> Map { get; set; } = new();
        }


        // ─────────────────────────────────────────────────────────────
        // 수명주기
        // ─────────────────────────────────────────────────────────────
        protected override async Task OnInitializedAsync()
        {
            if (DocksOnly)
            {
                await ReloadDocksOnlyAsync();   // ✅ 도크 목록만 로드
                return;
            }

            await Reload();
            await LoadEventLogsAsync(1);
            StartAlarmPolling();
        }



        protected override void OnInitialized()
        {
            // 도식(타일) 모드에서 도크 라벨 표시 오버라이드 적용
            _dockDiagramRenderer.ResolveDockLabel = DockDisplayLabel;

            // 스탠드얼론(가로) LANE 라벨 옵션
            _renderer.DrawStandaloneLaneLabels = true;
            _renderer.StandaloneLabelUseScreenSpace = true;
            _renderer.StandaloneLaneLabelMarginPx = 16f;
            _renderer.StandaloneLaneLeaderGapPx = 8f;
            _renderer.StandaloneLabelFontPx = 24f;
            _renderer.StandaloneLaneLabelSide = DxfSkiaRendererFast.LabelSide.Auto;

            // 렌더 대상 블록 화이트리스트
            _renderer.SetBlockWhitelist(new[] { "LANE_HEAD", "LANE_LARGE", "LANE_IRREGULAR", "LANE_MEDIUM" });

            // 정책 샘플 (빨강/파랑/초록)
            _renderer.JudgePolicies[DxfSkiaRendererFast.ColorFamily.Red].Thresholds.Clear();
            _renderer.JudgePolicies[DxfSkiaRendererFast.ColorFamily.Red].Thresholds.AddRange(new[] { 0.30, 0.50, 0.60, 0.75, 0.90 });
            _renderer.JudgePolicies[DxfSkiaRendererFast.ColorFamily.Red].SaturationLevels.Clear();
            _renderer.JudgePolicies[DxfSkiaRendererFast.ColorFamily.Red].SaturationLevels.AddRange(new[] { 0.20f, 0.40f, 0.60f, 0.80f, 1.00f });
            _renderer.JudgePolicies[DxfSkiaRendererFast.ColorFamily.Red].RatioSelector
                = jd => jd.Total > 0 ? (jd.Small + jd.Medium) / jd.Total : 0.0;

            _renderer.JudgePolicies[DxfSkiaRendererFast.ColorFamily.Blue].Thresholds.Clear();
            _renderer.JudgePolicies[DxfSkiaRendererFast.ColorFamily.Blue].Thresholds.AddRange(new[] { 0.30, 0.50, 0.60, 0.75, 0.90 });
            _renderer.JudgePolicies[DxfSkiaRendererFast.ColorFamily.Blue].SaturationLevels.Clear();
            _renderer.JudgePolicies[DxfSkiaRendererFast.ColorFamily.Blue].SaturationLevels.AddRange(new[] { 0.20f, 0.40f, 0.60f, 0.80f, 1.00f });
            _renderer.JudgePolicies[DxfSkiaRendererFast.ColorFamily.Blue].RatioSelector
                = jd => jd.Total > 0 ? jd.Large / jd.Total : 0.0;

            _renderer.JudgePolicies[DxfSkiaRendererFast.ColorFamily.Green].Thresholds.Clear();
            _renderer.JudgePolicies[DxfSkiaRendererFast.ColorFamily.Green].Thresholds.AddRange(new[] { 0.30, 0.50, 0.60, 0.75, 0.90 });
            _renderer.JudgePolicies[DxfSkiaRendererFast.ColorFamily.Green].SaturationLevels.Clear();
            _renderer.JudgePolicies[DxfSkiaRendererFast.ColorFamily.Green].SaturationLevels.AddRange(new[] { 0.20f, 0.40f, 0.60f, 0.80f, 1.00f });
            _renderer.JudgePolicies[DxfSkiaRendererFast.ColorFamily.Green].RatioSelector
                = jd => jd.Total > 0 ? jd.Irregular / jd.Total : 0.0;
        }

        // ─────────────────────────────────────────────────────────────
        // 라벨 저장/불러오기 (LocalStorage)
        // ─────────────────────────────────────────────────────────────
        private string LabelStoreKey(string sceneId) => $"laneLabels:{sceneId}";
        private string DockLabelStoreKey(string sceneId) => $"dockLabels:{sceneId}";

        private const string DockIdMapCategory = "dock.id.map";
        private string DockIdMapStoreKey(string sceneId) => $"dockIdMap:{sceneId}";

        private async Task SaveDockIdMapAsync()
        {
            var dto = new DockIdMapConfigDto
            {
                SceneId = _sceneId,
                Map = new Dictionary<string, string>(_dockIdMap, StringComparer.OrdinalIgnoreCase)
            };
            var json = JsonSerializer.Serialize(dto);
            await JS.InvokeVoidAsync("localStorage.setItem", DockIdMapStoreKey(_sceneId), json);
        }

        private async Task LoadDockIdMapAsync()
        {
            // 씬이 바뀌면 이전 매핑이 남지 않도록 항상 초기화
            _dockIdMap.Clear();
            DockLabelUtil.ClearDockIdMap();

            var json = await JS.InvokeAsync<string?>("localStorage.getItem", DockIdMapStoreKey(_sceneId));
            if (string.IsNullOrEmpty(json)) return;

            try
            {
                var dto = JsonSerializer.Deserialize<DockIdMapConfigDto>(json);
                if (dto?.Map is { Count: > 0 })
                {
                    foreach (var kv in dto.Map)
                    {
                        var k = (kv.Key ?? "").Trim();
                        var v = (kv.Value ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(v)) continue;
                        _dockIdMap[k] = v;
                    }

                    // 핵심: 전역 매핑 주입
                    DockLabelUtil.SetDockIdMap(_dockIdMap);
                }
            }
            catch
            {
                // ignore
            }
        }

        private async Task TryLoadDockIdMapFromServerAsync()
        {
            if (string.IsNullOrWhiteSpace(LaneLabelApiBase)) return;
            if (string.IsNullOrWhiteSpace(_sceneId)) return;

            try
            {
                var baseUri = LaneLabelApiBase.TrimEnd('/');
                var url = $"{baseUri}?type=json&category={DockIdMapCategory}&machineId=global&groupId=global&key={Uri.EscapeDataString(_sceneId)}";

                var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[Viewer] dock id map GET {url} → {resp.StatusCode}");
                    return;
                }

                var text = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(text) || text == "null")
                {
                    Console.WriteLine("[Viewer] dock id map: empty or null");
                    return;
                }

                Dictionary<string, string>? dict = null;
                var jsonOpt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                // ① 그대로 Dictionary 로 시도
                try
                {
                    dict = JsonSerializer.Deserialize<Dictionary<string, string>>(text, jsonOpt);
                }
                catch (JsonException)
                {
                    // ② setting 래퍼(object/array)로 재파싱
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;

                        string? inner = null;

                        if (root.ValueKind == JsonValueKind.Object)
                        {
                            if (root.TryGetProperty("jsonValue", out var jsonValueEl) &&
                                jsonValueEl.ValueKind == JsonValueKind.String)
                            {
                                inner = jsonValueEl.GetString();
                            }
                        }
                        else if (root.ValueKind == JsonValueKind.Array)
                        {
                            JsonElement? best = null;
                            DateTimeOffset? bestUpdated = null;

                            foreach (var el in root.EnumerateArray())
                            {
                                DateTimeOffset? updated = null;
                                if (el.TryGetProperty("updatedAt", out var upEl) &&
                                    upEl.ValueKind == JsonValueKind.String &&
                                    DateTimeOffset.TryParse(upEl.GetString(), out var dto))
                                {
                                    updated = dto;
                                }

                                if (best is null || (updated.HasValue && updated > bestUpdated))
                                {
                                    best = el;
                                    bestUpdated = updated;
                                }
                            }

                            if (best.HasValue &&
                                best.Value.TryGetProperty("jsonValue", out var jsonValueEl) &&
                                jsonValueEl.ValueKind == JsonValueKind.String)
                            {
                                inner = jsonValueEl.GetString();
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(inner))
                            dict = JsonSerializer.Deserialize<Dictionary<string, string>>(inner!, jsonOpt);
                    }
                    catch (Exception ex2)
                    {
                        Console.WriteLine($"[Viewer] dock id map JSON wrapper parse error: {ex2}");
                        return;
                    }
                }

                if (dict is { Count: > 0 })
                {
                    _dockIdMap.Clear();

                    foreach (var kv in dict)
                    {
                        var k = (kv.Key ?? "").Trim();
                        var v = (kv.Value ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(v)) continue;
                        _dockIdMap[k] = v;
                    }

                    // 핵심: 전역 매핑 주입
                    DockLabelUtil.SetDockIdMap(_dockIdMap);

                    await SaveDockIdMapAsync();
                    Console.WriteLine($"[Viewer] dock id map loaded from API: {_dockIdMap.Count} entries");
                }
                else
                {
                    Console.WriteLine("[Viewer] dock id map: dict null or empty");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Viewer] Failed to load dock id map from API: {ex}");
            }
        }


        private async Task SaveLaneLabelsAsync()
        {
            var dto = new LaneLabelConfigDto { SceneId = _sceneId, Labels = _renderer.ExportLaneLabels() };
            var json = JsonSerializer.Serialize(dto);
            await JS.InvokeVoidAsync("localStorage.setItem", LabelStoreKey(_sceneId), json);
        }

        private async Task LoadLaneLabelsAsync()
        {
            var json = await JS.InvokeAsync<string?>("localStorage.getItem", LabelStoreKey(_sceneId));
            if (string.IsNullOrEmpty(json)) return;

            var dto = JsonSerializer.Deserialize<LaneLabelConfigDto>(json);
            if (dto?.Labels?.Count > 0)
                _renderer.ImportLaneLabels(dto.Labels, clearExisting: true);

            _canvas?.Invalidate();
        }


        private async Task SaveDockLabelsAsync()
        {
            // CanonLabel 기준으로 저장
            var dto = new DockLabelConfigDto
            {
                SceneId = _sceneId,
                Labels = new Dictionary<string, string>(_dockLabelOverrides, StringComparer.OrdinalIgnoreCase)
            };
            var json = JsonSerializer.Serialize(dto);
            await JS.InvokeVoidAsync("localStorage.setItem", DockLabelStoreKey(_sceneId), json);
        }

        private async Task LoadDockLabelsAsync()
        {
            var json = await JS.InvokeAsync<string?>("localStorage.getItem", DockLabelStoreKey(_sceneId));
            if (string.IsNullOrEmpty(json)) return;

            try
            {
                var dto = JsonSerializer.Deserialize<DockLabelConfigDto>(json);
                _dockLabelOverrides.Clear();
                if (dto?.Labels != null)
                {
                    foreach (var kv in dto.Labels)
                    {
                        var k = DockLabelNormalization.CanonLabel(kv.Key);
                        var v = (kv.Value ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(k))
                            _dockLabelOverrides[k] = v;
                    }
                }
            }
            catch
            {
                // ignore
            }

            // 도식 모드에서 즉시 반영되도록 invalidate
            _canvas?.Invalidate();
        }

        // ─────────────────────────────────────────────
        // 서버에 저장된 가로 LANE 라벨 1회 로드
        // ─────────────────────────────────────────────
        private async Task TryLoadLaneLabelsFromServerAsync()
        {
            // 설정이 비어 있거나 씬 ID가 아직 없으면 아무 것도 안 함
            if (string.IsNullOrWhiteSpace(LaneLabelApiBase)) return;
            if (string.IsNullOrWhiteSpace(_sceneId)) return;

            try
            {
                var baseUri = LaneLabelApiBase.TrimEnd('/');

                // ⚠ 여기 URL은 실제 서버 라우팅에 맞게 조정하세요.
                //    1) /json/{category}/{machineId}/{groupId}/{key} 패턴이라면:
                //       var url = $"{baseUri}/json/lane.group/global/global/{Uri.EscapeDataString(_sceneId)}";
                //
                //    2) 쿼리 스트링 패턴이라면:
                //       var url = $"{baseUri}?type=json&category=lane.label&key={Uri.EscapeDataString(_sceneId)}";
                //
                //    아래 한 줄은 예시입니다. 실제 사용하던 url 조합으로 바꿔 끼워 주세요.
                var url = $"{baseUri}?type=json&category=lane.group&machineId=global&groupId=global&key={Uri.EscapeDataString(_sceneId)}";

                var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[Viewer] lane labels GET {url} → {resp.StatusCode}");
                    return;
                }

                var text = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(text) || text == "null")
                {
                    Console.WriteLine("[Viewer] lane labels: empty or null");
                    return;
                }

                LaneLabelBundleDto? bundle = null;
                var jsonOpt = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                // ① 우선 그대로 LaneLabelBundleDto 로 시도
                try
                {
                    bundle = JsonSerializer.Deserialize<LaneLabelBundleDto>(text, jsonOpt);
                }
                catch (JsonException)
                {
                    // ② 안 되면 setting 래퍼 형태(배열/객체)로 간주하고 다시 파싱
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;

                        // 루트가 곧바로 LaneLabelBundleDto 인 경우도 처리 가능하게 해둠
                        if (root.ValueKind == JsonValueKind.Object &&
                            root.TryGetProperty("sceneId", out _) &&
                            root.TryGetProperty("lanes", out _))
                        {
                            bundle = JsonSerializer.Deserialize<LaneLabelBundleDto>(text, jsonOpt);
                        }
                        else if (root.ValueKind == JsonValueKind.Object)
                        {
                            // 2-1) 단일 setting 객체: { ..., jsonValue: "{...LaneLabelBundleDto...}" }
                            if (root.TryGetProperty("jsonValue", out var jsonValueEl) &&
                                jsonValueEl.ValueKind == JsonValueKind.String)
                            {
                                var inner = jsonValueEl.GetString();
                                if (!string.IsNullOrWhiteSpace(inner))
                                {
                                    bundle = JsonSerializer.Deserialize<LaneLabelBundleDto>(inner, jsonOpt);
                                }
                            }
                        }
                        else if (root.ValueKind == JsonValueKind.Array)
                        {
                            // 2-2) setting 리스트: [ { jsonValue: "..." , updatedAt: "..." }, ... ]
                            JsonElement? best = null;
                            DateTimeOffset? bestUpdated = null;

                            foreach (var el in root.EnumerateArray())
                            {
                                // updatedAt 있으면 가장 최근 것을 선택 (없으면 첫 번째가 선택될 수도 있음)
                                DateTimeOffset? updated = null;
                                if (el.TryGetProperty("updatedAt", out var upEl) &&
                                    upEl.ValueKind == JsonValueKind.String &&
                                    DateTimeOffset.TryParse(upEl.GetString(), out var dto))
                                {
                                    updated = dto;
                                }

                                if (best is null || (updated.HasValue && updated > bestUpdated))
                                {
                                    best = el;
                                    bestUpdated = updated;
                                }
                            }

                            if (best.HasValue &&
                                best.Value.TryGetProperty("jsonValue", out var jsonValueEl) &&
                                jsonValueEl.ValueKind == JsonValueKind.String)
                            {
                                var inner = jsonValueEl.GetString();
                                if (!string.IsNullOrWhiteSpace(inner))
                                {
                                    bundle = JsonSerializer.Deserialize<LaneLabelBundleDto>(inner, jsonOpt);
                                }
                            }
                        }
                    }
                    catch (Exception ex2)
                    {
                        Console.WriteLine($"[Viewer] lane labels JSON wrapper parse error: {ex2}");
                        return;
                    }
                }

                if (bundle?.Lanes is { Count: > 0 })
                {
                    // 기존에 모달 저장 시 쓰던 로직 재사용
                    await OnLaneLabelsSaved(bundle);
                    Console.WriteLine($"[Viewer] lane labels loaded from API: {bundle.Lanes.Count} lanes");
                }
                else
                {
                    Console.WriteLine("[Viewer] lane labels: bundle null or empty");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Viewer] Failed to load lane labels from API: {ex}");
            }
        }

        // ─────────────────────────────────────────────
        // 서버에 저장된 DOCK 라벨 1회 로드 (dock.label)
        // ─────────────────────────────────────────────
        private async Task TryLoadDockLabelsFromServerAsync()
        {
            if (string.IsNullOrWhiteSpace(LaneLabelApiBase)) return;
            if (string.IsNullOrWhiteSpace(_sceneId)) return;

            try
            {
                var baseUri = LaneLabelApiBase.TrimEnd('/');
                var url = $"{baseUri}?type=json&category=dock.label&machineId=global&groupId=global&key={Uri.EscapeDataString(_sceneId)}";

                var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[Viewer] dock labels GET {url} → {resp.StatusCode}");
                    return;
                }

                var text = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(text) || text == "null")
                {
                    Console.WriteLine("[Viewer] dock labels: empty or null");
                    return;
                }

                Dictionary<string, string>? dict = null;
                var jsonOpt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                // ① 우선 그대로 Dictionary<string,string> 로 시도
                try
                {
                    dict = JsonSerializer.Deserialize<Dictionary<string, string>>(text, jsonOpt);
                }
                catch (JsonException)
                {
                    // ② setting 래퍼 형태(배열/객체)로 간주하고 다시 파싱
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;

                        string? inner = null;

                        if (root.ValueKind == JsonValueKind.Object)
                        {
                            // 단일 setting 객체: { ..., jsonValue: "{...dict...}" }
                            if (root.TryGetProperty("jsonValue", out var jsonValueEl) &&
                                jsonValueEl.ValueKind == JsonValueKind.String)
                            {
                                inner = jsonValueEl.GetString();
                            }
                        }
                        else if (root.ValueKind == JsonValueKind.Array)
                        {
                            // setting 리스트: [ { jsonValue: "..." , updatedAt: "..." }, ... ]
                            JsonElement? best = null;
                            DateTimeOffset? bestUpdated = null;

                            foreach (var el in root.EnumerateArray())
                            {
                                DateTimeOffset? updated = null;
                                if (el.TryGetProperty("updatedAt", out var upEl) &&
                                    upEl.ValueKind == JsonValueKind.String &&
                                    DateTimeOffset.TryParse(upEl.GetString(), out var dto))
                                {
                                    updated = dto;
                                }

                                if (best is null || (updated.HasValue && updated > bestUpdated))
                                {
                                    best = el;
                                    bestUpdated = updated;
                                }
                            }

                            if (best.HasValue &&
                                best.Value.TryGetProperty("jsonValue", out var jsonValueEl) &&
                                jsonValueEl.ValueKind == JsonValueKind.String)
                            {
                                inner = jsonValueEl.GetString();
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(inner))
                            dict = JsonSerializer.Deserialize<Dictionary<string, string>>(inner!, jsonOpt);
                    }
                    catch (Exception ex2)
                    {
                        Console.WriteLine($"[Viewer] dock labels JSON wrapper parse error: {ex2}");
                        return;
                    }
                }

                if (dict is { Count: > 0 })
                {
                    _dockLabelOverrides.Clear();

                    foreach (var kv in dict)
                    {
                        var canon = DockLabelNormalization.CanonLabel(kv.Key);
                        var lbl = (kv.Value ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(canon) && !string.IsNullOrWhiteSpace(lbl))
                            _dockLabelOverrides[canon] = lbl;
                    }

                    await SaveDockLabelsAsync();
                    _canvas?.Invalidate();
                    Console.WriteLine($"[Viewer] dock labels loaded from API: {_dockLabelOverrides.Count} docks");
                }
                else
                {
                    Console.WriteLine("[Viewer] dock labels: dict null or empty");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Viewer] Failed to load dock labels from API: {ex}");
            }
        }


        // ─────────────────────────────────────────────────────────────
        // 로드 & 판정 데이터 주입
        // ─────────────────────────────────────────────────────────────
        private async Task Reload()
        {
            _loadCts.Cancel();
            _loadCts.Dispose();
            _loadCts = new();

            Endpoint ??= "/api/dxf";

            // ✅ 씬 식별자(저장 키)는 먼저 확정
            _sceneId = Endpoint!;

            // ✅ DOCK ID → DOCK LABEL 매핑을 먼저 로드/주입 (렌더러 LoadFromScene 전에!)
            await LoadDockIdMapAsync();              // localStorage
            await TryLoadDockIdMapFromServerAsync(); // server(있으면 덮어씀)

            // 서버에서 씬 수신
            var scene = await ServerAdapter.FetchSceneAsync(Endpoint!, _loadCts.Token);
            if (scene != null && _renderer.LoadFromScene(scene))
            {
                _sceneBounds = ToRect((scene.Bounds.Left, scene.Bounds.Top, scene.Bounds.Right, scene.Bounds.Bottom));

                var docks = _renderer.GetDockNames();
                if (DocksChanged.HasDelegate) await DocksChanged.InvokeAsync(docks);

                await LoadJudgeAndApplyAsync();   // 판정 데이터 로딩
                await LoadLaneLabelsAsync();      // 라벨 불러오기
                await LoadDockLabelsAsync();      // 도크 라벨(도식/칩 표시) 불러오기
                await TryLoadLaneLabelsFromServerAsync();
                await TryLoadDockLabelsFromServerAsync();

                Fit();
                StartStatsPolling();
            }
        }

        private async Task ReloadDocksOnlyAsync()
        {
            _loadCts.Cancel();
            _loadCts.Dispose();
            _loadCts = new();

            Endpoint ??= "/api/dxf";

            // ✅ 저장 키 먼저 확정 (Stats에서도 동일 Endpoint를 넘겨야 함)
            _sceneId = Endpoint!;

            // ✅ “그룹/라벨 기준”이 되는 맵을 먼저 로드/주입
            await LoadDockIdMapAsync();              // localStorage
            await TryLoadDockIdMapFromServerAsync(); // server (있으면 덮어씀)

            // (선택) 도크 표시 라벨까지 Stats에서 쓰고 있다면 이것도 같이
            await LoadDockLabelsAsync();
            await TryLoadDockLabelsFromServerAsync();

            var scene = await ServerAdapter.FetchSceneAsync(Endpoint!, _loadCts.Token);
            if (scene != null && _renderer.LoadFromScene(scene))
            {
                var docks = _renderer.GetDockNames();

                // ✅ DockIdMap 주입 완료 후에 도크 목록 전달
                if (DocksChanged.HasDelegate)
                    await DocksChanged.InvokeAsync(docks);
            }

            // ❌ 여기서는 Fit(), StartStatsPolling(), LoadEventLogsAsync(), AlarmPolling 등 절대 호출하지 않음
        }




        // 도크 표시 라벨(오버라이드 우선)
        private string DockDisplayLabel(string dockId)
        {
            var canon = DockLabelNormalization.CanonLabel(dockId);
            if (_dockLabelOverrides.TryGetValue(canon, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
            return canon;
        }

        // 모달 열기
        // === 모달 열기 ===
        private void OpenLabelModal()
        {
            // 1) 가로 레인 → 연결 도크 맵을 렌더러에서 바로 가져옴
            var map = _renderer.GetHorizontalLaneDockMap(); // IReadOnlyDictionary<string, LaneDockLink>

            // 2) 모달 표시용 엔트리로 투영
            _laneEntriesForModal = map.Values
                .OrderBy(v => v.LaneKey, StringComparer.OrdinalIgnoreCase)
                .Select(v => new LaneWithLinksDto
                {
                    Id = v.LaneKey,           // LaneDockLink.LaneKey
                    Label = v.LaneLabel ?? "",   // LaneDockLink.LaneLabel
                                                 // 필요하면 색상 계열/바운드도 함께 전달 가능
                                                 // Family = v.Family.ToString(),
                    Docks = v.DockLabels
                            .Select(d =>
                            {
                                var canon = DockLabelNormalization.CanonLabel(d);
                                _dockLabelOverrides.TryGetValue(canon, out var dl);
                                return new LaneDockLinkDto { DockId = canon, DockLabel = string.IsNullOrWhiteSpace(dl) ? null : dl };
                            })
                            .ToList()
                })
                .ToList();

            _labelsOpen = true;
        }

        // === 저장 성공 콜백: 화면 즉시 반영 ===
        private async Task OnLaneLabelsSaved(LaneLabelBundleDto bundle)
        {
            // 1) 레인 라벨 즉시 반영
            var newMap = bundle.Lanes?.ToDictionary(x => x.Id, x => x.Label ?? "", StringComparer.OrdinalIgnoreCase)
                       ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in newMap)
                _renderer.SetLaneLabel(kv.Key, kv.Value);

            // 2) 도크 라벨(오버라이드) 추출 반영
            _dockLabelOverrides.Clear();
            if (bundle.Lanes != null)
            {
                foreach (var lane in bundle.Lanes)
                {
                    if (lane?.Docks == null) continue;
                    foreach (var d in lane.Docks)
                    {
                        if (string.IsNullOrWhiteSpace(d.DockId)) continue;
                        var canon = DockLabelNormalization.CanonLabel(d.DockId);
                        var lbl = (d.DockLabel ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(lbl))
                            _dockLabelOverrides[canon] = lbl;
                    }
                }
            }

            await SaveLaneLabelsAsync(); // localStorage 갱신
            await SaveDockLabelsAsync(); // dockLabels localStorage 갱신

            _canvas?.Invalidate();
            _labelsOpen = false;
        }

        private async Task LoadJudgeAndApplyAsync()
        {
            //var url = ProcessCountEndpoint ?? "http://192.168.0.41:8121/api/processcount";
            var url = ProcessCountEndpoint ?? "/api/ProcessCount/live/latest-n-minutes";


            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };

            var apiData = await Http.GetFromJsonAsync<List<ApiProcessCount>>(url, options) ?? new();
            _counts = apiData.Select(ApiProcessCountMapper.Map).ToList();

            var routeSet = (JudgeRoutes ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var filtered = routeSet.Count > 0
                ? _counts.Where(p => !string.IsNullOrWhiteSpace(p.Route) && routeSet.Contains(p.Route!))
                : _counts;

            var dict = filtered
                .GroupBy(p => DockLabelNormalization.CanonLabel(p.DockId))
                .ToDictionary(g => g.Key, g =>
                {
                    long s = 0, m = 0, l = 0, ir = 0;
                    foreach (var p in g)
                    {
                        s += p.Judge.Small; m += p.Judge.Medium; l += p.Judge.Large; ir += p.Judge.Irregular;
                    }
                    long total = s + m + l + ir;
                    return new DxfSkiaRendererFast.JudgeData(s, m, l, ir, total);
                });

            _judgeMap.Clear();
            foreach (var kv in dict) _judgeMap[kv.Key] = kv.Value;

            foreach (var kv in dict)
            {
                var dockKey = kv.Key;          // CanonLabel 기반 키
                long total = (long)kv.Value.Total;    // long

                if (_lastTotalByDock.TryGetValue(dockKey, out var prev))
                    _dockRunStateByDock[dockKey] = (total > prev) ? ElementState.Run : ElementState.Stop;
                else
                    _dockRunStateByDock[dockKey] = ElementState.Stop; // 첫 샘플

                _lastTotalByDock[dockKey] = total;
            }

            _renderer.SetJudgeMap(_judgeMap);
            _renderer.WarmJudgeCache();

            await InvokeAsync(() => _canvas?.Invalidate());
        }

        // ─────────────────────────────────────────────────────────────
        // Fit & DPR와 연동
        // ─────────────────────────────────────────────────────────────
        public void Fit()
        {
            FitIfReady(_viewW, _viewH, 24f);
            _firstPaint = true;
            _renderer.HighlightDockIndex = null;
            _canvas?.Invalidate();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (DocksOnly) return;

            if (firstRender)
            {
                _wheelModule = await JS.InvokeAsync<IJSObjectReference>("import", "/js/wheel-blocker.js");
                await _wheelModule.InvokeVoidAsync("blockWheel", _host);

                // 현재 모드에 맞게 휠 동작 조정(도식 모드는 스크롤 허용)
                await UpdateWheelBlockerAsync();

                _dpr = await JS.InvokeAsync<double>("eval", "window.devicePixelRatio || 1");
                _dprModule = await JS.InvokeAsync<IJSObjectReference>("import", "/js/dpr-watcher.js");
                _selfRef = DotNetObjectReference.Create(this);
                _dprHandle = await _dprModule.InvokeAsync<IJSObjectReference>("watchDpr", _selfRef);
                _resizeModule = await JS.InvokeAsync<IJSObjectReference>("import", "/js/resize-observer.js");
                _resizeHandle = await _resizeModule.InvokeAsync<IJSObjectReference>(
                    "observeSize", _diagramScroll, _selfRef!, nameof(OnDiagramScrollResized));

                await LoadDiagramColumnsFromLocalStorageAsync();
            }

            // ✅ OFF로 전환 직후: 스크롤만 리셋 (DOM 교체 X)
            if (_pendingResetDiagramScroll && _wheelModule is not null)
            {
                _pendingResetDiagramScroll = false;
                try { await _wheelModule.InvokeVoidAsync("setScrollTop", _diagramScroll, 0); } catch { }
            }

            // 모드 전환/레이아웃 변경 직후에는 여기서 한 번 더 invalidate (CSS가 반영된 뒤)
            if (_invalidateAfterRender)
            {
                // 한글 폰트 로드(범례/그룹 헤더 등 Skia 텍스트용)
                await EnsureKoreanTypefaceAsync();

                _invalidateAfterRender = false;
                _canvas?.Invalidate();
            }
        }

        [JSInvokable]
        public Task OnDiagramScrollResized(double w, double h)
        {
            if (!UseDockStatusDiagram) return Task.CompletedTask;

            // 미세 흔들림 무시
            if (Math.Abs(_lastScrollW - w) < 0.5 && Math.Abs(_lastScrollH - h) < 0.5)
                return Task.CompletedTask;

            _lastScrollW = w;
            _lastScrollH = h;

            RequestDiagramRelayout();
            return Task.CompletedTask;
        }

        [JSInvokable]
        public Task OnDprChanged(double newDpr)
        {
            _dpr = newDpr;
            _pendingFit = true;
            _canvas?.Invalidate();
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            try { if (_wheelModule is not null) await _wheelModule.InvokeVoidAsync("unblockWheel", _host); } catch { }
            try { if (_wheelModule is not null) await _wheelModule.DisposeAsync(); } catch { }
            if (_dprHandle is not null) await _dprHandle.InvokeVoidAsync("dispose");
            if (_dprModule is not null) await _dprModule.DisposeAsync();
            try { if (_resizeHandle is not null) await _resizeHandle.InvokeVoidAsync("dispose"); } catch { }
            try { if (_resizeModule is not null) await _resizeModule.DisposeAsync(); } catch { }
            try { _alarmCts?.Cancel(); } catch { }
            try { _alarmCts?.Dispose(); } catch { }


            StopAlarmPolling();
            StopStatsPolling();
            StopDockStatusPolling();
            StopMockDockStatusPolling();

            _loadCts.Cancel();
            _loadCts.Dispose();
            _selfRef?.Dispose();

            try { _koreanTypeface?.Dispose(); } catch { }
        }

        public void Dispose() => _animCts?.Cancel();

        private static SKRect ToRect((double Left, double Top, double Right, double Bottom) b)
        {
            float left = (float)Math.Min(b.Left, b.Right);
            float right = (float)Math.Max(b.Left, b.Right);
            float top = (float)Math.Min(b.Top, b.Bottom);
            float bottom = (float)Math.Max(b.Top, b.Bottom);
            return new SKRect(left, top, right, bottom);
        }

        private void FitIfReady(int viewW, int viewH, float margin = 24f)
        {
            if (_sceneBounds.IsEmpty || viewW <= 0 || viewH <= 0) { _pendingFit = true; return; }
            float m = margin + ComputeLabelExtraMarginPx();
            var (s, px, py) = _renderer.ComputeFit(_sceneBounds, viewW, viewH, m);
            _zoom = s; _panX = px; _panY = py; _pendingFit = false;
        }

        private float ComputeLabelExtraMarginPx()
        {
            if (!_renderer.DrawStandaloneLaneLabels) return 0f;

            var labels = _renderer.ExportLaneLabels();
            int maxChars = labels.Count > 0 ? labels.Values.Max(s => (s ?? "").Length) : 0;

            float fontPx = _renderer.StandaloneLabelFontPx;
            float textPx = maxChars > 0 ? maxChars * fontPx * 0.62f : 2.2f * fontPx;
            float gapPx = _renderer.StandaloneLaneLeaderGapPx;
            float marginPx = _renderer.StandaloneLaneLabelMarginPx;

            return marginPx + gapPx + textPx + 6f;
        }

        // ─────────────────────────────────────────────────────────────
        // 포인터/휠
        // ─────────────────────────────────────────────────────────────
        private void OnPointerDown(PointerEventArgs e)
        {
            if (UseDockStatusDiagram) return;
            _dragging = true; _renderer.IsInteracting = true;
            _last = (e.OffsetX * _dpr, e.OffsetY * _dpr);
            _down = _last;
            _anchorWorld = null;
        }

        private async Task OnPointerMove(PointerEventArgs e)
        {
            if (UseDockStatusDiagram) return;
            if (!_dragging) return;
            var cx = e.OffsetX * _dpr; var cy = e.OffsetY * _dpr;
            _panX += cx - _last.x; _panY += cy - _last.y; _last = (cx, cy);
            _renderer.HighlightDockIndex = null;

            if (_drawPending) return;
            _drawPending = true;
            await Task.Yield();
            _canvas?.Invalidate();
            _drawPending = false;
        }

        private async void OnPointerUp(PointerEventArgs e)
        {
            if (UseDockStatusDiagram) return;
            _dragging = false;
            _renderer.IsInteracting = false;

            var up = (e.OffsetX * _dpr, e.OffsetY * _dpr);
            double dx = Math.Abs(up.Item1 - _down.x);
            double dy = Math.Abs(up.Item2 - _down.y);
            bool asClick = dx < 5 && dy < 5;

            if (asClick && e.ShiftKey)
            {
                var world = DeviceToWorld(up.Item1, up.Item2);
                float tolWorld = (float)(12.0 / _zoom);

                var key = _renderer.HitTestStandaloneLaneKey(world, tolWorld);
                if (key is not null)
                {
                    var cur = _renderer.GetLaneLabelByKey(key) ?? "";
                    var input = await JS.InvokeAsync<string?>("prompt", $"Label for {key}", cur);
                    if (input is not null)
                    {
                        _renderer.SetLaneLabel(key, input);
                        await SaveLaneLabelsAsync();
                        _canvas?.Invalidate();
                    }
                }
            }

            _canvas?.Invalidate();
        }

        private CancellationTokenSource? _wheelDebounce;

        private void OnWheel(WheelEventArgs e)
        {
            if (UseDockStatusDiagram) return;
            _renderer.IsInteracting = true;

            double factor = e.DeltaY < 0 ? 1.1 : 1.0 / 1.1;
            double oldZoom = _zoom;
            double newZoom = Math.Clamp(oldZoom * factor, MIN_ZOOM, MAX_ZOOM);

            double mx = e.OffsetX * _dpr, my = e.OffsetY * _dpr;
            double wx = (mx - _panX) / oldZoom;
            double wy = (_panY + _viewH - my) / oldZoom;

            _panX = mx - wx * newZoom;
            _panY = my + wy * newZoom - _viewH;
            _zoom = newZoom;

            if (_drawPending) return;
            _drawPending = true;

            _ = InvokeAsync(async () =>
            {
                await Task.Yield();
                _canvas?.Invalidate();
                _drawPending = false;
            });

            _wheelDebounce?.Cancel();
            _wheelDebounce = new CancellationTokenSource();
            var token = _wheelDebounce.Token;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(120, token); }
                catch { return; }
                if (!token.IsCancellationRequested)
                    await InvokeAsync(() => { _renderer.IsInteracting = false; _canvas?.Invalidate(); });
            });
        }

        // ─────────────────────────────────────────────────────────────
        // 그리기
        // ─────────────────────────────────────────────────────────────
        private void OnPaint(SKPaintSurfaceEventArgs e)
        {
            var info = e.Info;
            bool resized = (info.Width != _viewW) || (info.Height != _viewH);
            _canvasPixelWidth = info.Width; _canvasPixelHeight = info.Height;

            if (_firstPaint)
            {
                float m = 24f + ComputeLabelExtraMarginPx();
                var (z, px, py) = _renderer.ComputeFit(_sceneBounds, _canvasPixelWidth, _canvasPixelHeight, m);
                _zoom = z; _panX = px; _panY = py; _firstPaint = false;
            }
            else if (resized)
            {
                if (_anchorDockRectWorld is SKRect wr && !wr.IsEmpty)
                {
                    int marginPx = 30;
                    double availW = Math.Max(1, info.Width - 2 * marginPx);
                    double availH = Math.Max(1, info.Height - 2 * marginPx);
                    double zX = availW / wr.Width;
                    double zY = availH / wr.Height;
                    _zoom = Math.Clamp(Math.Min(zX, zY), MIN_ZOOM, MAX_ZOOM);
                    var cx = (wr.Left + wr.Right) * 0.5;
                    var cy = (wr.Top + wr.Bottom) * 0.5;
                    _panX = info.Width * 0.5 - cx * _zoom;
                    _panY = cy * _zoom - info.Height * 0.5;
                }
                else if (_anchorWorld is SKPoint aw)
                {
                    _panX = info.Width * 0.5 - aw.X * _zoom;
                    _panY = aw.Y * _zoom - info.Height * 0.5;
                }
                else if (_pendingFit)
                {
                    FitIfReady(info.Width, info.Height, 20f);
                }

                else
                {
                    // ✅ 추가: “현재 화면 중심”이 가리키던 월드 좌표를 유지
                    // 리사이즈 전 뷰포트 크기
                    var oldW = _viewW;
                    var oldH = _viewH;

                    if (oldW > 0 && oldH > 0 && _zoom > 0)
                    {
                        // 화면 중심(픽셀)에서의 월드 좌표
                        // wheel 계산식과 동일한 좌표계:
                        // wx = (mx - panX) / zoom
                        // wy = (panY + viewH - my) / zoom
                        double mx = oldW * 0.5;
                        double my = oldH * 0.5;

                        double wcx = (mx - _panX) / _zoom;
                        double wcy = (_panY + oldH - my) / _zoom;

                        // 새 뷰포트에서도 같은 월드 중심이 화면 중앙으로 오게 pan 재계산
                        _panX = info.Width * 0.5 - wcx * _zoom;
                        _panY = wcy * _zoom - info.Height * 0.5;
                    }
                }
            }

            _viewW = info.Width; _viewH = info.Height;

            // ✅ 도식 모드면 DXF 대신 도식 렌더링
            if (UseDockStatusDiagram)
            {
                var canvas = e.Surface.Canvas;
                canvas.Clear(new SKColor(0x12, 0x12, 0x12));

                // 도크 목록: DXF에서 얻는 이름을 우선 사용(없으면 상태맵 키)
                var dockNames = _renderer.GetDockNames();
                var list = (dockNames != null && dockNames.Count > 0)
                    ? dockNames
                    : _dockRunStateByDock.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

                if (!string.IsNullOrWhiteSpace(FocusDockId))
                {
                    var focus = DockLabelNormalization.CanonLabel(FocusDockId);
                    var filtered = list
                        .Where(n => string.Equals(DockLabelNormalization.CanonLabel(n), focus, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    // focus가 목록에 없으면(오타/로딩 전) 전체 유지
                    if (filtered.Count > 0) list = filtered;
                }

                var models = list.Select(BuildDockDiagramModel).ToList();

                // ✅ 스크롤 뷰포트(보이는 영역) 높이를 기준으로 레이아웃 판단
                var viewportCssH = (_lastScrollH > 0.5) ? _lastScrollH : (info.Height / Math.Max(1.0, _dpr));
                var viewportPxH = (int)Math.Max(1, Math.Round(viewportCssH * Math.Max(1.0, _dpr)));

                // 1) “필요 높이”는 viewportPxH 기준으로만 계산 (캔버스가 커져도 루프 방지)
                var neededPx = _dockDiagramRenderer.MeasureHeight(info.Width, viewportPxH, models, showLegend: false);

                // 2) 캔버스 CSS 높이는 “뷰포트 높이 vs 필요 높이”로만 결정
                var cssViewH = viewportPxH / Math.Max(1.0, _dpr);
                var cssNeedH = neededPx / Math.Max(1.0, _dpr);
                var cssTarget = Math.Max(cssViewH, cssNeedH);

                if (!_invalidateAfterRender)
                {
                    if (_diagramCanvasHeightCssPx <= 0 || Math.Abs(_diagramCanvasHeightCssPx - cssTarget) > 1.0)
                    {
                        _diagramCanvasHeightCssPx = cssTarget;
                        _invalidateAfterRender = true;
                        _ = InvokeAsync(StateHasChanged);
                    }
                }

                // 3) 실제 그리기는 “현재 캔버스 크기(info.Height)”로 하되,
                //    레이아웃 판단은 viewportPxH 기준으로 하도록 renderer에 같이 넘김
                _dockDiagramRenderer.Render(canvas, info.Width, info.Height, viewportPxH, models, _dockDiagramView, showLegend: false);
                _stat = $"dock-diagram={_dockDiagramView}, docks={models.Count}";
                return;
            }

            // 기존 DXF 렌더
            _renderer.Render(e.Surface.Canvas, info.Width, info.Height, _zoom, _panX, _panY, _time);
            _stat = $"zoom={_zoom:0.00}, pan=({_panX:0},{_panY:0}) size={info.Width}x{info.Height}";
        }
        public void RequestDiagramRelayout()
        {
            if (!UseDockStatusDiagram) return;

            _diagramCanvasHeightCssPx = 0;   // ✅ CanvasHeightCss가 "100%"로 돌아감
            _invalidateAfterRender = true;  // ✅ CSS 반영 후 다시 그리도록
            _ = InvokeAsync(StateHasChanged);
        }

        private SKPoint DeviceToWorld(double px, double py)
        {
            double wx = (px - _panX) / _zoom;
            double wy = (_panY + _viewH - py) / _zoom;
            return new SKPoint((float)wx, (float)wy);
        }

        // ─────────────────────────────────────────────────────────────
        // 키보드
        // ─────────────────────────────────────────────────────────────
        private void OnKeyDown(KeyboardEventArgs e)
        {
            if ((e.Key?.Equals("r", StringComparison.OrdinalIgnoreCase) ?? false) || e.Code == "KeyR")
            {
                var (z, px, py) = _renderer.ComputeFit(_sceneBounds, _canvasPixelWidth, _canvasPixelHeight);
                _zoom = z; _panX = px; _panY = py; _renderer.HighlightDockIndex = null; _canvas?.Invalidate(); return;
            }
            if (e.Key?.Equals("a", StringComparison.OrdinalIgnoreCase) == true)
            { if (_animating) StopAnimation(); else _ = StartAnimation(300); return; }

            int? digit = e.Key is not null && e.Key.Length == 1 && char.IsDigit(e.Key[0]) ? (int?)(e.Key[0] - '0')
                        : (e.Code != null && e.Code.StartsWith("Numpad") && e.Code.Length == 7 && char.IsDigit(e.Code[6]) ? (int?)(e.Code[6] - '0') : null);

            if (digit is int d)
            { _renderer.OverrideColor = (d == 0) ? null : _palette[d % _palette.Length]; _canvas?.Invalidate(); return; }

            if (string.Equals(e.Key, "Escape", StringComparison.OrdinalIgnoreCase))
            { _renderer.OverrideColor = null; _canvas?.Invalidate(); }
        }

        // ─────────────────────────────────────────────────────────────
        // 애니메이션
        // ─────────────────────────────────────────────────────────────
        private async Task StartAnimation(double fps = 60)
        {
            if (_animating) return; _renderer.OverrideColor = null; _animating = true;
            _animCts?.Cancel(); _animCts = new CancellationTokenSource();
            var tkn = _animCts.Token;
            var period = TimeSpan.FromMilliseconds(1000.0 / fps);
            var timer = new PeriodicTimer(period);

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (await timer.WaitForNextTickAsync(tkn))
                {
                    _time = sw.Elapsed.TotalSeconds;
                    await InvokeAsync(() => _canvas?.Invalidate());
                }
            }
            catch (OperationCanceledException) { }
            finally { _animating = false; }
        }

        private void StopAnimation() => _animCts?.Cancel();

        // ─────────────────────────────────────────────────────────────
        // 공개 API
        // ─────────────────────────────────────────────────────────────
        public DockInfo? GetDockInfo(string dockName)
        {
            if (_renderer.TryGetDockInfo(dockName, out var dto))
            {
                return new DockInfo
                {
                    Name = dto.Name,
                    Index = dto.Index,
                    Left = dto.Bounds.Left,
                    Top = dto.Bounds.Top,
                    Right = dto.Bounds.Right,
                    Bottom = dto.Bounds.Bottom,
                    Cx = dto.Center.X,
                    Cy = dto.Center.Y,
                    InsertCount = dto.InsertCount,
                    HeaderCount = dto.HeaderCount,
                    LaneCount = dto.LaneCount,
                    TboxCount = dto.TboxCount
                };
            }
            return null;
        }

        public async Task HighlightAndEmitAsync(string dockName)
        {
            ZoomToDockByName(dockName, marginPx: 30);
            HighlightDockInRenderer(dockName);
            var info = GetDockInfo(dockName);
            if (info is not null && DockInfoChanged.HasDelegate)
                await DockInfoChanged.InvokeAsync(info);
        }

        public void HighlightDockInRenderer(string? dockName)
        {
            int? idx = null;
            if (!string.IsNullOrWhiteSpace(dockName)
                && dockName.StartsWith("DOCK", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(dockName.AsSpan(4), out var n)) idx = n;

            _renderer.HighlightDockIndex = idx;
            _canvas?.Invalidate();
        }

        public void ZoomToDockByName(string? dockName, int marginPx = 30)
        {
            if (string.IsNullOrWhiteSpace(dockName) || _viewW <= 0 || _viewH <= 0) return;
            if (!_renderer.TryGetDockWorldRect(dockName, out var rect) || rect.IsEmpty) return;

            double availW = Math.Max(1, _viewW - 2 * marginPx);
            double availH = Math.Max(1, _viewH - 2 * marginPx);
            double zoomX = availW / rect.Width;
            double zoomY = availH / rect.Height;
            double targetZoom = Math.Clamp(Math.Min(zoomX, zoomY), MIN_ZOOM, MAX_ZOOM);

            double cx = (rect.Left + rect.Right) * 0.5;
            double cy = (rect.Top + rect.Bottom) * 0.5;

            _zoom = targetZoom;
            _panX = _viewW * 0.5 - cx * _zoom;
            _panY = cy * _zoom - _viewH * 0.5;

            _anchorWorld = new SKPoint((float)cx, (float)cy);
            _anchorDockRectWorld = rect;

            _canvas?.Invalidate();
        }

        // Offline: [도크 끊김 판정 - 원인 3] 통신 끊김 (서버 TCP 끊김 또는 일정 시간 heartbeat 없음)
        private enum ElementState { Stop, Run, Error, Emergency, Offline }
        private enum DockDiagramView { Full, ConnectionsOnly } // (유지해도 되고, ConnectionsOnly만 써도 됨)

        private sealed class DockDiagramModel
        {
            // ✅ 정렬 기준이 되는 ID (예: DOCK1, DOCK2 ...)
            public required string DockId { get; init; }

            // ✅ 상태 조회/라벨 조회용 CanonLabel (예: B01, C07 ...)
            public required string DockKey { get; init; }

            public ElementState Conv1_MainTop { get; init; }
            public ElementState Conv2_MainMid { get; init; }
            public ElementState Conv3_MainBot { get; init; }
            public ElementState Conv4_CenterDown { get; init; }
            public ElementState Conv5_LeftDiag { get; init; }
            public ElementState Conv6_LeftDown { get; init; }
            public ElementState Conv7_RightDiag { get; init; }
            public ElementState Conv8_RightDown { get; init; }

            public ElementState Encoder { get; init; }
            public string EncoderLabel { get; init; } = "ENC";
            public ElementState Plc { get; init; }
            public ElementState ReadSystem { get; init; }
        }


        private sealed class DockStatusDiagramRenderer
        {
            // 도크 라벨 표시(오버라이드)용 리졸버
            public Func<string, string>? ResolveDockLabel { get; set; }

            // ===== Layout (그룹별 "열(컬럼) 고정" + "너비 꽉 채우기") =====
            private readonly Dictionary<string, int> GroupColumns = new(StringComparer.OrdinalIgnoreCase)
            {
                ["B"] = 36,
                ["C"] = 36,
                ["D"] = 36,
                ["E"] = 36,
            };

            // ✅ 기본(미등록 그룹) 열 수
            private int _defaultCols = 36;
            public int DefaultCols
            {
                get => _defaultCols;
                set => _defaultCols = Math.Max(1, value);
            }

            // ✅ 그룹별 열 수 일괄 적용
            public void SetGroupColumns(IReadOnlyDictionary<string, int> map)
            {
                if (map is null) return;

                foreach (var (k, v) in map)
                {
                    var key = (k ?? "").Trim();
                    if (string.IsNullOrEmpty(key)) continue;
                    GroupColumns[key] = Math.Max(1, v);
                }
            }

            // (선택) 현재 값 읽기용
            public IReadOnlyDictionary<string, int> GetGroupColumnsSnapshot()
                => new Dictionary<string, int>(GroupColumns, StringComparer.OrdinalIgnoreCase);

            public void ApplyTypefaces(SKTypeface? regular, SKTypeface? bold)
            {
                // regular
                if (regular is not null)
                {
                    _groupSub.Typeface = regular;
                    _legendText.Typeface = regular;
                    _legendTextSmall.Typeface = regular;

                    // 아래 페인트가 존재한다면 같이 적용 (파일에 있는 필드명에 맞춰 조정)
                    _dockLabelText.Typeface = regular;
                    _encoderTagText.Typeface = regular;
                }

                // bold
                if (bold is not null)
                {
                    _groupTitle.Typeface = bold;
                    _legendTitle.Typeface = bold;

                    // 도크 라벨을 굵게 쓰고 싶으면 bold로 덮어도 됨
                    _dockLabelText.Typeface = bold;

                    _encoderTagText.Typeface = bold;
                    _plcTagText.Typeface = bold;

                }
            }

            // 기존(기준) 디자인 값
            private const float BasePad = 14f;
            private const float BaseTileW = 120f;
            private const float BaseTileH = 260f;
            private const float BaseGroupHeaderH = 28f;
            private const float BaseGroupGap = 10f;

            private const float MaxPaintScale = 8f;       // 그룹 헤더/범례/타일 테두리 등(텍스트+선) 상한
            private const float MaxDockLabelScale = 8f;   // 타일 상단 DockId 라벨 텍스트 상한

            private const float MaxIconStrokePx = 30f;      // 아이콘 내부 외곽선(흰색) 최대 두께(px)
            private const float MaxIconDarkStrokePx = 60f;  // 아이콘 내부 진한선 최대 두께(px)
            private const float MaxEncoderTextPx = 140f;    // 아이콘 내부 E-1 텍스트 최대 픽셀 크기
            private const float MaxPlcTextPx = 140f;        // 아이콘 내부 PLC 텍스트 최대 픽셀 크기

            private static float ClampMax(float v, float max) => (v > max) ? max : v;

            private const float TileAspect = BaseTileH / BaseTileW; // 260/120

            // ===== Colors =====
            private static SKColor StateFill(ElementState st) => st switch
            {
                ElementState.Run => new SKColor(0x1B, 0xC8, 0x73),       // green
                ElementState.Error => new SKColor(0xFF, 0xD2, 0x00),     // yellow
                ElementState.Emergency => new SKColor(0xFF, 0x2A, 0x2A), // red
                ElementState.Offline => new SKColor(0x5A, 0x5F, 0x66),   // offline (dark gray)
                _ => new SKColor(0xD8, 0xD8, 0xD8),                      // stop (light gray)
            };

            private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
            private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = SKColors.White };
            private readonly SKPaint _strokeDark = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(0x10, 0x2A, 0x3A) };

            private readonly SKPaint _tileBg = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0x1D, 0x1D, 0x1D) };
            private readonly SKPaint _tileBorder = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(255, 255, 255, 60) };

            private readonly SKPaint _groupTitle = new() { IsAntialias = true, Color = SKColors.White };
            private readonly SKPaint _groupSub = new() { IsAntialias = true, Color = new SKColor(220, 220, 220) };
            private readonly SKPaint _groupLine = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(255, 255, 255, 40) };

            private readonly SKPaint _dockLabelText = new() { IsAntialias = true, Color = SKColors.White };
            private readonly SKPaint _dockLabelBg = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0, 0, 0, 150) };

            private readonly SKPaint _legendTitle = new() { IsAntialias = true, Color = SKColors.White };
            private readonly SKPaint _legendText = new() { IsAntialias = true, Color = SKColors.White };
            private readonly SKPaint _legendTextSmall = new() { IsAntialias = true, Color = new SKColor(230, 230, 230) };

            private readonly SKPaint _encoderTagText = new() { IsAntialias = true, TextSize = 12, Color = SKColors.Black };
            private readonly SKPaint _plcTagText = new()
            {
                IsAntialias = true,
                TextSize = 9f,              // 유닛 좌표 기준(Scale(s) 영향 받음)
                Color = SKColors.Black,
                TextAlign = SKTextAlign.Center
            };


            public void SetTypeface(SKTypeface? typeface)
            {
                // 한글 포함 텍스트가 들어가는 paint들에 동일 타입페이스 적용
                _groupTitle.Typeface = typeface;
                _groupSub.Typeface = typeface;

                _dockLabelText.Typeface = typeface;

                _legendTitle.Typeface = typeface;
                _legendText.Typeface = typeface;
                _legendTextSmall.Typeface = typeface;

                _encoderTagText.Typeface = typeface;
                _plcTagText.Typeface = typeface;
            }

            public float MeasureHeight(int canvasWidthPx, int viewportHeightPx, IReadOnlyList<DockDiagramModel> docks, bool showLegend)
            {
                if (docks.Count == 0) return BasePad;
                var layout = BuildLayout(canvasWidthPx, viewportHeightPx, docks, showLegend);
                return layout.TotalHeightPx;
            }

            public void Render(
            SKCanvas canvas,
            int canvasWidthPx,
            int canvasHeightPx,     // 실제 캔버스 높이(스크롤로 커질 수 있음)
            int viewportHeightPx,   // ✅ 레이아웃 판단 기준 높이(보이는 영역)
            IReadOnlyList<DockDiagramModel> docks,
            DockDiagramView view,
            bool showLegend)
            {
                if (docks.Count == 0) return;

                // ✅ 레이아웃은 viewportHeightPx 기준
                var layout = BuildLayout(canvasWidthPx, viewportHeightPx, docks, showLegend);

                // 공통 텍스트 스케일(가장 큰 타일 기준)
                float globalK = layout.GlobalScale;
                float paintK = ClampMax(globalK, MaxPaintScale);     // ✅ “페인트만” 클램프
                ApplyGlobalPaintScale(paintK);

                float y = layout.OffsetY + layout.Pad;

                foreach (var sec in layout.Sections)
                {
                    var headerRect = SKRect.Create(layout.OffsetX + layout.Pad, y, canvasWidthPx - 2 * layout.Pad, sec.HeaderH);
                    DrawGroupHeader(canvas, headerRect, sec.GroupKey, sec.Count, sec.Cols);
                    y += sec.HeaderH;

                    y += layout.Pad;

                    for (int i = 0; i < sec.Items.Count; i++)
                    {
                        int c = i % sec.Cols;
                        int r = i / sec.Cols;

                        float x = layout.OffsetX + layout.Pad + c * (sec.TileW + layout.Pad);
                        float ty = y + r * (sec.TileH + layout.Pad);

                        var tile = SKRect.Create(x, ty, sec.TileW, sec.TileH);
                        DrawDockStatus1Tile(canvas, tile, sec.Items[i], view, sec.TileScale);
                    }

                    int rows = (int)Math.Ceiling(sec.Count / (double)sec.Cols);
                    y += rows * sec.TileH + (rows - 1) * layout.Pad;
                    y += layout.Pad;
                    y += layout.GroupGap;
                }

                // 하단 범례
                if (showLegend)
                {
                    // ✅ 페이지(캔버스) 너비를 끝까지 사용
                    var legendW = Math.Max(1f, canvasWidthPx - 2 * layout.Pad);
                    var legendRect = SKRect.Create(layout.Pad, y, legendW, layout.LegendH);
                    DrawLegend(canvas, legendRect, globalK);
                }
            }

            // ===== Layout builder =====
            private Layout BuildLayout(int canvasWidthPx, int viewportHeightPx, IReadOnlyList<DockDiagramModel> docks, bool showLegend)
            {
                // ✅ 1) 더 촘촘한 간격
                float pad = 6f;          // 기존 BasePad(14)보다 타이트
                float groupGap = 0f;

                // 정렬/필터는 기존 로직 그대로 유지
                string SortText(DockDiagramModel d) => (d.DockId ?? "").Trim();
                int SortNum(DockDiagramModel d) => DockSortKey(SortText(d));

                var items = docks
                    .Where(d => d is not null)
                    .OrderBy(d => SortNum(d))
                    .ThenBy(d => SortText(d), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int count = items.Count;
                if (count == 0)
                {
                    return new Layout
                    {
                        Pad = pad,
                        GroupGap = groupGap,
                        LegendH = 0f,
                        TotalHeightPx = pad,
                        GlobalScale = 1f,
                        Sections = new List<Section>(),
                        OffsetX = 0f,
                        OffsetY = 0f
                    };
                }

                int cols = Math.Max(1, DefaultCols);
                cols = Math.Min(cols, count);
                int rows = (int)Math.Ceiling(count / (double)cols);

                // ✅ 2) 폭은 기존 공식대로 (좌우 padding 포함해서 “꽉” 맞춤)
                float tileW = ComputeTileW(canvasWidthPx, cols, pad);

                // ✅ 3) 높이도 화면을 채우도록(Stretch) 계산
                // Render의 높이 누적: total = rows*tileH + (rows+2)*pad  (헤더/그룹갭=0, legend=false)
                float tileHFill = Math.Max(1f, (viewportHeightPx - (rows + 2) * pad) / Math.Max(1, rows));

                // 너무 작아지면 보기 힘드니, 이 경우는 “스크롤 모드”로 돌림
                const float MinTileHForFill = 70f; // 취향값(조절 가능)

                bool fillMode = (!showLegend) && (tileHFill >= MinTileHForFill);

                float tileH;
                float totalHeight;

                if (fillMode)
                {
                    tileH = tileHFill;
                    totalHeight = viewportHeightPx; // ✅ 캔버스가 아니라 “보이는 영역”을 채우는 높이로 고정
                }
                else
                {
                    tileH = Math.Max(1f, tileW * TileAspect);
                    totalHeight = pad + pad + rows * tileH + Math.Max(0, rows - 1) * pad + pad + groupGap;
                }

                // ✅ 4) 텍스트/선 스케일: tileW 뿐 아니라 tileH도 고려(늘어났을 때 과대해지지 않게)
                const float RefTileW = 190f;
                const float RefTileH = 190f * 2.35f;
                float tileScale = Math.Min(tileW / RefTileW, tileH / RefTileH);
                tileScale = Math.Clamp(tileScale, 0.55f, 1.35f);

                var sec = new Section
                {
                    GroupKey = "",
                    Items = items,
                    Count = count,
                    Cols = cols,
                    Rows = rows,
                    TileW = tileW,
                    TileH = tileH,
                    TileScale = tileScale,
                    HeaderH = 0f
                };

                // ✅ 5) 중앙 정렬: “스크롤 모드 + 내용이 화면보다 작을 때”만 offset 부여
                float offsetX = 0f;
                float offsetY = 0f;

                // 가로는 tileW가 이미 폭을 꽉 채우는 공식이라 보통 0인데,
                // 안전하게 “남는 픽셀”이 있으면 가운데 정렬
                float gridW = cols * tileW + (cols - 1) * pad + 2 * pad;
                float remainX = canvasWidthPx - gridW;
                if (remainX > 1f) offsetX = remainX * 0.5f;

                if (!fillMode)
                {
                    float remainY = viewportHeightPx - totalHeight;
                    if (remainY > 1f) offsetY = remainY * 0.5f; // ✅ 중앙정렬도 viewport 기준
                }

                float globalScale = tileScale;
                float paintK = ClampMax(globalScale, MaxPaintScale);
                float legendH = showLegend ? Math.Max(80f, 120f * paintK) : 0f;

                if (showLegend)
                {
                    // legend=true인 경우는 기존 누적 로직대로 totalHeight 갱신(여기선 거의 안 쓰는 상태)
                    totalHeight += pad + legendH + pad;
                }

                return new Layout
                {
                    Pad = pad,
                    GroupGap = groupGap,
                    LegendH = legendH,
                    TotalHeightPx = totalHeight,
                    GlobalScale = globalScale,
                    Sections = new List<Section> { sec },
                    OffsetX = offsetX,
                    OffsetY = offsetY
                };
            }


            private static float ComputeTileW(int canvasWidthPx, int cols, float pad)
            {
                // width = cols*tileW + (cols+1)*pad  => tileW = (width - (cols+1)*pad)/cols
                var w = Math.Max(1f, canvasWidthPx);
                var t = (w - (cols + 1) * pad) / cols;
                return Math.Max(1f, t);
            }

            private int GetColumnsForGroup(string groupKey, int count)
            {
                if (count <= 0) return 1;

                int cols = GroupColumns.TryGetValue(groupKey, out var c) ? c : DefaultCols;
                cols = Math.Max(1, cols);

                // 도크 수보다 큰 열 수는 의미가 없어서 클램프(원치 않으면 제거 가능)
                return Math.Min(cols, count);
            }

            private static List<string> BuildGroupOrder(IEnumerable<string> keys)
            {
                var preferred = new List<string> { "B", "C", "D", "E" };
                var rest = keys.Where(k => !preferred.Contains(k, StringComparer.OrdinalIgnoreCase))
                               .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                               .ToList();

                var result = preferred.Where(k => keys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
                result.AddRange(rest);
                return result;
            }

            private static string ExtractGroupId(string dockId)
            {
                if (string.IsNullOrWhiteSpace(dockId)) return "?";
                var ch = dockId.Trim()[0];
                return char.IsLetter(ch) ? char.ToUpperInvariant(ch).ToString() : "?";
            }

            private static int DockSortKey(string dockId)
            {
                if (string.IsNullOrWhiteSpace(dockId)) return int.MaxValue;

                int n = 0;
                bool any = false;
                foreach (var ch in dockId)
                {
                    if (char.IsDigit(ch))
                    {
                        any = true;
                        n = n * 10 + (ch - '0');
                    }
                }
                return any ? n : int.MaxValue;
            }

            // ===== Paint scaling =====
            private void ApplyGlobalPaintScale(float k)
            {
                // stroke
                _stroke.StrokeWidth = Math.Max(1f, 2f * k);
                _strokeDark.StrokeWidth = Math.Max(1f, 3f * k);
                _tileBorder.StrokeWidth = Math.Max(1f, 2f * k);
                _groupLine.StrokeWidth = Math.Max(1f, 2f * k);

                // group header fonts
                _groupTitle.TextSize = Math.Max(16f, 18f * k);
                _groupSub.TextSize = Math.Max(12f, 12f * k);

                // legend fonts
                _legendTitle.TextSize = Math.Max(16f, 18f * k);
                _legendText.TextSize = Math.Max(13f, 14f * k);
                _legendTextSmall.TextSize = Math.Max(12f, 13f * k);
            }

            // ===== Group header =====
            private void DrawGroupHeader(SKCanvas canvas, SKRect r, string groupKey, int count, int cols)
            {
                // ✅ 단일(시퀀셜) 레이아웃에서는 그룹 헤더를 숨김
                if (string.IsNullOrWhiteSpace(groupKey)) return;

                canvas.DrawLine(r.Left, r.Bottom, r.Right, r.Bottom, _groupLine);

                var title = $"{groupKey} 그룹";
                canvas.DrawText(title, r.Left, r.MidY + (_groupTitle.TextSize * 0.35f), _groupTitle);

                var meta = $"도크 {count}개 · 열 {cols} 고정";
                float metaW = _groupSub.MeasureText(meta);
                canvas.DrawText(meta, r.Right - metaW, r.MidY + (_groupSub.TextSize * 0.35f), _groupSub);
            }

            // ===== Tile =====
            private void DrawDockStatus1Tile(SKCanvas canvas, SKRect tile, DockDiagramModel m, DockDiagramView view, float tileScale)
            {
                float corner = Math.Max(8f, 12f * tileScale);

                // 배경 + 테두리
                //canvas.DrawRoundRect(tile, corner, corner, _tileBg);
                //canvas.DrawRoundRect(tile, corner, corner, _tileBorder);

                // 타일 라벨 텍스트(타일 비율로 스케일)
                var labelK = MathF.Min(tileScale, MaxDockLabelScale);
                _dockLabelText.TextSize = Math.Max(12f, 14f * labelK);

                DrawDockLabel(canvas, tile, m.DockKey, tileScale);
                DrawDockStatus1Icon(canvas, tile, m, view, tileScale);
            }

            private void DrawDockLabel(SKCanvas canvas, SKRect tile, string dockId, float tileScale)
            {
                var text = ResolveDockLabel?.Invoke(dockId ?? "") ?? (dockId ?? "");
                float tw = _dockLabelText.MeasureText(text);
                float padX = 6f * tileScale;
                float h = 20f * tileScale;
                float r = 8f * tileScale;

                var bg = SKRect.Create(tile.Left + 8f * tileScale, tile.Top + 8f * tileScale, tw + padX * 2f, h);
                canvas.DrawRoundRect(bg, r, r, _dockLabelBg);
                canvas.DrawText(text, bg.Left + padX, bg.MidY + (_dockLabelText.TextSize * 0.35f), _dockLabelText);
            }

            private void DrawDockStatus1Icon(SKCanvas canvas, SKRect tile, DockDiagramModel m, DockDiagramView view, float tileScale)
            {
                // 템플릿 좌표
                const float U_W = 100f;
                const float U_H = 230f;

                canvas.Save();

                // 라벨 영역 만큼 위쪽 여백, 나머지에 맞춰 스케일
                var inner = SKRect.Create(
                    tile.Left + 4f * tileScale,
                    tile.Top + 22f * tileScale,
                    tile.Width - 8f * tileScale,
                    tile.Height - 26f * tileScale);

                float s = Math.Min(inner.Width / U_W, inner.Height / U_H);
                float ox = inner.Left + (inner.Width - U_W * s) * 0.5f;
                float oy = inner.Top + (inner.Height - U_H * s) * 0.5f;

                canvas.Translate(ox, oy);
                canvas.Scale(s);

                // C group is drawn vertically mirrored to match the physical layout.
                bool flipY = !string.IsNullOrWhiteSpace(m.DockKey) &&
                             char.ToUpperInvariant(m.DockKey.Trim()[0]) == 'C';

                // 1) 도형(Shape) 먼저 그림: flipY면 반전된 좌표계에서
                canvas.Save();
                if (flipY)
                {
                    canvas.Translate(0, U_H);
                    canvas.Scale(1, -1);
                }

                // ✅ 아이콘 내부(스케일 좌표계)에서 선/텍스트가 과하게 두꺼워지는 것만 완화
                float savedStroke = _stroke.StrokeWidth;
                float savedStrokeDark = _strokeDark.StrokeWidth;
                float savedEncTs = _encoderTagText.TextSize;
                float savedPlcTs = _plcTagText.TextSize;

                if (s > 0.0001f)
                {
                    float maxStrokeUnit = MaxIconStrokePx / s;
                    if (_stroke.StrokeWidth > maxStrokeUnit) _stroke.StrokeWidth = maxStrokeUnit;

                    float maxDarkUnit = MaxIconDarkStrokePx / s;
                    if (_strokeDark.StrokeWidth > maxDarkUnit) _strokeDark.StrokeWidth = maxDarkUnit;

                    float maxEncUnit = MaxEncoderTextPx / s;
                    if (_encoderTagText.TextSize > maxEncUnit) _encoderTagText.TextSize = maxEncUnit;

                    float maxPlcUnit = MaxPlcTextPx / s;
                    if (_plcTagText.TextSize > maxPlcUnit) _plcTagText.TextSize = maxPlcUnit;
                }

                DrawIconShapesOnly(canvas, m, view);
                DrawIconTextsOnly(canvas, m, view, flipY); // 이제 flip 포함 좌표계에서 “위치 기준” 계산됨
                canvas.Restore();




                _stroke.StrokeWidth = savedStroke;
                _strokeDark.StrokeWidth = savedStrokeDark;
                _encoderTagText.TextSize = savedEncTs;
                _plcTagText.TextSize = savedPlcTs;

                canvas.Restore();
            }
            private static SKRect _plcPanel;
            private static SKRect _encoderTag;
            void DrawIconShapesOnly(SKCanvas canvas, DockDiagramModel m, DockDiagramView view)
            {
                // ✅ 반전되어도 되는 것들만 여기
                // - 배경(라운드 사각형)
                // - 상태 표시(색상 바, 아이콘 도형)
                // - 컨베이어/레인 도형(사각형/선 등)
                // - 작은 심볼(원/삼각형 등)

                // 컨베이어 8개
                DrawConvRect(canvas, new SKRect(44, 0, 56, 140), m.Conv1_MainTop);
                DrawConvRect(canvas, new SKRect(44, 140, 56, 160), m.Conv2_MainMid);
                DrawConvRect(canvas, new SKRect(44, 160, 56, 180), m.Conv3_MainBot);
                DrawConvRect(canvas, new SKRect(44, 180, 56, 220), m.Conv4_CenterDown);

                DrawConvPoly(canvas, new[]
                {
                    new SKPoint(44, 165),
                    new SKPoint(30, 178),
                    new SKPoint(32, 196),
                    new SKPoint(44, 186),
                }, m.Conv5_LeftDiag);
                DrawConvRect(canvas, new SKRect(18, 176, 30, 240), m.Conv6_LeftDown);

                DrawConvPoly(canvas, new[]
                {
                    new SKPoint(56, 165),
                    new SKPoint(70, 178),
                    new SKPoint(68, 196),
                    new SKPoint(56, 186),
                }, m.Conv7_RightDiag);
                DrawConvRect(canvas, new SKRect(70, 176, 82, 240), m.Conv8_RightDown);

                // 판독 시스템
                var station = new SKRect(35, 33, 65, 63);
                _fill.Color = StateFill(m.ReadSystem);
                canvas.DrawRect(station, _fill);
                canvas.DrawRect(station, _stroke);
                canvas.DrawCircle(station.MidX, station.MidY, 9f, _strokeDark);


                // PLC 패널(2셀)
                var plc1 = new SKRect(76, 38, 90, 56);
                var plc2 = new SKRect(76, 58, 90, 76);

                DrawPlcCell(canvas, plc1, m.Plc);
                DrawPlcCell(canvas, plc2, m.Plc);

                // 두 셀을 합친 “패널 전체” 영역
                _plcPanel = SKRect.Create(plc1.Left, plc1.Top, plc1.Width, plc2.Bottom - plc1.Top);

                // 엔코더 태그
                _encoderTag = new SKRect(34, 88, 66, 104);
                _fill.Color = StateFill(m.Encoder);
                canvas.DrawRect(_encoderTag, _fill);
                canvas.DrawRect(_encoderTag, _stroke);


            }

            void DrawIconTextsOnly(SKCanvas canvas, DockDiagramModel m, DockDiagramView view, bool flipY)
            {
                // ✅ 반드시 정상 방향이어야 하는 것만 여기
                // - DockId 텍스트, PLC 텍스트, 수치/카운트 텍스트
                // - 경고 문자열, 라벨 등
                // (필요하면 텍스트 배경 박스는 여기서 그려도 정상)



                // "PLC" 세로 텍스트 추가
                DrawPlcLabel(canvas, _plcPanel, m.Plc, flipY);



                _encoderTagText.TextAlign = SKTextAlign.Center;
                DrawTextUprightCenteredAtLocal(canvas, m.EncoderLabel, _encoderTag.MidX, _encoderTag.MidY, _encoderTagText);
            }

            private static float ApproxUniformScale(SKMatrix m)
            {
                // 회전/반전 포함에서도 안정적인 스케일 근사
                return MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
            }

            /// <summary>
            /// local 좌표계에서의 "중앙점(center)"을 기준으로, 텍스트를 화면에서 정방향(upright)으로 그린다.
            /// (핵심: baseline 보정)
            /// </summary>
            private static void DrawTextUprightCenteredAtLocal(SKCanvas canvas, string? text, float localCenterX, float localCenterY, SKPaint paint)
            {
                if (string.IsNullOrEmpty(text)) return;

                // 1) local(center) -> device(px)
                var dev = canvas.TotalMatrix.MapPoint(localCenterX, localCenterY);

                // 2) ResetMatrix로 그릴 거라, 텍스트 크기를 device 스케일로 보정
                float s = ApproxUniformScale(canvas.TotalMatrix);
                float savedSize = paint.TextSize;
                var savedAlign = paint.TextAlign;

                paint.TextSize = savedSize * s;

                // 3) baseline 계산: centerY에서 ascent/descent 이용해 baselineY 산출
                var fm = paint.FontMetrics;
                float baselineY = dev.Y - (fm.Ascent + fm.Descent) * 0.5f; // ✅ 중앙→baseline 보정

                canvas.Save();
                try
                {
                    canvas.ResetMatrix();
                    canvas.DrawText(text, dev.X, baselineY, paint);
                }
                finally
                {
                    canvas.Restore();
                    paint.TextSize = savedSize;
                    paint.TextAlign = savedAlign;
                }
            }

            private void DrawConvRect(SKCanvas c, SKRect r, ElementState st)
            {
                _fill.Color = StateFill(st);
                c.DrawRect(r, _fill);
                c.DrawRect(r, _stroke);
            }

            private static SKColor PlcPanelFill(ElementState st) => st switch
            {
                ElementState.Run or ElementState.Error or ElementState.Emergency or ElementState.Offline => StateFill(st),
                _ => new SKColor(0x1B, 0xC8, 0x73)
            };

            private static SKColor AutoTextColor(SKColor bg)
            {
                // 단순 휘도 기준(어두우면 흰색, 밝으면 검정)
                float y = 0.299f * bg.Red + 0.587f * bg.Green + 0.114f * bg.Blue;
                return (y < 140f) ? SKColors.White : SKColors.Black;
            }

            private void DrawPlcLabel(SKCanvas c, SKRect r, ElementState st, bool flipY)
            {
                const string text = "PLC";

                var bg = PlcPanelFill(st);
                _plcTagText.Color = AutoTextColor(bg);
                _plcTagText.TextAlign = SKTextAlign.Center;

                var fm = _plcTagText.FontMetrics;
                float lineH = (fm.Descent - fm.Ascent) * 0.95f;

                float totalH = lineH * text.Length;
                float x = r.MidX;

                // 각 글자의 "센터" y를 위에서 아래로 배치
                float y = r.MidY - totalH * 0.5f + lineH * 0.5f;

                for (int i = 0; i < text.Length; i++)
                {
                    // ✅ flip이면 글자 순서를 역순으로 그려서 화면에서는 정상(PLC)로 보이게 함
                    char ch = flipY ? text[text.Length - 1 - i] : text[i];

                    DrawTextUprightCenteredAtLocal(c, ch.ToString(), x, y, _plcTagText);
                    y += lineH;
                }
            }


            private void DrawConvPoly(SKCanvas c, IReadOnlyList<SKPoint> pts, ElementState st)
            {
                using var path = new SKPath();
                path.MoveTo(pts[0]);
                for (int i = 1; i < pts.Count; i++) path.LineTo(pts[i]);
                path.Close();

                _fill.Color = StateFill(st);
                c.DrawPath(path, _fill);
                c.DrawPath(path, _stroke);
            }

            private void DrawPlcCell(SKCanvas c, SKRect r, ElementState st)
            {
                var fill = st switch
                {
                    ElementState.Run => StateFill(st),
                    ElementState.Error => StateFill(st),
                    ElementState.Emergency => StateFill(st),
                    ElementState.Offline => StateFill(st),
                    _ => new SKColor(0xF2, 0xF2, 0xF2)
                };

                _fill.Color = fill;
                c.DrawRect(r, _fill);
                c.DrawRect(r, _stroke);
            }

            // ===== Legend =====
            private void DrawLegend(SKCanvas c, SKRect r, float k)
            {
                using var bg = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0x1D, 0x1D, 0x1D) };
                using var bd = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, 2f * k), Color = new SKColor(255, 255, 255, 60) };

                float corner = Math.Max(10f, 12f * k);
                c.DrawRoundRect(r, corner, corner, bg);
                c.DrawRoundRect(r, corner, corner, bd);

                float x = r.Left + 14f * k;
                float y = r.Top + 24f * k;

                c.DrawText("범례", x, y, _legendTitle);
                y += 40f * k;

                DrawLegendStation(c, x, y, ElementState.Run, k);
                c.DrawText("판독시스템(체적기/변위센서/트리거/SMS)", x + 34f * k, y + 5f * k, _legendTextSmall);

                y += 35f * k;
                DrawLegendBox(c, x, y, new SKColor(0xF2, 0xF2, 0xF2), k);
                c.DrawText("판넬(PLC)", x + 34f * k, y + 5f * k, _legendTextSmall);

                y += 35f * k;
                DrawLegendBox(c, x, y, StateFill(ElementState.Run), k);
                c.DrawText("엔코더(ENC)", x + 34f * k, y + 5f * k, _legendTextSmall);

                y += 35f * k;
                DrawLegendBox(c, x, y, StateFill(ElementState.Stop), k);
                c.DrawText("컨베이어", x + 34f * k, y + 5f * k, _legendTextSmall);

                // 상태색(우측)
                float sx = r.Left + Math.Min(r.Width * 0.55f, 520f * k);
                float sy = r.Top + 46f * k;

                DrawState(c, sx, sy, ElementState.Stop, "정지", k); sx += 90f * k;
                DrawState(c, sx, sy, ElementState.Run, "운전", k); sx += 90f * k;
                DrawState(c, sx, sy, ElementState.Error, "에러", k); sx += 90f * k;
                DrawState(c, sx, sy, ElementState.Emergency, "비상", k);
            }

            private void DrawLegendStation(SKCanvas c, float x, float y, ElementState st, float k)
            {
                var rr = new SKRect(x, y - 10f * k, x + 20f * k, y + 10f * k);
                _fill.Color = StateFill(st);
                c.DrawRect(rr, _fill);
                c.DrawRect(rr, _stroke);
                c.DrawCircle(rr.MidX, rr.MidY, 6f * k, _strokeDark);
            }

            private void DrawLegendBox(SKCanvas c, float x, float y, SKColor fill, float k)
            {
                var rr = new SKRect(x, y - 10f * k, x + 20f * k, y + 10f * k);
                _fill.Color = fill;
                c.DrawRect(rr, _fill);
                c.DrawRect(rr, _stroke);
            }

            private void DrawState(SKCanvas c, float x, float y, ElementState st, string label, float k)
            {
                var rr = new SKRect(x, y - 10f * k, x + 22f * k, y + 10f * k);
                _fill.Color = StateFill(st);
                c.DrawRect(rr, _fill);
                c.DrawRect(rr, _stroke);

                c.DrawText(label, x + 28f * k, y + 6f * k, _legendText);
            }

            // ===== Layout structs =====
            private sealed class Layout
            {
                public required float Pad { get; init; }
                public required float GroupGap { get; init; }
                public required float LegendH { get; init; }
                public required float TotalHeightPx { get; init; }
                public required float GlobalScale { get; init; }
                public required List<Section> Sections { get; init; }

                // ✅ 추가: 중앙정렬용
                public required float OffsetX { get; init; }
                public required float OffsetY { get; init; }
            }

            private sealed class Section
            {
                public required string GroupKey { get; init; }
                public required List<DockDiagramModel> Items { get; init; }
                public required int Count { get; init; }
                public required int Cols { get; init; }
                public required int Rows { get; init; }
                public required float TileW { get; init; }
                public required float TileH { get; init; }
                public required float TileScale { get; init; }
                public required float HeaderH { get; init; }
            }
        }
        private static class DockConvName
        {
            public const string ShinChuk = "신축";
            public const string M01 = "M01";
            public const string M02 = "M02";
            public const string M03 = "M03";
            public const string M04 = "M04";
            public const string M05 = "M05";
            public const string M06 = "M06";
            public const string M07 = "M07";

            public static readonly string[] All = { ShinChuk, M01, M02, M03, M04, M05, M06, M07 };
        }
    }

}




