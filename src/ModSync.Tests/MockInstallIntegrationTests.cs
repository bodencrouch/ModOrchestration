// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using ModSync.Core;

using ModSync.Tests.Fixtures;

using NUnit.Framework;

namespace ModSync.Tests
{
    /// <summary>
    /// Runs a real ModSync install against the synthetic KOTOR fixture and asserts the game
    /// directory actually changed. A zero exit code proves nothing on its own, so every assertion
    /// here is a before/after comparison of real bytes on disk: new override files, a longer
    /// dialog.tlk, an extra 2DA row.
    /// <para>
    /// Suffixed <c>LongRunning</c> because the bundled HoloPatcher unpacks itself on first run and
    /// each case takes roughly a minute. CI runs them by name.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class MockInstallIntegrationTests
    {
        private string _tempRoot;
        private MainConfig _previousMainConfig;
        private List<ModComponent> _previousComponents;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "ModSync_MockInstall_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            _previousMainConfig = MainConfig.Instance;
            _previousComponents = MainConfig.AllComponents;
        }

        [TearDown]
        public void TearDown()
        {
            MainConfig.Instance = _previousMainConfig;
            MainConfig.AllComponents = _previousComponents;

            if (!Directory.Exists(_tempRoot))
            {
                return;
            }

            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup.
            }
        }

        [TestCase(MockKotorGame.Kotor1)]
        [TestCase(MockKotorGame.Kotor2)]
        public void Install_AgainstMockGame_ChangesTheGameDirectory_LongRunning(MockKotorGame game)
        {
            if (!TryLinkHolopatcher())
            {
                Assert.Ignore("vendor/bin/HoloPatcher_linux is not available in this checkout.");
            }

            MockKotorInstallResult mock = MockKotorInstall.Create(Path.Combine(_tempRoot, "game"), game);
            MockModArchives.Create(Path.Combine(_tempRoot, "mods"));
            string buildFile = MockBuildFile.Write(Path.Combine(_tempRoot, "mock_build.toml"), game);

            string tlkPath = Path.Combine(mock.ContentRoot, "dialog.tlk");
            string tablePath = Path.Combine(mock.OverrideDirectory, MockModArchives.PatchedTableName);

            int overrideFilesBefore = CountFiles(mock.OverrideDirectory);
            long tlkBytesBefore = new FileInfo(tlkPath).Length;
            string tlkHashBefore = Sha256(tlkPath);
            int tableRowsBefore = ReadTwoDaRowCount(tablePath);

            int exitCode = ModSync.Core.Program.Main(new[]
            {
                "install",
                "-i", buildFile,
                "-g", mock.InstallRoot,
                "-s", Path.Combine(_tempRoot, "mods"),
                "--skip-validation",
                "--no-checkpoint",
                "-y",
            });

            int overrideFilesAfter = CountFiles(mock.OverrideDirectory);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0), "The install must report success.");

                // A successful exit with an untouched game directory is the failure mode this
                // fixture exists to catch, so the counts are asserted independently of the code.
                Assert.That(
                    overrideFilesAfter,
                    Is.GreaterThan(overrideFilesBefore),
                    "The install must add files to the override directory.");
                Assert.That(
                    File.Exists(Path.Combine(mock.OverrideDirectory, MockModArchives.LooseFileMarker)),
                    Is.True,
                    "The loose-files mod must land in the override directory.");
                Assert.That(
                    File.Exists(Path.Combine(mock.OverrideDirectory, MockModArchives.TslPatcherInstalledMarker)),
                    Is.True,
                    "The patcher's [InstallList] must copy its file into the override directory.");

                Assert.That(
                    new FileInfo(tlkPath).Length,
                    Is.GreaterThan(tlkBytesBefore),
                    "[TLKList] must append to dialog.tlk.");
                Assert.That(
                    Sha256(tlkPath),
                    Is.Not.EqualTo(tlkHashBefore),
                    "dialog.tlk must differ byte-for-byte after patching.");
                Assert.That(
                    ReadTlkStrings(tlkPath),
                    Is.SupersetOf(MockModArchives.AppendedTlkStrings),
                    "The appended strings must be readable back out of dialog.tlk.");

                Assert.That(
                    ReadTwoDaRowCount(tablePath),
                    Is.EqualTo(tableRowsBefore + 1),
                    "[2DAList] must append exactly one row.");
            });
        }

        /// <summary>
        /// The override directory casing differs between the two games, and on Linux that is a real
        /// distinction. This checks the install wrote into the directory the game actually has and
        /// did not create a second one beside it.
        /// </summary>
        [TestCase(MockKotorGame.Kotor1)]
        [TestCase(MockKotorGame.Kotor2)]
        public void Install_AgainstMockGame_DoesNotCreateASecondOverrideDirectory_LongRunning(MockKotorGame game)
        {
            if (!TryLinkHolopatcher())
            {
                Assert.Ignore("vendor/bin/HoloPatcher_linux is not available in this checkout.");
            }

            MockKotorInstallResult mock = MockKotorInstall.Create(Path.Combine(_tempRoot, "game"), game);
            MockModArchives.Create(Path.Combine(_tempRoot, "mods"));
            string buildFile = MockBuildFile.Write(Path.Combine(_tempRoot, "mock_build.toml"), game);

            ModSync.Core.Program.Main(new[]
            {
                "install",
                "-i", buildFile,
                "-g", mock.InstallRoot,
                "-s", Path.Combine(_tempRoot, "mods"),
                "--skip-validation",
                "--no-checkpoint",
                "-y",
            });

            string[] overrideDirectories = Directory
                .GetDirectories(mock.ContentRoot)
                .Where(d => string.Equals(Path.GetFileName(d), "override", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            Assert.That(
                overrideDirectories.Length,
                Is.EqualTo(1),
                "Exactly one override directory must exist afterwards: " + string.Join(", ", overrideDirectories));
            Assert.That(
                Path.GetFileName(overrideDirectories[0]),
                Is.EqualTo(Path.GetFileName(mock.OverrideDirectory)),
                "The install must reuse the game's own casing.");
        }

        private static int CountFiles(string directory)
        {
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length
                : 0;
        }

        private static string Sha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                return Convert.ToHexString(sha.ComputeHash(stream));
            }
        }

        private static IReadOnlyList<string> ReadTlkStrings(string path)
        {
            byte[] tlk = File.ReadAllBytes(path);
            uint count = BitConverter.ToUInt32(tlk, 12);
            uint textOffset = BitConverter.ToUInt32(tlk, 16);

            var strings = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                int entry = (int)(20 + (i * 40));
                uint offset = BitConverter.ToUInt32(tlk, entry + 28);
                uint length = BitConverter.ToUInt32(tlk, entry + 32);
                strings.Add(Encoding.ASCII.GetString(tlk, (int)(textOffset + offset), (int)length));
            }

            return strings;
        }

        /// <summary>Reads the row count out of a 2DA V2.b header without a full parse.</summary>
        private static int ReadTwoDaRowCount(string path)
        {
            byte[] data = File.ReadAllBytes(path);

            // Header is "2DA V2.b\n", then tab-separated column names, then a NUL, then the count.
            int cursor = 9;
            while (cursor < data.Length && data[cursor] != 0)
            {
                cursor++;
            }

            cursor++;
            return (int)BitConverter.ToUInt32(data, cursor);
        }

        /// <summary>
        /// Links the vendored Linux HoloPatcher into the test output's Resources directory, which is
        /// where <c>InstallationService.FindHolopatcherAsync</c> looks. Mirrors the helper the
        /// existing full-build tests use.
        /// </summary>
        private static bool TryLinkHolopatcher()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string resourcesDir = Path.Combine(baseDir, "Resources");
            Directory.CreateDirectory(resourcesDir);

            string targetPath = Path.Combine(resourcesDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "holopatcher.exe" : "holopatcher");
            if (File.Exists(targetPath))
            {
                return true;
            }

            string vendorName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "HoloPatcher.exe" : "HoloPatcher_linux";
            string vendorPath = Path.GetFullPath(Path.Combine(
                baseDir, "..", "..", "..", "..", "..", "vendor", "bin", vendorName));

            if (!File.Exists(vendorPath))
            {
                return false;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.Copy(vendorPath, targetPath, overwrite: true);
            }
            else
            {
                File.CreateSymbolicLink(targetPath, vendorPath);
            }

            return true;
        }
    }
}
