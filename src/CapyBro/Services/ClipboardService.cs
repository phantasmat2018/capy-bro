using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace CapyBro.Services;

public sealed class ClipboardService : IClipboardService
{
    // Win32 clipboard is a single-owner resource; clipboard managers,
    // RDP virtual channels, and even the OS shell briefly hold it.
    // Without retry, any concurrent open throws CLIPBRD_E_CANT_OPEN
    // (HRESULT 0x800401D0) and we either lose the AI result or the
    // user's original selection. WPF (unlike WinForms) has no built-in
    // retry overload, so we wrap every clipboard call in a manual loop.
    private const int ClipboardCantOpenHResult = unchecked((int)0x800401D0);
    private const int RetryAttempts = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    public Task<string> GetTextAsync(CancellationToken ct = default) =>
        InvokeOnUiThreadAsync(
            () => RetryAsync(
                () => Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty,
                ct),
            ct);

    public Task SetTextAsync(string text, CancellationToken ct = default) =>
        InvokeOnUiThreadAsync(
            async () =>
            {
                // SetText throws ArgumentNullException for null/empty in some
                // versions; route empty through Clear which has the same
                // observable effect.
                var safe = text ?? string.Empty;
                await RetryAsync<object?>(
                    () =>
                    {
                        if (safe.Length == 0)
                        {
                            Clipboard.Clear();
                        }
                        else
                        {
                            Clipboard.SetText(safe);
                        }

                        return null;
                    },
                    ct).ConfigureAwait(true);
                return string.Empty;
            },
            ct);

    public Task ClearAsync(CancellationToken ct = default) =>
        InvokeOnUiThreadAsync(
            async () =>
            {
                await RetryAsync<object?>(
                    () =>
                    {
                        Clipboard.Clear();
                        return null;
                    },
                    ct).ConfigureAwait(true);
                return string.Empty;
            },
            ct);

    /// <summary>
    /// Async clipboard retry.  Each unsuccessful attempt yields to the
    /// dispatcher via <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// instead of <see cref="Thread.Sleep(TimeSpan)"/> — without that
    /// change the worst-case 10 × 50 ms retry budget froze the UI for
    /// up to 500 ms when a clipboard manager / RDP channel / AV held
    /// the Win32 clipboard open.  The individual <c>action()</c> call
    /// is still synchronous (the Win32 API is sync), but the gaps
    /// between attempts now release the dispatcher so WPF can pump
    /// messages, repaint, and respond to user input.
    /// </summary>
    private static async Task<T> RetryAsync<T>(Func<T> action, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= RetryAttempts; attempt++)
        {
            // Honour the caller's cancellation between retries.  We can't
            // interrupt a Win32 Clipboard.* call mid-flight (the OS API
            // is synchronous and offers no cancellation hook), but we can
            // refuse to start the next attempt if the user cancelled — the
            // alternative is up to 10×50ms = 500ms of pointless retries
            // after the cancel signal arrived.  Throwing here is correct:
            // ProcessAsync's catch path will route through
            // RestoreClipboardAsync with CancellationToken.None for the
            // restore itself, so user cancel doesn't strand a half-written
            // clipboard.
            ct.ThrowIfCancellationRequested();

            try
            {
                return action();
            }
            catch (COMException ex) when (ex.HResult == ClipboardCantOpenHResult && attempt < RetryAttempts)
            {
                // ConfigureAwait(true): stay on the dispatcher so the
                // next attempt at the (STA-required) Clipboard API runs
                // on the same UI thread we started on.
                await Task.Delay(RetryDelay, ct).ConfigureAwait(true);
            }
            catch (ExternalException) when (attempt < RetryAttempts)
            {
                // Some Win32 errors (E_FAIL, RPC_E_*) on overloaded systems —
                // retry the same way clipboard managers do.
                await Task.Delay(RetryDelay, ct).ConfigureAwait(true);
            }
        }

        // Final attempt — let the exception propagate so callers see the failure.
        return action();
    }

    /// <summary>
    /// Marshals <paramref name="func"/> onto the WPF UI thread (the
    /// only STA thread that owns the clipboard) and returns its
    /// awaitable result.  Bridges through a TaskCompletionSource so an
    /// async clipboard retry awaited inside <paramref name="func"/>
    /// still settles a single outer Task — without this the dispatcher
    /// would return a <c>Task&lt;Task&lt;T&gt;&gt;</c> that callers
    /// would have to double-await.
    /// </summary>
    private static Task<T> InvokeOnUiThreadAsync<T>(Func<Task<T>> func, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException(
                "ClipboardService requires a running WPF Application — cannot access Clipboard.");

        // Fast-path: caller already cancelled before we even queued the
        // dispatcher work.  No reason to schedule a UI-thread invocation
        // we'd just have to abandon.
        ct.ThrowIfCancellationRequested();

        if (dispatcher.CheckAccess())
        {
            // Already on the UI thread — invoke directly so the async
            // retry's Task.Delay continuations resume here too.
            return func();
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.InvokeAsync(
            async () =>
            {
                try
                {
                    var result = await func().ConfigureAwait(true);
                    tcs.TrySetResult(result);
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled(ct);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    tcs.TrySetException(ex);
                }
            },
            DispatcherPriority.Send);
        return tcs.Task;
    }
}
