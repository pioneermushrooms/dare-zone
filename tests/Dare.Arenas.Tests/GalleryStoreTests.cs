using System.Buffers.Binary;
using Dare.Arenas.Core;
using Microsoft.Data.Sqlite;

namespace Dare.Arenas.Tests;

// The shared gallery: kinds come from a sheet's size (checked against the game's own sheets), shared
// designs are found by kind, ship, keyword and pilot, kept as copies, and taken down by their pilot or staff.
public sealed class GalleryStoreTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"dare-gallery-{Guid.NewGuid():N}.db");
    private AccountStore _s;
    private readonly Account _dare;
    private readonly Account _other;
    private byte _mark;

    public GalleryStoreTests()
    {
        _s = Open();
        _dare = Signup("dare");
        _other = Signup("other");
    }

    private Account Signup(string name)
    {
        Assert.True(_s.StartSignup(name, "hunter22pw", name + "@example.com", out var code).Ok);
        Assert.True(_s.VerifySignup(name + "@example.com", code, out var a).Ok);
        return a!;
    }

    private AccountStore Open() => new(_db, () => new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _s.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm", _db + "-journal" })
            try { File.Delete(f); } catch { }
    }

    /// <summary>A PNG header of that size; each call's bytes differ, so none is a duplicate.</summary>
    private byte[] Png(uint w, uint h)
    {
        var b = new byte[33 + 64];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 13);
        "IHDR"u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), w);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), h);
        b[^1] = ++_mark;
        return b;
    }

    private static byte[] Bmp(int w, int h)
    {
        var b = new byte[54 + 16];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BitConverter.GetBytes(w).CopyTo(b, 18);
        BitConverter.GetBytes(h).CopyTo(b, 22);
        return b;
    }

    private static IReadOnlyList<string> Detect(byte[] image) => AccountStore.DetectGalleryKinds(image, out _, out _, out _);

    private static string StockFile(string name) => Path.Combine(AppContext.BaseDirectory, "gfx", name + ".bm2");

    // Every stock sheet is recognised as its own kind, and only the sizes that really are shared are ambiguous.
    [Fact]
    public void every_stock_sheet_is_detected_as_its_own_kind()
    {
        var shared = new Dictionary<string, string[]>
        {
            ["repel"] = ["repel", "ship", "over4"],
            ["over4"] = ["repel", "ship", "over4"],
            ["empburst"] = ["ship", "empburst"],
            ["flag"] = ["flag", "wall"], ["wall"] = ["flag", "wall"],
            ["over1"] = ["over1", "over3"], ["over3"] = ["over1", "over3"],
            ["prizes"] = ["prizes", "warppnt", "shield", "super"], ["shield"] = ["prizes", "warppnt", "shield", "super"],
            ["super"] = ["prizes", "warppnt", "shield", "super"], ["warppnt"] = ["prizes", "warppnt", "shield", "super"],
        };
        foreach (var kind in AccountStore.GalleryKinds.Where(k => k.Name != "ship"))
        {
            var file = File.ReadAllBytes(StockFile(kind.Name));
            var found = AccountStore.DetectGalleryKinds(file, out var w, out var h, out var error);
            Assert.True(found.Count > 0, $"{kind.Name}: {error}");
            Assert.Equal((kind.Width, kind.Height), (w, h));
            Assert.Equal(shared.TryGetValue(kind.Name, out var expect) ? expect : [kind.Name], found);
        }
        // Every graphic the game lets arenas replace is shareable, except the colour table and ships (one "ship" kind).
        Assert.Equal(GameGraphics.Slots.Where(s => GameGraphics.ShipIndex(s.Name) == 0 && s.Name != "gradient").Select(s => s.Name).Order(),
            AccountStore.GalleryKinds.Where(k => k.Name != "ship").Select(k => k.Name).Order());
    }

    // The stock ships come as one sheet of all eight; one ship's 4 rows is a ship at any frame size.
    [Fact]
    public void ships_are_one_kind_and_the_whole_stock_sheet_is_refused()
    {
        Assert.Equal(["ship"], Detect(Png(360, 144)));
        Assert.Equal(["ship"], Detect(Png(640, 256))); // 64-pixel ship
        Assert.Empty(AccountStore.DetectGalleryKinds(File.ReadAllBytes(StockFile("ships")), out _, out _, out var whole));
        Assert.Contains("all eight ships", whole);
        Assert.Empty(AccountStore.DetectGalleryKinds(Png(360, 1152), out _, out _, out whole));
        Assert.Contains("360×144", whole);
        Assert.Empty(AccountStore.DetectGalleryKinds(Bmp(360, 144), out _, out _, out var bmp));
        Assert.Contains("PNG", bmp);
        Assert.Equal(["bombs"], Detect(Bmp(160, 208))); // other graphics may be BMP, like the game's own
    }

    [Fact]
    public void a_size_that_fits_nothing_is_refused_clearly()
    {
        Assert.Empty(AccountStore.DetectGalleryKinds(Png(161, 208), out _, out _, out var why));
        Assert.Contains("No game graphic is 161×208", why);
        Assert.Empty(AccountStore.DetectGalleryKinds("GIF89a........................"u8.ToArray(), out _, out _, out why));
        Assert.Contains("PNG or BMP", why);
        var big = new byte[AccountStore.MaxGalleryImageBytes + 1];
        Png(160, 208).CopyTo(big, 0);
        Assert.Empty(AccountStore.DetectGalleryKinds(big, out _, out _, out why));
        Assert.Contains("KB", why);
    }

    // Ambiguous sizes ask, and a chosen kind must be one the size fits.
    [Fact]
    public void ambiguous_sheets_ask_and_wrong_kinds_are_refused()
    {
        var ask = _s.AddGalleryItem(_dare.Id, null, 0, "Ring", null, null, "uploaded", Png(480, 192));
        Assert.False(ask.Ok);
        Assert.Equal(["repel", "ship", "over4"], ask.Choices);
        Assert.Contains("Space station", ask.Message);

        var wrong = _s.AddGalleryItem(_dare.Id, "bombs", 0, "Ring", null, null, "uploaded", Png(480, 192));
        Assert.False(wrong.Ok);
        Assert.Contains("can't be a bombs", wrong.Message);

        Assert.True(_s.AddGalleryItem(_dare.Id, "repel", 0, "Ring", null, null, "uploaded", Png(480, 192)).Ok);
        Assert.True(_s.AddGalleryItem(_dare.Id, "", 0, "Plasma", null, null, "uploaded", Png(160, 208)).Ok); // one fit: no need to say
        var page = _s.Gallery(0);
        Assert.Equal(["bombs", "repel"], page.Items.Select(i => i.Kind));
        Assert.Equal(2, page.Count);
    }

    [Fact]
    public void the_same_picture_is_shared_once_and_each_pilot_has_a_limit()
    {
        var png = Png(160, 208);
        Assert.True(_s.AddGalleryItem(_dare.Id, null, 0, "A", null, null, "uploaded", png).Ok);
        var again = _s.AddGalleryItem(_other.Id, null, 0, "B", null, null, "uploaded", png);
        Assert.False(again.Ok);
        Assert.Contains("already shared", again.Message);
        for (var i = 1; i < AccountStore.MaxGalleryPerAccount; i++)
            Assert.True(_s.AddGalleryItem(_dare.Id, null, 0, "x", null, null, "uploaded", Png(20, 50)).Ok);
        var full = _s.AddGalleryItem(_dare.Id, null, 0, "x", null, null, "uploaded", Png(20, 50));
        Assert.False(full.Ok);
        Assert.Contains("Take one down", full.Message);
    }

    [Fact]
    public void filters_by_kind_ship_keyword_and_pilot_with_credit_and_paging()
    {
        var a = _s.AddGalleryItem(_dare.Id, null, 3, "Red Spider", "crimson, arachnid", null, "uploaded", Png(360, 144)).Id;
        var b = _s.AddGalleryItem(_dare.Id, null, 0, "Any ship", null, null, "uploaded", Png(360, 144)).Id;
        var c = _s.AddGalleryItem(_other.Id, null, 0, "Plasma orbs", "glowing", "Glowing green plasma orbs", "uploaded", Png(160, 208)).Id;
        var d = _s.AddGalleryItem(_other.Id, null, 5, "Bombs aren't ships", null, null, "uploaded", Png(160, 208)).Id; // ship ignored

        Assert.Equal([b, a], _s.Gallery(0, kind: "ship").Items.Select(i => i.Id));
        Assert.Equal([b, a], _s.Gallery(0, kind: "ship", ship: 3).Items.Select(i => i.Id)); // the Spider's, and any ship's
        Assert.Equal([b], _s.Gallery(0, kind: "ship", ship: 1).Items.Select(i => i.Id));
        Assert.Equal(0, _s.Gallery(0).Items.Single(i => i.Id == d).Ship);
        Assert.Equal([c], _s.Gallery(0, query: "GREEN pla").Items.Select(i => i.Id)); // words from the prompt, prefixes
        Assert.Equal([a], _s.Gallery(0, query: "arach").Items.Select(i => i.Id));
        Assert.Empty(_s.Gallery(0, query: "rimson").Items); // a word must start a keyword
        Assert.Empty(_s.Gallery(0, query: "100%").Items);
        Assert.Equal([d, c], _s.Gallery(0, pilot: "OTHER").Items.Select(i => i.Id));

        var page = _s.Gallery(_other.Id, limit: 3);
        Assert.Equal(4, page.Count);
        Assert.Equal([d, c, b], page.Items.Select(i => i.Id));
        Assert.Equal(b, page.Next);
        Assert.Equal(["other", "other", "dare"], page.Items.Select(i => i.By));
        Assert.Equal([true, true, false], page.Items.Select(i => i.Mine));
        var rest = _s.Gallery(0, before: page.Next, limit: 3);
        Assert.Equal([a], rest.Items.Select(i => i.Id));
        Assert.Equal(0, rest.Next);
    }

    [Fact]
    public void the_pilot_or_staff_can_take_one_down()
    {
        var id = _s.AddGalleryItem(_dare.Id, null, 0, "Mine", null, null, "uploaded", Png(160, 128)).Id;
        var refused = _s.RemoveGalleryItem(_other.Id, id, staff: false);
        Assert.False(refused.Ok);
        Assert.Contains("Only the pilot", refused.Message);
        Assert.True(_s.RemoveGalleryItem(_other.Id, id, staff: true).Ok);
        Assert.Null(_s.GalleryImage(id));
        var own = _s.AddGalleryItem(_dare.Id, null, 0, "Mine", null, null, "uploaded", Png(160, 128)).Id;
        Assert.True(_s.RemoveGalleryItem(_dare.Id, own, staff: false).Ok);
        Assert.False(_s.RemoveGalleryItem(_dare.Id, own, staff: false).Ok);
    }

    // A kept copy goes where that kind lives: shipyard, armory, or the pilot's own other graphics.
    [Fact]
    public void keeping_a_copy_goes_to_the_shipyard_armory_or_graphics()
    {
        var ship = _s.AddGalleryItem(_dare.Id, null, 2, "Dart", null, null, "uploaded", Png(360, 144)).Id;
        var bombs = _s.AddGalleryItem(_dare.Id, null, 0, "Orbs", null, null, "uploaded", Png(160, 208)).Id;
        var boom = _s.AddGalleryItem(_dare.Id, null, 0, "Boom", null, null, "uploaded", Png(288, 288)).Id;

        Assert.True(_s.SaveGalleryItem(_other.Id, ship).Ok);
        Assert.True(_s.SaveGalleryItem(_other.Id, bombs).Ok);
        Assert.True(_s.SaveGalleryItem(_other.Id, boom).Ok);
        Assert.Equal("Dart", _s.Shipyard(_other.Id).Ships.Single().Name);
        Assert.Equal(("bombs", "Orbs"), (_s.Armory(_other.Id).Items.Single().Kind, _s.Armory(_other.Id).Items.Single().Name));
        Assert.Equal(_s.GalleryImage(boom)!.Value.Image, _s.PilotGraphic(_other.Id, "explode1"));
        Assert.Equal(1, _s.Gallery(0).Items.Single(i => i.Id == ship).Saves);
        Assert.False(_s.SaveGalleryItem(_other.Id, 999).Ok);
    }

    // Sharing from your own armory or shipyard brings the prompt along, so others can find it by it.
    [Fact]
    public void sharing_a_saved_sheet_keeps_its_prompt_as_keywords()
    {
        Assert.True(_s.AddArmoryItem(_dare.Id, "mines", "Spiky", "generated", Png(160, 128), out var mine, "rusty spiked sea mine").Ok);
        Assert.Equal("rusty spiked sea mine", _s.Armory(_dare.Id).Items.Single().Prompt);
        Assert.True(_s.AddShipyardShip(_dare.Id, "Hornet", "generated", Png(360, 144), out var hornet, "yellow wasp fighter").Ok);
        Assert.Equal("yellow wasp fighter", _s.Shipyard(_dare.Id).Ships.Single().Prompt);

        var m = _s.AddGalleryFromSaved(_dare.Id, "armory", mine, null, 0, null, "ocean");
        Assert.True(m.Ok, m.Message);
        var h = _s.AddGalleryFromSaved(_dare.Id, "shipyard", hornet, null, 4, null, null);
        Assert.True(h.Ok, h.Message);
        Assert.False(_s.AddGalleryFromSaved(_other.Id, "armory", mine, null, 0, null, null).Ok); // not theirs

        var shared = _s.Gallery(0).Items;
        var sm = shared.Single(i => i.Id == m.Id);
        Assert.Equal(("mines", "Spiky", "armory", "rusty spiked sea mine"), (sm.Kind, sm.Name, sm.Source, sm.Prompt));
        Assert.Equal("spiky ocean rusty spiked sea mine", sm.Keywords);
        Assert.Equal(4, shared.Single(i => i.Id == h.Id).Ship);
        Assert.Equal([m.Id], _s.Gallery(0, query: "sea").Items.Select(i => i.Id));
    }

    // Before prompts were kept, a prompted sheet was named after its prompt; that is filled in once.
    [Fact]
    public void prompts_are_backfilled_from_the_names_of_prompted_sheets()
    {
        Assert.True(_s.AddArmoryItem(_dare.Id, "bombs", "glowing purple orb", "generated", Png(160, 208), out _).Ok);
        Assert.True(_s.AddArmoryItem(_dare.Id, "bombs", "Bombs", "random", Png(160, 208), out _).Ok);
        Assert.True(_s.AddShipyardShip(_dare.Id, "chrome needle", "generated", Png(360, 144), out _).Ok);
        _s.Dispose();
        SqliteConnection.ClearAllPools();
        using (var conn = new SqliteConnection($"Data Source={_db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE armory_items DROP COLUMN prompt; ALTER TABLE shipyard_ships DROP COLUMN prompt;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        _s = Open();
        Assert.Equal(["glowing purple orb", ""], _s.Armory(_dare.Id).Items.Select(i => i.Prompt));
        Assert.Equal("chrome needle", _s.Shipyard(_dare.Id).Ships.Single().Prompt);
    }

    [Fact]
    public void keywords_are_lower_case_words_once_without_small_words()
    {
        Assert.Equal("red spider crimson 2nd", AccountStore.GalleryKeywords("Red Spider", "crimson, the red; a 2nd"));
        Assert.Equal("", AccountStore.GalleryKeywords(null, "  ,, "));
    }
}
