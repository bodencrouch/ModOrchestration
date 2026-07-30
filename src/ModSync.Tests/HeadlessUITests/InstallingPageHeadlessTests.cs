// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ModSync.Core;
using ModSync.Dialogs.WizardPages;
using Xunit;

namespace ModSync.Tests.HeadlessUITests
{
    [Collection(HeadlessTestApp.CollectionName)]
    public sealed class InstallingPageHeadlessTests
    {
        [AvaloniaFact(DisplayName = "Installing page success enables Next and hides Resume")]
        public async Task InstallingPage_Success_EnablesNext_HidesResume()
        {
            var completed = new ModComponent
            {
                Guid = Guid.NewGuid(),
                Name = "DoneMod",
                IsSelected = true,
                InstallState = ModComponent.ComponentInstallState.Pending,
            };

            InstallingPage page = await CreatePageAsync(new List<ModComponent> { completed });
            page.InstallRunner = async (components, progress, token) =>
            {
                progress?.Invoke(0, 1, completed.Name);
                completed.InstallState = ModComponent.ComponentInstallState.Completed;
                progress?.Invoke(1, 1, completed.Name);
                await Task.Yield();
                return ModComponent.InstallExitCode.Success;
            };

            Window window = await HostInWindowAsync(page);
            try
            {
                await page.OnNavigatedToAsync(CancellationToken.None);
                await WaitForAsync(() => Task.FromResult(page.InstallationSucceeded), TimeSpan.FromSeconds(5));

                (bool isValid, string _) = await page.ValidateAsync(CancellationToken.None);
                Assert.True(isValid);
                Assert.False(page.IsFailurePanelVisible);
                Assert.False(page.IsResumeRetryVisible);
                Assert.Contains("succeeded", page.RunStateDisplayText, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await CloseWindowAsync(window);
            }
        }

        [AvaloniaFact(DisplayName = "Installing page failure shows completed vs remaining and Resume")]
        public async Task InstallingPage_PartialFailure_ShowsResume()
        {
            var done = new ModComponent
            {
                Guid = Guid.NewGuid(),
                Name = "Done",
                IsSelected = true,
            };
            var fail = new ModComponent
            {
                Guid = Guid.NewGuid(),
                Name = "Fail",
                IsSelected = true,
            };

            InstallingPage page = await CreatePageAsync(new List<ModComponent> { done, fail });
            int runCount = 0;
            page.InstallRunner = async (components, progress, token) =>
            {
                runCount++;
                progress?.Invoke(0, 2, done.Name);
                done.InstallState = ModComponent.ComponentInstallState.Completed;
                progress?.Invoke(1, 2, fail.Name);
                fail.InstallState = ModComponent.ComponentInstallState.Failed;
                await Task.Yield();
                return ModComponent.InstallExitCode.UnknownError;
            };

            Window window = await HostInWindowAsync(page);
            try
            {
                await page.OnNavigatedToAsync(CancellationToken.None);
                await WaitForAsync(
                    () => Task.FromResult(page.IsFailurePanelVisible && page.IsResumeRetryVisible),
                    TimeSpan.FromSeconds(5));

                (bool isValid, string error) = await page.ValidateAsync(CancellationToken.None);
                Assert.False(isValid);
                Assert.Contains("Resume", error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("1 of 2", page.FailureSummaryDisplayText, StringComparison.Ordinal);
                Assert.Contains("remaining", page.FailureSummaryDisplayText, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("does not restore a pristine", page.FailureSummaryDisplayText, StringComparison.OrdinalIgnoreCase);

                Button resume = page.FindControl<Button>("ResumeRetryButton");
                Assert.NotNull(resume);
                resume.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

                await WaitForAsync(() => Task.FromResult(runCount >= 2 && page.IsFailurePanelVisible), TimeSpan.FromSeconds(5));
                Assert.Equal(2, runCount);
                Assert.Equal(ModComponent.ComponentInstallState.Completed, done.InstallState);
            }
            finally
            {
                await CloseWindowAsync(window);
            }
        }

        [AvaloniaFact(DisplayName = "Installing page cancel does not claim success")]
        public async Task InstallingPage_Cancel_DoesNotClaimSuccess()
        {
            var mod = new ModComponent
            {
                Guid = Guid.NewGuid(),
                Name = "CancelMe",
                IsSelected = true,
            };

            InstallingPage page = await CreatePageAsync(new List<ModComponent> { mod });
            page.InstallRunner = async (components, progress, token) =>
            {
                progress?.Invoke(0, 1, mod.Name);
                await Task.Yield();
                return ModComponent.InstallExitCode.UserCancelledInstall;
            };

            Window window = await HostInWindowAsync(page);
            try
            {
                await page.OnNavigatedToAsync(CancellationToken.None);
                await WaitForAsync(() => Task.FromResult(page.IsFailurePanelVisible), TimeSpan.FromSeconds(5));

                Assert.False(page.InstallationSucceeded);
                (bool isValid, _) = await page.ValidateAsync(CancellationToken.None);
                Assert.False(isValid);
                Assert.Contains("cancelled", page.RunStateDisplayText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("complete!", page.FindControl<TextBlock>("CurrentModText")?.Text ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await CloseWindowAsync(window);
            }
        }

        [AvaloniaFact(DisplayName = "Installing page hides Resume/Retry after an explicit Stop Install")]
        public async Task InstallingPage_StopInstallCancellation_HidesResumeRetry()
        {
            var mod = new ModComponent
            {
                Guid = Guid.NewGuid(),
                Name = "StoppedMod",
                IsSelected = true,
            };

            InstallingPage page = await CreatePageAsync(new List<ModComponent> { mod });
            page.InstallRunner = async (components, progress, token) =>
            {
                progress?.Invoke(0, 1, mod.Name);
                await Task.Yield();
                throw new OperationCanceledException();
            };

            Window window = await HostInWindowAsync(page);
            try
            {
                await page.OnNavigatedToAsync(CancellationToken.None);
                await WaitForAsync(() => Task.FromResult(page.IsFailurePanelVisible), TimeSpan.FromSeconds(5));

                // A real Stop Install click cancels the wizard dialog's single-lifetime
                // CancellationTokenSource, which stays cancelled for the rest of the dialog's
                // life. Re-invoking install with that same token would hang immediately
                // (Task.Run never runs its delegate for an already-cancelled token), so
                // in-page Resume/Retry must not be offered after this kind of cancellation.
                Assert.False(page.IsResumeRetryVisible);
                Assert.Contains("reopen this wizard", page.FailureSummaryDisplayText, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await CloseWindowAsync(window);
            }
        }

        private static async Task<InstallingPage> CreatePageAsync(List<ModComponent> components)
        {
            string temp = Path.Combine(Path.GetTempPath(), "ModSync_InstallingPage", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var mainConfig = new MainConfig
            {
                destinationPath = new DirectoryInfo(temp),
                sourcePath = new DirectoryInfo(temp),
                allComponents = components,
            };

            return await Dispatcher.UIThread.InvokeAsync(
                () => new InstallingPage(components, mainConfig, new CancellationTokenSource()),
                DispatcherPriority.Background);
        }

        private static async Task<Window> HostInWindowAsync(Control control)
        {
            Window window = await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    var host = new Window { Content = control };
                    host.Show();
                    return host;
                },
                DispatcherPriority.Background);

            await PumpEventsAsync();
            return window;
        }

        private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await condition())
                {
                    return;
                }

                await PumpEventsAsync();
                await Task.Delay(50);
            }

            throw new TimeoutException("Condition was not satisfied before timeout.");
        }

        private static async Task PumpEventsAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        private static async Task CloseWindowAsync(Window window)
        {
            if (window == null)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    if (window.IsVisible)
                    {
                        window.Close();
                    }
                },
                DispatcherPriority.Background);

            await PumpEventsAsync();
        }
    }
}
