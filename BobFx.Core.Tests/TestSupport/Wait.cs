using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BobFx.Core.Tests;

internal static class Wait
{
    /// <summary>Waits for something a background loop does. A test that times out says which condition never
    /// became true, instead of only that a task was cancelled.</summary>
    public static async Task Until(Func<bool> condition, int seconds = 5, [CallerArgumentExpression(nameof(condition))] string expression = "")
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(seconds)) Assert.Fail($"Waited {seconds} s for: {expression}");
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
    }
}
