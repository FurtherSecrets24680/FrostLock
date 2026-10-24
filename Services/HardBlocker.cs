using FrostLock.Interop;

namespace FrostLock.Services;

/// <summary>
/// Wraps Win32 <c>BlockInput</c>. The block and the matching release both happen on a
/// dedicated thread, because only the thread that blocked input may unblock it.
/// Requires administrator rights.
/// </summary>
public sealed class HardBlocker : IDisposable
{
    private volatile bool _release;
    private Thread? _thread;

    public bool Succeeded { get; private set; }
    public bool Attempted { get; private set; }

    public bool Start()
    {
        var ready = new ManualResetEventSlim(false);
        bool result = false;

        _thread = new Thread(() =>
        {
            result = Native.BlockInput(true);
            Attempted = true;
            ready.Set();

            if (!result) return;

            // Hold the block until released. Input stays frozen for every other process.
            var spin = new SpinWait();
            while (!_release)
            {
                if (spin.NextSpinWillYield) Thread.Sleep(40);
                else spin.SpinOnce();
            }

            Native.BlockInput(false);
        })
        {
            IsBackground = true,
            Name = "FrostLock.HardBlock"
        };

        _thread.Start();
        ready.Wait(2000);

        Succeeded = result;
        return result;
    }

    public void Release() => _release = true;

    public void Dispose()
    {
        _release = true;
    }
}