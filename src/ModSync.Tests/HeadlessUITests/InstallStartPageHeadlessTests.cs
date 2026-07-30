// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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
    public sealed class InstallStartPageHeadlessTests
    {
        [AvaloniaFact(DisplayName = "InstallStart readiness blocks Next when directories are unset")]
        public async Task InstallStartPage_Readiness_BlocksWhenDirectoriesUnset()
        {
            MainConfig previous = MainConfig.Instance;
            try
            {
                MainConfig.Instance = new MainConfig
                {
                    sourcePath = null,
                    destinationPath = null,
                };

                var component = new ModComponent
                {
                    Guid = Guid.NewGuid(),
                    Name = "Readiness Block Mod",
                    IsSelected = true,
                };

                InstallStartPage page = await Dispatcher.UIThread.InvokeAsync(
                    () => new InstallStartPage(new List<ModComponent> { component }),
                    DispatcherPriority.Background);

                Window window = await HostInWindowAsync(page);
                try
                {
                    await page.OnNavigatedToAsync(CancellationToken.None);
                    await PumpEventsAsync();

                    (bool isValid, string errorMessage) = await page.ValidateAsync(CancellationToken.None);

                    Assert.False(isValid);
                    Assert.True(page.ReadinessHasCriticalErrors);
                    Assert.False(string.IsNullOrWhiteSpace(errorMessage));
                    Assert.Contains("director", errorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    await CloseWindowAsync(window);
                }
            }
            finally
            {
                MainConfig.Instance = previous;
            }
        }

        [AvaloniaFact(DisplayName = "InstallStart readiness summary visible and Next allowed when environment is clean")]
        public async Task InstallStartPage_Readiness_AllowsWhenEnvironmentClean()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "ModSync_InstallStartReadiness_" + Guid.NewGuid().ToString("N"));
            string gameDir = Path.Combine(tempRoot, "game");
            string modDir = Path.Combine(tempRoot, "mods");
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(modDir);
            File.WriteAllText(Path.Combine(gameDir, "swkotor.exe"), string.Empty);
            EnsureHolopatcherInTestResources();

            MainConfig previous = MainConfig.Instance;
            try
            {
                MainConfig.Instance = new MainConfig
                {
                    destinationPath = new DirectoryInfo(gameDir),
                    sourcePath = new DirectoryInfo(modDir),
                };

                var component = new ModComponent
                {
                    Guid = Guid.NewGuid(),
                    Name = "Clean Env Mod",
                    IsSelected = true,
                };
                MainConfig.AllComponents = new List<ModComponent> { component };

                InstallStartPage page = await Dispatcher.UIThread.InvokeAsync(
                    () => new InstallStartPage(new List<ModComponent> { component }),
                    DispatcherPriority.Background);

                Window window = await HostInWindowAsync(page);
                try
                {
                    await page.OnNavigatedToAsync(CancellationToken.None);
                    await PumpEventsAsync();

                    Assert.False(string.IsNullOrWhiteSpace(page.ReadinessStatusDisplayText));
                    Assert.Contains("Ready", page.ReadinessStatusDisplayText ?? string.Empty, StringComparison.OrdinalIgnoreCase);

                    (bool isValid, string errorMessage) = await page.ValidateAsync(CancellationToken.None);
                    Assert.True(isValid, errorMessage);
                    Assert.False(page.ReadinessHasCriticalErrors);
                }
                finally
                {
                    await CloseWindowAsync(window);
                }
            }
            finally
            {
                MainConfig.Instance = previous;
                TryDeleteDirectory(tempRoot);
            }
        }

        private static void EnsureHolopatcherInTestResources()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string resourcesDir = Path.Combine(baseDir, "Resources");
            Directory.CreateDirectory(resourcesDir);
            string targetPath = Path.Combine(resourcesDir, "holopatcher");
            if (File.Exists(targetPath))
            {
                return;
            }

            string vendorHolopatcher = Path.GetFullPath(Path.Combine(
                baseDir,
                "..", "..", "..", "..", "..",
                "vendor", "bin", "HoloPatcher_linux"));
            if (!File.Exists(vendorHolopatcher))
            {
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.Copy(vendorHolopatcher, targetPath, overwrite: true);
            }
            else
            {
                File.CreateSymbolicLink(targetPath, vendorHolopatcher);
            }
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

        private static async Task CloseWindowAsync(Window window)
        {
            await Dispatcher.UIThread.InvokeAsync(() => window.Close(), DispatcherPriority.Background);
            await PumpEventsAsync();
        }

        private static async Task PumpEventsAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(10);
        }

        private static void TryDeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
