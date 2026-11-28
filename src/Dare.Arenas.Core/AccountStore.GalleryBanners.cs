using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Core;

/// <summary>One banner of a collection brought in from elsewhere (the SubSpace Banner Emporium), credited to its author's name as text.</summary>
public sealed record LibraryBanner(long Id, string Source, long SourceId, string Author, string Added, long Players, long Saves);

/// <summary>A page of library banners: <see cref="PerPage"/> to an atlas, the total that match, and the revision atlas URLs carry.</summary>
public sealed record BannerLibraryPage(IReadOnlyList<LibraryBanner> Items, long Count, int Page, int PerPage, string Revision);

/// <summary>One banner to import: its id where it came from, author, date added (ISO), how many players used it there, and the file.</summary>
public sealed record BannerImportRow(long SourceId, string Author, string Added, long Players, byte[] File);

/// <summary>What an import did: new banners, ones already here (details refreshed), pictures already here under another id, and files that aren't banners.</summary>
public sealed record BannerImportCounts(int Added, int Known, int Duplicates, int Bad)
{
    public static BannerImportCounts operator +(BannerImportCounts a, BannerImportCounts b) =>
        new(a.Added + b.Added, a.Known + b.Known, a.Duplicates + b.Duplicates, a.Bad + b.Bad);
}

/// <summary>
/// Banners in the Foundry. A pilot shares one like any design (a gallery item of kind "banner",
/// under the usual per-pilot and gallery caps); keeping one puts it in their Armory, and the
/// Armory's banner slot is the banner the zone shows beside their name (see <see cref="PilotBanner"/>).
/// A collection brought in from elsewhere (<see cref="EmporiumSource"/>: the SubSpace Banner Emporium,
/// ~150,000 banners) lives in its own table, banner_library, outside those caps: 96 bytes a banner,
/// credited to the author's name as plain text, never to an account, de-duplicated by picture, and
/// served as atlas pages (<see cref="BannerFile.AtlasPng"/>) of <see cref="BannerLibraryPerPage"/>.
/// </summary>
public sealed partial class AccountStore
{
    public const string EmporiumSource = "emporium";
    public const int BannerLibraryPerPage = 200;
    public const int BannerLibraryColumns = 20;
    public static readonly string[] BannerLibrarySorts = ["new", "old", "popular"];

    private static IEnumerable<GalleryFileKind> BannerGalleryKinds() =>
    [
        new(new GalleryKind("banner", "Banner", "Banners", BannerFile.Width, BannerFile.Height, "armory"), BannerFile.MaxFileBytes,
            BannerFile.Looks, BannerFile.Check, BannerFile.Normalise, Extension: "bmp", ContentType: "image/bmp"),
    ];

    // ---- a pilot's own banner: the Armory's banner slot ------------------------------------------

    /// <summary>The 96 pixels of the banner <paramref name="accountId"/> (a pilot name) chose in their Armory, or null.</summary>
    public byte[]? PilotBanner(long accountId)
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT i.png FROM armory_slots s JOIN armory_items i ON i.id = s.item_id " +
                              "WHERE s.account_id = $a AND s.kind = 'banner' AND s.ship = 0", ("$a", accountId));
            return c.ExecuteScalar() is byte[] b && BannerFile.TryPixels(b, out var px, out _) ? px : null;
        }
    }

    /// <summary>
    /// Puts banner file <paramref name="bmp"/> in the pilot's Armory (the one already there when it's
    /// the same picture) and, with <paramref name="use"/>, makes it their banner. <paramref name="armoryId"/> is the item.
    /// </summary>
    public Result KeepBanner(long accountId, string name, byte[] bmp, bool use, out long armoryId)
    {
        armoryId = 0;
        if (BannerFile.Check(bmp) is { } bad) return Fail(bad);
        var file = BannerFile.Normalise(bmp);
        long existing;
        lock (_gate)
            existing = Scalar("SELECT COALESCE((SELECT id FROM armory_items WHERE person_id = $p AND kind = 'banner' AND png = $b ORDER BY id LIMIT 1), 0)",
                ("$p", PersonKey(accountId)), ("$b", file));
        Result r;
        if (existing > 0) { armoryId = existing; r = Ok("It's already in your armory."); }
        else
        {
            r = AddArmoryItem(accountId, "banner", name, "uploaded", file, out armoryId);
            if (!r.Ok) return r;
        }
        if (!use) return r;
        var s = SetArmorySlot(accountId, "banner", 0, armoryId);
        return s.Ok ? Ok(existing > 0 ? "It's your banner now." : "Added to your armory; it's your banner now.") : s;
    }

    /// <summary>Keeps a shared banner (a gallery item) and makes it the pilot's banner.</summary>
    public Result UseGalleryBanner(long accountId, long itemId)
    {
        if (GalleryImage(itemId) is not { } item) return Fail("That design isn't in the gallery.");
        if (item.Kind != "banner") return Fail("That isn't a banner.");
        var r = KeepBanner(accountId, item.Name, item.Image, true, out _);
        if (r.Ok) CountGallerySave(itemId);
        return r;
    }

    // ---- the library: banners brought in from elsewhere ------------------------------------------

    /// <summary>
    /// Imports <paramref name="rows"/> from <paramref name="source"/>; safe to run again and again: a
    /// banner already here (same source id) only has its author, date and player count refreshed, and
    /// one whose picture is already here (in the library or shared by a pilot) is left out.
    /// </summary>
    public BannerImportCounts ImportBanners(string source, IEnumerable<BannerImportRow> rows)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 32) throw new ArgumentException("Name the source.", nameof(source));
        int added = 0, known = 0, dupes = 0, bad = 0;
        foreach (var batch in rows.Chunk(500))
            Tx(() =>
            {
                foreach (var row in batch)
                {
                    if (!BannerFile.TryPixels(row.File, out var px, out _)) { bad++; continue; }
                    var author = CleanAuthor(row.Author);
                    var addedAt = (row.Added ?? "").Trim();
                    if (addedAt.Length > 32) addedAt = addedAt[..32];
                    if (Scalar("SELECT COUNT(*) FROM banner_library WHERE source = $s AND source_id = $id", ("$s", source), ("$id", row.SourceId)) > 0)
                    {
                        Run("UPDATE banner_library SET author = $a, added = $d, players = $n, keywords = $k WHERE source = $s AND source_id = $id",
                            ("$a", author), ("$d", addedAt), ("$n", Math.Max(0, row.Players)), ("$k", BannerKeywords(author, px)), ("$s", source), ("$id", row.SourceId));
                        known++;
                        continue;
                    }
                    var sha = BannerFile.Sha(px);
                    if (Scalar("SELECT COUNT(*) FROM banner_library WHERE sha = $h", ("$h", sha)) > 0
                        || Scalar("SELECT COUNT(*) FROM gallery_items WHERE sha = $h", ("$h", sha)) > 0) { dupes++; continue; }
                    Run("INSERT INTO banner_library(source, source_id, author, added, players, px, sha, keywords) VALUES($s, $id, $a, $d, $n, $px, $h, $k)",
                        ("$s", source), ("$id", row.SourceId), ("$a", author), ("$d", addedAt), ("$n", Math.Max(0, row.Players)), ("$px", px), ("$h", sha),
                        ("$k", BannerKeywords(author, px)));
                    added++;
                }
                BumpBannerRevision();
                return Ok("");
            });
        return new(added, known, dupes, bad);
    }

    /// <summary>
    /// Brings in what the import script (deploy/import-banner-emporium.py) has fetched so far into
    /// <paramref name="stagingPath"/>: only rows staged since the last time, so it can run often while
    /// the script is still going. Missing file: nothing to do.
    /// </summary>
    public BannerImportCounts IngestBannerStaging(string stagingPath, string source = EmporiumSource)
    {
        var total = new BannerImportCounts(0, 0, 0, 0);
        if (!File.Exists(stagingPath)) return total;
        long after;
        lock (_gate) after = Scalar("SELECT COALESCE((SELECT staged_seq FROM banner_imports WHERE source = $s), 0)", ("$s", source));
        using var staging = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = stagingPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        staging.Open();
        using (var check = staging.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'banners'";
            if (Convert.ToInt64(check.ExecuteScalar()) == 0) return total;
        }
        while (true)
        {
            var rows = new List<BannerImportRow>();
            var last = after;
            using (var c = staging.CreateCommand())
            {
                c.CommandText = "SELECT seq, id, status, author, added, players, bmp FROM banners WHERE seq > $after ORDER BY seq LIMIT 2000";
                c.Parameters.AddWithValue("$after", after);
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    last = r.GetInt64(0);
                    if (r.GetString(2) != "ok" || r.IsDBNull(6)) continue;
                    rows.Add(new BannerImportRow(r.GetInt64(1), r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4),
                        r.IsDBNull(5) ? 0 : r.GetInt64(5), (byte[])r.GetValue(6)));
                }
            }
            if (last == after) break;
            total += ImportBanners(source, rows);
            after = last;
            Tx(() =>
            {
                Run("INSERT INTO banner_imports(source, staged_seq) VALUES($s, $q) ON CONFLICT(source) DO UPDATE SET staged_seq = $q", ("$s", source), ("$q", after));
                return Ok("");
            });
        }
        return total;
    }

    /// <summary>
    /// A page of the library (<see cref="BannerLibraryPerPage"/> banners, the order of its atlas):
    /// by keywords (author names and colour words; every word must start one), by exact
    /// <paramref name="author"/>, sorted "new" (default), "old" or "popular" (most used where it came from).
    /// </summary>
    public BannerLibraryPage BannerLibrary(string? query = null, string? author = null, string? sort = null, int page = 0)
    {
        page = Math.Max(0, page);
        var (where, ps) = BannerLibraryFilter(query, author);
        lock (_gate)
        {
            var count = Scalar("SELECT COUNT(*) FROM banner_library b WHERE " + where, [.. ps]);
            var items = new List<LibraryBanner>();
            using (var c = Cmd("SELECT id, source, source_id, author, added, players, saves FROM banner_library b WHERE " + where + OrderOf(sort) + " LIMIT $n OFFSET $o",
                       [.. ps, ("$n", BannerLibraryPerPage), ("$o", (long)page * BannerLibraryPerPage)]))
            using (var r = c.ExecuteReader())
                while (r.Read())
                    items.Add(new LibraryBanner(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6)));
            return new BannerLibraryPage(items, count, page, BannerLibraryPerPage, BannerLibraryRevisionLocked());
        }
    }

    /// <summary>The atlas PNG of the same page <see cref="BannerLibrary"/> lists (same filters, same order).</summary>
    public byte[] BannerLibraryAtlas(string? query = null, string? author = null, string? sort = null, int page = 0)
    {
        page = Math.Max(0, page);
        var (where, ps) = BannerLibraryFilter(query, author);
        var banners = new List<byte[]>();
        lock (_gate)
        {
            using var c = Cmd("SELECT px FROM banner_library b WHERE " + where + OrderOf(sort) + " LIMIT $n OFFSET $o",
                [.. ps, ("$n", BannerLibraryPerPage), ("$o", (long)page * BannerLibraryPerPage)]);
            using var r = c.ExecuteReader();
            while (r.Read()) banners.Add((byte[])r.GetValue(0));
        }
        return BannerFile.AtlasPng(banners, BannerLibraryColumns);
    }

    /// <summary>Changes whenever the library does (an import, a takedown), so atlas URLs that carry it can be cached for good.</summary>
    public string BannerLibraryRevision()
    {
        lock (_gate) return BannerLibraryRevisionLocked();
    }

    private const string RevisionRow = "#revision";

    private string BannerLibraryRevisionLocked() =>
        Scalar("SELECT COALESCE((SELECT staged_seq FROM banner_imports WHERE source = $r), 0)", ("$r", RevisionRow)).ToString();

    /// <summary>Counts a change to the library (inside a transaction): pages and atlases cached under the old revision are left behind.</summary>
    private void BumpBannerRevision() =>
        Run("INSERT INTO banner_imports(source, staged_seq) VALUES($r, 1) ON CONFLICT(source) DO UPDATE SET staged_seq = staged_seq + 1", ("$r", RevisionRow));

    /// <summary>One library banner and its canonical file, or null (also when taken down).</summary>
    public (LibraryBanner Banner, byte[] File)? LibraryBannerFile(long id)
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT id, source, source_id, author, added, players, saves, px FROM banner_library WHERE id = $id AND hidden = 0", ("$id", id));
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            return (new LibraryBanner(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6)),
                BannerFile.Bmp((byte[])r.GetValue(7)));
        }
    }

    /// <summary>Keeps a library banner in the pilot's Armory, and with <paramref name="use"/> makes it their banner.</summary>
    public Result KeepLibraryBanner(long accountId, long id, bool use)
    {
        if (LibraryBannerFile(id) is not { } b) return Fail("That banner isn't in the Foundry.");
        var r = KeepBanner(accountId, b.Banner.Author.Length > 0 ? $"Banner by {b.Banner.Author}" : "Banner", b.File, use, out _);
        if (r.Ok) Tx(() => { Run("UPDATE banner_library SET saves = saves + 1 WHERE id = $id", ("$id", id)); return Ok(""); });
        return r;
    }

    /// <summary>
    /// Takes a library banner down (staff). It stays hidden: importing again doesn't bring it back.
    /// Copies pilots already kept stay theirs.
    /// </summary>
    public Result HideLibraryBanner(long id) => Tx(() =>
    {
        if (Scalar("SELECT COUNT(*) FROM banner_library WHERE id = $id AND hidden = 0", ("$id", id)) == 0) return Fail("That banner isn't in the Foundry.");
        Run("UPDATE banner_library SET hidden = 1 WHERE id = $id", ("$id", id));
        BumpBannerRevision();
        return Ok("Taken down.");
    });

    private static string OrderOf(string? sort) => sort switch
    {
        "old" => " ORDER BY b.source_id ASC, b.id ASC",
        "popular" => " ORDER BY b.players DESC, b.source_id DESC",
        _ => " ORDER BY b.source_id DESC, b.id DESC",
    };

    private static (string Where, List<(string, object)> Ps) BannerLibraryFilter(string? query, string? author)
    {
        var where = new List<string> { "b.hidden = 0" };
        var ps = new List<(string, object)>();
        var n = 0;
        foreach (var w in WordsOf(query ?? "").Distinct().Take(8))
        {
            where.Add($"(' ' || b.keywords) LIKE $q{n} ESCAPE '\\'");
            ps.Add(($"$q{n}", "% " + w.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));
            n++;
        }
        if (!string.IsNullOrWhiteSpace(author)) { where.Add("b.author = $au COLLATE NOCASE"); ps.Add(("$au", author.Trim())); }
        return (string.Join(" AND ", where), ps);
    }

    private static string BannerKeywords(string author, byte[] px) => GalleryKeywords([author, .. BannerFile.ColourWords(px)]);

    /// <summary>An author's name as text: printable, at most 40 characters ("" when there's none).</summary>
    private static string CleanAuthor(string? name)
    {
        var s = new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return s.Length > 40 ? s[..40] : s;
    }

    private void CreateBannerTables()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS banner_library (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              source TEXT NOT NULL,
              source_id INTEGER NOT NULL,
              author TEXT NOT NULL,
              added TEXT NOT NULL,
              players INTEGER NOT NULL DEFAULT 0,
              px BLOB NOT NULL,
              sha TEXT NOT NULL UNIQUE,
              keywords TEXT NOT NULL,
              hidden INTEGER NOT NULL DEFAULT 0,
              saves INTEGER NOT NULL DEFAULT 0,
              UNIQUE(source, source_id)
            );
            CREATE INDEX IF NOT EXISTS ix_banner_library_new ON banner_library(hidden, source_id);
            CREATE INDEX IF NOT EXISTS ix_banner_library_popular ON banner_library(hidden, players, source_id);
            CREATE INDEX IF NOT EXISTS ix_banner_library_author ON banner_library(author COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS banner_imports (
              source TEXT PRIMARY KEY,
              staged_seq INTEGER NOT NULL
            );
            """);
    }
}
