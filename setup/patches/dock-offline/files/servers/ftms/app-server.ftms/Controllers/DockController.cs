using ftms.app.Managers;
using ftms.app.TcpMessageHandlers;
using ftms.shared.Model.Messages;
using Microsoft.AspNetCore.Mvc;

namespace Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DockController : ControllerBase
    {
        private readonly LogisticsSystemStateManager _systemStatusManager;

        // [도크 끊김 판정] 화면에 전달할 끊김 판정 시간(ms) = 서버 TCP 무수신 타임아웃
        private readonly long? _dockStaleMs;

        public DockController(LogisticsSystemStateManager systemStatusManager, IConfiguration configuration)
        {
            _systemStatusManager = systemStatusManager;

            // TcpServerFinalService 와 같은 방법으로 읽음 (섹션이 없으면 TcpServerConfig 기본값 사용)
            var tcpConfig = configuration.GetSection(Constants.TcpServerConfig).Get<TcpServerConfig>() ?? new TcpServerConfig();
            _dockStaleMs = tcpConfig.TimeoutSeconds > 0 ? tcpConfig.TimeoutSeconds * 1000L : null;  // 0 이하(타임아웃 끔)면 화면 기본값
        }

    //    //  "/api/dock/status"
    //    [HttpGet("status")]
    //    public async Task<IActionResult> GetAllStatus()
    //    {
    //        var status = await _statusManager?.GetAllStatusAsync() ?? Enumerable.Empty<HeartBeatReportMessage>();

    ////public async Task<IEnumerable<>> GetAllStatusAsync(IEnumerable<string>? dockIds = null, CancellationToken stoppingToken = default)

    //        return Ok(status);
    //    }


        // GET /api/dock/status?dockIds=A&dockIds=B
        [HttpGet("status")]
        public async Task<IActionResult> GetAllStatus([FromQuery] IEnumerable<string>? dockIds, CancellationToken cancellationToken)
        {
            var heartBeatStatus = await _systemStatusManager.GetAllStatusAsync(dockIds, cancellationToken);
            var json = heartBeatStatus.ToJson(_dockStaleMs);
            return Ok(json);
        }

    }
}
