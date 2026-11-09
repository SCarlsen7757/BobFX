namespace BobFx.Core.Services;

public enum BusyBarSelector { Unknown = -1, Busy = 0, Custom = 1, Off = 2, Apps = 3, Settings = 4 }

/// <summary>Serializes observed selector changes with outgoing display requests.</summary>
internal sealed class BusyBarModeGate
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private int selector = (int)BusyBarSelector.Unknown;
    public BusyBarSelector Selector => (BusyBarSelector)Volatile.Read(ref selector);
    public async Task SetAsync(BusyBarSelector value, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try { Volatile.Write(ref selector, (int)value); }
        finally { gate.Release(); }
    }
    public async Task<bool> InAppsAsync(Func<Task> action, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (Selector != BusyBarSelector.Apps) return false;
            await action();
            return true;
        }
        finally { gate.Release(); }
    }
}
