namespace Fa.Core.Common;

/// <summary>모션/IO 명령 결과. 실패 시 사유(인터록, 서보 OFF 등)를 담는다.</summary>
public readonly record struct OpResult(bool Ok, string? Error)
{
    public static readonly OpResult Success = new(true, null);
    public static OpResult Fail(string error) => new(false, error);
    public override string ToString() => Ok ? "OK" : $"FAIL: {Error}";
}
