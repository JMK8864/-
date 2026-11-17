using Fa.Machine.Sample;
using Fa.Web.Components;
using Fa.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// 설비 모듈 선택: 새 설비는 IEquipmentModule 구현 클래스로 교체
builder.AddFaPlatform<SampleMachineModule>();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Host.UseWindowsService(); // Windows 서비스로도 실행 가능 (콘솔 실행 시에는 영향 없음)

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
