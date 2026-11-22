using System;
using System.Collections.Generic;
using System.Linq;
using Viff.NeoRadar.Cdm;
using Xunit;

namespace Viff.NeoRadar.Tests.Cdm;

public sealed class SequencerTests
{
    private static readonly IReadOnlyDictionary<string, DateTimeOffset> s_none = new Dictionary<string, DateTimeOffset>();
    private static readonly DateTimeOffset s_now = TestData.Noon;

    [Fact]
    public void Build_KeepsTsatAtTobtWhenTheRunwayIsFree()
    {
        SequenceSlot slot = Assert.Single(Sequencer.Build(new[] { Flight("AFR1", tobtMinutes: 10) }, 30, s_none, s_none, s_now));

        Assert.Equal(s_now.AddMinutes(10), slot.Tsat);
        Assert.Equal(s_now.AddMinutes(20), slot.Ttot);
    }

    [Fact]
    public void Build_SpacesTakeOffsByTheRateAndDelaysStartUp()
    {
        SequenceInput[] flights = { Flight("AFR1", 10), Flight("AFR2", 10), Flight("AFR3", 10) };

        IReadOnlyList<SequenceSlot> slots = Sequencer.Build(flights, 30, s_none, s_none, s_now);

        Assert.Equal(new[] { 20, 22, 24 }, slots.Select(s => (int)(s.Ttot - s_now).TotalMinutes));
        Assert.Equal(new[] { 10, 12, 14 }, slots.Select(s => (int)(s.Tsat - s_now).TotalMinutes));
    }

    [Fact]
    public void Build_NeverGivesATsatBeforeNow()
    {
        SequenceSlot slot = Assert.Single(Sequencer.Build(new[] { Flight("AFR1", tobtMinutes: -20) }, 30, s_none, s_none, s_now));

        Assert.Equal(s_now, slot.Tsat);
    }

    [Fact]
    public void Build_PlacesConfirmedFlightsBeforeUnconfirmedOnes()
    {
        SequenceInput[] flights = { Flight("EARLY", 10), Flight("CONFIRMED", 11) with { Priority = true } };

        IReadOnlyList<SequenceSlot> slots = Sequencer.Build(flights, 30, s_none, s_none, s_now);

        Assert.Equal(new[] { "CONFIRMED", "EARLY" }, slots.Select(s => s.Callsign));
        Assert.Equal(s_now.AddMinutes(21), slots[0].Ttot);
        Assert.Equal(s_now.AddMinutes(23), slots[1].Ttot);
    }

    [Fact]
    public void Build_HoldsACtotFlightToItsCtotAndSequencesOthersAroundIt()
    {
        SequenceInput[] flights =
        {
            Flight("SLOT", 0) with { Ctot = s_now.AddMinutes(20) },
            Flight("FREE", 10),
        };

        Dictionary<string, SequenceSlot> slots = Sequencer.Build(flights, 30, s_none, s_none, s_now).ToDictionary(s => s.Callsign);

        Assert.Equal(s_now.AddMinutes(20), slots["SLOT"].Ttot);
        Assert.Equal(s_now.AddMinutes(10), slots["SLOT"].Tsat);
        Assert.False(slots["SLOT"].CtotMissed);
        Assert.Equal(s_now.AddMinutes(22), slots["FREE"].Ttot);
    }

    [Fact]
    public void Build_FlagsACtotThatCanNoLongerBeMet()
    {
        SequenceInput flight = Flight("LATE", tobtMinutes: 20) with { Ctot = s_now.AddMinutes(15) };

        SequenceSlot slot = Assert.Single(Sequencer.Build(new[] { flight }, 30, s_none, s_none, s_now));

        Assert.True(slot.CtotMissed);
        Assert.Equal(s_now.AddMinutes(30), slot.Ttot);
    }

    [Fact]
    public void Build_KeepsStartedFlightsWhereTheyAre()
    {
        SequenceInput[] flights =
        {
            Flight("TAXI", 0) with { Started = true, CurrentTsat = s_now.AddMinutes(-5), CurrentTtot = s_now.AddMinutes(20) },
            Flight("NEXT", 10),
        };

        Dictionary<string, SequenceSlot> slots = Sequencer.Build(flights, 30, s_none, s_none, s_now).ToDictionary(s => s.Callsign);

        Assert.Equal(s_now.AddMinutes(-5), slots["TAXI"].Tsat);
        Assert.Equal(s_now.AddMinutes(22), slots["NEXT"].Ttot);
    }

    [Fact]
    public void Build_AppliesRunwayStartUpAndDepartureDelays()
    {
        Dictionary<string, DateTimeOffset> startUp = new() { ["27L"] = s_now.AddMinutes(30) };
        Dictionary<string, DateTimeOffset> departure = new() { ["26R"] = s_now.AddMinutes(45) };
        SequenceInput[] flights = { Flight("ONE", 0) with { Runway = "27L" }, Flight("TWO", 0) with { Runway = "26R" } };

        Dictionary<string, SequenceSlot> slots = Sequencer.Build(flights, 30, startUp, departure, s_now).ToDictionary(s => s.Callsign);

        Assert.Equal(s_now.AddMinutes(30), slots["ONE"].Tsat);
        Assert.Equal(s_now.AddMinutes(45), slots["TWO"].Ttot);
    }

    private static SequenceInput Flight(string callsign, int tobtMinutes) =>
        new(callsign, "27L", s_now.AddMinutes(tobtMinutes), TimeSpan.FromMinutes(10), null, false, null, null, false);
}
