// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System.IO;

using ModSync.Core.Utility;

using NUnit.Framework;

namespace ModSync.Tests
{
    [TestFixture]
    public sealed class ResolveInstallGameDirectoryTests
    {
        [Test]
        public void ResolveInstallGameDirectory_AspyrParent_RewritesToSteamassets()
        {
            string root = Path.Combine(Path.GetTempPath(), "ModSync_aspyr_" + Path.GetRandomFileName());
            string steamassets = Path.Combine(root, "steamassets");
            string steamOverride = Path.Combine(steamassets, "override");
            Directory.CreateDirectory(steamOverride);
            File.WriteAllText(Path.Combine(steamassets, "dialog.tlk"), "x");

            try
            {
                string resolved = PathUtilities.ResolveInstallGameDirectory(root);
                Assert.That(resolved, Is.EqualTo(Path.GetFullPath(steamassets)));
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
        public void ResolveInstallGameDirectory_K1Root_Unchanged()
        {
            string root = Path.Combine(Path.GetTempPath(), "ModSync_k1_" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(root, "Override"));
            File.WriteAllText(Path.Combine(root, "dialog.tlk"), "x");

            try
            {
                string resolved = PathUtilities.ResolveInstallGameDirectory(root);
                Assert.That(resolved, Is.EqualTo(Path.GetFullPath(root)));
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
    }
}
