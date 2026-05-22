using CapyBro.Platform;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Platform;

public class SingleInstanceTests
{
    [Fact]
    public void FirstInstance_IsTrue_WhenMutexIsCreated()
    {
        var mutexName = UniqueMutexName();

        using var instance = new SingleInstance(mutexName);

        instance.IsFirstInstance.Should().BeTrue();
    }

    [Fact]
    public void SecondInstance_IsFalse_WhenAnotherHoldsTheMutex()
    {
        var mutexName = UniqueMutexName();
        using var first = new SingleInstance(mutexName);
        first.IsFirstInstance.Should().BeTrue("test prerequisite");

        using var second = new SingleInstance(mutexName);

        second.IsFirstInstance.Should().BeFalse();
    }

    [Fact]
    public void ThirdInstance_AfterFirstDisposed_IsFirstAgain()
    {
        var mutexName = UniqueMutexName();
        using (var first = new SingleInstance(mutexName))
        {
            first.IsFirstInstance.Should().BeTrue();
        }

        using var third = new SingleInstance(mutexName);

        third.IsFirstInstance.Should().BeTrue("after first disposes, mutex slot is free");
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var instance = new SingleInstance(UniqueMutexName());

        instance.Dispose();
        var act = () => instance.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task SecondInstance_SignalExisting_RaisesActivationRequestedOnFirstAsync()
    {
        // Cross-instance IPC: a duplicate launch is supposed to wake the
        // first instance instead of silently exiting.  Pre-fix the second
        // launch just called Shutdown(0) and the user got zero feedback
        // when they double-clicked the .exe a second time.
        var mutexName = UniqueMutexName();
        using var first = new SingleInstance(mutexName);
        first.IsFirstInstance.Should().BeTrue("test prerequisite");

        using var activated = new ManualResetEventSlim(initialState: false);
        first.ActivationRequested += (_, _) => activated.Set();

        // SignalExisting is async-by-nature (the OS-level Set returns
        // immediately, but the threadpool callback that fires
        // ActivationRequested may have a small scheduling delay).  Allow
        // up to 2s for the round-trip; in practice it lands in <50ms.
        using (var second = new SingleInstance(mutexName))
        {
            second.IsFirstInstance.Should().BeFalse();
            second.SignalExisting();
        }

        var fired = activated.Wait(TimeSpan.FromSeconds(2));
        fired.Should().BeTrue(
            "the duplicate-launch signal must wake the first instance so the host can bring its UI forward");

        // Yield once to drain any pending threadpool callbacks before
        // the test scope tears down handles.
        await Task.Yield();
    }

    [Fact]
    public void SignalExisting_OnFirstInstance_IsNoOp()
    {
        // Belt-and-braces: SignalExisting should never fire the live
        // instance's own ActivationRequested (would race with normal
        // startup and try to show Settings before the host wired up).
        using var first = new SingleInstance(UniqueMutexName());
        var raised = false;
        first.ActivationRequested += (_, _) => raised = true;

        first.SignalExisting();

        // Give any erroneous threadpool callback a brief chance to fire.
        Thread.Sleep(100);
        raised.Should().BeFalse(
            "the first instance is the listener, not a signaller — calling SignalExisting on it must short-circuit");
    }

    // Z4-F6 / M12 regression: pre-fix Dispose() called
    // `_activationRegistration.Unregister(null)` — "don't wait for
    // in-flight callbacks" — and the worker's `if (_disposed)` check
    // used a non-volatile bool.  A queued threadpool callback that
    // entered OnActivationSignaled AFTER our Dispose-set-disposed write
    // could read a stale `_disposed = false` due to missing memory
    // barrier and proceed to invoke ActivationRequested on a subscriber
    // whose own state was already torn down.  This test pins the
    // post-fix invariant: a callback that arrives AFTER Dispose
    // observes the flip and short-circuits without invoking subscribers.
    [Fact]
    public void OnActivationSignaled_AfterDispose_DoesNotInvokeSubscribers()
    {
        var instance = new SingleInstance(UniqueMutexName());
        var fired = false;
        instance.ActivationRequested += (_, _) => fired = true;

        instance.Dispose();

        // Driving the callback directly (rather than via a real kernel
        // signal) makes the post-Dispose path deterministic — no race
        // window where Dispose runs faster than the threadpool can
        // dispatch.  The behaviour we're pinning is the volatile-read
        // gate at the top of OnActivationSignaled.
        instance.OnActivationSignaled(state: null, timedOut: false);

        fired.Should().BeFalse(
            "the volatile _disposed flag must be observed by the callback even when it arrives on a different thread than Dispose ran on");
    }

    // Z4-F6 / M12 regression: a subscriber's
    // `Dispatcher.BeginInvoke(...)` (App.xaml.cs:140) can throw
    // ObjectDisposedException if the dispatcher already shut down
    // between our volatile-flag check and the Invoke.  Pre-fix that
    // exception propagated to the threadpool worker as an
    // UnobservedTaskException — the canonical "silent app crash"
    // path.  Post-fix the callback swallows the disposed-dispatcher
    // case so the threadpool stays healthy and the duplicate-launch
    // signal gracefully degrades.
    [Fact]
    public void OnActivationSignaled_SubscriberThrowsObjectDisposed_DoesNotPropagate()
    {
        using var instance = new SingleInstance(UniqueMutexName());
        instance.ActivationRequested += (_, _) =>
            throw new ObjectDisposedException("simulated subscriber-side dispatcher teardown");

        var act = () => instance.OnActivationSignaled(state: null, timedOut: false);

        act.Should().NotThrow(
            "ObjectDisposedException from a late-stage subscriber must not surface as UnobservedTaskException");
    }

    // Z4-F6 / M12 regression: the timeout branch of WaitOrTimerCallback
    // is a separate fast-exit — the threadpool dispatches us with
    // timedOut=true when the registered WaitHandle is unregistered.
    // Pin that this branch also short-circuits BEFORE touching the
    // subscriber.
    [Fact]
    public void OnActivationSignaled_TimedOut_DoesNotInvokeSubscribers()
    {
        using var instance = new SingleInstance(UniqueMutexName());
        var fired = false;
        instance.ActivationRequested += (_, _) => fired = true;

        instance.OnActivationSignaled(state: null, timedOut: true);

        fired.Should().BeFalse(
            "timedOut=true means the registered wait was cancelled, not a real signal — invoking subscribers would be a spurious activation");
    }

    private static string UniqueMutexName() =>
        $"CapyBro-Tests-{Guid.NewGuid():N}";
}
