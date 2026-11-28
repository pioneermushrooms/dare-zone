using Dare.Arenas.Core;

namespace Dare.Arenas.Tests;

public class TutorialSeatsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TileSpot Spot = new(474, 462);
    private static readonly CastLine Placed = new("swarm", "in", "chase") { At = Spot };
    private static readonly CastLine Loose = new("swarm", "in", "chase");

    [Fact] public void SpectatorIntoShip_WarpsToTheLinesSpot()
    {
        var seats = new TutorialSeats();
        Assert.Equal(Spot, seats.Changed(0, wasSpec: true, isSpec: false, freqChanged: true, Placed, T0));
    }

    [Fact] public void NoSpot_NoWarp() => Assert.Null(new TutorialSeats().Changed(0, true, false, false, Loose, T0));

    [Fact] public void FreqSettlingJustAfterTheSeat_WarpsAgain_LaterChangesDoNot()
    {
        var seats = new TutorialSeats();
        seats.Changed(0, true, false, false, Placed, T0);
        Assert.Equal(Spot, seats.Changed(0, false, false, freqChanged: true, Placed, T0.AddSeconds(1)));
        Assert.Null(seats.Changed(0, false, false, freqChanged: true, Placed, T0.AddSeconds(5)));
        Assert.Null(seats.Changed(0, false, false, freqChanged: false, Placed, T0.AddSeconds(1)));
    }

    [Fact] public void GoingToSpec_ThenBackIn_WarpsAgain()
    {
        var seats = new TutorialSeats();
        seats.Changed(0, true, false, false, Placed, T0);
        Assert.Null(seats.Changed(0, false, true, true, Placed, T0.AddSeconds(10)));
        Assert.Equal(Spot, seats.Changed(0, true, false, false, Placed, T0.AddSeconds(20)));
    }

    [Fact] public void StepStart_WarpsABotInItsShip_OnlyIfJustSeatedOrNeverWarped()
    {
        var seats = new TutorialSeats();
        Assert.Null(seats.StepStarted(0, inShip: false, Placed, T0));
        // sat down with no spot, then a step gives it one: never warped, so warped once
        seats.Changed(0, true, false, false, Loose, T0);
        Assert.Equal(Spot, seats.StepStarted(0, true, Placed, T0.AddSeconds(10)));
        Assert.Null(seats.StepStarted(0, true, Placed, T0.AddSeconds(20)));
        // another bot sat down a moment before the step: warped though it was warped before
        seats.Changed(1, true, false, false, Placed, T0.AddSeconds(30));
        Assert.Equal(Spot, seats.StepStarted(1, true, Placed, T0.AddSeconds(31)));
        Assert.Null(seats.StepStarted(1, true, Placed, T0.AddSeconds(40)));
    }

    [Fact] public void BotsAreTrackedApart()
    {
        var seats = new TutorialSeats();
        seats.Changed(0, true, false, false, Placed, T0);
        Assert.Equal(Spot, seats.StepStarted(1, true, Placed, T0.AddSeconds(10)));
        Assert.Null(seats.StepStarted(0, true, Placed, T0.AddSeconds(10)));
    }
}
