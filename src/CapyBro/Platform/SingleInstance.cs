namespace CapyBro.Platform;

/// <summary>
/// Acquires a process-wide named mutex; if another instance already holds it, the constructor
/// reports that this instance is a duplicate. Caller is responsible for shutting down on duplicate.
///
/// Adds a cross-instance activation channel: a paired named
/// <see cref="EventWaitHandle"/> lets a duplicate instance signal the
/// already-running first instance to bring its UI forward (mirrors the
/// Win32 single-instance pattern that most desktop apps implement so
/// double-clicking the .exe / shortcut activates the existing window
/// instead of silently failing).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // Local\ (per-session) is correct for a per-user desktop app — the
    // tray utility runs only in the user's interactive session and we
    // want each Windows session to have its own single-instance slot.
    // Was Global\ pre-fix; that namespace requires the SeCreateGlobalPrivilege
    // right which is granted to interactive users by default but can be
    // stripped on locked-down domain machines (kiosk profiles, AppLocker
    // configurations). On those systems Mutex(...) threw
    // UnauthorizedAccessException at process start and the app failed to
    // launch entirely. Local\ has no such restriction and matches the
    // session boundary we actually care about — a Run-at-startup launch
    // and a Desktop-shortcut launch are always in the same session and
    // therefore find each other in the Local\ namespace.
    public const string DefaultMutexName = @"Local\CapyBroV2";

    // Named globally so a Run-at-startup instance and a desktop-shortcut
    // instance can find each other.  The "\Activate" suffix is documentation
    // for anyone inspecting kernel objects via WinObj / Process Explorer.
    private const string ActivationEventSuffix = @".Activate";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activationEvent;
    private readonly RegisteredWaitHandle? _activationRegistration;
    private bool _disposed;

    public SingleInstance(string mutexName = DefaultMutexName)
    {
        // initiallyOwned: false — we don't actually need ownership semantics.
        // The named mutex's existence is the cross-process signal. Ownership
        // also caused AbandonedMutexException after a crash because the OS
        // marks the mutex abandoned and the next constructor would fault.
        // With initiallyOwned: false, a process death silently cleans up.
        var mutex = new Mutex(initiallyOwned: false, name: mutexName, createdNew: out var createdNew);

        IsFirstInstance = createdNew;
        var activationName = mutexName + ActivationEventSuffix;

        if (IsFirstInstance)
        {
            _mutex = mutex;

            // First instance: own the activation event and listen for sets
            // from any duplicate that comes after us.  AutoReset so a single
            // signal corresponds to a single ActivationRequested event,
            // never two.  initialState=false because we want to wait for an
            // explicit Set() from a duplicate launch, not fire on creation.
            _activationEvent = new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: activationName);

            // ThreadPool.RegisterWaitForSingleObject avoids burning a
            // dedicated thread on a long WaitOne — the runtime parks the
            // wait at the kernel level and dispatches OnActivationSignaled
            // to a threadpool worker only when the handle actually fires.
            _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
                _activationEvent,
                callBack: OnActivationSignaled,
                state: null,
                timeout: Timeout.InfiniteTimeSpan,
                executeOnlyOnce: false);
        }
        else
        {
            // Another instance already created (and is keeping alive) the
            // mutex. Drop ours; the kernel reference stays via the other
            // process's handle.
            mutex.Dispose();
            _mutex = null;

            // Try to grab a handle to the same activation event so this
            // duplicate instance can poke the live one before exiting.
            // OpenExisting can fail if the first instance crashed without
            // disposing — treat that as "no live instance to signal" and
            // continue silently; the duplicate will exit anyway.
            try
            {
                _activationEvent = EventWaitHandle.OpenExisting(activationName);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                _activationEvent = null;
            }
            catch (UnauthorizedAccessException)
            {
                // Different security context (rare on per-user apps but
                // possible across UAC boundaries).  Same handling: skip.
                _activationEvent = null;
            }
        }
    }

    public bool IsFirstInstance { get; }

    /// <summary>
    /// Fires on a threadpool thread when a duplicate instance signals us.
    /// Subscribers must marshal back to their UI thread before touching
    /// dispatcher-affine state.
    /// </summary>
    public event EventHandler? ActivationRequested;

    /// <summary>
    /// Called by a duplicate instance to wake the first instance.  No-op
    /// when this instance IS the first or when the event handle could not
    /// be opened (e.g. first instance crashed).
    /// </summary>
    public void SignalExisting()
    {
        if (IsFirstInstance)
        {
            return;
        }

        try
        {
            _activationEvent?.Set();
        }
        catch (ObjectDisposedException)
        {
            // Event was disposed between OpenExisting and Set — first
            // instance is shutting down.  Nothing useful to do.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Z4-F6 / M12: volatile write so the threadpool worker thread
        // running OnActivationSignaled sees the disposal even if the JIT
        // would otherwise cache the boolean in a register.  Paired with
        // Volatile.Read in the callback below.
        Volatile.Write(ref _disposed, true);

        if (_activationRegistration is not null)
        {
            // Pass a wait handle to Unregister so the threadpool signals
            // it once every in-flight OnActivationSignaled callback has
            // returned.  Pre-fix this was `Unregister(null)` ("don't wait
            // for in-flight callbacks"), which left a window where a
            // queued worker could enter OnActivationSignaled with the
            // event already disposed.  We bound the wait so a stuck
            // callback can't block app shutdown — the volatile flag plus
            // the try/catch in OnActivationSignaled cover the post-
            // timeout case where the drain didn't complete in time.
            using var drained = new ManualResetEvent(initialState: false);
            _activationRegistration.Unregister(drained);
            drained.WaitOne(TimeSpan.FromMilliseconds(100));
        }

        _activationEvent?.Dispose();

        // We never called WaitOne, so we never owned the mutex — only
        // dispose the handle. The kernel deallocates the named object once
        // the last handle closes.
        _mutex?.Dispose();
    }

    /// <summary>
    /// Threadpool callback fired by
    /// <c>ThreadPool.RegisterWaitForSingleObject</c>.  Internal (not
    /// private) so the Z4-F6 / M12 regression tests can drive the
    /// dispose-vs-callback race deterministically without relying on
    /// kernel-scheduler timing.
    /// </summary>
    internal void OnActivationSignaled(object? state, bool timedOut)
    {
        // Volatile read pairs with the Volatile.Write in Dispose() — if
        // Dispose ran on another thread between the threadpool's
        // dispatch decision and our entry here, we see the flip.
        if (timedOut || Volatile.Read(ref _disposed))
        {
            return;
        }

        try
        {
            ActivationRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (ObjectDisposedException)
        {
            // Z4-F6 / M12: a subscriber's `Dispatcher.BeginInvoke` (the
            // documented activation-handling shape in App.xaml.cs) can
            // observe a disposed dispatcher mid-shutdown — the volatile
            // flag closes the common race but a late kernel-level signal
            // sneaking past Unregister's drain window can still arrive.
            // Swallow so the threadpool worker doesn't propagate this
            // as an UnobservedTaskException (the canonical "silent app
            // crash" path that H17 already plumbs into a toast).
        }
    }
}
