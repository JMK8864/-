using Fa.Core.Alarms;
using Fa.Core.Data;
using Fa.Core.Hardware;
using Fa.Core.Io;
using Fa.Core.Machine;
using Fa.Core.Motion;
using Fa.Hal.Mmce;
using Fa.Hal.Simulation;
using Fa.Hal.Wmx3;
using Fa.Infrastructure;
using Fa.Infrastructure.Comm;
using Fa.Infrastructure.Database;
using Fa.Infrastructure.Logging;
using Fa.Infrastructure.Storage;

namespace Fa.Web.Services;

/// <summary>플랫폼 서비스 등록. 설비가 바뀌면 TModule 만 바꾸면 된다.</summary>
public static class PlatformSetup
{
    public static WebApplicationBuilder AddFaPlatform<TModule>(this WebApplicationBuilder builder)
        where TModule : class, IEquipmentModule
    {
        var opt = builder.Configuration.GetSection("Fa").Get<PlatformOptions>() ?? new PlatformOptions();
        var dataDir = Path.IsPathRooted(opt.DataDirectory)
            ? opt.DataDirectory
            : Path.Combine(AppContext.BaseDirectory, opt.DataDirectory);

        var store = new JsonDataStore(dataDir);
        var fileLog = new FileLoggerProvider(opt.FileLog, dataDir);
        builder.Logging.AddProvider(fileLog);

        var machineConfig = store.Load<MachineConfig>("Config/machine")
            ?? throw new FileNotFoundException($"설비 구성 파일이 없습니다: {Path.Combine(dataDir, "Config", "machine.json")}");

        var s = builder.Services;
        s.AddSingleton(opt);
        s.AddSingleton<IDataStore>(store);
        s.AddSingleton(fileLog);
        s.AddSingleton(machineConfig);

        // ── 하드웨어: machine.json 의 Controller.Kind 로 선택 ──
        s.AddSingleton<IHardwareFactory>(machineConfig.Controller.Kind switch
        {
            ControllerKind.Wmx3 => new Wmx3HardwareFactory(),
            ControllerKind.Mmce => new MmceHardwareFactory(),
            _ => new SimulationHardwareFactory()
        });
        s.AddSingleton(sp => sp.GetRequiredService<IHardwareFactory>().CreateMotion(machineConfig));
        s.AddSingleton(sp => sp.GetRequiredService<IHardwareFactory>().CreateIo(machineConfig));

        // ── Core ──
        s.AddSingleton<InterlockManager>();
        s.AddSingleton<IoManager>();
        s.AddSingleton<MotionManager>();
        s.AddSingleton(sp => new AlarmManager(
            store.Load<List<AlarmDefinition>>("Config/alarms") ?? new(),
            sp.GetRequiredService<ILogger<AlarmManager>>()));
        s.AddSingleton<RecipeManager>();
        s.AddSingleton<ParameterManager>();
        s.AddSingleton<UserManager>();
        s.AddSingleton(sp => new Localizer(store, sp.GetRequiredService<ParameterManager>().Get(PlatformParameters.Language)));
        s.AddSingleton<MachineController>();
        s.AddSingleton<IEquipmentModule, TModule>();

        // ── 이력 저장: DB 사용 여부 ──
        if (opt.Database.Enabled)
        {
            s.AddSingleton(_ => new PostgresRepository(opt.Database.ConnectionString));
            s.AddSingleton<IAlarmHistoryRepository>(sp => sp.GetRequiredService<PostgresRepository>());
            s.AddSingleton<IProductionRepository>(sp => sp.GetRequiredService<PostgresRepository>());
        }
        else
        {
            s.AddSingleton<IAlarmHistoryRepository>(new CsvAlarmHistoryRepository(Path.Combine(dataDir, "AlarmHistory")));
            s.AddSingleton<IProductionRepository>(new CsvProductionRepository(Path.Combine(dataDir, "Production")));
        }

        // ── 백그라운드 서비스 (등록 순서대로 시작) ──
        s.AddHostedService<MachineRuntimeService>();
        s.AddHostedService<HistoryWriter>();
        s.AddSingleton<HostCommandHandler>();
        s.AddSingleton<HostCommService>();
        s.AddHostedService(sp => sp.GetRequiredService<HostCommService>());

        // ── 화면 ──
        s.AddScoped<UserSession>();
        return builder;
    }
}
