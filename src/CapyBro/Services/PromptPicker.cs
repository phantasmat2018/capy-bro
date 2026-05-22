using System.Windows;
using System.Windows.Threading;

using CapyBro.Models;
using CapyBro.Views;

namespace CapyBro.Services;

public sealed class PromptPicker : IPromptPicker
{
    private readonly IConfigStore _configStore;

    public PromptPicker(IConfigStore configStore)
    {
        _configStore = configStore;
    }

    public async Task<Prompt?> ShowAsync(
        IReadOnlyDictionary<string, Prompt> options,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("PromptPicker requires a running WPF Application.");

        // Read the active provider before the dispatcher hop so the
        // UI-thread show step doesn't include a synchronous I/O.
        // v15: surfaced to PromptPickerWindow so the header can show
        // an "Ollama" pill when local-only mode is active.  Best-
        // effort: a failure here only loses the pill, never blocks
        // the picker — TextProcessor will still route the run through
        // whatever provider the config eventually resolves to.
        var isOllama = false;
        try
        {
            var config = await _configStore.LoadAsync(ct).ConfigureAwait(false);
            isOllama = config.Provider == LlmProviderKind.Ollama;
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                    and not OutOfMemoryException
                                    and not StackOverflowException)
        {
            Serilog.Log.Warning(ex, "PromptPicker could not read Provider; defaulting indicator to OpenRouter");
        }

        return await dispatcher.InvokeAsync(() => ShowOnUiThread(options, timeout, isOllama, ct));
    }

    private static Prompt? ShowOnUiThread(
        IReadOnlyDictionary<string, Prompt> options,
        TimeSpan timeout,
        bool isOllamaProvider,
        CancellationToken ct)
    {
        if (options.Count == 0)
        {
            return null;
        }

        var window = new PromptPickerWindow(options, isOllamaProvider);
        var closing = 0;

        void TryClose()
        {
            // Both timer and ct can fire — only first one closes; second is no-op.
            if (Interlocked.Exchange(ref closing, 1) != 0)
            {
                return;
            }

            if (!window.IsVisible)
            {
                return;
            }

            try
            {
                window.DialogResult = false;
                window.Close();
            }
            catch (InvalidOperationException)
            {
                // Window already in process of closing — race lost, ignore.
            }
        }

        using var timer = new DispatcherTimerWrapper(timeout, TryClose);

        using var ctReg = ct.Register(() =>
        {
            _ = Application.Current?.Dispatcher.InvokeAsync(TryClose);
        });

        timer.Start();
        var result = window.ShowDialog();
        return result == true ? window.SelectedPrompt : null;
    }

    private sealed class DispatcherTimerWrapper : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private bool _disposed;

        public DispatcherTimerWrapper(TimeSpan interval, Action onTick)
        {
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                onTick();
            };
        }

        public void Start() => _timer.Start();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _timer.Stop();
            _disposed = true;
        }
    }
}
