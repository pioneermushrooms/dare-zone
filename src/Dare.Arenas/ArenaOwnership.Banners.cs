using Dare.Arenas.Core;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Packets;

namespace Dare.Arenas;

// ---------------------------------------------------------------------------------------------
// Banners: the one a pilot chose in their Armory is shown beside their name when they enter an
// arena (only under a name they proved, and only when their client hasn't sent one of its own:
// a banner set in Continuum wins). Banners imported from the SubSpace Banner Emporium are fetched
// by deploy/import-banner-emporium.py into data/banner-emporium.sqlite, which is brought into the
// Foundry at load and then every few minutes while that file keeps growing.
// ---------------------------------------------------------------------------------------------
public sealed partial class ArenaOwnership
{
    internal const string BannerStagingPath = "data/banner-emporium.sqlite";
    private static readonly TimeSpan BannerIngestEvery = TimeSpan.FromMinutes(10);
    private CancellationTokenSource? _bannerIngest;

    /// <summary>At arena entry: the pilot's Armory banner, unless their client already set one.</summary>
    private void ApplyStoredBanner(Player player)
    {
        if (player.Name is null || !player.Flags.Authenticated || _accounts is not { } accounts) return;
        var banners = _broker.GetInterface<IBanners>();
        if (banners is null) return;
        try
        {
            if (banners.TryGetBanner(player, out _)) return;
            if (accounts.Find(player.Name) is not { } account || accounts.PilotBanner(account.Id) is not { } px) return;
            var banner = new Banner(px);
            banners.SetBanner(player, in banner);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warn, $"Banner for {player.Name} not applied: {ex.Message}");
        }
        finally
        {
            _broker.ReleaseInterface(ref banners);
        }
    }

    /// <summary>
    /// Mainloop: shows <paramref name="pixels"/> (or no banner, when null) beside <paramref name="name"/>
    /// now, when that pilot is in the game under a name they proved. The pilot chose it on the site, so it
    /// replaces whatever banner they had.
    /// </summary>
    internal void ShowBanner(string name, byte[]? pixels)
    {
        if (_playerData.FindPlayer(name) is not { } player || !player.Flags.Authenticated) return;
        var banners = _broker.GetInterface<IBanners>();
        if (banners is null) return;
        try
        {
            var banner = pixels is { Length: BannerFile.PixelBytes } ? new Banner(pixels) : default;
            banners.SetBanner(player, in banner);
        }
        finally
        {
            _broker.ReleaseInterface(ref banners);
        }
    }

    /// <summary>Starts bringing in staged Emporium banners: now, then whenever the staging file has changed.</summary>
    private void StartBannerIngest()
    {
        if (_accounts is not { } accounts) return;
        var cts = _bannerIngest = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            var seen = DateTime.MinValue;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var stamp = File.Exists(BannerStagingPath) ? File.GetLastWriteTimeUtc(BannerStagingPath) : DateTime.MinValue;
                    var wal = BannerStagingPath + "-wal";
                    if (File.Exists(wal) && File.GetLastWriteTimeUtc(wal) > stamp) stamp = File.GetLastWriteTimeUtc(wal);
                    if (stamp != DateTime.MinValue && stamp != seen)
                    {
                        seen = stamp;
                        var n = accounts.IngestBannerStaging(BannerStagingPath);
                        if (n.Added + n.Known + n.Duplicates + n.Bad > 0)
                            Log(LogLevel.Info, $"Banner Emporium import: {n.Added} added, {n.Known} refreshed, {n.Duplicates} duplicate pictures, {n.Bad} not banners.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log(LogLevel.Warn, $"Banner Emporium import paused: {ex.Message}");
                }
                try { await Task.Delay(BannerIngestEvery, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    private void StopBannerIngest()
    {
        _bannerIngest?.Cancel();
        _bannerIngest = null;
    }
}
