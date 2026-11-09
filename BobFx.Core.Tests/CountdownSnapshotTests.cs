using BobFx.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BobFx.Core.Tests;

public sealed class CountdownSnapshotTests
{
    [Fact]
    public void TimestampsPreserveSchedulerOvershootAndResetForNewRuns()
    {
        var clock = new FakeTimeProvider();
        var countdown = new CountdownService(NullLogger<CountdownService>.Instance, Options.Create(new CountdownOptions()), clock);
        var initial = clock.GetUtcNow();
        countdown.Start(true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.Zero);
        clock.Advance(TimeSpan.FromSeconds(7)); countdown.Advance();
        Assert.Equal(initial.AddSeconds(5), countdown.Snapshot.StartedUtc);
        clock.Advance(TimeSpan.FromSeconds(9)); countdown.Advance();
        Assert.Equal(initial.AddSeconds(15), countdown.Snapshot.EndedUtc);
        countdown.Start(true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.Zero);
        Assert.Null(countdown.Snapshot.StartedUtc); Assert.Null(countdown.Snapshot.EndedUtc);
    }
}
