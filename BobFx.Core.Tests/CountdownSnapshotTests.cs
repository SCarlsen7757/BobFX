using BobFx.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BobFx.Core.Tests;

public sealed class CountdownSnapshotTests
{
    [Fact]
    public void TimestampsPreserveSchedulerOvershootAndResetForNewRuns()
    {
        var h = new TestHarness();
        var (clock, countdown) = (h.Clock, h.Countdown);
        var initial = clock.GetUtcNow();
        countdown.Start(true, TimeSpan.FromSeconds(10), TimeSpan.Zero);
        clock.Advance(TimeSpan.FromSeconds(5)); countdown.Advance();
        Assert.Equal(initial.AddSeconds(3), countdown.Snapshot.StartedUtc); // After the fixed three-second preparation.
        clock.Advance(TimeSpan.FromSeconds(9)); countdown.Advance();
        Assert.Equal(initial.AddSeconds(13), countdown.Snapshot.EndedUtc);
        countdown.Start(true, TimeSpan.FromSeconds(10), TimeSpan.Zero);
        Assert.Null(countdown.Snapshot.StartedUtc); Assert.Null(countdown.Snapshot.EndedUtc);
    }
}
