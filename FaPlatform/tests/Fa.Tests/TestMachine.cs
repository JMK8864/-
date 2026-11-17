using Fa.Core.Alarms;
using Fa.Core.Data;
using Fa.Core.Hardware;
using Fa.Core.Io;
using Fa.Core.Machine;
using Fa.Core.Motion;
using Fa.Hal.Simulation;
using Fa.Infrastructure.Storage;
using Fa.Machine.Sample;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fa.Tests;

/// <summary>시뮬레이션 + 예제 설비를 DI 없이 조립 (Fa.Web PlatformSetup 과 같은 순서).</summary>
public sealed class TestMachine : IDisposable
{
    public MachineController M { get; }
    public string DataDir { get; }

    public TestMachine()
    {
        DataDir = Path.Combine(Path.GetTempPath(), "fa-test-" + Guid.NewGuid().ToString("N"));
        CopyDir(Path.Combine(AppContext.BaseDirectory, "Data"), DataDir);
        var store = new JsonDataStore(DataDir);
        var cfg = store.Load<MachineConfig>("Config/machine")!;
        var hw = new SimulationHardwareFactory();
        var il = new InterlockManager();
        var io = new IoManager(hw.CreateIo(cfg), cfg, il);
        var motion = new MotionManager(hw.CreateMotion(cfg), cfg, il);
        var alarms = new AlarmManager(store.Load<List<AlarmDefinition>>("Config/alarms")!, NullLogger<AlarmManager>.Instance);
        M = new MachineController(cfg, motion, io, alarms, il, new RecipeManager(store), new ParameterManager(store), NullLoggerFactory.Instance);
        new SampleMachineModule().Configure(M);
        M.Recipe.Select("Default");
        M.StartRuntime();
    }

    public static bool WaitUntil(Func<bool> cond, int timeoutMs = 10000)
    {
        var end = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < end) { if (cond()) return true; Thread.Sleep(10); }
        return cond();
    }

    public void HomeAndStart(OperationMode mode = OperationMode.Auto)
    {
        Assert.True(M.Home().Ok);
        Assert.True(WaitUntil(() => M.State == MachineState.Ready), $"원점복귀 후 Ready 가 되어야 함: {M.State} homing={M.IsHoming} units={string.Join(",", M.Units.Select(u => u.Name + ":" + u.HomeState))} alarms={AlarmText}");
        Assert.True(M.SetMode(mode).Ok);
        Assert.True(M.Start().Ok);
    }

    public string AlarmText => string.Join(", ", M.Alarms.Active.Select(a => $"{a.Code}/{a.Source}/{a.Detail}"));

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    public void Dispose()
    {
        M.StopRuntime();
        try { Directory.Delete(DataDir, true); } catch { }
    }
}
