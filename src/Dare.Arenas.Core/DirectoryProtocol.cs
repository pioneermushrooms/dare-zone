using System.Buffers.Binary;

namespace Dare.Arenas.Core;

/// <summary>
/// The client side of the core protocol, just enough to ask a directory server for its zone list:
/// connect (00 01 key, type 1), the request as reliable packet 0, ACKs for every reliable packet,
/// clusters (00 0E), sized (00 0A) and big (00 08/09) transfers, until a zone list (type 0x01) is
/// complete. Works on decrypted packets; encryption is the caller's. Not thread-safe.
/// </summary>
public sealed class DirectoryProtocol
{
    public const int MaxListBytes = 512 * 1024;

    private readonly List<byte[]> _out = new();
    private const int MaxPendingReliable = 2048;
    private readonly Dictionary<int, byte[]> _pendingReliable = new();
    private int _nextReliable;
    private byte[]? _sized;
    private int _sizedTotal, _sizedGot;
    private List<byte>? _big;
    private bool _asked;

    public List<DirectoryZone>? Result { get; private set; }
    public string? Failure { get; private set; }
    public bool Done => Result is not null || Failure is not null;

    /// <summary>The connection request (00 01 key, VIE client type 1).</summary>
    public static byte[] ConnectRequest(int key)
    {
        var b = new byte[8];
        b[1] = 0x01;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(2), key);
        b[6] = 0x01;
        return b;
    }

    /// <summary>Packets to send now (to be encrypted by the caller).</summary>
    public List<byte[]> TakeOutgoing()
    {
        var o = new List<byte[]>(_out);
        _out.Clear();
        return o;
    }

    /// <summary>Handles one decrypted datagram.</summary>
    public void Handle(ReadOnlySpan<byte> p)
    {
        if (Done || p.Length < 1) return;
        if (p[0] != 0x00)
        {
            Game(p);
            return;
        }
        if (p.Length < 2) return;
        switch (p[1])
        {
            case 0x02: // connected
                if (!_asked)
                {
                    _asked = true;
                    var req = ZoneDirectory.Request();
                    var r = new byte[6 + req.Length];
                    r[1] = 0x03; // reliable, sequence 0
                    req.CopyTo(r, 6);
                    _out.Add(r);
                }
                break;
            case 0x03: // reliable: ACK, then the contents once and in sequence order
                if (p.Length < 7) return;
                var seq = BinaryPrimitives.ReadInt32LittleEndian(p[2..]);
                var ack = new byte[6];
                ack[1] = 0x04;
                BinaryPrimitives.WriteInt32LittleEndian(ack.AsSpan(2), seq);
                _out.Add(ack);
                if (seq < _nextReliable || seq - _nextReliable >= MaxPendingReliable) return;
                _pendingReliable.TryAdd(seq, p[6..].ToArray());
                while (!Done && _pendingReliable.Remove(_nextReliable, out var next))
                {
                    _nextReliable++;
                    Handle(next);
                }
                break;
            case 0x07: // disconnect
                Failure ??= "the directory closed the connection";
                break;
            case 0x08:
            case 0x09: // big data
                _big ??= new List<byte>();
                if (_big.Count + p.Length - 2 > MaxListBytes) { Failure = "zone list too large"; return; }
                _big.AddRange(p[2..].ToArray());
                if (p[1] == 0x09)
                {
                    var data = _big.ToArray();
                    _big = null;
                    Handle(data);
                }
                break;
            case 0x0A: // sized data: u32 total size, then a chunk
                if (p.Length < 7) return;
                var total = BinaryPrimitives.ReadInt32LittleEndian(p[2..]);
                if (total <= 0 || total > MaxListBytes) { Failure = "zone list too large"; return; }
                if (_sized is null || _sizedTotal != total) { _sized = new byte[total]; _sizedTotal = total; _sizedGot = 0; }
                var chunk = p[6..];
                if (_sizedGot + chunk.Length > total) { Failure = "bad sized transfer"; return; }
                chunk.CopyTo(_sized.AsSpan(_sizedGot));
                _sizedGot += chunk.Length;
                if (_sizedGot == total)
                {
                    var whole = _sized;
                    _sized = null;
                    Handle(whole);
                }
                break;
            case 0x0E: // cluster: u8 length, packet, ...
                var rest = p[2..];
                while (rest.Length > 0)
                {
                    int len = rest[0];
                    if (len == 0 || len + 1 > rest.Length) break;
                    Handle(rest.Slice(1, len));
                    rest = rest[(len + 1)..];
                }
                break;
            default:
                break; // sync requests, sync responses and others: not needed for one request
        }
    }

    private void Game(ReadOnlySpan<byte> p)
    {
        if (p[0] != ZoneDirectory.ListType) return;
        Result = ZoneDirectory.Parse(p) ?? new List<DirectoryZone>();
    }
}
