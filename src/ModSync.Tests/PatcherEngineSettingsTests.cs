// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System.IO;

using ModSync.Core;
using ModSync.Core.Services;
using ModSync.Core.Services.FileSystem;

using Moq;

using NUnit.Framework;

namespace ModSync.Tests
{
    [TestFixture]
    public sealed class PatcherEngineSettingsTests
    {
        private string _savedEngine;
        private string _savedKPath;
        private string _savedOdyPath;

        [SetUp]
        public void Save()
        {
            _savedEngine = MainConfig.PatcherEngine;
            _savedKPath = MainConfig.KPatcherExecutablePath;
            _savedOdyPath = MainConfig.OdyPatcherExecutablePath;
        }

        [TearDown]
        public void Restore()
        {
            MainConfig.Instance.patcherEngine = _savedEngine;
            MainConfig.Instance.kpatcherExecutablePath = _savedKPath;
            MainConfig.Instance.odyPatcherExecutablePath = _savedOdyPath;
        }

        [Test]
        public void FindOdyPatcherExecutableAsync_ResolvesReleaseBinary_WhenWorkspaceDirectoryGiven()
        {
            string root = Path.Combine(Path.GetTempPath(), "ModSync_ody_ws_" + Path.GetRandomFileName());
            string releaseDir = Path.Combine(root, "target", "release");
            string debugDir = Path.Combine(root, "target", "debug");
            Directory.CreateDirectory(releaseDir);
            Directory.CreateDirectory(debugDir);
            string releaseExe = Path.Combine(releaseDir, "odypatcher");
            string debugExe = Path.Combine(debugDir, "odypatcher");
            File.WriteAllText(releaseExe, "release");
            File.WriteAllText(debugExe, "debug");
            try
            {
                MainConfig.Instance.patcherEngine = PatcherEngines.OdyPatcher;
                MainConfig.Instance.odyPatcherExecutablePath = root;

                (string path, bool found) = InstallationService.FindOdyPatcherExecutableAsync().GetAwaiter().GetResult();

                Assert.That(found, Is.True);
                Assert.That(path, Is.EqualTo(releaseExe));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Test]
        public void FindOdyPatcherExecutableAsync_UsesConfiguredPath_WhenFileExists()
        {
            string tempExe = Path.Combine(Path.GetTempPath(), "ModSync_odypatcher_test_" + Path.GetRandomFileName());
            File.WriteAllText(tempExe, string.Empty);
            try
            {
                MainConfig.Instance.patcherEngine = PatcherEngines.OdyPatcher;
                MainConfig.Instance.odyPatcherExecutablePath = tempExe;

                (string path, bool found) = InstallationService.FindOdyPatcherExecutableAsync().GetAwaiter().GetResult();

                Assert.That(found, Is.True);
                Assert.That(path, Is.EqualTo(tempExe));
            }
            finally
            {
                try
                {
                    File.Delete(tempExe);
                }
                catch
                {
                }
            }
        }

        [Test]
        public void RunTslPatcherCliAsync_OdyPatcher_ForwardsNativeArgumentsWithoutKpatcherConsoleFlag()
        {
            string tempExe = Path.Combine(Path.GetTempPath(), "ModSync_odypatcher_test_" + Path.GetRandomFileName());
            File.WriteAllText(tempExe, string.Empty);
            try
            {
                MainConfig.Instance.patcherEngine = PatcherEngines.OdyPatcher;
                MainConfig.Instance.odyPatcherExecutablePath = tempExe;
                var fileSystem = new Mock<IFileSystemProvider>(MockBehavior.Strict);
                // Space-separated: OdyPatcher rejects --flag=value (and quote-collapsed equivalents).
                const string args = "--install --game-dir game --tslpatchdata mod --cli -y";
                // Only dry-run providers route the patcher process through the provider; a real
                // install launches the executable directly.
                _ = fileSystem.SetupGet(provider => provider.IsDryRun).Returns(true);
                _ = fileSystem
                    .Setup(provider => provider.ExecuteProcessAsync(tempExe, args))
                    .ReturnsAsync((0, "ok", string.Empty));

                (int exitCode, string stdout, string stderr) = InstallationService
                    .RunTslPatcherCliAsync(args, fileSystem.Object)
                    .GetAwaiter()
                    .GetResult();

                Assert.Multiple(() =>
                {
                    Assert.That(exitCode, Is.Zero);
                    Assert.That(stdout, Is.EqualTo("ok"));
                    Assert.That(stderr, Is.Empty);
                });
                fileSystem.VerifyAll();
            }
            finally
            {
                try
                {
                    File.Delete(tempExe);
                }
                catch
                {
                }
            }
        }

        [Test]
        public void FindKPatcherExecutableAsync_UsesConfiguredPath_WhenFileExists()
        {
            string tempExe = Path.Combine(Path.GetTempPath(), "ModSync_kpatcher_test_" + Path.GetRandomFileName());
            File.WriteAllText(tempExe, string.Empty);
            try
            {
                MainConfig.Instance.patcherEngine = PatcherEngines.KPatcher;
                MainConfig.Instance.kpatcherExecutablePath = tempExe;

                (string path, bool found) = InstallationService.FindKPatcherExecutableAsync().GetAwaiter().GetResult();

                Assert.That(found, Is.True);
                Assert.That(path, Is.EqualTo(tempExe));
            }
            finally
            {
                try
                {
                    File.Delete(tempExe);
                }
                catch
                {
                }
            }
        }
    }
}
