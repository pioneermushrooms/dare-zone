using System.Security.Cryptography;
using System.Text;

namespace Dare.Arenas.Core;

/// <summary>
/// A kind of picture pilots can share: "ship" (any rotation sheet: 40 square frames in 10 columns
/// and 4 rows, any side 8..96) or one of the game's other graphics at its exact stock size.
/// <see cref="Save"/> is where a copy goes when a pilot keeps one: their shipyard, their armory,
/// or their own other graphics.
/// </summary>
public sealed record GalleryKind(string Name, string Label, string Group, int Width, int Height, string Save);

/// <summary>Whether a file is this kind's format, from its raw bytes (before any picture check).</summary>
public delegate bool GalleryFits(ReadOnlySpan<byte> file);

/// <summary>Null when the file is a good one of this kind, else why not.</summary>
public delegate string? GalleryCheck(ReadOnlySpan<byte> file);

/// <summary>
/// A kind that isn't a picture at a stock size (a map, a tileset, a banner...), plugged into the
/// kinds table from its own partial file: <see cref="Fits"/> runs on the raw bytes before the picture
/// check, so its files need not be pictures at all; <see cref="Validate"/> then checks one that fits.
/// <see cref="Stored"/> is what is kept (e.g. the tileset cut out of a map; null keeps the file as
/// sent). <see cref="ThumbWidth"/>×<see cref="ThumbHeight"/>, when not 0, is the PNG preview that
/// may come with it, built once by the sharer and served with the item.
/// </summary>
public sealed record GalleryFileKind(GalleryKind Kind, int MaxBytes, GalleryFits Fits, GalleryCheck Validate,
    Func<byte[], byte[]>? Stored = null, string Extension = "bin", string ContentType = "application/octet-stream",
    int ThumbWidth = 0, int ThumbHeight = 0);

/// <summary>One shared design. <see cref="By"/> is the contributing pilot name, and nothing else about them.</summary>
public sealed record GalleryItem(long Id, string Kind, int Ship, string Name, string Keywords, string Prompt, string Source,
    string By, bool Mine, string Created, long Saves);

/// <summary>A contribution's outcome. <see cref="Choices"/> lists the kinds a sheet could be when it fits more than one.</summary>
public sealed record GalleryAddResult(bool Ok, string Message, long Id, IReadOnlyList<string> Choices);

/// <summary>A page of the gallery: the items, the total that match, and the id to continue from (0 at the end).</summary>
public sealed record GalleryPage(IReadOnlyList<GalleryItem> Items, long Count, long Next);

/// <summary>
/// The shared gallery: every pilot can contribute ships and graphics, everyone can browse them,
/// and a signed-in pilot can keep a copy in their own shipyard, armory or graphics. A sheet's kind
/// comes from its size (see <see cref="DetectGalleryKinds"/>), so nothing is filed under a kind the
/// game would cut wrongly. The contributor or staff can take one down.
/// </summary>
public sealed partial class AccountStore
{
    public const int MaxGalleryPerAccount = 60;
    private const string GalleryCredit =
        "COALESCE(a.name, (SELECT o.name FROM accounts o WHERE o.person_id = g.person_id AND o.status = 'active' ORDER BY o.name COLLATE NOCASE LIMIT 1))";
    public const int MaxGalleryItems = 20_000;
    public const int MaxGalleryImageBytes = 256 * 1024;
    public const int MaxGalleryKeywordsLength = 200;
    public const int MaxGalleryPageSize = 60;
    public static readonly string[] GallerySources = ["uploaded", "armory", "shipyard"];

    private static readonly (string Name, int W, int H)[] StockSizes =
    [
        ("bullets", 20, 50), ("bombs", 160, 208), ("mines", 160, 128), ("shrapnel", 80, 48), ("repel", 480, 192), ("rocket", 312, 48),
        ("prizes", 160, 16), ("powerb", 160, 48), ("flag", 160, 32), ("goal", 144, 32),
        ("explode0", 112, 16), ("explode1", 288, 288), ("explode2", 320, 880), ("empburst", 400, 160), ("warp", 288, 144),
        ("warppnt", 160, 16), ("exhaust", 304, 32), ("shield", 160, 16), ("super", 160, 16),
        ("over1", 240, 32), ("over2", 320, 96), ("over3", 240, 32), ("over4", 480, 192), ("over5", 320, 480), ("wall", 160, 32),
    ];

    public const int MaxGalleryThumbBytes = 128 * 1024;

    /// <summary>
    /// Kinds that aren't stock-size pictures, each from its own partial file (AccountStore.GalleryMaps.cs,
    /// AccountStore.GalleryBanners.cs...). Add yours here: this one line is the only shared edit.
    /// Declared before <see cref="GalleryKinds"/>, which lists them last.
    /// </summary>
    public static readonly IReadOnlyList<GalleryFileKind> GalleryFileKinds = [.. MapGalleryKinds()];

    /// <summary>Ships first, then the armory's weapons, then the rest in the game's order, then the file kinds. The gradient (a colour table) isn't shared.</summary>
    public static readonly IReadOnlyList<GalleryKind> GalleryKinds =
    [
        new("ship", "Ship", "Ships", 360, 144, "shipyard"),
        .. StockSizes.Select(s => (Slot: GameGraphics.Find(s.Name)!, s.W, s.H))
            .Select(s => new GalleryKind(s.Slot.Name, s.Slot.Label, s.Slot.Group, s.W, s.H,
                FindArmoryKind(s.Slot.Name) is null ? "graphics" : "armory")),
        .. GalleryFileKinds.Select(f => f.Kind),
    ];

    public static GalleryFileKind? FindGalleryFileKind(string? name) =>
        GalleryFileKinds.FirstOrDefault(k => string.Equals(k.Kind.Name, name, StringComparison.OrdinalIgnoreCase));

    public static GalleryKind? FindGalleryKind(string? name) =>
        GalleryKinds.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool IsShipGrid(int w, int h) => w % 10 == 0 && h == w / 10 * 4 && w / 10 is >= 8 and <= 96;

    /// <summary>
    /// The kinds <paramref name="image"/> can be, from its size and frame grid: a ship sheet is 10×4
    /// square frames (any side 8..96, and a PNG), every other graphic is its exact stock size. Several
    /// stock sheets share a size (repel and the space station 480×192, which is also a 48-pixel ship;
    /// the EMP burst 400×160, also a 40-pixel ship; prizes, shield, super and warp point 160×16; flag
    /// and bricks 160×32; both small asteroids 240×32), so the answer can hold more than one; empty
    /// with <paramref name="error"/> when it fits none.
    /// </summary>
    public static IReadOnlyList<string> DetectGalleryKinds(ReadOnlySpan<byte> image, out int width, out int height, out string error)
    {
        width = height = 0;
        // File kinds first, on the raw bytes: a map isn't a picture, and a map with a tileset starts like a BMP.
        var files = new List<string>();
        var fileError = "";
        foreach (var f in GalleryFileKinds)
        {
            if (!f.Fits(image)) continue;
            if (image.Length > f.MaxBytes) fileError = $"{f.Kind.Label}s must be {f.MaxBytes / 1024} KB or smaller.";
            else if (f.Validate(image) is { } bad) fileError = bad;
            else files.Add(f.Kind.Name);
        }
        var matches = DetectPictureKinds(image, out width, out height, out error);
        if (files.Count > 0)
        {
            error = "";
            return [.. files, .. matches.Where(m => !files.Contains(m))];
        }
        if (fileError.Length > 0) { error = fileError; return []; }
        return matches;
    }

    private static List<string> DetectPictureKinds(ReadOnlySpan<byte> image, out int width, out int height, out string error)
    {
        width = height = 0;
        if (!GameGraphics.TryCheckImage(image, out var ext, out width, out height, out error)) return [];
        if (width % 10 == 0 && width / 10 is >= 8 and <= 96 && height == width / 10 * 32)
        {
            error = $"That is all eight ships in one sheet ({width}×{height}). Share one ship at a time: its own 4 rows, {width}×{width / 10 * 4}.";
            return [];
        }
        if (image.Length > MaxGalleryImageBytes) { error = $"That picture is over {MaxGalleryImageBytes / 1024} KB."; return []; }
        var matches = new List<string>();
        if (IsShipGrid(width, height) && ext == ".png") matches.Add("ship");
        foreach (var k in GalleryKinds)
            if (k.Name != "ship" && k.Width == width && k.Height == height && FindGalleryFileKind(k.Name) is null) matches.Add(k.Name);
        // Weapons before ships before the rest, so the likelier reading comes first.
        matches.Sort((a, b) => Rank(a).CompareTo(Rank(b)));
        if (matches.Count > 0) { error = ""; return matches; }

        if (IsShipGrid(width, height))
            error = "Save ship sheets as PNG.";
        else
            error = $"No game graphic is {width}×{height}. A ship is 40 square frames in 10 columns and 4 rows (360×144 for 36-pixel ships); " +
                    "every other graphic keeps the game's own size, e.g. bullets 20×50, bombs 160×208, mines 160×128, shrapnel 80×48, repel 480×192, rocket 312×48.";
        return [];

        static int Rank(string k) => FindArmoryKind(k) is not null ? 0 : k == "ship" ? 1 : 2;
    }

    /// <summary>Lower-case words of <paramref name="text"/> (letters and digits), each once, small words dropped: what keyword filters match.</summary>
    public static string GalleryKeywords(params string?[] text)
    {
        var stop = new HashSet<string> { "a", "an", "the", "of", "and", "or", "with", "in", "on", "to", "for", "is", "it", "its" };
        var words = new List<string>();
        foreach (var t in text)
            foreach (var w in WordsOf(t ?? ""))
                if (w.Length > 1 && !stop.Contains(w) && !words.Contains(w) && words.Count < 32) words.Add(w);
        return string.Join(' ', words);
    }

    private static IEnumerable<string> WordsOf(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { if (sb.Length < 24) sb.Append(ch); }
            else if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>
    /// Shares <paramref name="image"/>. <paramref name="kind"/> may be empty: the size decides, and when
    /// several kinds fit, the result asks for one of its <see cref="GalleryAddResult.Choices"/>. A chosen
    /// kind must be one the size fits. <paramref name="ship"/> is the ship a ship sheet was made for (0 = any).
    /// </summary>
    public GalleryAddResult AddGalleryItem(long accountId, string? kind, int ship, string? name, string? keywords, string? prompt,
        string source, byte[] image, byte[]? thumb = null)
    {
        if (!GallerySources.Contains(source)) return new(false, "Unknown source.", 0, []);
        var matches = DetectGalleryKinds(image, out _, out _, out var why);
        if (matches.Count == 0) return new(false, why, 0, []);
        string chosen;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (FindGalleryKind(kind) is not { } k) return new(false, "That isn't one of the kinds.", 0, matches);
            if (!matches.Contains(k.Name))
                return new(false, $"That picture can't be {Article(k.Label)}: its size fits {string.Join(", ", matches.Select(LabelOf))}.", 0, matches);
            chosen = k.Name;
        }
        else if (matches.Count == 1) chosen = matches[0];
        else return new(false, $"That size fits more than one kind: {string.Join(", ", matches.Select(LabelOf))}. Choose which it is.", 0, matches);

        if (FindGalleryFileKind(chosen) is { } file)
        {
            if (file.Stored is { } cut) image = cut(image);
            if (thumb is { Length: > 0 })
            {
                if (file.ThumbWidth == 0) return new(false, $"{LabelOf(chosen)}s don't take a preview picture.", 0, []);
                if (thumb.Length > MaxGalleryThumbBytes || !IsPngOfSize(thumb, file.ThumbWidth, file.ThumbHeight))
                    return new(false, $"The preview must be a {file.ThumbWidth}×{file.ThumbHeight} PNG of at most {MaxGalleryThumbBytes / 1024} KB.", 0, []);
            }
            else if (file.ThumbWidth > 0) return new(false, $"{Article(file.Kind.Label)[..1].ToUpperInvariant()}{Article(file.Kind.Label)[1..]} needs its {file.ThumbWidth}×{file.ThumbHeight} preview picture (the Foundry page draws it when you share).", 0, []);
            else thumb = null;
        }
        else thumb = null;
        if (chosen != "ship") ship = 0;
        if (ship is < 0 or > ShipSlots) return new(false, "Choose any ship, or a ship from 1 to 8.", 0, []);
        var label = LabelOf(chosen);
        var clean = CleanShipName(string.IsNullOrWhiteSpace(name) ? label : name);
        var cleanPrompt = CleanPrompt(prompt);
        if ((keywords ?? "").Length > MaxGalleryKeywordsLength) return new(false, $"Keep the keywords under {MaxGalleryKeywordsLength + 1} characters.", 0, []);
        var words = GalleryKeywords(clean, keywords, cleanPrompt);
        var sha = Convert.ToHexString(SHA256.HashData(image));
        long id = 0;
        var r = Tx(() =>
        {
            if (Scalar("SELECT COUNT(*) FROM accounts WHERE id = $id AND status = 'active'", ("$id", accountId)) == 0)
                return Fail("Sign in again first.");
            if (Scalar("SELECT COUNT(*) FROM gallery_items WHERE sha = $h", ("$h", sha)) > 0)
                return Fail("That exact picture is already shared.");
            if (Scalar("SELECT COUNT(*) FROM gallery_items WHERE person_id = $p", ("$p", PersonKey(accountId))) >= MaxGalleryPerAccount)
                return Fail($"You've shared {MaxGalleryPerAccount}, the most one pilot can. Take one down first.");
            if (Scalar("SELECT COUNT(*) FROM gallery_items") >= MaxGalleryItems)
                return Fail("The gallery is full for now.");
            Run("INSERT INTO gallery_items(account_id, person_id, kind, ship, name, keywords, prompt, source, png, sha, created, thumb) VALUES($a, $who, $k, $s, $n, $w, $p, $src, $img, $h, $c, $th)", ("$who", PersonKey(accountId)),
                ("$a", accountId), ("$k", chosen), ("$s", ship), ("$n", clean), ("$w", words), ("$p", cleanPrompt), ("$src", source),
                ("$img", image), ("$h", sha), ("$c", Now()), ("$th", (object?)thumb ?? DBNull.Value));
            id = Scalar("SELECT last_insert_rowid()");
            return Ok($"Shared: {label.ToLowerInvariant()} “{clean}”.");
        });
        return new(r.Ok, r.Message, r.Ok ? id : 0, []);
    }

    /// <summary>
    /// Shares one of the pilot's own saved sheets (<paramref name="from"/> "armory" or "shipyard"),
    /// with its name and the prompt it was drawn from. Weapons keep their armory kind.
    /// </summary>
    public GalleryAddResult AddGalleryFromSaved(long accountId, string? from, long itemId, string? kind, int ship, string? name, string? keywords)
    {
        string savedKind, savedName, prompt;
        byte[] image;
        lock (_gate)
        {
            var sql = from switch
            {
                "armory" => "SELECT kind, name, prompt, png FROM armory_items WHERE id = $id AND person_id = $a",
                "shipyard" => "SELECT 'ship', name, prompt, png FROM shipyard_ships WHERE id = $id AND person_id = $a",
                _ => null,
            };
            if (sql is null) return new(false, "Choose your armory or your shipyard.", 0, []);
            using var c = Cmd(sql, ("$id", itemId), ("$a", PersonKey(accountId)));
            using var r = c.ExecuteReader();
            if (!r.Read()) return new(false, $"That isn't in your {from}.", 0, []);
            (savedKind, savedName, prompt, image) = (r.GetString(0), r.GetString(1), r.GetString(2), (byte[])r.GetValue(3));
        }
        return AddGalleryItem(accountId, string.IsNullOrWhiteSpace(kind) ? savedKind : kind, ship,
            string.IsNullOrWhiteSpace(name) ? savedName : name, keywords, prompt, from!, image);
    }

    /// <summary>
    /// The gallery newest first, filtered by kind (and for ships, the ship it was made for: that
    /// ship's plus any-ship ones), by keywords (every word must start a keyword), and by contributor.
    /// </summary>
    public GalleryPage Gallery(long viewerId, string? kind = null, int ship = 0, string? query = null, string? pilot = null,
        long before = 0, int limit = 30)
    {
        limit = Math.Clamp(limit, 1, MaxGalleryPageSize);
        var where = new List<string>();
        var ps = new List<(string, object)>();
        if (!string.IsNullOrWhiteSpace(kind))
        {
            where.Add("g.kind = $k");
            ps.Add(("$k", FindGalleryKind(kind)?.Name ?? kind));
            if (ship is >= 1 and <= ShipSlots) { where.Add("g.ship IN (0, $s)"); ps.Add(("$s", ship)); }
        }
        var n = 0;
        foreach (var w in WordsOf(query ?? "").Distinct().Take(8))
        {
            where.Add($"(' ' || g.keywords) LIKE $q{n} ESCAPE '\\'");
            ps.Add(($"$q{n}", "% " + w.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));
            n++;
        }
        if (!string.IsNullOrWhiteSpace(pilot)) { where.Add(GalleryCredit + " = $p COLLATE NOCASE"); ps.Add(("$p", pilot.Trim())); }
        var filter = where.Count == 0 ? "" : " AND " + string.Join(" AND ", where);
        // credited to the name that shared it; once that name is deleted, to the account's first remaining name
        const string from = " FROM gallery_items g LEFT JOIN accounts a ON a.id = g.account_id WHERE " + GalleryCredit + " IS NOT NULL";
        lock (_gate)
        {
            var viewer = PersonKey(viewerId);
            var count = Scalar("SELECT COUNT(*)" + from + filter, [.. ps]);
            var items = new List<GalleryItem>();
            var page = filter + (before > 0 ? " AND g.id < $before" : "");
            using (var c = Cmd("SELECT g.id, g.kind, g.ship, g.name, g.keywords, g.prompt, g.source, " + GalleryCredit + ", g.person_id, g.created, g.saves" + from + page +
                       " ORDER BY g.id DESC LIMIT $limit", [.. ps, ("$before", before), ("$limit", limit + 1)]))
            using (var r = c.ExecuteReader())
                while (r.Read())
                    items.Add(new GalleryItem(r.GetInt64(0), r.GetString(1), (int)r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5),
                        r.GetString(6), r.GetString(7), !r.IsDBNull(8) && r.GetInt64(8) == viewer, r.GetString(9), r.GetInt64(10)));
            var next = items.Count > limit ? items[limit - 1].Id : 0;
            if (items.Count > limit) items.RemoveAt(limit);
            return new GalleryPage(items, count, next);
        }
    }

    /// <summary>A shared design's picture and kind, or null.</summary>
    public (byte[] Image, string Kind, string Name)? GalleryImage(long itemId)
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT png, kind, name FROM gallery_items WHERE id = $id", ("$id", itemId));
            using var r = c.ExecuteReader();
            return r.Read() ? ((byte[])r.GetValue(0), r.GetString(1), r.GetString(2)) : null;
        }
    }

    /// <summary>A shared design's preview PNG (file kinds that have one), or null.</summary>
    public byte[]? GalleryThumb(long itemId)
    {
        lock (_gate)
        {
            using var c = Cmd("SELECT thumb FROM gallery_items WHERE id = $id", ("$id", itemId));
            return c.ExecuteScalar() is byte[] b ? b : null;
        }
    }

    /// <summary>Counts one more keep of <paramref name="itemId"/> (a keep that happened outside this store, e.g. into the map library).</summary>
    public void CountGallerySave(long itemId) =>
        Tx(() => { Run("UPDATE gallery_items SET saves = saves + 1 WHERE id = $id", ("$id", itemId)); return Ok(""); });

    private static bool IsPngOfSize(ReadOnlySpan<byte> b, int w, int h) =>
        b.Length >= 33 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) && b.Slice(12, 4).SequenceEqual("IHDR"u8)
        && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b[16..20]) == w && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b[20..24]) == h;

    /// <summary>Takes a design down: its contributor may, and so may staff (<paramref name="staff"/>).</summary>
    public Result RemoveGalleryItem(long accountId, long itemId, bool staff) => Tx(() =>
    {
        if (Scalar("SELECT COUNT(*) FROM gallery_items WHERE id = $id", ("$id", itemId)) == 0) return Fail("That design isn't in the gallery.");
        var owner = Scalar("SELECT COALESCE((SELECT person_id FROM gallery_items WHERE id = $id), 0)", ("$id", itemId));
        if (owner != PersonKey(accountId) && !staff) return Fail("Only the pilot who shared it, or staff, can take it down.");
        Run("DELETE FROM gallery_items WHERE id = $id", ("$id", itemId));
        return Ok("Taken down.");
    });

    /// <summary>
    /// Keeps a copy of a shared design: ships go to the pilot's shipyard, weapons to their armory,
    /// other graphics become their own picture for that graphic (replacing the one before).
    /// </summary>
    public Result SaveGalleryItem(long accountId, long itemId)
    {
        string kind, name, prompt;
        byte[] image;
        lock (_gate)
        {
            using var c = Cmd("SELECT kind, name, prompt, png FROM gallery_items WHERE id = $id", ("$id", itemId));
            using var r = c.ExecuteReader();
            if (!r.Read()) return Fail("That design isn't in the gallery.");
            (kind, name, prompt, image) = (r.GetString(0), r.GetString(1), r.GetString(2), (byte[])r.GetValue(3));
        }
        var k = FindGalleryKind(kind);
        var result = k?.Save switch
        {
            "shipyard" => AddShipyardShip(accountId, name, "uploaded", image, out _, prompt),
            "armory" => AddArmoryItem(accountId, kind, name, "uploaded", image, out _, prompt),
            "graphics" => SetPilotGraphic(accountId, kind, image),
            // file kinds kept somewhere other than the account (maps and tilesets: the editor's library) are kept by the web layer
            _ => Fail("That kind can't be kept."),
        };
        if (!result.Ok) return result;
        CountGallerySave(itemId);
        return result;
    }

    private static string LabelOf(string kind) => FindGalleryKind(kind)?.Label ?? kind;

    private static string Article(string label) =>
        ("aeiou".Contains(char.ToLowerInvariant(label[0])) ? "an " : "a ") + label.ToLowerInvariant();

    private void CreateGalleryTables()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS gallery_items (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              account_id INTEGER NOT NULL,
              kind TEXT NOT NULL,
              ship INTEGER NOT NULL DEFAULT 0,
              name TEXT NOT NULL,
              keywords TEXT NOT NULL,
              prompt TEXT NOT NULL,
              source TEXT NOT NULL,
              png BLOB NOT NULL,
              sha TEXT NOT NULL UNIQUE,
              created TEXT NOT NULL,
              saves INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_gallery_account ON gallery_items(account_id);
            CREATE INDEX IF NOT EXISTS ix_gallery_kind ON gallery_items(kind, id);
            """);
        // gallery_items.person_id (the account that owns it) is added by AddLibraryPersons
        // A file kind's preview PNG (maps), built once when it is shared.
        if (Scalar("SELECT COUNT(*) FROM pragma_table_info('gallery_items') WHERE name = 'thumb'") == 0)
            Exec("ALTER TABLE gallery_items ADD COLUMN thumb BLOB NULL;");
        // The prompt a saved sheet was drawn from (added later), so shared ones can be found by it.
        foreach (var table in new[] { "armory_items", "shipyard_ships" })
            if (Scalar($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = 'prompt'") == 0)
            {
                Exec($"ALTER TABLE {table} ADD COLUMN prompt TEXT NOT NULL DEFAULT '';");
                // Before then a prompted sheet was named after its prompt's first 40 characters: the best there is.
                Exec($"UPDATE {table} SET prompt = name WHERE source = 'generated';");
            }
    }
}
