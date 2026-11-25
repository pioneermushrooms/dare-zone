using System.Net.Sockets;
using System.Text;

namespace Dare.Arenas;

/// <summary>One line of chat for the home page's chat panel.</summary>
internal sealed record ChatLine(long Id, DateTimeOffset At, string Kind, string? From, string Text);

/// <summary>
/// A pilot chatting from the home page: a connection to the zone's own chat-client protocol
/// (the server's ChatNetwork module, on the zone's port on this machine), logged in as the pilot
/// with a one-time game pass. The pilot is in the arena like any chat client, and everything they
/// type goes through the zone's chat handling (public, private, team, ?commands). What arrives is
/// kept for the page to poll. Thread-safe; closes itself when the page stops asking.
/// </summary>
internal sealed class ChatBridge : IDisposable
{
    public const int MaxLines = 200;
    public const int MaxMessageChars = 240;
    private static readonly Encoding Wire = Encoding.Latin1;

    private readonly object _lock = new();
    private readonly List<ChatLine> _lines = new();
    private readonly TcpClient _tcp = new();
    private StreamWriter? _writer;
    private long _seq;
    private DateTimeOffset _lastSent = DateTimeOffset.MinValue;

    public ChatBridge(long accountId, string pilot, string arena)
    {
        AccountId = accountId;
        Pilot = pilot;
        Arena = arena;
        LastSeen = DateTimeOffset.UtcNow;
    }

    public long AccountId { get; }
    public string Pilot { get; private set; }
    /// <summary>The arena asked for, then the one the zone put the pilot in.</summary>
    public string Arena { get; private set; }
    public short Freq { get; private set; } = -1;
    /// <summary>connecting, in (the arena), or closed.</summary>
    public string State { get; private set; } = "connecting";
    public string? Error { get; private set; }
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>Connects and logs in; returns when the connection ends. Never throws.</summary>
    public async Task RunAsync(string host, int port, string pass)
    {
        try
        {
            await _tcp.ConnectAsync(host, port).ConfigureAwait(false);
            var stream = _tcp.GetStream();
            _writer = new StreamWriter(stream, Wire) { NewLine = "\n", AutoFlush = true };
            var reader = new StreamReader(stream, Wire);
            await WriteAsync($"LOGIN:1;foundation-web:{Pilot}:{pass}").ConfigureAwait(false);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                await HandleAsync(line).ConfigureAwait(false);
            Fail("The zone closed the chat connection.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            Fail(State == "closed" ? Error : "Lost the chat connection.");
        }
    }

    private async Task HandleAsync(string line)
    {
        var colon = line.IndexOf(':');
        var head = colon < 0 ? line : line[..colon];
        var rest = colon < 0 ? "" : line[(colon + 1)..];
        switch (head)
        {
            case "LOGINOK":
                Pilot = rest.Length > 0 ? rest : Pilot;
                await WriteAsync($"GO:{Arena}").ConfigureAwait(false);
                break;
            case "LOGINBAD":
                Fail(rest.Length > 0 ? rest : "The zone refused the chat login.");
                Dispose();
                break;
            case "INARENA":
            {
                var i = rest.LastIndexOf(':');
                Arena = i < 0 ? rest : rest[..i];
                if (i >= 0 && short.TryParse(rest[(i + 1)..], out var f)) Freq = f;
                State = "in";
                break;
            }
            case "SHIPFREQCHANGE":
            {
                // SHIPFREQCHANGE:name:ship:freq; ours tells us our team
                var parts = rest.Split(':');
                if (parts.Length == 3 && string.Equals(parts[0], Pilot, StringComparison.OrdinalIgnoreCase)
                    && short.TryParse(parts[2], out var f))
                    Freq = f;
                break;
            }
            case "MSG":
                AddMessage(rest);
                break;
        }
    }

    // MSG:<kind>:<from>:<text>, or MSG:<kind>:<text> for ARENA/SYSOP/CMD; MSG:SQUAD:<squad>:<from>:<text>
    private void AddMessage(string rest)
    {
        var colon = rest.IndexOf(':');
        if (colon < 0) return;
        var kind = rest[..colon];
        var body = rest[(colon + 1)..];
        string? from = null;
        if (kind == "SQUAD")
        {
            var c = body.IndexOf(':');
            if (c >= 0) body = body[(c + 1)..];
        }
        if (kind is not ("ARENA" or "SYSOP" or "CMD"))
        {
            var c = body.IndexOf(':');
            if (c < 0) return;
            from = body[..c];
            body = body[(c + 1)..];
        }
        Add(kind, from, body);
    }

    private void Add(string kind, string? from, string text)
    {
        lock (_lock)
        {
            _lines.Add(new ChatLine(++_seq, DateTimeOffset.UtcNow, kind, from, text));
            if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        }
    }

    /// <summary>Lines after <paramref name="after"/>, and the newest id.</summary>
    public (IReadOnlyList<ChatLine> Lines, long Last) Since(long after)
    {
        lock (_lock)
            return (_lines.Where(l => l.Id > after).ToList(), _seq);
    }

    /// <summary>
    /// Sends what the pilot typed: to <paramref name="to"/> privately when given, as team chat when
    /// it starts with "//", else public (a "?" line is a command, as in the game). Null when sent,
    /// else why not.
    /// </summary>
    public async Task<string?> SayAsync(string text, string? to)
    {
        if (State != "in") return Error ?? "Not in the arena yet.";
        text = Clean(text);
        if (text.Length == 0) return "Type something first.";
        if (text.Length > MaxMessageChars) return $"Keep it to {MaxMessageChars} characters.";
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (now - _lastSent < TimeSpan.FromMilliseconds(500)) return "Slow down a little.";
            _lastSent = now;
        }
        string line;
        if (!string.IsNullOrWhiteSpace(to))
        {
            var name = Clean(to);
            if (name.Length == 0 || name.Length > 24 || name.Contains(':')) return "That isn't a player name.";
            line = $"SEND:PRIV:{name}:{text}";
            Add("PRIV", "to " + name, text); // the zone doesn't echo our own private messages
        }
        else if (text.StartsWith("//", StringComparison.Ordinal) && text.Length > 2)
        {
            if (Freq < 0) return "You're not on a team yet.";
            line = $"SEND:FREQ:{Freq}:{text[2..]}";
            Add("FREQ", Pilot, text[2..]); // our own lines aren't echoed back to us
        }
        else
        {
            line = $"SEND:PUB:{text}";
            if (text[0] is not ('?' or '*')) Add("PUB", Pilot, text);
        }
        await WriteAsync(line).ConfigureAwait(false);
        return null;
    }

    /// <summary>One line of plain text: no control characters (they would split the protocol's lines).</summary>
    public static string Clean(string s) =>
        new string((s ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();

    private async Task WriteAsync(string line)
    {
        var w = _writer;
        if (w is null) return;
        try
        {
            await w.WriteLineAsync(line).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Fail("Lost the chat connection.");
        }
    }

    private void Fail(string? why)
    {
        if (State != "closed") Error = why;
        State = "closed";
    }

    public void Dispose()
    {
        if (State != "closed") Error ??= null;
        State = "closed";
        try { _writer?.WriteLine("LOGOUT"); } catch { }
        try { _tcp.Dispose(); } catch { }
    }
}
