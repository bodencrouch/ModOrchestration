// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ModSync.Core;
using ModSync.Core.Installation;
using ModSync.Core.Services.Checkpoints;
using NUnit.Framework;

namespace ModSync.Tests
{
    [TestFixture]
    public sealed class WizardResumeReentryTests
    {
        private string _tempRoot;
        private DirectoryInfo _destination;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "ModSync_WizardResume", Guid.NewGuid().ToString("N"));
            _destination = Directory.CreateDirectory(Path.Combine(_tempRoot, "game"));
        }

        [TearDown]
        public void TearDown()
        {
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
        public async Task Probe_NoSession_IsNotResumable()
        {
            var components = new List<ModComponent>
            {
                new ModComponent { Guid = Guid.NewGuid(), Name = "A", IsSelected = true },
            };

            ResumableSessionInfo info = await CheckpointManager.ProbeResumableSessionAsync(_destination, components);
            Assert.That(info.IsResumable, Is.False);
        }

        [Test]
        public async Task Probe_PartialSession_IsResumable()
        {
            var done = new ModComponent { Guid = Guid.NewGuid(), Name = "Done", IsSelected = true };
            var pending = new ModComponent { Guid = Guid.NewGuid(), Name = "Pending", IsSelected = true };
            var components = new List<ModComponent> { done, pending };

            using (var coordinator = new InstallCoordinator())
            {
                await coordinator.InitializeAsync(components, _destination, default, enableGitCheckpoints: false);
                done.InstallState = ModComponent.ComponentInstallState.Completed;
                pending.InstallState = ModComponent.ComponentInstallState.Pending;
                coordinator.CheckpointManager.UpdateComponentState(done);
                coordinator.CheckpointManager.UpdateComponentState(pending);
                await coordinator.CheckpointManager.SaveAsync();
            }

            ResumableSessionInfo info = await CheckpointManager.ProbeResumableSessionAsync(_destination, components);
            Assert.That(info.IsResumable, Is.True);
            Assert.That(info.CompletedSelectedCount, Is.EqualTo(1));
            Assert.That(info.RemainingSelectedCount, Is.EqualTo(1));
        }

        [Test]
        public async Task Probe_DifferentDestination_IsNotResumable()
        {
            var component = new ModComponent { Guid = Guid.NewGuid(), Name = "A", IsSelected = true };
            var components = new List<ModComponent> { component };

            using (var coordinator = new InstallCoordinator())
            {
                await coordinator.InitializeAsync(components, _destination, default, enableGitCheckpoints: false);
                component.InstallState = ModComponent.ComponentInstallState.Pending;
                coordinator.CheckpointManager.UpdateComponentState(component);
                await coordinator.CheckpointManager.SaveAsync();
            }

            var otherDestination = Directory.CreateDirectory(Path.Combine(_tempRoot, "other-game"));

            ResumableSessionInfo info = await CheckpointManager.ProbeResumableSessionAsync(otherDestination, components);
            Assert.That(info.IsResumable, Is.False);
        }

        [Test]
        public async Task Initialize_SessionCopiedFromDifferentDestination_DoesNotAdoptStaleCompletedState()
        {
            var component = new ModComponent { Guid = Guid.NewGuid(), Name = "A", IsSelected = true };
            var components = new List<ModComponent> { component };

            var originalDestination = Directory.CreateDirectory(Path.Combine(_tempRoot, "original-game"));
            using (var coordinator = new InstallCoordinator())
            {
                await coordinator.InitializeAsync(components, originalDestination, default, enableGitCheckpoints: false);
                component.InstallState = ModComponent.ComponentInstallState.Completed;
                coordinator.CheckpointManager.UpdateComponentState(component);
                await coordinator.CheckpointManager.SaveAsync();
            }

            // Simulate the whole game folder (including its session file) being copied to a
            // new destination -- the session's recorded DestinationPath now points elsewhere.
            Directory.CreateDirectory(CheckpointPaths.GetRoot(_destination.FullName));
            File.Copy(
                Path.Combine(CheckpointPaths.GetRoot(originalDestination.FullName), "install_session.json"),
                Path.Combine(CheckpointPaths.GetRoot(_destination.FullName), "install_session.json"),
                overwrite: true);

            component.InstallState = ModComponent.ComponentInstallState.Pending;

            using (var coordinator = new InstallCoordinator())
            {
                await coordinator.InitializeAsync(components, _destination, default, enableGitCheckpoints: false);

                Assert.That(component.InstallState, Is.Not.EqualTo(ModComponent.ComponentInstallState.Completed),
                    "A session copied in from a different destination must not donate its Completed markers to this install.");
            }
        }

        [Test]
        public async Task StartOver_DeletesSession_ProbeNoLongerResumable()
        {
            var done = new ModComponent { Guid = Guid.NewGuid(), Name = "Done", IsSelected = true };
            var pending = new ModComponent { Guid = Guid.NewGuid(), Name = "Pending", IsSelected = true };
            var components = new List<ModComponent> { done, pending };

            using (var coordinator = new InstallCoordinator())
            {
                await coordinator.InitializeAsync(components, _destination, default, enableGitCheckpoints: false);
                done.InstallState = ModComponent.ComponentInstallState.Completed;
                coordinator.CheckpointManager.UpdateComponentState(done);
                await coordinator.CheckpointManager.SaveAsync();
            }

            Assert.That(
                (await CheckpointManager.ProbeResumableSessionAsync(_destination, components)).IsResumable,
                Is.True);

            await CheckpointManager.DeleteSessionFileAsync(_destination);
            done.InstallState = ModComponent.ComponentInstallState.Pending;
            pending.InstallState = ModComponent.ComponentInstallState.Pending;

            ResumableSessionInfo after = await CheckpointManager.ProbeResumableSessionAsync(_destination, components);
            Assert.That(after.IsResumable, Is.False);
        }
    }
}
