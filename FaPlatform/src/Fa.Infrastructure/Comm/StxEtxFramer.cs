namespace Fa.Infrastructure.Comm;

/// <summary>
/// STX(0x02) ~ ETX(0x03) 로 메시지를 구분한다. TCP 는 메시지가 쪼개지거나 붙어서 오므로
/// 받은 바이트를 계속 넣어주면 완성된 메시지(STX/ETX 제외)만 돌려준다.
/// STX 앞의 쓰레기 바이트는 버리고, MaxLength 를 넘으면 버퍼를 비운다.
/// </summary>
public sealed class StxEtxFramer
{
    public const byte STX = 0x02;
    public const byte ETX = 0x03;

    private readonly List<byte> _buf = new();
    private bool _inFrame;

    public StxEtxFramer(int maxLength = 64 * 1024) { MaxLength = maxLength; }
    public int MaxLength { get; }

    public IEnumerable<byte[]> Push(ReadOnlySpan<byte> data)
    {
        var frames = new List<byte[]>();
        foreach (var b in data)
        {
            if (b == STX) { _buf.Clear(); _inFrame = true; continue; } // 새 STX 가 오면 이전 미완성 메시지는 버림
            if (!_inFrame) continue;
            if (b == ETX) { frames.Add(_buf.ToArray()); _buf.Clear(); _inFrame = false; continue; }
            _buf.Add(b);
            if (_buf.Count > MaxLength) { _buf.Clear(); _inFrame = false; }
        }
        return frames;
    }

    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[payload.Length + 2];
        frame[0] = STX;
        payload.CopyTo(frame.AsSpan(1));
        frame[^1] = ETX;
        return frame;
    }
}
