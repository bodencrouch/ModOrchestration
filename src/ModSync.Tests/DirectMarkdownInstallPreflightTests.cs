// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ModSync.Core;
using ModSync.Core.Parsing;
using ModSync.Core.Services;

using NUnit.Framework;

namespace ModSync.Tests
{
    [TestFixture]
    public sealed class DirectMarkdownInstallPreflightTests
    {
        [TestCase("k1", 186)]
        [TestCase("k2", 145)]
        public void CanonicalFullGuide_LoadsWithNlpDrafts_AndIsInstallReady(
            string game,
            int expectedComponents)
        {
            string guidePath = Path.Combine(FindRepoRoot(), "mod-builds", "content", game, "full.md");
            Assert.That(File.Exists(guidePath), Is.True, $"Canonical guide not found: {guidePath}");

            List<ModComponent> components = DirectMarkdownInstallPreflight.LoadGuideAsync(guidePath)
                .GetAwaiter()
                .GetResult();
            foreach (ModComponent component in components)
            {
                component.IsSelected = true;
            }

            DirectMarkdownInstallPreflightResult result = DirectMarkdownInstallPreflight.Apply(components);

            int withInstructions = components.Count(c =>
                c.IsSelected && (c.Instructions.Count > 0 || c.Options.Any(o => o.Instructions.Count > 0)));
            int draftFlagged = components.Count(c =>
                c.InstallationWarning?.Contains(DraftInstructionService.ReviewFlagMessage, StringComparison.Ordinal) == true);

            Assert.Multiple(() =>
            {
                Assert.That(components, Has.Count.EqualTo(expectedComponents));
                Assert.That(result.SkippedFourGb, Is.EqualTo(1));
                Assert.That(components.Where(component => component.IsSelected),
                    Has.None.Matches<ModComponent>(component => component.WidescreenOnly));
                Assert.That(components.Single(component => string.Equals(
                    component.Name,
                    "4GB Patcher",
                    StringComparison.OrdinalIgnoreCase)).IsSelected,
                    Is.False);
                Assert.That(result.DraftedFromProse, Is.GreaterThan(0));
                Assert.That(draftFlagged, Is.GreaterThan(0));
                Assert.That(withInstructions, Is.GreaterThan(0));
                // Full guides still have many undrafted components; IsReady is false until NLP
                // coverage improves. Best-effort install deselects those and continues.
                Assert.That(result.UnresolvedComponents.Count, Is.GreaterThan(0));
                Assert.That(result.IsReady, Is.False);
            });
        }

        [Test]
        public void Apply_DeselectsWidescreenAndFourGb_ButKeepsReviewedMarkdownActions()
        {
            var normal = new ModComponent { Name = "Reviewed", IsSelected = true };
            normal.Instructions.Add(new Instruction { Action = Instruction.ActionType.DelDuplicate });
            var widescreen = new ModComponent { Name = "Wide", IsSelected = true, WidescreenOnly = true };
            var fourGb = new ModComponent { Name = "4GB Patcher", IsSelected = true };

            DirectMarkdownInstallPreflightResult result = DirectMarkdownInstallPreflight.Apply(
                new[] { normal, widescreen, fourGb });

            Assert.Multiple(() =>
            {
                Assert.That(result.IsReady, Is.True);
                Assert.That(result.SkippedWidescreen, Is.EqualTo(1));
                Assert.That(result.SkippedFourGb, Is.EqualTo(1));
                Assert.That(normal.IsSelected, Is.True);
                Assert.That(widescreen.IsSelected, Is.False);
                Assert.That(fourGb.IsSelected, Is.False);
            });
        }

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "ModSync.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName
                ?? throw new DirectoryNotFoundException("Could not locate ModSync.sln from the test directory.");
        }
    }
}
