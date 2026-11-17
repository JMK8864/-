using System.Security.Cryptography;

namespace Fa.Core.Data;

/// <summary>사용자 권한 등급. 로그인하지 않으면 Operator.</summary>
public enum UserLevel { Operator = 0, Engineer = 1, Admin = 2 }

public sealed class UserAccount
{
    public string Name { get; set; } = "";
    public UserLevel Level { get; set; }
    public string Salt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

/// <summary>사용자 계정 (Data/Config/users.json). 비밀번호는 PBKDF2 해시로 저장.</summary>
public sealed class UserManager
{
    private const string Path = "Config/users";
    private readonly IDataStore _store;
    private readonly List<UserAccount> _users;
    private readonly object _lock = new();

    public UserManager(IDataStore store)
    {
        _store = store;
        _users = store.Load<List<UserAccount>>(Path) ?? new();
        if (_users.Count == 0)
        {
            // 최초 실행용 기본 계정. 설치 후 반드시 비밀번호 변경.
            AddOrUpdate("admin", "admin", UserLevel.Admin);
            AddOrUpdate("engineer", "engineer", UserLevel.Engineer);
        }
    }

    public IReadOnlyList<UserAccount> Users { get { lock (_lock) return _users.ToList(); } }

    public UserAccount? Authenticate(string name, string password)
    {
        UserAccount? u;
        lock (_lock) u = _users.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (u == null) return null;
        var hash = Hash(password, Convert.FromBase64String(u.Salt));
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(u.PasswordHash)) ? u : null;
    }

    public void AddOrUpdate(string name, string password, UserLevel level)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        lock (_lock)
        {
            _users.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            _users.Add(new UserAccount
            {
                Name = name, Level = level,
                Salt = Convert.ToBase64String(salt),
                PasswordHash = Convert.ToBase64String(Hash(password, salt))
            });
            _store.Save(Path, _users);
        }
    }

    public bool Remove(string name)
    {
        lock (_lock)
        {
            if (_users.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0) return false;
            _store.Save(Path, _users);
            return true;
        }
    }

    private static byte[] Hash(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
}
