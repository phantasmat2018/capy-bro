using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;

using CapyBro.Models;
using CapyBro.Platform;
using CapyBro.Services;
using CapyBro.ViewModels;
using CapyBro.Views;

using H.NotifyIcon;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Serilog;

namespace CapyBro;

[SuppressMessage(
    "Performance",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF Application lifecycle disposes via OnExit override.")]
public partial class App : Application
{
    private IHost? _host;
    private SingleInstance? _singleInstance;
    private TaskbarIcon? _trayIcon;
    private CancellationTokenSource? _currentProcessingCts;

    // Guard against re-entrant ShowSettingsWindow.  Without this, a fast
    // double-click on the tray icon (or click while the wizard's "Open
    // settings" handler is also firing) launches two parallel
    // GeneralTab.LoadFromConfigAsync chains that race on the same VM
    // properties — the second can land on top of the first mid-update,
    // leaving Models / SelectedModel / Hotkey in mismatched states.
    // Single-flight: while a load is in progress, additional Show requests
    // just bring the window forward without re-issuing the load.
    private bool _settingsLoadInProgress;

    // Reference to the History MenuItem in the tray context menu so the
    // v11 ExperimentalHistory master flag can flip its Visibility live —
    // matches the SettingsWindow sidebar gate so the user sees a
    // consistent state across both surfaces.
    private System.Windows.Controls.MenuItem? _trayHistoryItem;

    /// <summary>
    /// Z8-F5 / M21: extracted from <see cref="OnStartup"/> so the
    /// "Windows autostart launch must stay tray-only" contract is
    /// unit-testable.  Matching is exact-token, case-insensitive — a
    /// leading or trailing whitespace would NOT be tolerated, which
    /// matches what <c>AutostartService.Enable()</c> writes verbatim into
    /// the Run-key value.  A hand-edited registry that introduced a
    /// trailing space would (correctly) fail to silence the launch — the
    /// user would then see the Settings window open and could fix the
    /// registry value themselves.
    /// </summary>
    internal static bool IsSilentLaunch(string[]? args)
    {
        return args is not null
            && args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Z10-F6 fix: wrap the entire OnStartup body in a top-level
        // try/catch so any failure in the early region (SingleInstance
        // mutex acquisition, logging init, ReduceMotion resource walk)
        // surfaces a localized-as-possible MessageBox + log entry rather
        // than the bare Windows "an unhandled exception has occurred"
        // dialog.  Pre-fix everything before line 102 ran with no guard.
        try
        {
            OnStartupCore(e);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            try
            {
                Log.Fatal(ex, "Startup failed");
            }
            catch (Exception logEx) when (logEx is not OutOfMemoryException and not StackOverflowException)
            {
                // Logging itself may have failed — fall through to the
                // MessageBox so the user gets at least one signal.
                _ = logEx;
            }

            try
            {
                MessageBox.Show(
                    "CapyBro could not start.\n\n" + ex.GetType().Name + ": " + ex.Message,
                    "CapyBro — Startup error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (Exception msgEx) when (msgEx is not OutOfMemoryException and not StackOverflowException)
            {
                // Even the MessageBox can fail if Application.Current is
                // half-initialised. We've done all we can — let the CLR
                // tear us down rather than recurse.
                _ = msgEx;
            }

            Shutdown(1);
        }
    }

    private void OnStartupCore(StartupEventArgs e)
    {
        _singleInstance = new SingleInstance();
        if (!_singleInstance.IsFirstInstance)
        {
            // Another instance is already running — wake it up so the user
            // sees the existing window come to the foreground (or the
            // Settings window open from tray-only mode), then exit.  Pre-fix
            // we just silently called Shutdown(0), which made double-click
            // on the .exe / desktop shortcut feel broken — the user got
            // zero visible feedback that anything happened.
            //
            // FZ5-F3 / M38 — log the sender side of the single-instance
            // activation handshake.  Pre-fix the duplicate process was
            // completely silent in the log file (the first instance has
            // its own "Activation requested by duplicate launch" line on
            // the receiver side, but if Serilog was filtered or the
            // first instance's log roll cut at the wrong moment there
            // was no breadcrumb that the duplicate even ran).  Logged
            // BEFORE SignalExisting + Shutdown so even a crash inside
            // those calls leaves a trail.
            Log.Information("Duplicate launch — signalling live instance and exiting");
            _singleInstance.SignalExisting();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown(0);
            return;
        }

        // First instance: react to activation pings from later launches by
        // pulling whichever window is alive (wizard / Settings) to the
        // front, or opening Settings if the app is tray-only.
        // ActivationRequested fires on a threadpool thread, so marshal back
        // to the dispatcher before touching WPF window state.  Discard the
        // DispatcherOperation — we don't need to await it; the analyzer
        // requires explicit assignment so it doesn't think we lost an
        // awaitable.
        _singleInstance.ActivationRequested += (_, _) =>
        {
            // FZ5-F3: also log the activation so support staff can
            // distinguish "user reports duplicate launch did nothing AND
            // the activation channel fired" from "channel never fired".
            Log.Information("Activation requested by duplicate launch — restoring window");
            _ = Dispatcher.BeginInvoke(ActivateExistingApp);
        };

        ConfigureLogging();
        Log.Information("Application starting (version {Version})", "2.0.0");

        // §8.4: collapse Motion.* tokens to "0:0:0" before any window
        // is shown if the user has disabled animations in Windows.
        // Idempotent no-op when motion is allowed.
        ReduceMotion.ApplyToApplicationResources(Resources);

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log.Fatal(ex.ExceptionObject as Exception, "Unhandled domain exception (terminating={Terminating})", ex.IsTerminating);
        DispatcherUnhandledException += (_, ex) =>
        {
            // Z10-F3 / Z10-F10 fix (FZ3-F1 lived this 7+ times in production
            // logs): pre-fix `ex.Handled = false` let the CLR tear the
            // process down with no toast, no MessageBox, and no chance for
            // OnExit's flush block to run — the user lost their last edit
            // every time. Now we attempt a graceful shutdown that flushes
            // pending writes, shows the user an error, then exits with a
            // non-zero code so launcher scripts can detect the crash.
            Log.Fatal(ex.Exception, "Unhandled dispatcher exception");
            try
            {
                ShutdownGracefully(ex.Exception);
                ex.Handled = true;
            }
            catch (Exception graceEx) when (graceEx is not OutOfMemoryException and not StackOverflowException)
            {
                // Graceful path itself failed — let the CLR take us out so
                // we don't deadlock the dispatcher; logged via Log.Fatal so
                // the post-mortem has both exceptions.
                Log.Fatal(graceEx, "ShutdownGracefully failed during dispatcher exception handling");
                ex.Handled = false;
            }
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            // Z10-F3 fix: surface to the user via toast instead of only
            // logging. SetObserved() still prevents the .NET runtime from
            // crashing the process, but at least the user knows something
            // went wrong — pre-fix the only signal was the log file.
            Log.Error(ex.Exception, "Unobserved task exception");
            ex.SetObserved();

            try
            {
                var notifications = _host?.Services.GetService<INotificationService>();
                var translator = _host?.Services.GetService<ITranslator>();
                if (notifications is not null && translator is not null)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                        notifications.ShowError(translator["msg_background_task_failed"]));
                }
            }
            catch (Exception toastEx) when (toastEx is not OutOfMemoryException and not StackOverflowException)
            {
                Log.Debug(toastEx, "Could not surface unobserved-task toast");
            }
        };

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(ConfigureServices)
            .Build();

        _host.Start();
        Log.Debug("Host started, beginning runtime wiring");

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        InitializeFromConfig();
        Log.Debug("InitializeFromConfig done");

        InitializeTrayIcon();
        Log.Debug("InitializeTrayIcon done");

        WireRuntimeBehavior();
        Log.Debug("WireRuntimeBehavior done");

        // --silent flag is set by AutostartService.Enable() so the
        // Windows boot autostart launch keeps the app tray-only.
        // Any other launch (Start Menu / Desktop shortcut / Explorer
        // double-click — no args) opens the Settings window so the
        // user gets immediate feedback that the click did something.
        var silentLaunch = IsSilentLaunch(e?.Args);
        if (silentLaunch)
        {
            Log.Information("Silent launch (autostart) — staying tray-only");
        }
        else if (ShouldShowOnboardingWizard())
        {
            // First launch (or upgrader who never saw the wizard):
            // walk the user through Language → API key → Hotkey →
            // Done before anything else. Wizard's Skip / Finish path
            // sets OnboardingCompleted=true so it doesn't reappear.
            ShowOnboardingWizard();
        }
        else
        {
            ShowSettingsWindow();
        }

        Log.Debug("Startup complete");
    }

    private bool _shutdownInProgress;

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Application exiting");
        ShutdownGracefully(triggeringException: null);
        base.OnExit(e);
    }

    /// <summary>
    /// Tears the app down in the right order:
    /// (a) cancel any in-flight TextProcessor pipeline so its HTTP request
    /// stops issuing wire traffic immediately (Z10-F2),
    /// (b) dispose the tray icon so the user can't reactivate the half-dead
    /// UI mid-shutdown (Z10-F12),
    /// (c) flush every debounced write — API key, general config,
    /// prompts auto-save — with a 5-second hard cap each so a wedged disk
    /// can't block shutdown forever (Z10-F1, Z10-F5),
    /// (d) dispose the host + single-instance, wrapped so the final
    /// Log.CloseAndFlush is reached even if Dispose throws (Z10-F11),
    /// (e) if a triggering exception is supplied (DispatcherUnhandledException
    /// path), show the user a MessageBox so a crash is not silent (Z10-F10),
    /// then call <c>Shutdown(1)</c> so callers can detect the failure.
    /// Idempotent — guards against the dispatcher exception path racing OnExit.
    /// </summary>
    private void ShutdownGracefully(Exception? triggeringException)
    {
        if (_shutdownInProgress)
        {
            return;
        }

        _shutdownInProgress = true;

        // (a) Cancel any in-flight TextProcessor work BEFORE the flush block.
        // This is the Z10-F2 fix — pre-fix the HTTP request continued to
        // completion AFTER _host.Dispose() ran, scribbling to a disposed
        // dispatcher and (worst case) trying to access torn-down credentials.
        try
        {
            var inflight = Interlocked.Exchange(ref _currentProcessingCts, null);
            inflight?.Cancel();
            inflight?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Log.Debug(ex, "Cancelling in-flight processing during shutdown raised — benign");
        }

        // (b) Z10-F12 fix: tray icon goes FIRST so the user can't click
        // Settings again while we're flushing. Pre-fix tray stayed visible
        // during the 5-second flush window and clicking it would start
        // loading Settings against a host that was about to be disposed.
        try
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Log.Debug(ex, "Tray icon dispose raised — benign");
        }

        // (c) Z10-F1 + Z10-F5 fix: flush ALL debounced writers. Pre-fix only
        // the API-key debounce was flushed; the user's last prompt edit and
        // every checkbox toggle inside the 400ms debounce window were silently
        // lost. Each task is wrapped individually so a hang on one doesn't
        // starve the others.
#pragma warning disable VSTHRD002 // Sync waits intentional — we are in shutdown
        try
        {
            var general = _host?.Services.GetService<GeneralTabViewModel>();
            var prompts = _host?.Services.GetService<PromptsTabViewModel>();

            var pending = new List<Task>();
            if (general is not null)
            {
                // FlushApiKeyAsync has an optional `CancellationToken` param;
                // method-group conversion requires a no-arg signature, so
                // wrap explicitly. FlushPendingConfigAsync / FlushPendingAsync
                // are already param-less so they convert directly.
                pending.Add(Task.Run(() => general.FlushApiKeyAsync()));
                pending.Add(Task.Run(general.FlushPendingConfigAsync));
            }

            if (prompts is not null)
            {
                pending.Add(Task.Run(prompts.FlushPendingAsync));
            }

            if (pending.Count > 0)
            {
                // 5-second budget for the lot, not per-task — matches the
                // existing API-key flush budget and prevents Quit from
                // feeling sticky.
                Task.WhenAll(pending).Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Log.Warning(ex, "Failed to flush pending writes during shutdown");
        }
#pragma warning restore VSTHRD002

        // (d) Z10-F11 fix: every Dispose can throw. Pre-fix a host-Dispose
        // throw would escape OnExit and Log.CloseAndFlush would never run,
        // losing the buffered diagnostic trail. Wrap each dispose so the
        // last Log.CloseAndFlush is guaranteed to run.
        try
        {
            _host?.Dispose();
            _host = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Log.Warning(ex, "Host dispose raised");
        }

        try
        {
            _singleInstance?.Dispose();
            _singleInstance = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Log.Debug(ex, "SingleInstance dispose raised — benign");
        }

        // (e) Z10-F10 fix: if we got here because of a dispatcher exception
        // (not a normal Quit), tell the user. Pre-fix `ex.Handled = false`
        // let the window vanish silently.
        if (triggeringException is not null)
        {
            try
            {
                MessageBox.Show(
                    "CapyBro encountered an unexpected error and is closing.\n\n"
                        + triggeringException.GetType().Name + ": " + triggeringException.Message
                        + "\n\nYour last edit may not have been saved. The full crash trace is in:\n"
                        + Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".ai_text_improver_v2.log"),
                    "CapyBro — Unexpected error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (Exception msgEx) when (msgEx is not OutOfMemoryException and not StackOverflowException)
            {
                // The MessageBox itself can fail if the dispatcher is too
                // broken — we've already logged via Log.Fatal upstream, so
                // we just push on through to the flush.
                _ = msgEx;
            }
        }

        Log.CloseAndFlush();

        if (triggeringException is not null)
        {
            // Re-issue an explicit shutdown so the process exits with a
            // non-zero code. OnExit will re-enter ShutdownGracefully but
            // _shutdownInProgress short-circuits it.
            try
            {
                Shutdown(1);
            }
            catch (Exception shutdownEx) when (shutdownEx is not OutOfMemoryException and not StackOverflowException)
            {
                // If even Shutdown() fails, the next dispatcher exit will
                // take us out — nothing more we can do here.
                _ = shutdownEx;
            }
        }
    }

    private void InitializeFromConfig()
    {
        if (_host is null)
        {
            return;
        }

        var configStore = _host.Services.GetRequiredService<IConfigStore>();
        var translator = _host.Services.GetRequiredService<ITranslator>();
        var hotkeys = _host.Services.GetRequiredService<IHotkeyManager>();
        var autostart = _host.Services.GetRequiredService<IAutostartService>();

        // Self-heal a stale Run-key entry that points at an old exe path
        // (installer reinstall, user-move, etc.). No-op when the entry is
        // absent or already matches the current exe.
        autostart.RepairIfStale();

        var isFirstRun = !File.Exists(ConfigStore.DefaultConfigPath)
            && !File.Exists(ConfigStore.DefaultLegacyConfigPath);

        AppConfig config;
        if (isFirstRun)
        {
            // Brand decision: always start in English on a fresh install.
            // Pre-rebrand we ran DetectLocale() here so a Ukrainian /
            // Russian Windows would auto-pick its own UI language; that
            // made sense back when the app was a UA-team-internal tool,
            // but CapyBro ships with English
            // as the canonical default.  AppConfig.Default.Language is
            // Language.English, so we just save defaults verbatim.  Users
            // explicitly opt into UA / RU via the onboarding wizard's
            // Language step or Settings → General → Language.
            config = AppConfig.Default;
            RunOffUiThread(() => configStore.SaveAsync(config));
            Log.Information("First run detected — wrote config with language={Language}", config.Language);
        }
        else
        {
            config = RunOffUiThread(() => configStore.LoadAsync());
        }

        // Cosmetic: re-canonicalise hotkey casing so legacy migrations or hand-edited configs
        // settle into "Ctrl+Shift+E" form. Registration is case-insensitive — this only affects display.
        var normalizedHotkey = HotkeyAccelerator.Normalize(config.Hotkey);
        var normalizedMenuHotkey = HotkeyAccelerator.Normalize(config.MenuHotkey);
        var normalizedUndoHotkey = HotkeyAccelerator.Normalize(config.UndoHotkey);
        if (!string.Equals(normalizedHotkey, config.Hotkey, StringComparison.Ordinal)
            || !string.Equals(normalizedMenuHotkey, config.MenuHotkey, StringComparison.Ordinal)
            || !string.Equals(normalizedUndoHotkey, config.UndoHotkey, StringComparison.Ordinal))
        {
            config = config with
            {
                Hotkey = normalizedHotkey,
                MenuHotkey = normalizedMenuHotkey,
                UndoHotkey = normalizedUndoHotkey,
            };
            var snapshot = config;
            RunOffUiThread(() => configStore.SaveAsync(snapshot));
            Log.Information(
                "Normalised hotkey casing → {Hotkey} / {MenuHotkey} / {UndoHotkey}",
                normalizedHotkey,
                normalizedMenuHotkey,
                normalizedUndoHotkey);
        }

        translator.SetLanguage(config.Language);

        if (!string.IsNullOrWhiteSpace(config.Hotkey))
        {
            hotkeys.TryRegister(HotkeyKind.Default, config.Hotkey);
        }

        if (!string.IsNullOrWhiteSpace(config.MenuHotkey))
        {
            hotkeys.TryRegister(HotkeyKind.Menu, config.MenuHotkey);
        }

        if (!string.IsNullOrWhiteSpace(config.UndoHotkey))
        {
            hotkeys.TryRegister(HotkeyKind.Undo, config.UndoHotkey);
        }
    }

    private void InitializeTrayIcon()
    {
        if (_host is null)
        {
            return;
        }

        var translator = _host.Services.GetRequiredService<ITranslator>();
        var menu = BuildTrayMenu(translator);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = BuildTrayTooltip(LlmProviderKind.OpenRouter),
            ContextMenu = menu,
            Icon = LoadEmbeddedTrayIcon(),
            LeftClickCommand = new RelayCommand(ShowSettingsWindow),
            Visibility = Visibility.Visible,
        };
        try
        {
            _trayIcon.ForceCreate(enablesEfficiencyMode: false);
            Log.Information("Tray icon ForceCreate completed (icon size {Size})", _trayIcon.Icon?.Size);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Log.Error(ex, "Tray icon creation failed");
        }

        // Initial History-menu Visibility + tray tooltip provider tag:
        // read straight from disk so the tray reflects reality even when
        // the user never opens Settings this session.  GeneralTabViewModel
        // properties default to their type defaults until LoadFromConfigAsync
        // runs, so we cannot rely on the VM for the first paint.
        try
        {
            var configStore = _host.Services.GetRequiredService<IConfigStore>();
            var config = RunOffUiThread(() => configStore.LoadAsync());
            UpdateTrayHistoryVisibility(config.ExperimentalHistory);
            _trayIcon.ToolTipText = BuildTrayTooltip(config.Provider);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Log.Warning(ex, "Could not seed tray state from config — defaulting to hidden/OpenRouter");
            UpdateTrayHistoryVisibility(false);
        }

        // Live updates: subscribe once to the GeneralTabVM so flipping the
        // master flag in Settings → Experimental immediately reflects in
        // the tray menu without restart.  GeneralTab is a singleton, so
        // this subscription stays alive for the app's lifetime.
        // v15: also reflect Provider changes — tray tooltip carries an
        // "· Ollama" suffix when the local provider is active so the
        // user can confirm at a glance that text isn't going to the
        // cloud.
        var generalTab = _host.Services.GetRequiredService<GeneralTabViewModel>();
        generalTab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GeneralTabViewModel.ExperimentalHistory))
            {
                _ = Dispatcher.BeginInvoke(() =>
                    UpdateTrayHistoryVisibility(generalTab.ExperimentalHistory));
            }
            else if (e.PropertyName == nameof(GeneralTabViewModel.Provider))
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (_trayIcon is not null)
                    {
                        _trayIcon.ToolTipText = BuildTrayTooltip(generalTab.Provider);
                    }
                });
            }
        };
    }

    private static string BuildTrayTooltip(LlmProviderKind provider) =>
        provider == LlmProviderKind.Ollama
            ? "CapyBro · Ollama"
            : "CapyBro";

    private void UpdateTrayHistoryVisibility(bool available)
    {
        if (_trayHistoryItem is not null)
        {
            _trayHistoryItem.Visibility = available
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private static System.Drawing.Icon LoadEmbeddedTrayIcon()
    {
        // Load assets/logo.ico — the multi-resolution brand icon
        // generated by scripts/png-to-ico.ps1. Bundled as a WPF
        // Resource (csproj <Resource Include="...\assets\logo.ico"/>),
        // accessible via the pack URI scheme. Windows shell picks the
        // best size from the .ico for the user's DPI.
        try
        {
            var resourceUri = new Uri("pack://application:,,,/assets/logo.ico", UriKind.Absolute);
            var streamInfo = GetResourceStream(resourceUri);
            if (streamInfo?.Stream is { } stream)
            {
                using (stream)
                {
                    return new System.Drawing.Icon(stream);
                }
            }

            Log.Warning("assets/logo.ico resource missing — falling back to SystemIcons.Application");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Log.Warning(ex, "Tray icon load failed — falling back to system default");
        }

        return System.Drawing.SystemIcons.Application;
    }

    private System.Windows.Controls.ContextMenu BuildTrayMenu(ITranslator translator)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var settingsItem = new System.Windows.Controls.MenuItem();
        settingsItem.SetBinding(
            System.Windows.Controls.HeaderedItemsControl.HeaderProperty,
            new System.Windows.Data.Binding
            {
                Source = translator,
                Path = new PropertyPath("[tray_settings]"),
            });
        settingsItem.Click += (_, _) => ShowSettingsWindow();
        menu.Items.Add(settingsItem);

        var historyItem = new System.Windows.Controls.MenuItem();
        historyItem.SetBinding(
            System.Windows.Controls.HeaderedItemsControl.HeaderProperty,
            new System.Windows.Data.Binding
            {
                Source = translator,
                Path = new PropertyPath("[tray_history]"),
            });
        historyItem.Click += (_, _) => ShowHistoryTab();
        menu.Items.Add(historyItem);
        _trayHistoryItem = historyItem;

        menu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem();
        exitItem.SetBinding(
            System.Windows.Controls.HeaderedItemsControl.HeaderProperty,
            new System.Windows.Data.Binding
            {
                Source = translator,
                Path = new PropertyPath("[tray_exit]"),
            });
        exitItem.Click += (_, _) => Shutdown(0);
        menu.Items.Add(exitItem);

        return menu;
    }

    private void ShowHistoryTab()
    {
        if (_host is null)
        {
            return;
        }

        // History is now an in-window tab (sidebar entry under "Промти").
        // Open the Settings shell if it's not already visible, then flip
        // SelectedTab to the singleton HistoryViewModel so the indicator
        // strip lights up and the content pane swaps with the §8.3 motion.
        var settingsVm = _host.Services.GetRequiredService<SettingsWindowViewModel>();
        ShowSettingsWindow();
        settingsVm.SelectedTab = settingsVm.HistoryTab;
    }

    private void WireRuntimeBehavior()
    {
        if (_host is null)
        {
            return;
        }

        var translator = _host.Services.GetRequiredService<ITranslator>();
        var notifications = _host.Services.GetRequiredService<INotificationService>();
        var processor = _host.Services.GetRequiredService<TextProcessor>();
        var hotkeys = _host.Services.GetRequiredService<IHotkeyManager>();
        var dispatcher = Dispatcher;

        var generalTabForToast = _host.Services.GetRequiredService<GeneralTabViewModel>();
        processor.ProcessingStarted += (_, e) =>
            _ = dispatcher.InvokeAsync(() =>
            {
                // Cost suffix only when the credits/cost experiment is on
                // (TextProcessor passes null when it's off — the gate is
                // upstream, not here).
                var msg = translator["toast_processing"];
                if (e.EstimatedCostUsd is { } cost)
                {
                    msg += " " + translator.Format("toast_cost_estimate", FormatUsd(cost));
                }

                // v17 (free-core): model suffix on the toast so the user
                // can confirm at a glance which model is processing.  Format
                // is the raw id — OpenRouter slug ("openai/gpt-4o") or
                // Ollama tag ("gemma3:latest") — which also implicitly
                // signals the provider, so we drop the older "· Ollama"
                // tag from v15 (the tag was added then because there was
                // no other provider signal in the toast).
                if (!string.IsNullOrWhiteSpace(e.EffectiveModel))
                {
                    msg += " · " + e.EffectiveModel;
                }

                notifications.ShowProgress(msg, onCancel: CancelCurrentProcessing);
            });

        processor.ProcessingCompleted += (_, _) =>
            _ = dispatcher.InvokeAsync(() =>
            {
                notifications.CloseProgress();
                notifications.ShowInfo(translator["toast_done"]);
            });

        // The diff-preview path: API returned but we're not committing yet.
        // Hide the progress toast so the user isn't staring at "Processing…"
        // while the modal asks them what to do. No "Done" toast — that comes
        // later only if they Accept (via the regular Completed event).
        processor.ProcessingProgressClosed += (_, _) =>
            _ = dispatcher.InvokeAsync(() => notifications.CloseProgress());

        // Streaming feedback: each SSE chunk arrives as a fresh accumulated
        // string. Push to the toast on the dispatcher; ToastWindow truncates
        // to a tail window so the toast doesn't grow unbounded.
        // InvokeAsync (not Invoke) — ConfigureAwait(false) on the streaming
        // helper means events fire on the threadpool thread; blocking the
        // threadpool waiting for UI render would tank streaming throughput.
        processor.ProcessingStreamUpdated += (_, e) =>
            _ = dispatcher.InvokeAsync(() => notifications.UpdateStreamingContent(e.AccumulatedContent));

        processor.ProcessingUndone += (_, _) =>
            _ = dispatcher.InvokeAsync(() =>
            {
                // Undo is fast (no API call), so we never showed Progress for
                // it — there's nothing to close. We just emit the success
                // toast so the user sees the global hotkey actually fired.
                notifications.ShowInfo(translator["history_undo_done"]);
            });

        processor.ProcessingFailed += (_, e) =>
            _ = dispatcher.InvokeAsync(() =>
            {
                notifications.CloseProgress();

                // v15 special-case: a hotkey-time Ollama-unreachable
                // failure (LocalizationKey == "ollama_unreachable")
                // routes through GeneralTabViewModel.HandleOllamaUnreachable
                // so the user gets a single COMBINED toast ("Couldn't
                // reach Ollama — switched to OpenRouter") and the
                // provider auto-reverts.  Without this short-circuit,
                // the unreachable toast would surface plain and the
                // next hotkey would just hit the same failure again.
                if (e.LocalizationKey == "ollama_unreachable")
                {
                    _ = generalTabForToast.HandleOllamaUnreachableAsync();
                    return;
                }

                // Z10-F7 / M27: when the failure carries a Translator key,
                // re-resolve it against the CURRENT locale.  Mid-flight
                // language switch (e.g. user opened Settings → switched
                // language during a slow API call) then delivers the toast
                // in the new locale rather than the locale captured at
                // raise time.  When no key is set (OpenRouter path), fall
                // back to the eagerly-resolved snapshot.
                var message = !string.IsNullOrEmpty(e.LocalizationKey)
                    ? translator[e.LocalizationKey]
                    : e.LocalizedMessage;
                notifications.ShowError(message);
            });

        // H18 (Z10-F4) fix: cancellation that races AFTER the paste
        // already committed used to be silent — toast just closed and
        // the user could not tell their text was already in the
        // document.  ShowInfo (not ShowError) — the outcome is a partial
        // success, not a failure.
        processor.ProcessingCancelledWithResult += (_, _) =>
            _ = dispatcher.InvokeAsync(() =>
            {
                notifications.CloseProgress();
                notifications.ShowInfo(translator["msg_cancelled_with_result"]);
            });

        hotkeys.HotkeyPressed += (_, e) =>
        {
            // Per-invocation CTS so the user can cancel via the toast's ✕ button.
            //
            // Install the slot via CompareExchange(..., null) so a duplicate
            // hotkey press fired WHILE a previous run is still in flight is
            // refused at the App layer.  Pre-fix this path did
            // Interlocked.Exchange + previous?.Dispose() under the comment
            // "TextProcessor's Interlocked guard ensures only one in-flight
            // at a time, so the previous CTS is for a finished call" — but
            // TextProcessor's guard refuses the SECOND call's BODY (returns
            // false fast), it does not prevent THIS handler from creating /
            // installing a new cts.  Net effect: the first run's cts was
            // disposed mid-flight and the second handler's finally nulled
            // out the slot, so the toast ✕ button silently no-op'd for the
            // remainder of the first run.  Refusing the duplicate at install
            // time keeps the live cancel handle in place until the first run
            // finishes.
            var cts = new CancellationTokenSource();
            if (Interlocked.CompareExchange(ref _currentProcessingCts, cts, null) is not null)
            {
                cts.Dispose();
                Log.Debug(
                    "Hotkey {Kind} ignored — a previous run still owns the cancel slot",
                    e.Kind);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await processor.HandleHotkeyAsync(e.Kind, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // User cancelled — already surfaced by toast close. No further UI noise.
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    Log.Error(ex, "TextProcessor.HandleHotkeyAsync threw");
                }
                finally
                {
                    // Slot ownership: we are the sole owner under the
                    // CompareExchange install above, so the slot still
                    // points at our cts unless something has gone very
                    // wrong.  Clear it FIRST so a Cancel-button click
                    // racing the dispose below sees null and short-
                    // circuits instead of touching the doomed cts.
                    Interlocked.CompareExchange(ref _currentProcessingCts, null, cts);
                    cts.Dispose();
                }
            });
        };
    }

    private void CancelCurrentProcessing()
    {
        // Z10-F8 / M28: CancellationTokenSource.Cancel() runs every
        // Token.Register'd callback synchronously on the calling thread.
        // This method is invoked from ToastWindow.OnCancelClick on the
        // dispatcher, so any in-line registrant (HttpClient SendAsync's
        // disposer, future stream-shutdown logic, future "log on cancel"
        // hook) would execute inside the click handler — and if any one
        // of them surfaced UI (showed another toast, posted to a
        // collection-view, etc.) we'd re-enter the dispatcher mid-click.
        // Punt the Cancel to the threadpool so callbacks run there;
        // the dispatcher click handler returns immediately and the
        // CloseProgress below paints synchronously without contention.
        var cts = _currentProcessingCts;
        if (cts is not null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Race with the finally-dispose in the hotkey handler — benign.
                }
            });
        }

        // Close the progress toast immediately; cancellation will play out in the background
        // and won't fire ProcessingCompleted/Failed (TextProcessor rethrows OperationCanceledException).
        var notifications = _host?.Services.GetService<INotificationService>();
        notifications?.CloseProgress();
    }

    private bool ShouldShowOnboardingWizard()
    {
        if (_host is null)
        {
            return false;
        }

        try
        {
            var configStore = _host.Services.GetRequiredService<IConfigStore>();
            var config = RunOffUiThread(() => configStore.LoadAsync());
            return !config.OnboardingCompleted;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Z8-F4 / M20 fix: pre-fix this returned false ("skip wizard
            // to be safe"), dropping the user into Settings on top of an
            // unreadable config — and any subsequent save would overwrite
            // the corrupt state silently. Fail OPEN (show wizard) so the
            // user is greeted by the first-run flow which rebuilds a
            // clean AppConfig.Default on Finish; AND surface a notification
            // so they know something went wrong with the previous config.
            Log.Warning(ex, "Could not read OnboardingCompleted; falling back to wizard on a fresh config");
            try
            {
                var notifications = _host.Services.GetService<INotificationService>();
                var translator = _host.Services.GetService<ITranslator>();
                if (notifications is not null && translator is not null)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                        notifications.ShowError(translator["msg_save_settings_failed"]));
                }
            }
            catch (Exception toastEx) when (toastEx is not OutOfMemoryException and not StackOverflowException)
            {
                Log.Debug(toastEx, "Could not surface config-load-failure toast");
            }

            return true;
        }
    }

    private void ShowOnboardingWizard()
    {
        if (_host is null)
        {
            return;
        }

        try
        {
            var wizard = _host.Services.GetRequiredService<OnboardingWizard>();

            // After Finish (Done button) the user has explicitly opted in,
            // so transition them straight into the app surface — without
            // this they used to land in tray-only mode wondering whether
            // the app actually started.  Skip and the [×] close path leave
            // VM.WasFinished false so they keep the original "dismiss
            // without engaging" semantic.  The dedicated "Open settings"
            // button on the Done page was removed at the user's request:
            // Finish now unconditionally opens Settings, so a separate
            // button became redundant.
            wizard.Closed += (_, _) =>
            {
                if (wizard.DataContext is OnboardingWizardViewModel vm
                    && vm.WasFinished)
                {
                    ShowSettingsWindow();
                }
            };

            // M23 (Z8-F7) fix: a returning user with a saved API key in
            // Credential Manager but no v2 config file used to see an
            // empty key field — the wizard hid the fact that their key
            // was still there.  Pre-populate from the credential store
            // before showing the window so they can simply click Next.
            // Fire-and-forget on a discardable continuation: the credential
            // probe is fast (synchronous registry / OSX-Keychain hop),
            // and any delay tying it to the visible window would only
            // surface in the rare slow-keychain case where we still want
            // to render the wizard rather than block on Show().
            if (wizard.DataContext is OnboardingWizardViewModel initVm)
            {
                _ = initVm.InitializeAsync();
            }

            wizard.Show();
            wizard.Activate();
            wizard.Focus();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // If the wizard itself blows up (missing resource, broken
            // binding) fall back to the regular Settings window so the
            // user is at least able to use the app.
            Log.Error(ex, "Onboarding wizard failed to open — falling back to Settings");
            ShowSettingsWindow();
        }
    }

    /// <summary>
    /// Brings any already-visible application window to the foreground.
    /// Called when a duplicate launch signals the live first instance via
    /// SingleInstance — without this the user double-clicking the .exe a
    /// second time would see nothing happen (we'd silently
    /// <c>Shutdown(0)</c>).  If no window is visible (the user previously
    /// closed Settings → app is tray-only), open the Settings window so
    /// the click feels productive.
    /// </summary>
    private void ActivateExistingApp()
    {
        if (_host is null)
        {
            return;
        }

        // Pick the most-likely "primary" surface.  Wizard outranks Settings
        // (it's modal-feeling and the user is mid-task), Settings outranks
        // anything else.  IsLoaded filter excludes singletons that were
        // constructed but never Show()n.
        var visible = Windows
            .OfType<Window>()
            .Where(w => w.IsLoaded && w.IsVisible)
            .OrderByDescending(w => w is OnboardingWizard ? 2 : (w is SettingsWindow ? 1 : 0))
            .FirstOrDefault();

        if (visible is not null)
        {
            if (visible.WindowState == WindowState.Minimized)
            {
                visible.WindowState = WindowState.Normal;
            }

            visible.Activate();
            visible.Topmost = true;
            visible.Topmost = false;  // toggle Topmost just to force z-bring-to-front
            visible.Focus();
            return;
        }

        // No visible window — Settings has been closed and the app is
        // running tray-only.  Show Settings as the canonical entry point.
        ShowSettingsWindow();
    }

    private void ShowSettingsWindow()
    {
        if (_host is null)
        {
            return;
        }

        var window = _host.Services.GetRequiredService<SettingsWindow>();
        var generalTab = _host.Services.GetRequiredService<GeneralTabViewModel>();
        var promptsTab = _host.Services.GetRequiredService<PromptsTabViewModel>();

        // Show the window FIRST so the click feels instant even when the
        // tab-load below is slow (cold disk, AV-scanned config, large
        // history file).  Pre-fix this method synchronously waited on a
        // Task.Run wrapping LoadFromConfigAsync via GetAwaiter().GetResult,
        // which (a) froze the UI thread for the whole I/O round-trip and
        // (b) ran ALL post-await property setters (Models, SelectedModel,
        // Language, Hotkey…) on the threadpool — raising PropertyChanged
        // on threadpool, which in turn called WPF binding handlers on
        // threadpool against UI elements they don't own, throwing
        // InvalidOperationException ("calling thread cannot access…").
        // The exception bubbled back through GetAwaiter().GetResult() and
        // crashed the dispatcher.  Symptom: "settings sometimes freezes
        // and crashes when I click it" — exactly the user's report.
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();

        // v15: re-probe Ollama every time Settings opens.  Replaces
        // the earlier 5s background timer (too chatty for an event
        // that the user controls) — now the probe fires on the
        // exact moments the user might care about the Provider
        // card's visibility (tray → Settings, sidebar tab clicks).
        // Fire-and-forget; the probe's own in-flight guard collapses
        // overlap with the ctor-fired startup probe or a rapid
        // re-show.
        _ = generalTab.RefreshOllamaAvailabilityAsync();

        // Then kick off the tab-load WITHOUT blocking the UI thread.  We're
        // already on the UI thread here, so awaits inside the async helper
        // capture the WPF DispatcherSynchronizationContext and resume back
        // on the UI thread — every property setter in LoadFromConfigAsync
        // / LoadAsync therefore lands on the dispatcher, where bindings
        // expect them.  The single-flight flag suppresses duplicate work
        // when the user double-clicks the tray icon.
        if (_settingsLoadInProgress)
        {
            return;
        }

        _settingsLoadInProgress = true;
        _ = LoadSettingsTabsAsync(generalTab, promptsTab);
    }

    private async Task LoadSettingsTabsAsync(
        GeneralTabViewModel generalTab,
        PromptsTabViewModel promptsTab)
    {
        try
        {
            // No ConfigureAwait(false) — we WANT to resume on the UI
            // thread so the property setters inside LoadFromConfigAsync /
            // LoadAsync raise PropertyChanged on the dispatcher (where
            // WPF bindings can react safely).
            await generalTab.LoadFromConfigAsync();
            await promptsTab.LoadAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Loading is best-effort: a transient I/O blip when re-opening
            // Settings shouldn't tear the app down.  The tabs already have
            // last-known-good state from their previous load (or defaults).
            Log.Warning(ex, "Refreshing Settings tabs from config failed");
        }
        finally
        {
            _settingsLoadInProgress = false;
        }
    }

    /// <summary>
    /// Runs an async operation on the thread pool and synchronously waits for the result.
    /// Used during App.OnStartup to avoid the classic dispatcher-deadlock that
    /// `await ... .GetAwaiter().GetResult()` causes when the await captures the WPF
    /// SynchronizationContext but the UI thread is blocked. Wrapping in Task.Run rebinds
    /// the continuation to the thread pool, breaking the cycle.
    /// </summary>
#pragma warning disable VSTHRD002 // Sync wait is intentional — we Task.Run to break sync-context capture
    private static T RunOffUiThread<T>(Func<Task<T>> work) =>
        Task.Run(work).GetAwaiter().GetResult();

    private static void RunOffUiThread(Func<Task> work) =>
        Task.Run(work).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

    /// <summary>
    /// Formats a USD amount for the toast. Sub-cent estimates round to
    /// 4 decimals so users still see "$0.0003" instead of a flat "$0.00".
    /// Uses invariant culture so the dot decimal separator stays
    /// regardless of system locale (matches OpenRouter's own UI).
    /// </summary>
    private static string FormatUsd(decimal usd)
    {
        var fmt = usd >= 0.01m ? "0.00" : "0.0000";
        return "$" + usd.ToString(fmt, CultureInfo.InvariantCulture);
    }

    // DetectLocale was used during first-run init to seed the config
    // language from the user's Windows UI culture.  Removed when the
    // product rebranded to "CapyBro" — the canonical default is now
    // English regardless of system locale, with explicit user opt-in
    // through the wizard's Language step.  Kept here as a deliberately
    // unused method so its rationale (and the System.Globalization
    // import upstream) stay searchable in source if we ever revisit
    // auto-detect behaviour.
    [SuppressMessage(
        "CodeQuality",
        "IDE0051:Remove unused private members",
        Justification = "Kept for documentation; see comment above.")]
    private static Language DetectLocale()
    {
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return lang switch
        {
            "uk" => Language.Ukrainian,
            "ru" => Language.Russian,
            _ => Language.English,
        };
    }

    private static void ConfigureLogging()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ai_text_improver_v2.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path: logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                encoding: Encoding.UTF8)
            .CreateLogger();
    }

    private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IConfigStore>(sp => new ConfigStore(
            ConfigStore.DefaultConfigPath,
            ConfigStore.DefaultLegacyConfigPath,
            sp.GetRequiredService<ILogger<ConfigStore>>()));

        services.AddSingleton<ITranslator>(_ => Translator.Instance);

        services.AddHttpClient<IOpenRouterClient, OpenRouterClient>(client =>
        {
            // Disable the HttpClient-level timeout so the per-request CTS
            // (CancelAfter(config.Timeout)) is the single source of truth.
            // With a fixed 2-minute outer timeout, any user request whose
            // config.Timeout > 120s would mysteriously fail with a generic
            // TaskCanceledException at the 2-minute mark instead of the
            // user-configured deadline.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        // Ollama local-provider client.  Same Timeout.InfiniteTimeSpan
        // rationale as OpenRouter — per-request CancelAfter from
        // TextProcessor is the single deadline source.  No BaseAddress
        // pre-set: the endpoint comes from AppConfig.OllamaEndpoint on
        // every call so the user can change it (e.g. point at a remote
        // Ollama host on their LAN) without a process restart.
        services.AddHttpClient<OllamaClient>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        services.AddSingleton<ILlmProviderFactory, LlmProviderFactory>();

        services.AddSingleton<ICredentialStore>(sp => CredentialStore.CreateDefault(
            sp.GetRequiredService<ILogger<CredentialStore>>()));

        services.AddSingleton<IAutostartService>(sp => AutostartService.CreateDefault(
            sp.GetRequiredService<ILogger<AutostartService>>()));

        services.AddSingleton<IHotkeyManager, HotkeyManager>();

        services.AddSingleton<IToastPresenter>(_ => new ToastPresenter());
        services.AddSingleton<INotificationService>(sp =>
            new NotificationService(sp.GetRequiredService<IToastPresenter>()));

        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IInputSimulator, InputSimulator>();
        services.AddSingleton<IModifierReleaseWaiter, ModifierReleaseWaiter>();
        services.AddSingleton<IPromptRegistry, PromptRegistry>();
        services.AddSingleton<IPromptPicker, PromptPicker>();
        services.AddSingleton<IPromptSelector, DefaultPromptSelector>();
        services.AddSingleton<IModelBrowser, ModelBrowser>();
        services.AddTransient<ModelsDialogViewModel>();
        services.AddSingleton<IHistoryStore>(sp =>
            HistoryStore.CreateDefault(sp.GetRequiredService<ILogger<HistoryStore>>()));
        services.AddSingleton<IDiffPreviewService, DiffPreviewService>();
        services.AddSingleton<ICostEstimator, CostEstimator>();
        services.AddSingleton<IPrivacyRedactor, PrivacyRedactor>();
        services.AddSingleton<ITextSelectionExtender, TextSelectionExtender>();
        services.AddSingleton<TextProcessor>();

        services.AddSingleton<GeneralTabViewModel>();
        services.AddSingleton<PromptsTabViewModel>();
        // Singleton: the History tab subscribes to IHistoryStore.Changed
        // and is bound directly via SettingsWindowViewModel.HistoryTab —
        // a transient lifetime would orphan the live subscription each
        // time SettingsWindow is recreated.
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsWindowViewModel>();
        services.AddSingleton<SettingsWindow>();

        // Onboarding wizard — Transient because it's a one-shot UI: closed
        // and never re-opened in the same process. A Singleton would keep
        // the closed Window instance alive, and reopening it (e.g. for
        // testing) would throw "cannot show a closed window".
        services.AddTransient<OnboardingWizardViewModel>();
        services.AddTransient<OnboardingWizard>();
    }

    private sealed class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;

        public RelayCommand(Action execute)
        {
            _execute = execute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _execute();
    }
}
