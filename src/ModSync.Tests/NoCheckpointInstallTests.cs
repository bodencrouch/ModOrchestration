// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ModSync.Core;
using ModSync.Core.Installation;
using ModSync.Core.Services.Checkpoints;
using NUnit.Framework;

namespace ModSync.Tests
{
    [TestFixture]
    public sealed class NoCheckpointInstallTests
    {
        private string _tempRoot;
        private DirectoryInfo _destination;
        private DirectoryInfo _modDir;
        private MainConfig _previousConfig;
        private List<ModComponent> _previousAll;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "ModSync_NoCheckpoint", Guid.NewGuid().ToString("N"));
            _destination = Directory.CreateDirectory(Path.Combine(_tempRoot, "game"));
            _modDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "mods"));
            File.WriteAllText(Path.Combine(_destination.FullName, "swkotor.exe"), string.Empty);

            _previousConfig = MainConfig.Instance;
            _previousAll = MainConfig.AllComponents;
            MainConfig.Instance = new MainConfig
            {
                destinationPath = _destination,
                sourcePath = _modDir,
            };
        }

        [TearDown]
        public void TearDown()
        {
            MainConfig.Instance = _previousConfig;
            MainConfig.AllComponents = _previousAll;
            InstallCoordinator.ClearSessionForTests(_destination);
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, recursive: true);
                }
            }
            catch
            {
                // best effort
            }
        }

        [Test]
        public async Task InitializeAsync_NoGitCheckpoints_DoesNotCreateGitDir()
        {
            var components = new List<ModComponent>
            {
                new ModComponent { Guid = Guid.NewGuid(), Name = "A", IsSelected = true },
            };
            MainConfig.AllComponents = components;

            using (var coordinator = new InstallCoordinator())
            {
                ResumeResult resume = await coordinator.InitializeAsync(
                    components,
                    _destination,
                    CancellationToken.None,
                    enableGitCheckpoints: false);

                Assert.That(resume.SessionId, Is.Not.EqualTo(Guid.Empty));
                Assert.That(coordinator.CheckpointService, Is.Null);
            }

            string gitDir = CheckpointPaths.GetGitDirectory(_destination.FullName);
            Assert.That(Directory.Exists(gitDir), Is.False,
                "Git checkpoint directory should not be created when enableGitCheckpoints is false.");

            string sessionFile = Path.Combine(CheckpointPaths.GetRoot(_destination.FullName), "install_session.json");
            Assert.That(File.Exists(sessionFile), Is.True,
                "Session resume file should still be written when Git checkpoints are disabled.");
        }

        [Test]
        public async Task InitializeAsync_WithGitCheckpoints_CreatesGitService()
        {
            var components = new List<ModComponent>
            {
                new ModComponent { Guid = Guid.NewGuid(), Name = "A", IsSelected = true },
            };
            MainConfig.AllComponents = components;

            using (var coordinator = new InstallCoordinator())
            {
                await coordinator.InitializeAsync(
                    components,
                    _destination,
                    CancellationToken.None,
                    enableGitCheckpoints: true);

                Assert.That(coordinator.CheckpointService, Is.Not.Null);
            }
        }
    }
}
