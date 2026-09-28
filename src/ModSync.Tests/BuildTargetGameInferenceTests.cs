// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using ModSync.Core.Services;

using NUnit.Framework;

namespace ModSync.Tests
{
    /// <summary>
    /// Measured: every component in a `convert --auto-generate-local` run resolved with
    /// targetGame=None, because MainConfig.TargetGame is only set from a serialized `game` field and
    /// there is no installed game to inspect during a conversion. With None, every wrong-game guard
    /// in the resolver is inert -- which is how a K1 build was handed the KOTOR 2 mod
    /// "Rescaled Trandoshans.zip".
    /// </summary>
    [TestFixture]
    public class BuildTargetGameInferenceTests
    {
        [TestCase("# KOTOR 1 Full Build", "K1")]
        [TestCase("# KOTOR 2 Full Build", "TSL")]
        [TestCase("# KOTOR II Full Build", "TSL")]
        [TestCase("# TSL Full Build", "TSL")]
        [TestCase("# The Sith Lords Full Build", "TSL")]
        [TestCase("# K1 Mod List", "K1")]
        [TestCase("# Some Generic Build", "")]
        public void FromTitle_ReadsTheHeading(string title, string expected)
        {
            Assert.That(BuildTargetGameInference.FromTitle(title + "\n\nbody text"), Is.EqualTo(expected));
        }

        /// <summary>The real file's first line, verbatim.</summary>
        [Test]
        public void FromTitle_HandlesTheRealK1BuildDocument()
        {
            const string Document =
                "# KOTOR 1 Full Build\n\n## Installation Notes\n\n:::warning\nImportant\n:::\n";

            Assert.That(BuildTargetGameInference.FromTitle(Document), Is.EqualTo("K1"));
        }

        /// <summary>
        /// Body prose names both games constantly ("a port of the TSL version"), so only the title
        /// line is consulted.
        /// </summary>
        [Test]
        public void FromTitle_IgnoresProseBelowTheHeading()
        {
            const string Document = "# KOTOR 1 Full Build\n\nThis mod is a port of the TSL version.\n";

            Assert.That(BuildTargetGameInference.FromTitle(Document), Is.EqualTo("K1"));
        }

        [TestCase("mod-builds/content/k1/full.md", "K1")]
        [TestCase("mod-builds/content/k2/full.md", "TSL")]
        [TestCase("mod-builds/TOMLs/KOTOR1_Full.toml", "K1")]
        [TestCase("/tmp/builds/tsl/list.md", "TSL")]
        [TestCase("/tmp/builds/list.md", "")]
        public void FromPath_ReadsTheDirectoryConvention(string path, string expected)
        {
            Assert.That(BuildTargetGameInference.FromPath(path), Is.EqualTo(expected));
        }

        /// <summary>A path naming both games decides nothing rather than guessing.</summary>
        [Test]
        public void FromPath_RefusesAnAmbiguousPath()
        {
            Assert.That(BuildTargetGameInference.FromPath("/builds/k1-and-k2/full.md"), Is.Empty);
        }

        /// <summary>The title outranks the path: content beats a renameable convention.</summary>
        [Test]
        public void Infer_PrefersTheTitleOverThePath()
        {
            Assert.That(
                BuildTargetGameInference.Infer("# KOTOR 2 Full Build", "content/k1/full.md"),
                Is.EqualTo("TSL"));
        }

        [Test]
        public void Infer_FallsBackToThePathWhenTheTitleSaysNothing()
        {
            Assert.That(
                BuildTargetGameInference.Infer("# Full Build", "content/k1/full.md"),
                Is.EqualTo("K1"));
        }

        [Test]
        public void Infer_ReturnsEmptyWhenNothingDecides()
        {
            Assert.That(BuildTargetGameInference.Infer(null, null), Is.Empty);
        }
    }
}
