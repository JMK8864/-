using Fa.Core.Common;
using Fa.Core.Data;
using Fa.Core.Machine;
using Fa.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Fa.Web.Components;

/// <summary>
/// 화면 공통 기반. 주기적으로 다시 그려서 설비 상태를 실시간으로 보여준다 (기본 250ms).
/// 조작 결과 메시지와 권한 검사 도우미를 제공한다.
/// </summary>
public abstract class LiveComponent : ComponentBase, IDisposable
{
    private Timer? _timer;

    [Inject] protected MachineController M { get; set; } = null!;
    [Inject] protected Localizer L { get; set; } = null!;
    [Inject] protected UserSession Session { get; set; } = null!;
    [Inject] protected ILoggerFactory LoggerFactory { get; set; } = null!;

    protected virtual int RefreshMs => 250;
    protected string? Message { get; set; }
    protected bool MessageIsError { get; set; }

    protected override void OnInitialized()
    {
        _timer = new Timer(_ => InvokeAsync(StateHasChanged), null, RefreshMs, RefreshMs);
        L.Changed += Redraw;
        Session.Changed += Redraw;
    }

    private void Redraw() => InvokeAsync(StateHasChanged);

    /// <summary>조작 결과 표시 + 사용자 조작 로그.</summary>
    protected void Show(OpResult r, string action)
    {
        MessageIsError = !r.Ok;
        Message = r.Ok ? $"{action}: OK" : $"{action}: {r.Error}";
        LoggerFactory.CreateLogger("UI").LogInformation("[{User}] {Action} → {Result}", Session.Name, action, r);
    }

    /// <summary>권한 확인. 부족하면 메시지를 띄우고 false.</summary>
    protected bool Require(UserLevel level)
    {
        if (Session.Can(level)) return true;
        Show(OpResult.Fail($"{L["msg.noPermission"]} ({level})"), "권한");
        return false;
    }

    /// <summary>수동 조작 가능 상태 + 권한 확인.</summary>
    protected bool RequireManual(UserLevel level = UserLevel.Engineer)
    {
        if (!Require(level)) return false;
        if (M.CanManualOperate) return true;
        Show(OpResult.Fail(L["msg.manualOnly"]), "수동");
        return false;
    }

    public virtual void Dispose()
    {
        _timer?.Dispose();
        L.Changed -= Redraw;
        Session.Changed -= Redraw;
    }
}
