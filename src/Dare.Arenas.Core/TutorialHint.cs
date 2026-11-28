namespace Dare.Arenas.Core;

/// <summary>
/// The tutorial's hint lines for the Dare client: an arena message to that one player, which a client that sent
/// <c>?tutorialclient</c> recognises by <see cref="Prefix"/> and hides from chat. Mentor text keeps its
/// <c>{action}</c> placeholders (the client puts in the player's own keys). Other clients never get these.
/// </summary>
public static class TutorialHint
{
    public const string Prefix = "~tut~ ";

    /// <summary>A mentor line: <c>~tut~ say &lt;step&gt;/&lt;of&gt; &lt;text&gt;</c>.</summary>
    public static string Say(int step, int of, string text) => $"{Prefix}say {step}/{of} {text}";

    /// <summary>A HUD part's cover came off: <c>~tut~ reveal radar|energy|statbox|items|chat</c>.</summary>
    public static string Reveal(HudPart part) => $"{Prefix}reveal {part.ToString().ToLowerInvariant()}";

    /// <summary>Show the Skip / Leave buttons prominently (the start, and after a minute stuck on a step).</summary>
    public static string PromptSkip() => Prefix + "prompt skip";

    /// <summary>Offer the tutorial to a newcomer who entered another arena first.</summary>
    public static string PromptTry() => Prefix + "prompt try";

    /// <summary>The tutorial is over; <c>list</c> when nobody is on, so the client opens the arena list.</summary>
    public static string End(bool showArenaList) => Prefix + (showArenaList ? "end list" : "end none");
}

/// <summary>The tutorial LVZ's object ids (tools/tutorial-lvz/build.js). All are server controlled: they start hidden.</summary>
public static class TutorialLvz
{
    public const short MentorBox = 4100;
    public const short Portrait = 4101;
    public const short PortraitTalking = 4102;
    public const short SkipPopup = 4110;
    public const short TryPopup = 4111;
    public const short Beacon = 4200;
    private const short CoverBase = 4000, UpperChatBand = 4005, LineBase = 5000;

    /// <summary>Every black cover: one per <see cref="HudPart"/>, plus the band above the chat lines.</summary>
    public static readonly IReadOnlyList<short> AllCovers = [4000, 4001, 4002, 4003, 4004, UpperChatBand];

    /// <summary>The covers a reveal turns off: the part's own, and for the chat the band above it too.</summary>
    public static IReadOnlyList<short> CoversOf(HudPart part) =>
        part == HudPart.Chat ? [(short)(CoverBase + (int)part), UpperChatBand] : [(short)(CoverBase + (int)part)];

    /// <summary>The image of one mentor line or prompt.</summary>
    public static short Line(TutorialLine line) => (short)(LineBase + line.Index);
}
