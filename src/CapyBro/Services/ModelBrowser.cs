using System.Windows;

using CapyBro.ViewModels;
using CapyBro.Views;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CapyBro.Services;

internal sealed class ModelBrowser : IModelBrowser
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ModelBrowser> _logger;

    public ModelBrowser(IServiceProvider services, ILogger<ModelBrowser> logger)
    {
        _services = services;
        _logger = logger;
    }

    public Task<string?> BrowseAsync(CancellationToken ct = default)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("ModelBrowser requires a running WPF Application.");

        return dispatcher.InvokeAsync(() => ShowDialogOnUiThread(ct)).Task;
    }

    private string? ShowDialogOnUiThread(CancellationToken outerCt)
    {
        // Each invocation gets a fresh VM + window so reload state is clean.
        var vm = ActivatorUtilities.CreateInstance<ModelsDialogViewModel>(_services);

        // Prefer the active SettingsWindow as owner so the dialog centres
        // over it on multi-monitor setups; fall back to MainWindow only if
        // settings hasn't been opened yet.
        var owner = Application.Current.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive)
            ?? Application.Current.MainWindow;

        var dialog = new ModelsDialog(vm)
        {
            Owner = owner,
        };

        // M17 (Z6-F4) fix: link a CTS to dialog.Closed so a fast Cancel /
        // ESC during the catalogue fetch aborts the in-flight HTTP
        // request instead of letting it drain on a background thread
        // against bindings whose Window already closed.  Pre-fix the
        // caller passed CancellationToken.None and the request always
        // ran to completion regardless of user intent.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        dialog.Closed += (_, _) =>
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // CTS may already be disposed if both close paths race;
                // benign — nothing left to cancel.
            }
        };

        // LoadAsync is already async (HTTP `await` releases the UI thread
        // while the request is in flight). Wrapping it in Task.Run forces
        // [ObservableProperty] mutations onto a threadpool thread and the
        // ObservableCollection assignment in ApplyFilter then races with
        // the WPF binding system — the UI shows transient frames or, in
        // edge cases, throws InvalidOperationException. Calling on the
        // dispatcher keeps every state mutation on the UI thread; the
        // network wait still doesn't block it.
        //
        // M17 (Z6-F4) companion: attach a faulted-only continuation so a
        // future refactor that introduces a synchronous throw outside
        // LoadAsync's own try/catch lands in our logger rather than on
        // TaskScheduler.UnobservedTaskException (which currently has its
        // own user-visible toast via H17, but a quieter log is preferable
        // for a known dialog-cancellation race).
        var loadTask = vm.LoadAsync(cts.Token);
        _ = loadTask.ContinueWith(
            t => _logger.LogWarning(t.Exception, "ModelsDialog LoadAsync threw an unobserved exception"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            var ok = dialog.ShowDialog();
            return ok == true ? vm.SelectedModel : null;
        }
        finally
        {
            cts.Dispose();

            // Z7-F3 / M19 — VM subscribed to Translator.PropertyChanged so
            // a mid-flight language switch can re-resolve cached strings.
            // Without an explicit unsubscribe the Translator singleton
            // would hold a strong reference to this transient VM, leaking
            // one VM (plus its captured services) per dialog open.
            vm.Dispose();
        }
    }
}
