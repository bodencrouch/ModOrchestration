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
        public void StageIncludeChainForTests_ResolvesTransitiveIncludesRecursively()
        {
            string root = Path.Combine(Path.GetTempPath(), "modsync-nss-" + Path.GetRandomFileName());
            string searchRoot = Path.Combine(root, "tslpatchdata");
            string workDir = Path.Combine(root, "wine-workdir");
            Directory.CreateDirectory(searchRoot);
            Directory.CreateDirectory(workDir);

            // a.nss #includes b.nss, which itself #includes c.nss. Only a.nss is staged
            // directly; b.nss and c.nss must be pulled in transitively.
            File.WriteAllText(Path.Combine(searchRoot, "a.nss"), "#include \"b\"\nvoid main() {}\n");
            File.WriteAllText(Path.Combine(searchRoot, "b.nss"), "#include \"c\"\nint GetB() { return 1; }\n");
            File.WriteAllText(Path.Combine(searchRoot, "c.nss"), "int GetC() { return 2; }\n");

            try
            {
                File.Copy(Path.Combine(searchRoot, "a.nss"), Path.Combine(workDir, "a.nss"));

                UnixNssCompileRecovery.StageIncludeChainForTests(workDir, searchRoot, "a.nss");

                Assert.Multiple(() =>
                {
                    Assert.That(File.Exists(Path.Combine(workDir, "b.nss")), Is.True, "b.nss (direct include) should be staged.");
                    Assert.That(File.Exists(Path.Combine(workDir, "c.nss")), Is.True, "c.nss (transitive include) should be staged.");
                });
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void StageIncludeChainForTests_DoesNotInfiniteLoopOnCircularIncludes()
        {
            string root = Path.Combine(Path.GetTempPath(), "modsync-nss-" + Path.GetRandomFileName());
            string searchRoot = Path.Combine(root, "tslpatchdata");
            string workDir = Path.Combine(root, "wine-workdir");
            Directory.CreateDirectory(searchRoot);
            Directory.CreateDirectory(workDir);

            // x.nss #includes y.nss, and y.nss #includes x.nss right back.
            File.WriteAllText(Path.Combine(searchRoot, "x.nss"), "#include \"y\"\nvoid main() {}\n");
            File.WriteAllText(Path.Combine(searchRoot, "y.nss"), "#include \"x\"\nint GetY() { return 1; }\n");

            try
            {
                File.Copy(Path.Combine(searchRoot, "x.nss"), Path.Combine(workDir, "x.nss"));

                Assert.DoesNotThrow(() =>
                    UnixNssCompileRecovery.StageIncludeChainForTests(workDir, searchRoot, "x.nss"));

                Assert.That(File.Exists(Path.Combine(workDir, "y.nss")), Is.True);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void EnsureNwscriptInPatcherTree_StagesTransitiveIncludeOfNwscript()
        {
            // Regression test for the production K2 CLI halt (component "For Mandalore!",
            // script m_def_henchmand.nss): staging nwscript.nss must also pull in whatever
            // nwscript.nss itself #includes, not just nwscript.nss verbatim.
            string root = Path.Combine(Path.GetTempPath(), "modsync-nss-" + Path.GetRandomFileName());
            string tslpatchdata = Path.Combine(root, "tslpatchdata");
            Directory.CreateDirectory(tslpatchdata);

            File.WriteAllText(
                Path.Combine(tslpatchdata, "nwscript.nss"),
                "#include \"k_inc_foo\"\n// core engine declarations\n");
            File.WriteAllText(
                Path.Combine(tslpatchdata, "k_inc_foo.nss"),
                "int GetFoo() { return 1; }\n");

            try
            {
                UnixNssCompileRecovery.EnsureNwscriptInPatcherTree(new DirectoryInfo(root));

                Assert.Multiple(() =>
                {
                    Assert.That(File.Exists(Path.Combine(root, "nwscript.nss")), Is.True, "nwscript.nss itself should be staged at the patcher root.");
                    Assert.That(File.Exists(Path.Combine(root, "k_inc_foo.nss")), Is.True, "nwscript.nss's own #include should also be staged at the patcher root.");
                });
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
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
