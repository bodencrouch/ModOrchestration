// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using ModSync.Core.FileSystemUtils;
using ModSync.Core.Utility;

using ModSync.Tests.Fixtures;

using NUnit.Framework;

namespace ModSync.Tests
{
    /// <summary>
    /// Pins the synthetic KOTOR fixture against ModSync's own detection code. If a signature list
    /// or a path rule changes and the mock stops looking like a real install, these fail here
    /// rather than in a CI install run whose failure would be much harder to read.
    /// </summary>
    [TestFixture]
    public sealed class MockKotorInstallTests
    {
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "ModSync_MockFixture_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        [TearDown]
        public void TearDown()
        {
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

        [Test]
        public void Kotor1Mock_MatchesEveryKotor1Signature()
        {
            MockKotorInstallResult mock = MockKotorInstall.Create(Path.Combine(_tempRoot, "K1"), MockKotorGame.Kotor1);

            PathUtilities.DetailedGameDetectionSummary summary =
                PathUtilities.AnalyzeGameDirectoryDetailed(mock.InstallRoot);
            PathUtilities.DetailedGameDetectionResult k1 =
                summary.AllResults.First(r => r.Variant == PathUtilities.GameInstallVariant.PcKotor1);

            Assert.Multiple(() =>
            {
                Assert.That(
                    PathUtilities.DetectGame(mock.InstallRoot),
                    Is.EqualTo(PathUtilities.DetectedGame.Kotor1),
                    "The K1 mock must be recognised as KOTOR 1 by the same detector the GUI uses.");
                Assert.That(summary.Identity, Is.EqualTo(PathUtilities.GameInstallVariant.PcKotor1));
                Assert.That(
                    k1.Matches,
                    Is.EqualTo(k1.TotalChecks),
                    "The mock is generated from this signature list, so every entry must be present. Missing: "
                    + string.Join(", ", k1.MissingEvidence));
                Assert.That(mock.ContentRoot, Is.EqualTo(mock.InstallRoot), "K1 is not nested.");
                Assert.That(Directory.Exists(Path.Combine(mock.InstallRoot, "Override")), Is.True, "K1 uses a capitalised Override.");
            });
        }

        [Test]
        public void Kotor2Mock_NestsUnderSteamassetsAndResolvesToIt()
        {
            MockKotorInstallResult mock = MockKotorInstall.Create(Path.Combine(_tempRoot, "K2"), MockKotorGame.Kotor2);

            Assert.Multiple(() =>
            {
                Assert.That(
                    mock.ContentRoot,
                    Is.EqualTo(Path.Combine(mock.InstallRoot, "steamassets")),
                    "The Aspyr layout keeps content one level down.");
                Assert.That(
                    PathUtilities.ResolveInstallGameDirectory(mock.InstallRoot),
                    Is.EqualTo(Path.GetFullPath(mock.ContentRoot)),
                    "Pointing ModSync at the Steam app folder must rewrite to steamassets.");
                Assert.That(
                    Path.GetFileName(mock.OverrideDirectory),
                    Is.EqualTo("override"),
                    "K2 uses a lowercase override; the casing difference from K1 is the point of the fixture.");
            });
        }

        [Test]
        public void Kotor2Mock_IsDetectedAsAspyrAtTheContentRoot()
        {
            MockKotorInstallResult mock = MockKotorInstall.Create(Path.Combine(_tempRoot, "K2"), MockKotorGame.Kotor2);

            PathUtilities.DetectedGame atContentRoot = PathUtilities.DetectGame(mock.ContentRoot);

            Assert.That(
                atContentRoot,
                Is.EqualTo(PathUtilities.DetectedGame.Kotor2Aspyr),
                "The mock writes every file DetectKotor2Version scores, so it must clear the 70% Aspyr threshold.");
        }

        /// <summary>
        /// Documents a real gap rather than asserting the mock is wrong: detection scores paths
        /// relative to whatever directory it is handed, and the Aspyr install parent holds none of
        /// them. Only the install path calls <c>ResolveInstallGameDirectory</c> first.
        /// </summary>
        [Test]
        public void Kotor2Mock_IsNotDetectedAtTheInstallParent()
        {
            MockKotorInstallResult mock = MockKotorInstall.Create(Path.Combine(_tempRoot, "K2"), MockKotorGame.Kotor2);

            Assert.That(
                PathUtilities.DetectGame(mock.InstallRoot),
                Is.EqualTo(PathUtilities.DetectedGame.Unknown),
                "Detection at the Steam app folder finds nothing; callers must resolve the content root first.");
        }

        [TestCase(MockKotorGame.Kotor1)]
        [TestCase(MockKotorGame.Kotor2)]
        public void Mock_HasNoCaseInsensitiveDuplicates(MockKotorGame game)
        {
            MockKotorInstallResult mock = MockKotorInstall.Create(Path.Combine(_tempRoot, game.ToString()), game);

            List<FileSystemInfo> duplicates = PathHelper
                .FindCaseInsensitiveDuplicates(mock.InstallRoot)
                .ToList();

            Assert.That(
                duplicates,
                Is.Empty,
                "Case-insensitive duplicates make ModSync's environment validation complain: "
                + string.Join(", ", duplicates.Select(d => d.FullName)));
        }

        [TestCase(MockKotorGame.Kotor1)]
        [TestCase(MockKotorGame.Kotor2)]
        public void Mock_IsByteReproducible(MockKotorGame game)
        {
            MockKotorInstallResult first = MockKotorInstall.Create(Path.Combine(_tempRoot, "a"), game);
            MockKotorInstallResult second = MockKotorInstall.Create(Path.Combine(_tempRoot, "b"), game);

            IReadOnlyList<string> firstFiles = MockKotorInstall.ListFiles(first.InstallRoot);
            IReadOnlyList<string> secondFiles = MockKotorInstall.ListFiles(second.InstallRoot);

            Assert.That(secondFiles, Is.EqualTo(firstFiles), "Two runs must produce the same file list.");

            foreach (string relative in firstFiles)
            {
                Assert.That(
                    Sha256(Path.Combine(second.InstallRoot, relative)),
                    Is.EqualTo(Sha256(Path.Combine(first.InstallRoot, relative))),
                    "Two runs must produce identical bytes for " + relative);
            }
        }

        [Test]
        public void MockTlk_HasAConsistentV30HeaderAndStringTable()
        {
            MockKotorInstallResult mock = MockKotorInstall.Create(Path.Combine(_tempRoot, "K1"), MockKotorGame.Kotor1);
            byte[] tlk = File.ReadAllBytes(Path.Combine(mock.ContentRoot, "dialog.tlk"));

            Assert.That(Encoding.ASCII.GetString(tlk, 0, 8), Is.EqualTo("TLK V3.0"));

            uint stringCount = BitConverter.ToUInt32(tlk, 12);
            uint textOffset = BitConverter.ToUInt32(tlk, 16);

            Assert.That(stringCount, Is.GreaterThan(0u), "An empty talk table would not exercise anything.");
            Assert.That(
                textOffset,
                Is.EqualTo(20 + (stringCount * 40)),
                "Text data starts immediately after the 20-byte header and the 40-byte entry headers.");

            long declaredTextBytes = 0;
            for (uint i = 0; i < stringCount; i++)
            {
                int entry = (int)(20 + (i * 40));
                uint entryTextOffset = BitConverter.ToUInt32(tlk, entry + 28);
                uint entryTextLength = BitConverter.ToUInt32(tlk, entry + 32);

                Assert.That(
                    entryTextOffset,
                    Is.EqualTo((uint)declaredTextBytes),
                    "Entry " + i + " must point at the end of the previous string.");
                declaredTextBytes += entryTextLength;
            }

            Assert.That(
                tlk.Length,
                Is.EqualTo(textOffset + declaredTextBytes),
                "The file must end exactly where the last string ends.");
        }

        [Test]
        public void MockChitinKey_IndexesEveryGeneratedBif()
        {
            MockKotorInstallResult mock = MockKotorInstall.Create(Path.Combine(_tempRoot, "K1"), MockKotorGame.Kotor1);
            byte[] key = File.ReadAllBytes(Path.Combine(mock.ContentRoot, "chitin.key"));

            Assert.That(Encoding.ASCII.GetString(key, 0, 8), Is.EqualTo("KEY V1  "));

            uint bifCount = BitConverter.ToUInt32(key, 8);
            uint keyCount = BitConverter.ToUInt32(key, 12);
            uint fileTableOffset = BitConverter.ToUInt32(key, 16);
            uint keyTableOffset = BitConverter.ToUInt32(key, 20);

            string[] bifsOnDisk = Directory.GetFiles(Path.Combine(mock.ContentRoot, "data"), "*.bif");

            Assert.Multiple(() =>
            {
                Assert.That(bifCount, Is.EqualTo((uint)bifsOnDisk.Length), "Every BIF on disk must appear in the index.");
                Assert.That(keyCount, Is.EqualTo(0u), "The mock ships no BIF-backed resources.");
                Assert.That(fileTableOffset, Is.EqualTo(64u), "The KEY header is 64 bytes.");
                Assert.That(keyTableOffset, Is.EqualTo((uint)key.Length), "An empty key table sits at end of file.");
            });

            for (uint i = 0; i < bifCount; i++)
            {
                int entry = (int)(fileTableOffset + (i * 12));
                uint declaredSize = BitConverter.ToUInt32(key, entry);
                uint nameOffset = BitConverter.ToUInt32(key, entry + 4);
                ushort nameLength = BitConverter.ToUInt16(key, entry + 8);

                string name = Encoding.ASCII.GetString(key, (int)nameOffset, nameLength).TrimEnd('\0');
                string onDisk = Path.Combine(mock.ContentRoot, name.Replace('\\', Path.DirectorySeparatorChar));

                Assert.That(File.Exists(onDisk), Is.True, "chitin.key references a BIF that does not exist: " + name);
                Assert.That(new FileInfo(onDisk).Length, Is.EqualTo(declaredSize), "Recorded size must match " + name);
            }
        }

        [Test]
        public void MockContainers_CarryTheCorrectMagicBytes()
        {
            MockKotorInstallResult k1 = MockKotorInstall.Create(Path.Combine(_tempRoot, "K1"), MockKotorGame.Kotor1);

            Assert.Multiple(() =>
            {
                Assert.That(Magic(Path.Combine(k1.ContentRoot, "data", "2da.bif")), Is.EqualTo("BIFFV1  "));
                Assert.That(Magic(Path.Combine(k1.ContentRoot, "rims", "mainmenu.rim")), Is.EqualTo("RIM V1.0"));
                Assert.That(Magic(Path.Combine(k1.ContentRoot, "modules", "global.mod")), Is.EqualTo("MOD V1.0"));
                Assert.That(Magic(Path.Combine(k1.ContentRoot, "patch.erf")), Is.EqualTo("ERF V1.0"));
                Assert.That(Magic(Path.Combine(k1.OverrideDirectory, "mock_baseline.2da")), Is.EqualTo("2DA V2.b"));
            });
        }

        [Test]
        public void MockArchives_KeepTheirRealInstallerNames()
        {
            MockModArchiveSet mods = MockModArchives.Create(Path.Combine(_tempRoot, "mods"));

            IReadOnlyList<string> patcherEntries = ZipEntries(mods.TslPatcherArchive);
            IReadOnlyList<string> namespaceEntries = ZipEntries(mods.NamespaceArchive);

            Assert.Multiple(() =>
            {
                Assert.That(
                    patcherEntries,
                    Does.Contain(MockModArchives.TslPatcherInstallerName),
                    "The installer name is the fixture's whole point.");
                Assert.That(
                    patcherEntries.Any(e => e.EndsWith("TSLPatcher.exe", StringComparison.OrdinalIgnoreCase)),
                    Is.False,
                    "No archive may contain the name a hardcoded fallback would guess.");
                Assert.That(
                    patcherEntries,
                    Does.Contain("tslpatchdata/changes.ini"),
                    "The installer must sit beside tslpatchdata, at the archive root.");

                Assert.That(
                    namespaceEntries.All(e => e.StartsWith("MockNamespaceMod/", StringComparison.Ordinal)),
                    Is.True,
                    "The namespace archive carries its own root folder.");
                Assert.That(
                    namespaceEntries,
                    Does.Contain("MockNamespaceMod/" + MockModArchives.NamespaceInstallerName),
                    "The only executable sits at the archive root, not inside a namespace folder.");
                Assert.That(
                    namespaceEntries,
                    Does.Contain("MockNamespaceMod/tslpatchdata/namespaces.ini"));
                Assert.That(
                    namespaceEntries.Any(e => e.Contains("/OptionA/", StringComparison.Ordinal))
                    && namespaceEntries.Any(e => e.Contains("/OptionB/", StringComparison.Ordinal)),
                    Is.True,
                    "Both namespace data folders must be present.");
            });
        }

        [Test]
        public void MockLooseArchive_HasNoWrappingFolder()
        {
            MockModArchiveSet mods = MockModArchives.Create(Path.Combine(_tempRoot, "mods"));

            IReadOnlyList<string> entries = ZipEntries(mods.LooseFilesArchive);

            Assert.That(
                entries.Any(e => e.Contains('/', StringComparison.Ordinal)),
                Is.False,
                "The loose-files archive is flat on purpose: " + string.Join(", ", entries));
        }


        [Test]
        public void MockModDirectory_ContainsOnlyTheThreeArchives()
        {
            MockModArchiveSet mods = MockModArchives.Create(Path.Combine(_tempRoot, "mods"));

            string[] entries = Directory.GetFileSystemEntries(mods.ModDirectory);

            Assert.That(
                entries.Length,
                Is.EqualTo(3),
                "Build scratch must not survive in the mod directory: " + string.Join(", ", entries));
        }

        private static string Magic(string path)
        {
            byte[] header = new byte[8];
            using (FileStream stream = File.OpenRead(path))
            {
                int read = stream.Read(header, 0, header.Length);
                Assert.That(read, Is.EqualTo(header.Length), "File is shorter than its own header: " + path);
            }

            return Encoding.ASCII.GetString(header);
        }

        private static string Sha256(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                return Convert.ToHexString(sha.ComputeHash(stream));
            }
        }

        private static IReadOnlyList<string> ZipEntries(string archivePath)
        {
            using (ZipArchive archive = ZipFile.OpenRead(archivePath))
            {
                return archive.Entries
                    .Select(e => e.FullName)
                    .OrderBy(e => e, StringComparer.Ordinal)
                    .ToList();
            }
        }
    }
}
