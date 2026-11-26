namespace Dare.Arenas.Core;

/// <summary>
/// Quirky space names for guests ("Wobbly Nebula", "Sneaky Quasar"): an adjective and a noun,
/// each pair a valid account name of at most 20 characters. The zone picks one per guest login
/// and skips names that are online or claimed.
/// </summary>
public static class GuestNames
{
    public static readonly IReadOnlyList<string> Adjectives =
    [
        "Wobbly", "Sneaky", "Cosmic", "Fuzzy", "Grumpy", "Sparkly", "Dizzy", "Sleepy", "Jolly", "Rusty",
        "Lucky", "Zippy", "Funky", "Gloomy", "Bouncy", "Spicy", "Crispy", "Groovy", "Nebulous", "Lunar",
        "Solar", "Orbital", "Stellar", "Feral", "Shiny", "Wonky", "Snazzy", "Turbo", "Quantum", "Atomic",
        "Plasma", "Galactic", "Rogue", "Polite", "Nervous", "Sassy", "Salty", "Cheeky", "Moody", "Breezy",
        "Hasty", "Clumsy", "Peppy", "Fizzy", "Drifting", "Lost", "Tiny", "Mighty", "Humble", "Giddy",
    ];

    public static readonly IReadOnlyList<string> Nouns =
    [
        "Nebula", "Quasar", "Comet", "Pulsar", "Asteroid", "Meteor", "Moonpie", "Starfish", "Wormhole", "Rocket",
        "Satellite", "Spacecat", "Moonbeam", "Stardust", "Supernova", "Blackhole", "Orbit", "Alien", "Astronaut",
        "Cosmonaut", "Saucer", "Martian", "Spacebug", "Toaster", "Penguin", "Llama", "Noodle", "Pickle", "Waffle",
        "Muffin", "Potato", "Walrus", "Goose", "Hamster", "Raccoon", "Teapot", "Banjo", "Kazoo", "Lobster",
        "Meatball", "Burrito", "Pretzel", "Sprocket", "Gizmo", "Widget", "Bucket", "Mothership", "Starling", "Voyager",
    ];

    /// <summary>A random name; <paramref name="next"/>(n) gives a number in [0, n).</summary>
    public static string Pick(Func<int, int> next) =>
        Adjectives[next(Adjectives.Count)] + " " + Nouns[next(Nouns.Count)];

    /// <summary>
    /// A name that <paramref name="taken"/> says is free: up to 40 random tries, then a noun with
    /// a number ("Comet 417"). Null when nothing is free.
    /// </summary>
    public static string? PickFree(Func<int, int> next, Func<string, bool> taken)
    {
        for (var i = 0; i < 40; i++)
        {
            var n = Pick(next);
            if (!taken(n)) return n;
        }
        for (var i = 0; i < 40; i++)
        {
            var n = Nouns[next(Nouns.Count)] + " " + (next(900) + 100);
            if (!taken(n)) return n;
        }
        return null;
    }
}
