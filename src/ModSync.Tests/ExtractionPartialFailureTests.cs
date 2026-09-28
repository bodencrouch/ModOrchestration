// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

using ModSync.Core.Services.Deployment;
using ModSync.Core.Services.FileSystem;

using NUnit.Framework;

namespace ModSync.Tests
{
    /// <summary>
    /// An extraction that stops part-way must FAIL, never return a short file list that the caller
    /// reads as success.
    /// <para>
    /// `RealFileSystemProvider.ExtractArchiveAsync` creates each entry's destination directory before
    /// writing the file, and used to swallow write failures: `ObjectDisposedException` hit a bare
    /// `return` that abandoned the whole remaining archive, and `UnauthorizedAccessException` logged a
    /// warning and carried on. Both returned normally with a partial `extractedFiles` list and no
    /// error, so a mod could extract nothing at all while every step reported OK.
    /// </para>
    /// <para>
    /// This is not hypothetical: the reference mod library contains folders frozen at exactly that
    /// point — `Ultimate Kashyyyk High Resolution - TPC Version-1365-1-2-1669476173/Kashyyyk HR/
    /// Override/` holds three nested directories and zero files, which is precisely the directory
    /// chain created for the archive's first entry before its write failed.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class ExtractionPartialFailureTests
    {
        private string _testDirectory;
        private string _archiveDirectory;
        private string _destinationDirectory;

        [SetUp]
        public void SetUp()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), "ModSync_PartialExtract_" + Guid.NewGuid());
            _archiveDirectory = Path.Combine(_testDirectory, "Archives");
            _destinationDirectory = Path.Combine(_testDirectory, "Extracted");
            _ = Directory.CreateDirectory(_archiveDirectory);
            _ = Directory.CreateDirectory(_destinationDirectory);
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
            catch (IOException)
            {
                // Temp cleanup is best-effort.
            }
        }

        private string CreateArchive(string archiveName, params string[] entryPaths)
        {
            string archivePath = Path.Combine(_archiveDirectory, archiveName);
            using (ZipArchive zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                foreach (string entryPath in entryPaths)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(entryPath);
                    using (var writer = new StreamWriter(entry.Open()))
                    {
                        writer.Write("payload");
                    }
                }
            }

            return archivePath;
        }

        /// <summary>
        /// A file that cannot be written must abort the extraction with an error naming the archive,
        /// rather than being skipped so the caller sees a short list and assumes success.
        /// </summary>
        [Test]
        public void UnwritableEntry_ThrowsInsteadOfReturningAPartialList()
        {
            string archivePath = CreateArchive("Blocked Mod.zip", "first.txt", "blocked.txt", "third.txt");

            // A directory standing where a file must be written makes the file write fail, which is
            // the reliable way to interrupt an extraction mid-archive.
            _ = Directory.CreateDirectory(Path.Combine(_destinationDirectory, "blocked.txt"));

            var provider = new RealFileSystemProvider();

            IOException ex = Assert.ThrowsAsync<IOException>(
                async () => await provider.ExtractArchiveAsync(archivePath, _destinationDirectory));

            Assert.That(
                ex.Message,
                Does.Contain("Blocked Mod.zip"),
                "The failure must name the archive so the operator can tell which mod is incomplete.");
        }

        /// <summary>
        /// The partial extraction must not be reported as a completed one. Before the fix this call
        /// returned a list containing only "first.txt" and no error at all.
        /// </summary>
        [Test]
        public void InterruptedExtraction_DoesNotReportSuccess()
        {
            string archivePath = CreateArchive("Silent Mod.zip", "first.txt", "blocked.txt");
            _ = Directory.CreateDirectory(Path.Combine(_destinationDirectory, "blocked.txt"));

            var provider = new RealFileSystemProvider();

            bool completedNormally;
            List<string> extracted = null;
            try
            {
                extracted = provider.ExtractArchiveAsync(archivePath, _destinationDirectory)
                    .GetAwaiter().GetResult();
                completedNormally = true;
            }
            catch (IOException)
            {
                completedNormally = false;
            }

            Assert.That(
                completedNormally,
                Is.False,
                $"Extraction returned normally with {extracted?.Count ?? 0} file(s) despite not extracting "
                + "the whole archive; a caller cannot distinguish that from success.");
        }

        /// <summary>
        /// The guard must not fire on healthy archives - every entry present means a clean return.
        /// </summary>
        [Test]
        public void HealthyArchive_ExtractsEveryEntry()
        {
            string archivePath = CreateArchive("Good Mod.zip", "a.txt", "sub/b.txt", "sub/deeper/c.txt");

            var provider = new RealFileSystemProvider();
            List<string> extracted = provider.ExtractArchiveAsync(archivePath, _destinationDirectory)
                .GetAwaiter().GetResult();

            Assert.That(extracted, Has.Count.EqualTo(3));
            Assert.That(File.Exists(Path.Combine(_destinationDirectory, "a.txt")), Is.True);
            Assert.That(File.Exists(Path.Combine(_destinationDirectory, "sub", "b.txt")), Is.True);
            Assert.That(File.Exists(Path.Combine(_destinationDirectory, "sub", "deeper", "c.txt")), Is.True);
        }

        /// <summary>
        /// Regression test for the zero-byte-fan-out corruption class documented in
        /// GitCheckpointService.CopyFileOverwriteWithRetry ("K1 appearance.2da at [18]") and
        /// mission notes 09/36: an existing destination that happens to be hardlinked elsewhere
        /// (a checkpoint snapshot, another install tree sharing the inode via a shared cache, etc.)
        /// must never be opened with <c>FileMode.Create</c> directly, because truncating it in
        /// place zeroes every hardlinked sibling simultaneously - not just the file being
        /// overwritten. `ExtractArchiveAsync` used to do exactly that. This test hardlinks a
        /// "sibling" file to the extraction destination before extracting a new entry over that
        /// path, and asserts the sibling keeps its original, non-zero content afterward.
        /// </summary>
        [Test]
        public void ExtractingOverAHardlinkedDestination_DoesNotZeroTheSibling()
        {
            const string relativePath = "override/c_bantha01.txt";
            string destinationItemPath = Path.Combine(_destinationDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            _ = Directory.CreateDirectory(Path.GetDirectoryName(destinationItemPath));

            // Simulate the pre-existing destination file, then hardlink a "sibling" (e.g. a
            // checkpoint git tree copy, or another install tree's Override) to the same inode.
            const string originalContent = "original-real-content-1398279-bytes-worth-of-texture";
            File.WriteAllText(destinationItemPath, originalContent);

            string siblingPath = Path.Combine(_testDirectory, "sibling_checkpoint_copy.txt");
            bool linked = HardLinkHelper.TryCreateHardLink(destinationItemPath, siblingPath);
            Assume.That(linked, Is.True, "Hardlink creation must succeed on this filesystem for the test to be meaningful.");

            string archivePath = CreateArchive("Ultimate Character Overhaul.zip", relativePath);

            var provider = new RealFileSystemProvider();
            List<string> extracted = provider.ExtractArchiveAsync(archivePath, _destinationDirectory)
                .GetAwaiter().GetResult();

            Assert.That(extracted, Has.Count.EqualTo(1));
            Assert.That(File.ReadAllText(destinationItemPath), Is.EqualTo("payload"), "The destination should contain the newly-extracted content.");

            string siblingContent = File.ReadAllText(siblingPath);
            Assert.That(
                siblingContent,
                Is.EqualTo(originalContent),
                "Extracting a new entry over a hardlinked destination must not truncate the shared inode: " +
                "the hardlinked sibling should be unaffected, never zero-byte.");
            Assert.That(siblingContent, Is.Not.Empty);
        }
    }
}
