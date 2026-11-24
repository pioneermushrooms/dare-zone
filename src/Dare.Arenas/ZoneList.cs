using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Core.Modules;

namespace Dare.Arenas;

// The live zone list for the web page: every zone the public directory servers list, equally (by
// name). Each directory is asked over its own UDP socket on a worker thread (DirectoryProtocol in
// Core); the server's EncryptionVIE cipher is borrowed for directories that encrypt, and a directory
// that echoes the key ("I will not encrypt") is read in the clear, as the Dare client does. That
// answer is unauthenticated, which is acceptable only because the list is public and shown as text.
// At most one refresh per ZoneListTtl; the web page reads the cache.
public sealed partial class ArenaOwnership
{
    internal const string DefaultDirectories =
        "sscentral.trenchwars.org:4990 sscentral.sscuservers.net:4990 directory.poid.be:4990 subspace.gamespec.org:4990";
    /// <summary>Zones the browser client can join (through the Foundation web gateway): Foundation only.</summary>
    internal static readonly string[] BrowserZones = ["5.161.238.245:5000"];

    internal static bool PlaysInBrowser(string address) => BrowserZones.Contains(address, StringComparer.Ordinal);

    private static readonly TimeSpan ZoneListTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DirectoryTimeout = TimeSpan.FromSeconds(8);

    private IClientEncrypt? _vieClient;
    private volatile bool _zonesStopped;
    private readonly object _zonesGate = new();
    private ZoneSnapshot _zones = new([], DateTimeOffset.MinValue, 0, 0);
    private DateTimeOffset _zonesStarted = DateTimeOffset.MinValue;

    internal sealed record ZoneSnapshot(IReadOnlyList<DirectoryZone> Zones, DateTimeOffset At, int Answered, int Asked);

    private void LoadZoneList(IComponentBroker broker)
    {
        _zonesStopped = false;
        _vieClient = broker.GetInterface<IClientEncrypt>(EncryptionVIE.InterfaceIdentifier);
        if (_vieClient is null)
            Log(LogLevel.Warn, "EncryptionVIE is not loaded; the web zone list only shows directories that don't encrypt.");
    }

    private void UnloadZoneList(IComponentBroker broker)
    {
        lock (_zonesGate) _zonesStopped = true; // fetches use the cipher only under this lock
        if (_vieClient is not null) broker.ReleaseInterface(ref _vieClient, EncryptionVIE.InterfaceIdentifier);
    }

    /// <summary>The cached list; starts a refresh in the background when it is older than the TTL. Any thread.</summary>
    internal ZoneSnapshot Zones()
    {
        lock (_zonesGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (!_zonesStopped && now - _zones.At >= ZoneListTtl && now - _zonesStarted >= ZoneListTtl)
            {
                _zonesStarted = now;
                var directories = _configManager.GetStr(_configManager.Global, ConfigSection, "Directories") is { Length: > 0 } s ? s : DefaultDirectories;
                _ = Task.Run(() => RefreshZonesAsync(directories));
            }
            return _zones;
        }
    }

    private async Task RefreshZonesAsync(string directories)
    {
        var servers = ZoneDirectory.ParseServers(directories);
        var tasks = servers.Select(s => FetchDirectoryAsync(s.Host, s.Port)).ToList();
        var results = new List<List<DirectoryZone>>();
        foreach (var t in tasks)
        {
            try
            {
                if (await t.ConfigureAwait(false) is { } list) results.Add(list);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Info, $"Zone list: a directory failed: {ex.Message}");
            }
        }

        lock (_zonesGate)
        {
            // Keep the last good list when every directory is down.
            if (results.Count > 0 || _zones.Zones.Count == 0)
                _zones = new ZoneSnapshot(results.Count > 0 ? ZoneDirectory.Merge(results) : _zones.Zones, DateTimeOffset.UtcNow, results.Count, servers.Count);
        }
        Log(LogLevel.Info, $"Zone list: {results.Count} of {servers.Count} directories answered.");
    }

    private async Task<List<DirectoryZone>?> FetchDirectoryAsync(string host, int port)
    {
        IPAddress[] addrs;
        using (var dns = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            addrs = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, dns.Token).ConfigureAwait(false);
        if (addrs.Length == 0) return null;

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(new IPEndPoint(addrs[0], port));
        var conn = new FetchConnection();
        var protocol = new DirectoryProtocol();
        var key = RandomNumberGenerator.GetInt32(0x400, int.MaxValue); // some servers ignore small keys
        bool? clear = null;
        var vieReady = false;

        // Runs the cipher (or not) under the gate; false once the module is unloading.
        bool Crypt(byte[] data, ref int len, bool encrypt)
        {
            lock (_zonesGate)
            {
                if (_zonesStopped) return false;
                if (clear == true) return true;
                var vie = _vieClient;
                if (vie is null) return clear is null; // no VIE: only a clear directory can be read
                if (!vieReady) { vie.Initialize(conn); vieReady = true; }
                len = encrypt ? vie.Encrypt(conn, data, len) : vie.Decrypt(conn, data, len);
                return true;
            }
        }

        try
        {
            var connect = Padded(DirectoryProtocol.ConnectRequest(key));
            var n = 8;
            if (!Crypt(connect, ref n, encrypt: true)) return null;
            await udp.SendAsync(connect.AsMemory(0, n)).ConfigureAwait(false);

            using var cts = new CancellationTokenSource(DirectoryTimeout);
            while (!protocol.Done)
            {
                UdpReceiveResult got;
                try { got = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { Log(LogLevel.Info, $"Zone list: directory {host} did not answer in {DirectoryTimeout.TotalSeconds:0} s."); return null; }
                catch (SocketException) { return null; } // port unreachable

                if (got.Buffer.Length > MaxDatagram) continue;
                var data = Padded(got.Buffer);
                var len = got.Buffer.Length;
                if (clear is null && len == 6 && data[0] == 0 && data[1] == 2)
                    clear = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(2)) == key;
                if (!Crypt(data, ref len, encrypt: false)) return null;
                protocol.Handle(data.AsSpan(0, len));

                foreach (var o in protocol.TakeOutgoing())
                {
                    var m = o.Length;
                    var buf = Padded(o);
                    if (!Crypt(buf, ref m, encrypt: true)) return null;
                    await udp.SendAsync(buf.AsMemory(0, m)).ConfigureAwait(false);
                }
            }

            var bye = Padded([0, 7]); // polite disconnect
            var b = 2;
            if (Crypt(bye, ref b, encrypt: true)) await udp.SendAsync(bye.AsMemory(0, b)).ConfigureAwait(false);

            if (protocol.Failure is not null) Log(LogLevel.Info, $"Zone list: directory {host}: {protocol.Failure}.");
            return protocol.Result;
        }
        finally
        {
            lock (_zonesGate)
                if (vieReady && !_zonesStopped) _vieClient?.Void(conn);
        }
    }

    private const int MaxDatagram = 520;

    /// <summary>A copy with room after it: the VIE cipher works in 4-byte blocks past the length.</summary>
    private static byte[] Padded(byte[] src)
    {
        var b = new byte[src.Length + 8];
        src.CopyTo(b, 0);
        return b;
    }

    /// <summary>Holds EncryptionVIE's per-connection state for one directory fetch (no server connection).</summary>
    private sealed class FetchConnection : IClientConnection, IClientConnectionHandler
    {
        private readonly Dictionary<Type, object> _extra = new();
        public IClientConnectionHandler Handler => this;
        public bool TryAddExtraData<T>(T value) where T : class => _extra.TryAdd(typeof(T), value);
        public bool TryGetExtraData<T>([MaybeNullWhen(false)] out T value) where T : class
        {
            value = _extra.TryGetValue(typeof(T), out var v) ? (T)v : null;
            return value is not null;
        }
        public bool TryRemoveExtraData<T>([MaybeNullWhen(false)] out T value) where T : class
        {
            value = _extra.Remove(typeof(T), out var v) ? (T)v : null;
            return value is not null;
        }
        public void Connected() { }
        public void HandlePacket(Span<byte> data, NetReceiveFlags flags) { }
        public void Disconnected() { }
    }
}
