using System.Diagnostics;

using CapyBro.Platform;

namespace CapyBro.Services;

public sealed class ModifierReleaseWaiter : IModifierReleaseWaiter
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);
    private static readonly int[] ModifierKeys =
    [
        NativeMethods.VkControl,
        NativeMethods.VkMenu,
        NativeMethods.VkShift,
        NativeMethods.VkLWin,
        NativeMethods.VkRWin,
    ];

    public async Task WaitForReleaseAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        // Stopwatch is monotonic; DateTime.UtcNow can jump backward (NTP step,
        // user clock change) and either prematurely exit or loop forever when
        // the system clock skews while we're waiting for the user to release Ctrl.
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsAnyModifierDown())
            {
                return;
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    public bool IsAnyModifierDown()
    {
        foreach (var vk in ModifierKeys)
        {
            // High-order bit set when key is currently pressed.
            if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
