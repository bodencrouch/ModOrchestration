// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using JetBrains.Annotations;
using ModSync.Core;
using ModSync.Core.Installation;
using ModSync.Core.Services.Checkpoints;
using ModSync.Core.Services.Fomod;
using ModSync.Core.Services.Validation;

namespace ModSync.Dialogs.WizardPages
{
    public partial class InstallStartPage : WizardPageBase
    {
        private enum ResumeChoice
        {
            None,
            Resume,
            StartOver,
        }

        private readonly List<ModComponent> _allComponents;
        private TextBlock _selectedModsText;
        private StackPanel _modListPanel;
        private TextBlock _readinessStatusText;
        private TextBlock _readinessDetailText;
        private Border _resumeOfferPanel;
        private TextBlock _resumeOfferDetailText;
        private TextBlock _resumeChoiceStatusText;
        private Button _resumePreviousButton;
        private Button _startOverButton;

        [CanBeNull]
        private ValidationPipelineResult _lastReadinessResult;

        private bool _readinessHasCriticalErrors;
        private string _readinessErrorMessage = string.Empty;
        private bool _resumeOfferVisible;
        private ResumeChoice _resumeChoice = ResumeChoice.None;
        private ResumableSessionInfo _resumeProbe = ResumableSessionInfo.None;

        public InstallStartPage()
            : this(new List<ModComponent>())
        {
        }

        public InstallStartPage([NotNull][ItemNotNull] List<ModComponent> allComponents)
        {
            _allComponents = allComponents ?? throw new ArgumentNullException(nameof(allComponents));

            InitializeComponent();
            RefreshSummary();
        }

        public override string Title => "Ready to Install";

        public override string Subtitle => "Review your selections and begin installation";

        /// <summary>True when the last readiness run found critical environment blockers.</summary>
        public bool ReadinessHasCriticalErrors => _readinessHasCriticalErrors;

        [CanBeNull]
        public string ReadinessStatusDisplayText => _readinessStatusText?.Text;

        /// <summary>True when a resumable prior session was offered on this page.</summary>
        public bool IsResumeOfferVisible => _resumeOfferVisible;

        public bool HasChosenResume => _resumeChoice == ResumeChoice.Resume;

        public bool HasChosenStartOver => _resumeChoice == ResumeChoice.StartOver;

        public override async Task OnNavigatedToAsync(CancellationToken cancellationToken)
        {
            RefreshSummary();
            await RunReadinessAsync(cancellationToken).ConfigureAwait(true);
            await ProbeResumeOfferAsync(cancellationToken).ConfigureAwait(true);
        }

        public override async Task<(bool isValid, string errorMessage)> ValidateAsync(CancellationToken cancellationToken)
        {
            int selectedCount = _allComponents.Count(c => c.IsSelected && !c.WidescreenOnly);
            if (selectedCount == 0)
            {
                return (false, "No mods are selected. Go back to Mod Selection and choose mods to install.");
            }

            string modDirectory = MainConfig.Instance?.sourcePath?.FullName;
            if (string.IsNullOrWhiteSpace(modDirectory) || !Directory.Exists(modDirectory))
            {
                return (
                    false,
                    "Mod directory is not set or does not exist. Set the mod directory before installing.");
            }

            if (_lastReadinessResult is null)
            {
                await RunReadinessAsync(cancellationToken).ConfigureAwait(true);
            }

            if (_readinessHasCriticalErrors)
            {
                return (false, string.IsNullOrWhiteSpace(_readinessErrorMessage)
                    ? "Environment readiness failed. Fix the issues above or re-run Validate for details."
                    : _readinessErrorMessage);
            }

            if (_resumeOfferVisible && _resumeChoice == ResumeChoice.None)
            {
                return (false, "Choose Resume previous install or Start over before continuing.");
            }

            if (_resumeChoice == ResumeChoice.StartOver)
            {
                await ApplyStartOverAsync().ConfigureAwait(true);
            }

            var selected = _allComponents.Where(c => c.IsSelected && !c.WidescreenOnly).ToList();
            FomodConfigurationGate.GateResult gateResult = FomodConfigurationGate.Validate(
                _allComponents,
                selected,
                modDirectory);
            if (!gateResult.Passed)
            {
                FomodConfigurationGate.GateIssue first = gateResult.Issues[0];
                string message = $"{first.Component.Name}: {FomodConfigurationGate.FormatIssueMessage(first)}";
                if (gateResult.Issues.Count > 1)
                {
                    message += $" (+{gateResult.Issues.Count - 1} more unconfigured FOMOD archive(s))";
                }

                return (false, message);
            }

            return (true, (string)null);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            _selectedModsText = this.FindControl<TextBlock>("SelectedModsText");
            _modListPanel = this.FindControl<StackPanel>("ModListPanel");
            _readinessStatusText = this.FindControl<TextBlock>("ReadinessStatusText");
            _readinessDetailText = this.FindControl<TextBlock>("ReadinessDetailText");
            _resumeOfferPanel = this.FindControl<Border>("ResumeOfferPanel");
            _resumeOfferDetailText = this.FindControl<TextBlock>("ResumeOfferDetailText");
            _resumeChoiceStatusText = this.FindControl<TextBlock>("ResumeChoiceStatusText");
            _resumePreviousButton = this.FindControl<Button>("ResumePreviousButton");
            _startOverButton = this.FindControl<Button>("StartOverButton");

            if (_resumePreviousButton != null)
            {
                _resumePreviousButton.Click += ResumePreviousButton_Click;
            }

            if (_startOverButton != null)
            {
                _startOverButton.Click += StartOverButton_Click;
            }
        }

        private async Task ProbeResumeOfferAsync(CancellationToken cancellationToken)
        {
            _resumeOfferVisible = false;
            _resumeChoice = ResumeChoice.None;
            _resumeProbe = ResumableSessionInfo.None;

            if (_resumeOfferPanel != null)
            {
                _resumeOfferPanel.IsVisible = false;
            }

            DirectoryInfo destination = MainConfig.Instance?.destinationPath;
            if (destination is null || !destination.Exists)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            _resumeProbe = await CheckpointManager.ProbeResumableSessionAsync(
                destination,
                _allComponents).ConfigureAwait(true);

            if (!_resumeProbe.IsResumable)
            {
                return;
            }

            _resumeOfferVisible = true;
            if (_resumeOfferPanel != null)
            {
                _resumeOfferPanel.IsVisible = true;
            }

            if (_resumeOfferDetailText != null)
            {
                _resumeOfferDetailText.Text =
                    $"{_resumeProbe.CompletedSelectedCount} selected mod(s) already completed; " +
                    $"{_resumeProbe.RemainingSelectedCount} remaining. " +
                    "Resume continues unfinished work. Start over clears the install session file (not Git checkpoint history).";
            }

            if (_resumeChoiceStatusText != null)
            {
                _resumeChoiceStatusText.Text = "Choose how to continue.";
            }
        }

        private void ResumePreviousButton_Click(object sender, RoutedEventArgs e)
        {
            _resumeChoice = ResumeChoice.Resume;
            if (_resumeChoiceStatusText != null)
            {
                _resumeChoiceStatusText.Text = "Will resume previous install (skip completed mods).";
            }
        }

        private void StartOverButton_Click(object sender, RoutedEventArgs e)
        {
            _resumeChoice = ResumeChoice.StartOver;
            if (_resumeChoiceStatusText != null)
            {
                _resumeChoiceStatusText.Text = "Will start over (clear install session, then install from the beginning).";
            }
        }

        private async Task ApplyStartOverAsync()
        {
            DirectoryInfo destination = MainConfig.Instance?.destinationPath;
            if (destination != null)
            {
                await CheckpointManager.DeleteSessionFileAsync(destination).ConfigureAwait(true);
            }

            foreach (ModComponent component in _allComponents.Where(c => c.IsSelected && !c.WidescreenOnly))
            {
                component.InstallState = ModComponent.ComponentInstallState.Pending;
                component.LastStartedUtc = null;
                component.LastCompletedUtc = null;
            }

            _resumeOfferVisible = false;
            _resumeChoice = ResumeChoice.None;
            if (_resumeOfferPanel != null)
            {
                _resumeOfferPanel.IsVisible = false;
            }
        }

        private async Task RunReadinessAsync(CancellationToken cancellationToken)
        {
            if (_readinessStatusText != null)
            {
                _readinessStatusText.Text = "Checking environment...";
            }

            if (_readinessDetailText != null)
            {
                _readinessDetailText.IsVisible = false;
                _readinessDetailText.Text = string.Empty;
            }

            var options = ValidationPipelineOptions.InstallStartReadiness;
            options.MainConfig = MainConfig.Instance;
            options.CancellationToken = cancellationToken;

            List<ModComponent> previousAll = MainConfig.AllComponents;
            MainConfig.AllComponents = _allComponents;

            try
            {
                ValidationPipelineResult result = await InstallationValidationPipeline.RunAsync(
                    _allComponents,
                    options).ConfigureAwait(true);

                _lastReadinessResult = result;
                _readinessHasCriticalErrors = result.HasCriticalErrors || !result.IsSuccess;

                ValidationPipelineStageResult envStage = result.Stages
                    .FirstOrDefault(s => s.Stage == ValidationPipelineStage.Environment);

                if (_readinessHasCriticalErrors)
                {
                    _readinessErrorMessage = envStage?.Summary
                        ?? result.Stages.FirstOrDefault()?.Summary
                        ?? "Environment readiness failed.";
                    if (_readinessStatusText != null)
                    {
                        _readinessStatusText.Text = "Not ready — fix blockers before installing.";
                    }

                    if (_readinessDetailText != null)
                    {
                        _readinessDetailText.Text = _readinessErrorMessage;
                        _readinessDetailText.IsVisible = true;
                    }
                }
                else
                {
                    _readinessErrorMessage = string.Empty;
                    if (_readinessStatusText != null)
                    {
                        _readinessStatusText.Text = "Ready — environment checks passed.";
                    }

                    if (_readinessDetailText != null)
                    {
                        _readinessDetailText.Text = envStage?.Summary ?? "Installation environment is valid.";
                        _readinessDetailText.IsVisible = true;
                    }
                }

                if (envStage != null && envStage.Messages.Count > 0 && _readinessDetailText != null)
                {
                    string extra = string.Join(Environment.NewLine, envStage.Messages.Take(3));
                    if (!string.IsNullOrWhiteSpace(extra) &&
                        !string.Equals(_readinessDetailText.Text, extra, StringComparison.Ordinal))
                    {
                        _readinessDetailText.Text = string.IsNullOrWhiteSpace(_readinessDetailText.Text)
                            ? extra
                            : _readinessDetailText.Text + Environment.NewLine + extra;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _lastReadinessResult = null;
                _readinessHasCriticalErrors = true;
                _readinessErrorMessage = "Environment readiness was cancelled.";
                if (_readinessStatusText != null)
                {
                    _readinessStatusText.Text = "Readiness check cancelled.";
                }
            }
            catch (Exception ex)
            {
                _lastReadinessResult = null;
                _readinessHasCriticalErrors = true;
                _readinessErrorMessage = $"Environment readiness failed: {ex.Message}";
                if (_readinessStatusText != null)
                {
                    _readinessStatusText.Text = "Not ready — readiness check failed.";
                }

                if (_readinessDetailText != null)
                {
                    _readinessDetailText.Text = _readinessErrorMessage;
                    _readinessDetailText.IsVisible = true;
                }
            }
            finally
            {
                MainConfig.AllComponents = previousAll;
            }
        }

        private void RefreshSummary()
        {
            var selectedMods = _allComponents.Where(c => c.IsSelected && !c.WidescreenOnly).ToList();

            if (_selectedModsText != null)
            {
                _selectedModsText.Text = selectedMods.Count == 0
                    ? "No mods selected — go back to Mod Selection before continuing."
                    : $"{selectedMods.Count} mods selected for installation";
            }

            if (_modListPanel == null)
            {
                return;
            }

            _modListPanel.Children.Clear();

            if (selectedMods.Count == 0)
            {
                _modListPanel.Children.Add(new TextBlock
                {
                    Text = "Use Mod Selection to choose at least one mod, then return here to review.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85,
                });
                return;
            }

            foreach (ModComponent mod in selectedMods)
            {
                _modListPanel.Children.Add(new TextBlock
                {
                    Text = $"• {mod.Name}",
                    TextWrapping = TextWrapping.Wrap,
                });

                if (!string.IsNullOrWhiteSpace(mod.InstallationWarning))
                {
                    _modListPanel.Children.Add(new TextBlock
                    {
                        Text = $"  ⚠ {mod.InstallationWarning}",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.9,
                    });
                }
            }
        }
    }
}
