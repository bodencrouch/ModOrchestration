// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System.IO;

using ModSync.Core.Services;

using NUnit.Framework;

namespace ModSync.Tests
{
    [TestFixture]
    public sealed class UnixNssCompileRecoveryTests
    {
        [SetUp]
        public void ResetCache()
        {
            UnixNssCompileRecovery.ResetCompilerCacheForTests();
        }

        [TearDown]
        public void ClearCache()
        {
            UnixNssCompileRecovery.ResetCompilerCacheForTests();
        }

        [Test]
        public void IsRecoveredNssSupportError_FiltersNwscriptCopyAndSummary()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    UnixNssCompileRecovery.IsRecoveredNssSupportError(
                        "[Error] 'str' object has no attribute 'info'"),
                    Is.True);
                Assert.That(
                    UnixNssCompileRecovery.IsRecoveredNssSupportError(
                        "[Error] Could not locate resource to copy: 'nwscript.nss'"),
                    Is.True);
                Assert.That(
                    UnixNssCompileRecovery.IsRecoveredNssSupportError(
                        "[Error] Could not load source file to copy:"),
                    Is.True);
                Assert.That(
                    UnixNssCompileRecovery.IsRecoveredNssSupportError(
                        "[Error] The install completed with errors!: The install completed with 3 errors"),
                    Is.True);
                Assert.That(
                    UnixNssCompileRecovery.IsRecoveredNssSupportError(
                        "[Error] Could not locate resource to patch: 'feat.2da'"),
                    Is.False);
            });
        }

        [Test]
        public void FindNwnnsscomp_UsesSiblingExtractWhenModArchiveOmitsCompiler()
        {
            string root = Path.Combine(Path.GetTempPath(), "modsync-nss-" + Path.GetRandomFileName());
            string multifire = Path.Combine(root, "Multifire");
            string sibling = Path.Combine(root, "KillCzerkaJerk", "tslpatchdata");
            Directory.CreateDirectory(Path.Combine(multifire, "tslpatchdata", "mod1"));
            Directory.CreateDirectory(sibling);
            string compiler = Path.Combine(sibling, "nwnnsscomp.exe");
            File.WriteAllBytes(compiler, new byte[] { 0x4D, 0x5A });

            try
            {
                string found = UnixNssCompileRecovery.FindNwnnsscomp(
                    Path.Combine(multifire, "tslpatchdata", "mod1"),
                    multifire);

                Assert.That(found, Is.EqualTo(compiler));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
