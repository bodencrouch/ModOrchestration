// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using JetBrains.Annotations;
using ModSync.Core;
using ModSync.Core.Services;
using ModSync.Core.Services.Installation;
using ModSync.Core.Services.Validation;

namespace ModSync.Dialogs.WizardPages
{
    public partial class WidescreenInstallingPage : WizardPageBase
    {
        public override string Title => "Installing Widescreen Mods";
        public override string Subtitle => "Please wait...";
        public override bool CanNavigateBack => false;
        public override bool CanNavigateForward => _canNavigateForward;

        private readonly List<ModComponent> _widescreenMods;
        private readonly MainConfig _mainConfig;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private ProgressBar _progressBar;
        private TextBlock _statusText;
        private bool _installationComplete;
        private bool _canNavigateForward;

        public WidescreenInstallingPage()
            : this(new List<ModComponent>(), new MainConfig(), new CancellationTokenSource())
        {
        }

        public WidescreenInstallingPage(
            [NotNull][ItemNotNull] List<ModComponent> widescreenMods,
            [NotNull] MainConfig mainConfig,
            [NotNull] CancellationTokenSource cancellationTokenSource)
        {
            _widescreenMods = widescreenMods ?? throw new ArgumentNullException(nameof(widescreenMods));
            if (mainConfig is null)
            {
                throw new ArgumentNullException(nameof(mainConfig));
            }
            _mainConfig = mainConfig;
            _cancellationTokenSource = cancellationTokenSource ?? throw new ArgumentNullException(nameof(cancellationTokenSource));

            InitializeComponent();
            CacheControls();
            InitializeDefaults();
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private void CacheControls()
        {
            _progressBar = this.FindControl<ProgressBar>("ProgressBar");
            _statusText = this.FindControl<TextBlock>("StatusText");
        }

        private void InitializeDefaults()
        {
            if (_statusText != null)
            {
                _statusText.Text = "Installing widescreen mods...";
            }

            if (_progressBar != null)
            {
                _progressBar.Value = 0;
            }
        }

        public override Task OnNavigatingFromAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override async Task OnNavigatedToAsync(CancellationToken cancellationToken)
        {
            if (_installationComplete)
            {
                return;
            }

            await Task.Run(async () =>
            {
                var selectedMods = _widescreenMods.Where(m => m.IsSelected).ToList();
                if (selectedMods.Count == 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (_statusText != null)
                        {
                            _statusText.Text = "Select at least one widescreen mod before installing.";
                        }
                    });
                    return;
                }

                using (var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                           cancellationToken,
                           _cancellationTokenSource.Token))
                {
                    var validationOptions = ValidationPipelineOptions.WizardFull;
                    validationOptions.MainConfig = _mainConfig;
                    InstallationInputKind inputKind = InstallationPipelineService.ClassifyInputKind(
                        selectedMods);
                    var request = new InstallationPipelineRequest(selectedMods)
                    {
                        Frontend = InstallationFrontend.GuiWidescreen,
                        InputKind = inputKind,
                        Mode = inputKind == InstallationInputKind.MarkdownGuide
                            ? InstallationPipelineMode.Reference
                            : InstallationPipelineMode.Standard,
                        Phase = InstallationPhase.Widescreen,
                        PreserveInputOrder = inputKind == InstallationInputKind.MarkdownGuide,
                        RunValidation = true,
                        ValidationOptions = validationOptions,
                        CancellationToken = linkedCancellation.Token,
                        InstallationProgress = (current, total, name) =>
                        {
                            _ = Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                if (_statusText != null)
                                {
                                    _statusText.Text = $"Installing: {name} ({current + 1}/{total})";
                                }

                                if (_progressBar != null)
                                {
                                    _progressBar.Value = total == 0 ? 0 : (double)current / total;
                                }
                            });
                        },
                    };
                    InstallationPipelineResult result = await InstallationPipelineService
                        .RunAsync(request)
                        .ConfigureAwait(false);
                    if (!result.Succeeded)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (_statusText != null)
                            {
                                _statusText.Text =
                                    $"Widescreen installation blocked ({result.ExitCode}). No later step was installed.";
                            }
                        });
                        return;
                    }
                }

                _installationComplete = true;
                _canNavigateForward = true;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_statusText != null)
                    {
                        _statusText.Text = "Widescreen installation complete!";
                    }

                    if (_progressBar != null)
                    {
                        _progressBar.Value = 1;
                    }
                });
            }, cancellationToken);
        }

        public override Task<(bool isValid, string errorMessage)> ValidateAsync(CancellationToken cancellationToken)
        {
            if (!_installationComplete)
            {
                return Task.FromResult((false, "Installation in progress"));
            }

            return Task.FromResult((true, (string)null));
        }
    }
}
