using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

using CapyBro.Services;
using CapyBro.ViewModels;

namespace CapyBro.Views;

public partial class OnboardingWizard : Window
{
    private readonly OnboardingWizardViewModel _viewModel;
    private readonly ITranslator _translator;

    public OnboardingWizard(OnboardingWizardViewModel viewModel, ITranslator translator)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(translator);

        _viewModel = viewModel;
        _translator = translator;

        InitializeComponent();
        DataContext = viewModel;

        viewModel.RequestedClose += (_, _) => Close();
        viewModel.LanguagePreviewChanged += (_, e) => _translator.SetLanguage(e.Language);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Persist a "skip" if the user closes the wizard with the [×]
        // button before clicking Skip / Finish.  Without this the wizard
        // would re-open every launch until the user actually walks through
        // it — frustrating for someone who wants to ignore it.
        //
        // Z8-F3 / H16 fix: pre-fix this had a 2-second hard cap and a
        // Debug.WriteLine on timeout, silently dropping the
        // `OnboardingCompleted` flag on slow disks. Bumped to 5 s to
        // match the App.OnExit credential-flush budget, and any failure
        // is routed through Serilog so the user has a real diagnostic
        // trail. We accept the 5-second worst-case stall on a wedged
        // disk because the alternative — wizard reopening every launch —
        // is the worse UX.
        try
        {
#pragma warning disable VSTHRD002
            Task.Run(_viewModel.PersistOnCloseAsync)
                .Wait(TimeSpan.FromSeconds(5));
#pragma warning restore VSTHRD002
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Serilog.Log.Warning(ex, "OnboardingWizard close persist failed");
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Drop the VM's translator subscription so the closed wizard is
        // eligible for collection.  The VM is registered as Transient in
        // DI (one fresh instance per Show), so without this Dispose call
        // every wizard appearance leaves a dangling
        // Translator.PropertyChanged subscriber pointing at a closed
        // window.  The translator is a singleton, so leaks accumulate
        // across the app's lifetime — and any later language switch
        // would also fan out a notification to every previously-closed
        // wizard's VM, paying allocation + virtual-call cost for nothing.
        try
        {
            _viewModel.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Debug.WriteLine($"OnboardingWizard VM dispose failed: {ex.Message}");
        }

        base.OnClosed(e);
    }

    /// <summary>
    /// Generic shell-out handler for any external <see cref="System.Windows.Documents.Hyperlink"/>
    /// in the wizard — used today by the OpenRouter signup link on the
    /// API-key step and by the capybro.app homepage link in the
    /// per-step footer.  Reads the navigate URI off the event args so
    /// the same method serves both destinations.
    /// </summary>
    private void OnExternalLinkClick(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            // UseShellExecute is required to launch a URL via the default
            // browser on Windows — without it, .NET tries to exec the URL
            // string directly and throws Win32Exception 193.
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
            e.Handled = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Best-effort — a failed link click must not block the wizard.
            Debug.WriteLine($"OnboardingWizard link click failed: {ex.Message}");
        }
    }
}
