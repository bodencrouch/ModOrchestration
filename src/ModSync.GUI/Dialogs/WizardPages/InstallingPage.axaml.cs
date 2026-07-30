// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using JetBrains.Annotations;
using ModSync.Core;
using ModSync.Core.Services;
using ModSync.Core.Utility;

namespace ModSync.Dialogs.WizardPages
{
    public partial class InstallingPage : WizardPageBase
    {
        public override string Title => "Installing Mods";
        public override string Subtitle => "Please wait while mods are being installed...";
        public override bool CanNavigateBack => false;
        public override bool CanNavigateForward => _canNavigateForward;

        private readonly List<ModComponent> _allComponents;

        private ProgressBar _mainProgressBar;
        private ProgressBar _currentModProgress;
        private TextBlock _percentText;
        private TextBlock _countText;
        private TextBlock _currentModText;
        private TextBlock _currentOperationText;
        private TextBlock _elapsedTimeText;
        private TextBlock _remainingTimeText;
        private TextBlock _rateText;
        private TextBlock _warningsText;
        private TextBlock _errorsText;
        private TextBlock _directionsText;
        private TextBlock _checkpointStatusText;
        private TextBlock _checkpointsCreatedText;
        private TextBlock _runStateText;
        private Border _failurePanel;
        private TextBlock _failureSummaryText;
        private Button _resumeRetryButton;

        private bool _isInstalling;
        private bool _installationSucceeded;
        private bool _runFinished;
        private bool _canNavigateForward;
        private int _installedCount;
        private int _warningCount;
        private int _errorCount;
        private int _checkpointsCreated;
        private Stopwatch _stopwatch;
        private CancellationToken _pageCancellationToken;

        /// <summary>
        /// Optional install runner for tests. Defaults to <see cref="InstallationService.InstallAllSelectedComponentsAsync"/>.
        /// </summary>
        [CanBeNull]
        internal Func<
            List<ModComponent>,
            Action<int, int, string>,
            CancellationToken,
            Task<ModComponent.InstallExitCode>> InstallRunner
        { get; set; }

        public InstallingPage()
            : this(new List<ModComponent>(), new MainConfig(), new CancellationTokenSource())
        {
        }

        public InstallingPage(
            [NotNull][ItemNotNull] List<ModComponent> allComponents,
            [NotNull] MainConfig mainConfig,
            [NotNull] CancellationTokenSource cancellationTokenSource)
        {
            _allComponents = allComponents ?? throw new ArgumentNullException(nameof(allComponents));
            if (mainConfig is null)
            {
                throw new ArgumentNullException(nameof(mainConfig));
            }

            if (cancellationTokenSource is null)
            {
                throw new ArgumentNullException(nameof(cancellationTokenSource));
            }

            InitializeComponent();
            CacheControls();
            InitializeDefaults();
        }

        /// <summary>True after a successful install exit (Next is allowed).</summary>
        public bool InstallationSucceeded => _installationSucceeded;

        /// <summary>Exposes resume button visibility for headless tests.</summary>
        public bool IsResumeRetryVisible => _resumeRetryButton?.IsVisible == true;

        /// <summary>Exposes failure panel visibility for headless tests.</summary>
        public bool IsFailurePanelVisible => _failurePanel?.IsVisible == true;

        public string RunStateDisplayText => _runStateText?.Text ?? string.Empty;

        public string FailureSummaryDisplayText => _failureSummaryText?.Text ?? string.Empty;

        public override Task OnNavigatedToAsync(CancellationToken cancellationToken)
        {
            _pageCancellationToken = cancellationToken;

            if (_isInstalling || _installationSucceeded)
            {
                return Task.CompletedTask;
            }

            // Do not auto-restart after a finished failure/cancel — user must Resume/Retry.
            if (_runFinished)
            {
                return Task.CompletedTask;
            }

            StartInstallation(cancellationToken);
            return Task.CompletedTask;
        }

        public override Task<(bool isValid, string errorMessage)> ValidateAsync(CancellationToken cancellationToken)
        {
            if (!_installationSucceeded)
            {
                if (_isInstalling)
                {
                    return Task.FromResult((false, "Installation is still in progress. Please wait for it to complete."));
                }

                if (_runFinished)
                {
                    return Task.FromResult((false,
                        "Installation did not complete successfully. Use Resume / Retry to continue remaining mods, or cancel the wizard."));
                }

                return Task.FromResult((false, "Installation has not finished yet."));
            }

            return Task.FromResult((true, (string)null));
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        public override Task OnNavigatingFromAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private void CacheControls()
        {
            _mainProgressBar = this.FindControl<ProgressBar>("MainProgressBar");
            _currentModProgress = this.FindControl<ProgressBar>("CurrentModProgress");
            _percentText = this.FindControl<TextBlock>("PercentText");
            _countText = this.FindControl<TextBlock>("CountText");
            _currentModText = this.FindControl<TextBlock>("CurrentModText");
            _currentOperationText = this.FindControl<TextBlock>("CurrentOperationText");
            _elapsedTimeText = this.FindControl<TextBlock>("ElapsedTimeText");
            _remainingTimeText = this.FindControl<TextBlock>("RemainingTimeText");
            _rateText = this.FindControl<TextBlock>("RateText");
            _warningsText = this.FindControl<TextBlock>("WarningsText");
            _errorsText = this.FindControl<TextBlock>("ErrorsText");
            _directionsText = this.FindControl<TextBlock>("DirectionsText");
            _checkpointStatusText = this.FindControl<TextBlock>("CheckpointStatusText");
            _checkpointsCreatedText = this.FindControl<TextBlock>("CheckpointsCreatedText");
            _runStateText = this.FindControl<TextBlock>("RunStateText");
            _failurePanel = this.FindControl<Border>("FailurePanel");
            _failureSummaryText = this.FindControl<TextBlock>("FailureSummaryText");
            _resumeRetryButton = this.FindControl<Button>("ResumeRetryButton");

            if (_resumeRetryButton != null)
            {
                _resumeRetryButton.Click += ResumeRetryButton_Click;
            }
        }

        private void InitializeDefaults()
        {
            if (_percentText != null)
            {
                _percentText.Text = "0%";
            }

            if (_countText != null)
            {
                _countText.Text = "0/0 mods installed";
            }

            if (_currentModText != null)
            {
                _currentModText.Text = "Preparing installation...";
            }

            if (_currentOperationText != null)
            {
                _currentOperationText.Text = "Initializing...";
            }

            if (_runStateText != null)
            {
                _runStateText.Text = "Status: ready";
            }

            if (_elapsedTimeText != null)
            {
                _elapsedTimeText.Text = "--:--:--";
            }

            if (_remainingTimeText != null)
            {
                _remainingTimeText.Text = "--:--:--";
            }

            if (_rateText != null)
            {
                _rateText.Text = "0.0 mods/min";
            }

            if (_warningsText != null)
            {
                _warningsText.Text = "0";
            }

            if (_errorsText != null)
            {
                _errorsText.Text = "0";
            }

            if (_mainProgressBar != null)
            {
                _mainProgressBar.Value = 0;
            }

            if (_currentModProgress != null)
            {
                _currentModProgress.IsIndeterminate = true;
                _currentModProgress.Value = 0;
            }

            if (_checkpointStatusText != null)
            {
                _checkpointStatusText.Text = "Checkpoint system enabled";
            }

            if (_checkpointsCreatedText != null)
            {
                _checkpointsCreatedText.Text = "0";
            }

            HideFailureUi();
        }

        private void StartInstallation(CancellationToken cancellationToken)
        {
            _isInstalling = true;
            _runFinished = false;
            _installationSucceeded = false;
            _canNavigateForward = false;
            _stopwatch = Stopwatch.StartNew();
            Logger.Logged += OnLogMessage;
            Logger.ExceptionLogged += OnException;

            HideFailureUi();
            if (_runStateText != null)
            {
                _runStateText.Text = "Status: installing (healthy)";
            }

            _ = Task.Run(async () => await RunInstallation(cancellationToken).ConfigureAwait(false), cancellationToken);
        }

        private void ResumeRetryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isInstalling)
            {
                return;
            }

            StartInstallation(_pageCancellationToken);
        }

        private async Task RunInstallation(CancellationToken cancellationToken)
        {
            try
            {
                var selectedMods = _allComponents.Where(c => c.IsSelected && !c.WidescreenOnly).ToList();
                int totalMods = selectedMods.Count;

                void ProgressCallback(int currentIndex, int total, string componentName)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    _ = UpdateUIAsync(() =>
                    {
                        double progress = total == 0 ? 0 : (double)currentIndex / total;
                        if (_mainProgressBar != null)
                        {
                            _mainProgressBar.Value = progress;
                        }

                        if (_percentText != null)
                        {
                            _percentText.Text = $"{Math.Round(progress * 100)}%";
                        }

                        if (_countText != null)
                        {
                            _countText.Text = $"{currentIndex}/{total} mods installed";
                        }

                        if (_currentModText != null)
                        {
                            _currentModText.Text = $"Installing: {componentName}";
                        }

                        ModComponent component = selectedMods.FirstOrDefault(c => string.Equals(c.Name, componentName, StringComparison.Ordinal));
                        if (_directionsText != null && component != null)
                        {
                            _directionsText.Text = component.Directions ?? string.Empty;
                        }

                        _installedCount = currentIndex;
                        UpdateMetrics(total);

                        if (_checkpointStatusText != null)
                        {
                            _checkpointStatusText.Text = $"Creating checkpoint for '{componentName}'...";
                        }

                        if (_runStateText != null)
                        {
                            _runStateText.Text = "Status: installing (healthy)";
                        }
                    });
                }

                void CheckpointLogHandler(string message)
                {
                    if (message != null && NetFrameworkCompatibility.Contains(message, "✓ Checkpoint created:", StringComparison.OrdinalIgnoreCase))
                    {
                        _checkpointsCreated++;
                        _ = UpdateUIAsync(() =>
                        {
                            if (_checkpointsCreatedText != null)
                            {
                                _checkpointsCreatedText.Text = _checkpointsCreated.ToString(CultureInfo.InvariantCulture);
                            }

                            if (_checkpointStatusText != null)
                            {
                                _checkpointStatusText.Text = "Checkpoint created successfully";
                            }
                        });
                    }
                }

                Logger.Logged += CheckpointLogHandler;

                try
                {
                    await Logger.LogAsync("Initializing checkpoint system...");

                    await UpdateUIAsync(() =>
                    {
                        if (_checkpointStatusText != null)
                        {
                            _checkpointStatusText.Text = "Initializing checkpoint system...";
                        }
                    });

                    Func<List<ModComponent>, Action<int, int, string>, CancellationToken, Task<ModComponent.InstallExitCode>> runner =
                        InstallRunner ?? ((components, progress, token) =>
                            InstallationService.InstallAllSelectedComponentsAsync(components, progress, token));

                    ModComponent.InstallExitCode exitCode = await runner(
                        _allComponents,
                        ProgressCallback,
                        cancellationToken
                    ).ConfigureAwait(false);

                    await ApplyTerminalOutcomeAsync(exitCode, selectedMods, wasCancelled: false).ConfigureAwait(false);
                }
                finally
                {
                    Logger.Logged -= CheckpointLogHandler;
                }
            }
            catch (OperationCanceledException)
            {
                var selectedMods = _allComponents.Where(c => c.IsSelected && !c.WidescreenOnly).ToList();
                await ApplyTerminalOutcomeAsync(
                    ModComponent.InstallExitCode.UserCancelledInstall,
                    selectedMods,
                    wasCancelled: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Logger.LogExceptionAsync(ex, "Error during installation");
                var selectedMods = _allComponents.Where(c => c.IsSelected && !c.WidescreenOnly).ToList();
                await ApplyTerminalOutcomeAsync(
                    ModComponent.InstallExitCode.UnknownError,
                    selectedMods,
                    wasCancelled: false,
                    exceptionMessage: ex.Message).ConfigureAwait(false);
            }
            finally
            {
                _isInstalling = false;
                _runFinished = true;
                _stopwatch?.Stop();
                Logger.Logged -= OnLogMessage;
                Logger.ExceptionLogged -= OnException;
            }
        }

        private async Task ApplyTerminalOutcomeAsync(
            ModComponent.InstallExitCode exitCode,
            [NotNull] List<ModComponent> selectedMods,
            bool wasCancelled,
            [CanBeNull] string exceptionMessage = null)
        {
            int completed = selectedMods.Count(c =>
                c.InstallState == ModComponent.ComponentInstallState.Completed ||
                c.InstallState == ModComponent.ComponentInstallState.Skipped);
            int remaining = selectedMods.Count - completed;
            bool success = !wasCancelled && exitCode == ModComponent.InstallExitCode.Success;

            _installationSucceeded = success;
            _canNavigateForward = success;

            await UpdateUIAsync(() =>
            {
                if (_currentModProgress != null)
                {
                    _currentModProgress.IsIndeterminate = false;
                    _currentModProgress.Value = success ? 1 : Math.Max(0, completed / (double)Math.Max(1, selectedMods.Count));
                }

                if (_countText != null)
                {
                    _countText.Text = $"{completed}/{selectedMods.Count} mods completed";
                }

                if (success)
                {
                    if (_mainProgressBar != null)
                    {
                        _mainProgressBar.Value = 1;
                    }

                    if (_percentText != null)
                    {
                        _percentText.Text = "100%";
                    }

                    if (_currentModText != null)
                    {
                        _currentModText.Text = "Installation complete!";
                    }

                    if (_currentOperationText != null)
                    {
                        _currentOperationText.Text = "Complete";
                    }

                    if (_runStateText != null)
                    {
                        _runStateText.Text = "Status: succeeded";
                    }

                    if (_checkpointStatusText != null)
                    {
                        _checkpointStatusText.Text = $"✓ {_checkpointsCreated} checkpoints created";
                    }

                    HideFailureUi();
                }
                else
                {
                    string outcomeLabel = wasCancelled || exitCode == ModComponent.InstallExitCode.UserCancelledInstall
                        ? "cancelled"
                        : "failed";

                    if (_currentModText != null)
                    {
                        _currentModText.Text = wasCancelled || exitCode == ModComponent.InstallExitCode.UserCancelledInstall
                            ? "Installation cancelled"
                            : "Installation did not complete";
                    }

                    if (_currentOperationText != null)
                    {
                        _currentOperationText.Text = wasCancelled ? "Cancelled" : "Failed";
                    }

                    if (_runStateText != null)
                    {
                        _runStateText.Text = $"Status: {outcomeLabel}";
                    }

                    if (_checkpointStatusText != null)
                    {
                        _checkpointStatusText.Text = wasCancelled
                            ? "Installation was cancelled"
                            : $"Stopped: {UtilityHelper.GetEnumDescription(exitCode)}";
                    }

                    string summary =
                        $"{completed} of {selectedMods.Count} selected mods completed; {remaining} remaining. " +
                        $"Exit: {UtilityHelper.GetEnumDescription(exitCode)}.";
                    if (!string.IsNullOrWhiteSpace(exceptionMessage))
                    {
                        summary += $" {exceptionMessage}";
                    }

                    if (remaining <= 0 && !success)
                    {
                        summary += " Session resume may be unavailable for further work.";
                    }

                    summary += " Resume continues remaining mods; it does not restore a pristine game folder.";

                    if (_failurePanel != null)
                    {
                        _failurePanel.IsVisible = true;
                    }

                    if (_failureSummaryText != null)
                    {
                        _failureSummaryText.Text = summary;
                    }

                    if (_resumeRetryButton != null)
                    {
                        _resumeRetryButton.IsVisible = remaining > 0 || !success;
                        _resumeRetryButton.Content = remaining > 0 ? "Resume / Retry" : "Retry";
                    }
                }

                UpdateMetrics(selectedMods.Count);
            }).ConfigureAwait(false);

            if (!success)
            {
                await Logger.LogErrorAsync(
                    $"Installation ended with exit code: {UtilityHelper.GetEnumDescription(exitCode)} " +
                    $"({completed} completed, {remaining} remaining)."
                ).ConfigureAwait(false);
            }
        }

        private void HideFailureUi()
        {
            if (_failurePanel != null)
            {
                _failurePanel.IsVisible = false;
            }

            if (_resumeRetryButton != null)
            {
                _resumeRetryButton.IsVisible = false;
            }

            if (_failureSummaryText != null)
            {
                _failureSummaryText.Text = string.Empty;
            }
        }

        private void OnLogMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            if (message.IndexOf("[Warning]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _warningCount++;
            }

            if (message.IndexOf("[Error]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _errorCount++;
            }

            _ = UpdateUIAsync(() =>
            {
                if (_warningsText != null)
                {
                    _warningsText.Text = _warningCount.ToString(CultureInfo.InvariantCulture);
                }

                if (_errorsText != null)
                {
                    _errorsText.Text = _errorCount.ToString(CultureInfo.InvariantCulture);
                }
            });
        }

        private void OnException(Exception ex)
        {
            _errorCount++;
            _ = UpdateUIAsync(() =>
            {
                if (_errorsText != null)
                {
                    _errorsText.Text = _errorCount.ToString(CultureInfo.InvariantCulture);
                }
            });
        }

        private void UpdateMetrics(int totalMods)
        {
            TimeSpan elapsed = _stopwatch?.Elapsed ?? TimeSpan.Zero;

            if (_elapsedTimeText != null)
            {
                _elapsedTimeText.Text = elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            }

            if (_installedCount > 0)
            {
                var avgPerMod = TimeSpan.FromTicks(elapsed.Ticks / Math.Max(1, _installedCount));
                int remaining = Math.Max(0, totalMods - _installedCount);
                var eta = TimeSpan.FromTicks(avgPerMod.Ticks * remaining);

                if (_remainingTimeText != null)
                {
                    _remainingTimeText.Text = eta.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
                }

                double perMinute = _installedCount / Math.Max(0.001, elapsed.TotalMinutes);
                if (_rateText != null)
                {
                    _rateText.Text = $"{perMinute:0.0} mods/min";
                }
            }
            else
            {
                if (_remainingTimeText != null)
                {
                    _remainingTimeText.Text = "--:--:--";
                }

                if (_rateText != null)
                {
                    _rateText.Text = "0.0 mods/min";
                }
            }
        }

        private Task UpdateUIAsync(Action action)
        {
            return Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Normal).GetTask();
        }
    }
}
