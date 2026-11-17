using Fa.Core.Data;

namespace Fa.Web.Services;

/// <summary>브라우저 연결(회로)마다 하나. 로그인하지 않으면 Operator 권한.</summary>
public sealed class UserSession
{
    private readonly UserManager _users;
    private readonly ILogger<UserSession> _log;
    public UserSession(UserManager users, ILogger<UserSession> log) { _users = users; _log = log; }

    public UserAccount? User { get; private set; }
    public string Name => User?.Name ?? "Operator";
    public UserLevel Level => User?.Level ?? UserLevel.Operator;
    public bool Can(UserLevel required) => Level >= required;
    public event Action? Changed;

    public bool Login(string name, string password)
    {
        User = _users.Authenticate(name, password);
        _log.LogInformation("로그인 {Name}: {Result}", name, User != null ? "성공" : "실패");
        Changed?.Invoke();
        return User != null;
    }

    public void Logout()
    {
        _log.LogInformation("로그아웃 {Name}", Name);
        User = null;
        Changed?.Invoke();
    }
}
