// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System.IO;
using System.IO.Compression;

using ModSync.Core.Utility;

using NUnit.Framework;

namespace ModSync.Tests
{
    /// <summary>
    /// Mod archives come in two shapes: self-rooted ("Mod Name/TSLPatcher.exe") and flat
    /// ("TSLPatcher.exe", where extracting creates the folder). Nothing in the file declares which,
    /// so a path lookup must accept either. <see cref="ArchiveHelper.MatchArchivePath"/> used to
    /// unconditionally prefix the archive name, which doubled the folder for self-rooted archives
    /// ("Character Start Up Changes/Character Start Up Changes/TSLPatcher.exe") and reported a file
    /// that was present as missing — failing the whole install at validation.
    /// </summary>
    [TestFixture]
    public sealed class ArchiveRootFolderMatchingTests
    {
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "modsync-archive-root-" + Path.GetRandomFileName());
            _ = Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }

        [Test]
        public void SelfRootedArchive_FindsFileWithoutDoublingTheRootFolder()
        {
            string archive = CreateZip("Character Start Up Changes.zip", "Character Start Up Changes/TSLPatcher.exe");

            ArchiveHelper.ArchiveMatchResult result =
                ArchiveHelper.MatchArchivePath(archive, @"Character Start Up Changes\TSLPatcher.exe");

            Assert.That(result.CouldOpen, Is.True, "archive should open");
            Assert.That(
                result.Matches,
                Is.True,
                "a self-rooted archive must match its entry without the archive name being prefixed twice");
        }

        [Test]
        public void FlatArchive_StillMatchesViaTheArchiveNameAsRoot()
        {
            string archive = CreateZip("Some Flat Mod.zip", "TSLPatcher.exe");

            ArchiveHelper.ArchiveMatchResult result =
                ArchiveHelper.MatchArchivePath(archive, @"Some Flat Mod\TSLPatcher.exe");

            Assert.That(result.Matches, Is.True, "a flat archive extracts into a folder named after the archive");
        }

        [Test]
        public void GenuinelyAbsentFile_StillReportsNoMatch()
        {
            string archive = CreateZip("Some Mod.zip", "Some Mod/readme.txt");

            ArchiveHelper.ArchiveMatchResult result =
                ArchiveHelper.MatchArchivePath(archive, @"Some Mod\TSLPatcher.exe");

            Assert.That(result.Matches, Is.False, "accepting both root forms must not make every lookup succeed");
        }

        private string CreateZip(string archiveName, params string[] entryPaths)
        {
            string path = Path.Combine(_tempDir, archiveName);
            using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            {
                foreach (string entryPath in entryPaths)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(entryPath);
                    using (Stream s = entry.Open())
                    {
                        s.WriteByte(0);
                    }
                }
            }

            return path;
        }
    }
}
