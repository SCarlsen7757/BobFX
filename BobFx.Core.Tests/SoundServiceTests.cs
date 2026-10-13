using BobFx.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BobFx.Core.Tests;

public sealed class SoundServiceTests
{
    private static SoundService Create() => new(NullLogger<SoundService>.Instance);

    [Fact]
    public async Task PublishesEndSoundToEverySubscriber()
    {
        var service = Create();
        var first = new List<string>();
        var second = new List<string>();
        service.SoundRequested += first.Add;
        service.SoundRequested += second.Add;
        await service.Play("end");
        Assert.Equal(new[] { "/sounds/game_over/game-over-yeah.mp3" }, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task RemovingOneSubscriberKeepsOtherConnected()
    {
        var service = Create();
        var removed = new List<string>();
        var remaining = new List<string>();
        service.SoundRequested += removed.Add;
        service.SoundRequested += remaining.Add;
        service.SoundRequested -= removed.Add;
        await service.Play("end");
        Assert.Empty(removed);
        Assert.Single(remaining);
    }

    [Fact]
    public async Task FaultySubscriberDoesNotPreventOtherDeliveries()
    {
        var service = Create();
        var delivered = false;
        service.SoundRequested += _ => throw new InvalidOperationException("Disconnected client");
        service.SoundRequested += _ => delivered = true;
        await service.Play("end");
        Assert.True(delivered);
    }

    [Fact]
    public async Task FatalSubscriberFailureIsNotHidden()
    {
        var service = Create();
        service.SoundRequested += _ => throw new OutOfMemoryException("Simulated fatal failure");
        await Assert.ThrowsAsync<OutOfMemoryException>(() => service.Play("end"));
    }

    [Fact]
    public async Task UnknownSoundCannotPublishArbitraryUrl()
    {
        var service = Create();
        var delivered = false;
        service.SoundRequested += _ => delivered = true;
        await Assert.ThrowsAsync<ArgumentException>(() => service.Play("https://example.com/audio.mp3"));
        Assert.False(delivered);
    }

    [Fact]
    public async Task PlayingWithoutBrowsersIsSafe()
    {
        await Create().Play("end");
    }
}
