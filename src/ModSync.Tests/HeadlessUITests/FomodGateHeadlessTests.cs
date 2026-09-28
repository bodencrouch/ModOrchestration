// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using ModSync.Core;
using ModSync.Core.Services.Fomod;
using ModSync.Dialogs.WizardPages;

using Xunit;

namespace ModSync.Tests.HeadlessUITests
{
    /// <summary>
    /// Headless smoke for InstallStartPage FOMOD configuration gate
    /// (blocks when unconfigured; passes when configured). No desktop required.
    /// </summary>
    [Collection(HeadlessTestApp.CollectionName)]
    public sealed class FomodGateHeadlessTests
    {
        [AvaloniaFact(DisplayName = "InstallStartPage blocks when selected FOMOD archive is unconfigured")]
        public async Task InstallStartPage_ValidateAsync_UnconfiguredFomod_ReturnsFalse()
        {
            string root = CreateTempModDir();
            string gameDir = Path.Combine(root, "game");
            string modDir = Path.Combine(root, "mods");
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(modDir);
            File.WriteAllText(Path.Combine(gameDir, "swkotor.exe"), string.Empty);
            EnsureHolopatcherInTestResources();

            MainConfig previous = MainConfig.Instance;

            try
            {
                string archiveName = "unconfigured-fomod.zip";
                CreateZipWithFomod(Path.Combine(modDir, archiveName));
                ModComponent component = BuildComponentWithArchive(archiveName);

                MainConfig.Instance = new MainConfig
                {
                    sourcePath = new DirectoryInfo(modDir),
                    destinationPath = new DirectoryInfo(gameDir),
                };

                var page = new InstallStartPage(new List<ModComponent> { component });
                (bool isValid, string errorMessage) = await page.ValidateAsync(CancellationToken.None);

                Assert.False(isValid);
                Assert.Contains("FOMOD archive", errorMessage ?? string.Empty, StringComparison.Ordinal);
                Assert.Contains(archiveName, errorMessage ?? string.Empty, StringComparison.Ordinal);
            }
            finally
            {
                MainConfig.Instance = previous;
                TryDeleteDirectory(root);
            }
        }

        [AvaloniaFact(DisplayName = "InstallStartPage allows continue when selected FOMOD archive is configured")]
        public async Task InstallStartPage_ValidateAsync_ConfiguredFomod_ReturnsTrue()
        {
            string root = CreateTempModDir();
            string gameDir = Path.Combine(root, "game");
            string modDir = Path.Combine(root, "mods");
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(modDir);
            File.WriteAllText(Path.Combine(gameDir, "swkotor.exe"), string.Empty);
            EnsureHolopatcherInTestResources();

            MainConfig previous = MainConfig.Instance;

            try
            {
                string archiveName = "configured-fomod.zip";
                CreateZipWithFomod(Path.Combine(modDir, archiveName));
                ModComponent component = BuildComponentWithArchive(archiveName);
                FomodDownloadPromptState.MarkConfigured(component, archiveName);

                MainConfig.Instance = new MainConfig
                {
                    sourcePath = new DirectoryInfo(modDir),
                    destinationPath = new DirectoryInfo(gameDir),
                };

                var page = new InstallStartPage(new List<ModComponent> { component });
                (bool isValid, string errorMessage) = await page.ValidateAsync(CancellationToken.None);

                Assert.True(isValid, errorMessage);
                Assert.True(string.IsNullOrEmpty(errorMessage));
            }
            finally
            {
                MainConfig.Instance = previous;
                TryDeleteDirectory(root);
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

        private static string CreateTempModDir()
        {
            string modDir = Path.Combine(
                Path.GetTempPath(),
                "ModSync_FomodGateHeadless",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(modDir);
            return modDir;
        }

        private static ModComponent BuildComponentWithArchive(string archiveFileName)
        {
            var component = new ModComponent
            {
                Guid = Guid.NewGuid(),
                Name = "FOMOD Gate Mod",
                IsSelected = true,
            };
            component.ResourceRegistry = new Dictionary<string, ResourceMetadata>
            {
                ["https://example.test/mod.zip"] = new ResourceMetadata
                {
                    Files = new Dictionary<string, bool?> { [archiveFileName] = true },
                    HandlerMetadata = new Dictionary<string, object>(),
                },
            };
            return component;
        }

        private static void CreateZipWithFomod(string archivePath)
        {
            string staging = Path.Combine(Path.GetDirectoryName(archivePath), "staging");
            Directory.CreateDirectory(Path.Combine(staging, "fomod"));
            File.WriteAllText(
                Path.Combine(staging, "fomod", "ModuleConfig.xml"),
                "<config><installSteps order=\"Explicit\"></installSteps></config>");

            ZipFile.CreateFromDirectory(staging, archivePath);
            Directory.Delete(staging, recursive: true);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }
}
