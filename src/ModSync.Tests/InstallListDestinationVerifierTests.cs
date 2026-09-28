// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using ModSync.Core.Services.FileSystem;
using ModSync.Core.TSLPatcher;

using NUnit.Framework;

namespace ModSync.Tests
{
    /// <summary>
    /// Covers the visibility gate added for the class of bug where a patcher subprocess exits 0 and logs
    /// per-file "Copying X..." success notes for `[InstallList]` destinations (e.g. `streamwaves`), but the
    /// file never actually lands on disk. These tests operate on the real filesystem (matching
    /// <see cref="TslPatcherVfsSimulatorTests"/>'s convention) since <see cref="InstallListDestinationVerifier"/>
    /// exists specifically to check real post-install disk state, not the VFS dry-run path.
    /// </summary>
    [TestFixture]
    public sealed class InstallListDestinationVerifierTests
    {
        private string _testDirectory;
        private string _tslPatcherDirectory;
        private string _kotorDirectory;
        private RealFileSystemProvider _provider;

        [SetUp]
        public void SetUp()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), "ModSync_InstallListVerify_" + Guid.NewGuid());
            _tslPatcherDirectory = Path.Combine(_testDirectory, "tslpatchdata");
            _kotorDirectory = Path.Combine(_testDirectory, "KOTOR");
            Directory.CreateDirectory(_tslPatcherDirectory);
            Directory.CreateDirectory(_kotorDirectory);
            _provider = new RealFileSystemProvider();
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_testDirectory))
                {
                    Directory.Delete(_testDirectory, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }

        [Test]
        public async Task VerifyAsync_DeclaredStreamwavesFileMissingOnDisk_IsReportedAsMissing()
        {
            File.WriteAllText(
                Path.Combine(_tslPatcherDirectory, "changes.ini"),
                "[InstallList]\r\ninstall_folder0=streamwaves\r\n\r\n[install_folder0]\r\nFile0=AVO_NiktAngS2.wav\r\n");

            // Deliberately do NOT create streamwaves/AVO_NiktAngS2.wav — this is the exact silent-loss shape
            // reported for K1 Ported Alien VO Replacements / KOTOR Community Patch.
            Directory.CreateDirectory(Path.Combine(_kotorDirectory, "streamwaves"));

            IReadOnlyList<string> missing = await InstallListDestinationVerifier.VerifyAsync(
                    _provider,
                    new DirectoryInfo(_tslPatcherDirectory),
                    _kotorDirectory,
                    namespaceArgument: null)
                .ConfigureAwait(false);

            Assert.That(missing, Has.Count.EqualTo(1));
            Assert.That(missing[0], Does.Contain("streamwaves").And.Contain("AVO_NiktAngS2.wav"));
        }

        [Test]
        public async Task VerifyAsync_DeclaredStreamwavesFilePresentOnDisk_ReportsNothing()
        {
            File.WriteAllText(
                Path.Combine(_tslPatcherDirectory, "changes.ini"),
                "[InstallList]\r\ninstall_folder0=streamwaves\r\n\r\n[install_folder0]\r\nFile0=AVO_NiktAngS2.wav\r\n");

            string streamwaves = Path.Combine(_kotorDirectory, "streamwaves");
            Directory.CreateDirectory(streamwaves);
            File.WriteAllText(Path.Combine(streamwaves, "AVO_NiktAngS2.wav"), "fake wav");

            IReadOnlyList<string> missing = await InstallListDestinationVerifier.VerifyAsync(
                    _provider,
                    new DirectoryInfo(_tslPatcherDirectory),
                    _kotorDirectory,
                    namespaceArgument: null)
                .ConfigureAwait(false);

            Assert.That(missing, Is.Empty);
        }

        [Test]
        public async Task VerifyAsync_InstallListTargetsOverrideOrModules_IsNotChecked()
        {
            File.WriteAllText(
                Path.Combine(_tslPatcherDirectory, "changes.ini"),
                "[InstallList]\r\ninstall_folder0=override\r\ninstall_folder1=modules\\some.mod\r\n\r\n"
                + "[install_folder0]\r\nFile0=missing_override_file.utc\r\n\r\n"
                + "[install_folder1]\r\nFile0=missing_module_file.utc\r\n");

            // Neither Override nor modules destination files exist, but neither is in
            // CheckpointPaths.ImmutableVanillaDirectoryNames, so neither should be reported.
            IReadOnlyList<string> missing = await InstallListDestinationVerifier.VerifyAsync(
                    _provider,
                    new DirectoryInfo(_tslPatcherDirectory),
                    _kotorDirectory,
                    namespaceArgument: null)
                .ConfigureAwait(false);

            Assert.That(missing, Is.Empty);
        }

        [Test]
        public async Task VerifyAsync_NamespacedChangesIniSelectsCorrectOption_DetectsMissingFileInThatOption()
        {
            // Mirrors the real KOTOR Community Patch namespaces.ini shape: [Namespaces] maps NamespaceN to a
            // section name, and that section carries IniName/DataPath pointing at a changes_N.ini under a
            // per-option subdirectory. --namespace-option-index (Arguments) is 0-based; "Option 2" here is
            // index 1.
            File.WriteAllText(
                Path.Combine(_tslPatcherDirectory, "namespaces.ini"),
                "[Namespaces]\r\n"
                + "Namespace1=Option 1\r\n"
                + "Namespace2=Option 2\r\n"
                + "\r\n"
                + "[Option 1]\r\n"
                + "IniName=changes_1.ini\r\n"
                + "DataPath=1 - Main\r\n"
                + "\r\n"
                + "[Option 2]\r\n"
                + "IniName=changes_2.ini\r\n"
                + "DataPath=2 - K1CP Patch\r\n");

            string option1Dir = Path.Combine(_tslPatcherDirectory, "1 - Main");
            string option2Dir = Path.Combine(_tslPatcherDirectory, "2 - K1CP Patch");
            Directory.CreateDirectory(option1Dir);
            Directory.CreateDirectory(option2Dir);

            // Option 1's InstallList declares a streamwaves file that IS present — must not be reported
            // when Option 2 is the one actually selected/executed.
            File.WriteAllText(
                Path.Combine(option1Dir, "changes_1.ini"),
                "[InstallList]\r\ninstall_folder0=streamwaves\r\n\r\n[install_folder0]\r\nFile0=option1_only.wav\r\n");

            // Option 2's InstallList declares a streamsounds file that is NOT present on disk.
            File.WriteAllText(
                Path.Combine(option2Dir, "changes_2.ini"),
                "[InstallList]\r\ninstall_folder0=streamsounds\r\n\r\n[install_folder0]\r\nFile0=k1cp_missing.wav\r\n");

            IReadOnlyList<string> missing = await InstallListDestinationVerifier.VerifyAsync(
                    _provider,
                    new DirectoryInfo(_tslPatcherDirectory),
                    _kotorDirectory,
                    namespaceArgument: "1") // 0-based: selects "Option 2"
                .ConfigureAwait(false);

            Assert.That(missing, Has.Count.EqualTo(1));
            Assert.That(missing[0], Does.Contain("streamsounds").And.Contain("k1cp_missing.wav"));
        }
    }
}
