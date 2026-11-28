namespace Dare.Arenas.Core;

/// <summary>
/// What one pilot name shows as owner of (the delete prompt lists it). When the name is deleted
/// its account keeps all of it: <see cref="HandOverName"/> moves it to another of its names.
/// </summary>
public sealed partial class ArenaStore
{
    /// <summary>What a name owns: public arenas (not archived) and private drafts (not archived, not public).</summary>
    public (IReadOnlyList<string> Arenas, IReadOnlyList<string> Drafts) Holdings(string player)
    {
        var arenas = new List<string>();
        var drafts = new List<string>();
        Tx(() =>
        {
            using (var c = Cmd("SELECT name FROM arenas WHERE owner = $p AND status <> 'Archived' ORDER BY name", ("$p", player)))
            using (var r = c.ExecuteReader())
                while (r.Read()) arenas.Add(r.GetString(0));
            using (var c = Cmd("SELECT name FROM drafts WHERE owner = $p AND status <> 'Archived' AND live = 0 ORDER BY name", ("$p", player)))
            using (var r = c.ExecuteReader())
                while (r.Read()) drafts.Add(r.GetString(0));
            return Ok("");
        });
        return (arenas, drafts);
    }
}
