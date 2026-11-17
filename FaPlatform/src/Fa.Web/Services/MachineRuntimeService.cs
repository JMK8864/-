using Fa.Core.Alarms;
using Fa.Core.Data;
using Fa.Core.Machine;
using Fa.Infrastructure;

namespace Fa.Web.Services;

/// <summary>웹 서버 시작 시 설비 모듈 구성 → 하드웨어 Open → 스레드 시작, 종료 시 안전 정지.</summary>
public sealed class MachineRuntimeService : IHostedService
{
    private readonly MachineController _machine;
    private readonly IEquipmentModule _module;
    private readonly PlatformOptions _opt;
    private readonly ParameterManager _params;
    private readonly Localizer _lang;
    private readonly AlarmManager _alarms;
    private readonly ILogger<MachineRuntimeService> _log;

    public MachineRuntimeService(MachineController machine, IEquipmentModule module, PlatformOptions opt,
        ParameterManager parameters, Localizer lang, AlarmManager alarms, ILogger<MachineRuntimeService> log)
    {
        _machine = machine; _module = module; _opt = opt; _params = parameters; _lang = lang; _alarms = alarms; _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _log.LogInformation("설비 모듈: {Module}", _module.Name);
        _module.Configure(_machine);

        // 언어 파라미터 ↔ 화면/알람 문구 연동
        _alarms.Language = _lang.Language;
        _alarms.CurrentRecipeProvider = () => _machine.Recipe.Current.Name;
        _params.Changed += key =>
        {
            if (key != PlatformParameters.Language) return;
            _lang.SetLanguage(_params.Get(key));
            _alarms.Language = _lang.Language;
        };

        var recipes = _machine.Recipe.List();
        _machine.Recipe.Select(recipes.Contains(_opt.DefaultRecipe) || recipes.Count == 0 ? _opt.DefaultRecipe : recipes[0]);

        _machine.StartRuntime();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _machine.StopRuntime();
        return Task.CompletedTask;
    }
}
