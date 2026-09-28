// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using ModSync.Core;
using ModSync.Core.Services.Validation;

using NUnit.Framework;

namespace ModSync.Tests
{
    [TestFixture]
    public sealed class InstallStartReadinessOptionsTests
    {
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "ModSync_InstallStartReadiness_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            string gameDir = Path.Combine(_tempDir, "game");
            string modDir = Path.Combine(_tempDir, "mods");
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(modDir);
            File.WriteAllText(Path.Combine(gameDir, "swkotor.exe"), string.Empty);

            MainConfig.Instance = new MainConfig
            {
                destinationPath = new DirectoryInfo(gameDir),
                sourcePath = new DirectoryInfo(modDir),
            };

            EnsureHolopatcherInTestResources();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
            {
                try
                {
                    Directory.Delete(_tempDir, recursive: true);
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }

        [Test]
        public void InstallStartReadiness_EnablesEnvironmentAndDisablesDryRun()
        {
            ValidationPipelineOptions options = ValidationPipelineOptions.InstallStartReadiness;

            Assert.That(options.FullValidation, Is.True);
            Assert.That(options.DryRun, Is.False);
            Assert.That(options.DryRunOnly, Is.False);
            Assert.That(options.UseFileSelection, Is.True);
            Assert.That(options.SkipEnvironmentValidation, Is.False);
            Assert.That(options.SkipComponentArchiveValidation, Is.True);
            Assert.That(options.SkipFomodConfigurationGate, Is.True);
            Assert.That(options.SkipConflictAndOrderValidation, Is.True);
        }

        [Test]
        public async Task InstallStartReadiness_RunAsync_ProducesEnvironmentOnly_WithoutDryRun()
        {
            var component = new ModComponent
            {
                Guid = Guid.NewGuid(),
                Name = "Readiness Mod",
                IsSelected = true,
            };
            MainConfig.AllComponents = new List<ModComponent> { component };

            ValidationPipelineOptions options = ValidationPipelineOptions.InstallStartReadiness;
            options.MainConfig = MainConfig.Instance;

            ValidationPipelineResult result = await InstallationValidationPipeline.RunAsync(
                MainConfig.AllComponents.ToList(),
                options).ConfigureAwait(false);

            Assert.That(result.Stages.Count, Is.EqualTo(1), "Readiness should run Environment only.");
            Assert.That(result.Stages[0].Stage, Is.EqualTo(ValidationPipelineStage.Environment));
            Assert.That(result.DryRunResult, Is.Null);
            Assert.That(
                result.Stages.Any(s => s.Stage == ValidationPipelineStage.DryRun),
                Is.False);
            Assert.That(
                result.Stages.Any(s => s.Stage == ValidationPipelineStage.Conflicts),
                Is.False);
            Assert.That(
                result.Stages.Any(s => s.Stage == ValidationPipelineStage.ComponentValidation),
                Is.False);
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
    }
}
