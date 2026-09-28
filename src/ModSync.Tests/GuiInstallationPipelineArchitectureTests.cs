// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.IO;
using System.Linq;

using NUnit.Framework;

namespace ModSync.Tests
{
    [TestFixture]
    public sealed class GuiInstallationPipelineArchitectureTests
    {
        [Test]
        public void GuiInstallAdapters_UseSharedPipelineAndNeverCallInstallationServiceDirectly()
        {
            string repoRoot = Environment.GetEnvironmentVariable("MODSYNC_REPO_ROOT");
            Assert.That(repoRoot, Is.Not.Null.And.Not.Empty,
                "Set MODSYNC_REPO_ROOT so the source architecture guard can inspect the GUI adapters.");

            string guiRoot = Path.Combine(repoRoot, "src", "ModSync.GUI");
            string[] sources = Directory.GetFiles(guiRoot, "*.cs", SearchOption.AllDirectories);
            string combined = string.Join("\n", sources.Select(File.ReadAllText));

            Assert.Multiple(() =>
            {
                Assert.That(combined, Does.Contain("InstallationPipelineService.RunAsync"),
                    "The GUI must retain an adapter into the shared Core installation pipeline.");
                Assert.That(combined, Does.Not.Contain("InstallationService.InstallAllSelectedComponentsAsync"),
                    "GUI code must not bypass the shared pipeline with its own install loop.");
                Assert.That(combined, Does.Not.Contain("InstallationService.InstallSingle"),
                    "GUI code must not bypass the shared pipeline for single-component tools.");
            });
        }
    }
}
