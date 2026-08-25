// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ModSync.Core;
using ModSync.Core.Parsing;
using ModSync.Core.Ports.Guides;
using ModSync.Core.Services;

using NUnit.Framework;

namespace ModSync.Tests
{
    [TestFixture]
    [NonParallelizable]
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

            TestContext.Progress.WriteLine(
                $"{game}: components={components.Count}, selectedWithInstructions={withInstructions}, "
                + $"draftFlagged={draftFlagged}, unresolved={result.UnresolvedComponents.Count}");
            foreach (string unresolved in result.UnresolvedComponents)
            {
                TestContext.Progress.WriteLine($"{game}: unresolved: {unresolved}");
            }

            Assert.Multiple(() =>
            {
                Assert.That(components, Has.Count.EqualTo(expectedComponents));
                Assert.That(result.SkippedFourGb, Is.EqualTo(1));
                Assert.That(components.Where(component => component.IsSelected),
                    Has.Some.Matches<ModComponent>(component => component.WidescreenOnly));
                Assert.That(components.Single(component => string.Equals(
                    component.Name,
                    "4GB Patcher",
                    StringComparison.OrdinalIgnoreCase)).IsSelected,
                    Is.False);
                Assert.That(result.DraftedFromProse, Is.GreaterThan(0));
                Assert.That(draftFlagged, Is.GreaterThan(0));
                Assert.That(withInstructions, Is.GreaterThan(0));
                // Raw prose alone is intentionally not ready. Archive-derived operations close the
                // remaining payload gaps in the separate cold-library readiness gate.
                Assert.That(result.UnresolvedComponents.Count, Is.GreaterThan(0));
                Assert.That(result.IsReady, Is.False);
            });
        }

        [TestCase("k1", 186)]
        [TestCase("k2", 145)]
        public void CanonicalFullGuide_ParsesResourceLinks(string game, int expectedComponents)
        {
            string guidePath = Path.Combine(FindRepoRoot(), "mod-builds", "content", game, "full.md");
            string markdown = File.ReadAllText(guidePath);
            GuideIngestResult result = GuideIngestService.Instance.IngestFromText(
                markdown,
                formatHint: "markdown",
                parseDirections: false);
            List<ModComponent> missing = result.Components
                .Where(component => component.ResourceRegistry.Count == 0)
                .ToList();

            TestContext.Progress.WriteLine(
                $"{game}: components={result.Components.Count}, withResources="
                + $"{result.Components.Count - missing.Count}, withoutResources={missing.Count}");
            foreach (ModComponent component in missing)
            {
                TestContext.Progress.WriteLine($"{game}: no-resource: {component.Name}");
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.Components, Has.Count.EqualTo(expectedComponents));
                Assert.That(missing, Is.Empty,
                    "Every canonical mod component must preserve at least one download/source link.");
            });
        }

        [TestCase("k1", 186, MainConfig.ValidTargetGames.K1)]
        [TestCase("k2", 145, MainConfig.ValidTargetGames.TSL)]
        public void CanonicalFullGuide_WithLocalArchives_IsInstallReadyLongRunning(
            string game,
            int expectedComponents,
            string targetGame)
        {
            string requestedGame = Environment.GetEnvironmentVariable("MODSYNC_AUDIT_GAME");
            if (!string.IsNullOrWhiteSpace(requestedGame)
                && !string.Equals(requestedGame, game, StringComparison.OrdinalIgnoreCase))
            {
                Assert.Ignore($"Readiness audit limited to {requestedGame}.");
            }

            string archiveRoot = Environment.GetEnvironmentVariable("MODSYNC_ARCHIVE_ROOT");
            if (string.IsNullOrWhiteSpace(archiveRoot) || !Directory.Exists(archiveRoot))
            {
                Assert.Ignore("Set MODSYNC_ARCHIVE_ROOT to run the cold-library readiness audit.");
            }

            string guidePath = Path.Combine(FindRepoRoot(), "mod-builds", "content", game, "full.md");
            string scratchRoot = Environment.GetEnvironmentVariable("MODSYNC_EXTRACT_SCRATCH");
            if (string.IsNullOrWhiteSpace(scratchRoot))
            {
                scratchRoot = Path.Combine(Path.GetTempPath(), "modsync-readiness-audit");
            }

            Directory.CreateDirectory(scratchRoot);
            DirectoryInfo previousSource = MainConfig.SourcePath;
            DirectoryInfo previousScratch = MainConfig.ExtractScratchPath;
            string previousTargetGame = MainConfig.TargetGame;
            List<ModComponent> previousComponents = MainConfig.AllComponents;

            try
            {
                var config = MainConfig.Instance;
                config.sourcePath = new DirectoryInfo(archiveRoot);
                config.extractScratchPath = new DirectoryInfo(scratchRoot);
                config.targetGame = targetGame;

                List<ModComponent> components = DirectMarkdownInstallPreflight.LoadGuideAsync(guidePath)
                    .GetAwaiter()
                    .GetResult();
                foreach (ModComponent component in components)
                {
                    component.IsSelected = true;
                }

                int generated = ComponentProcessingService.TryGenerateFromLocalArchivesAsync(components)
                    .GetAwaiter()
                    .GetResult();
                DirectMarkdownInstallPreflightResult result = DirectMarkdownInstallPreflight.Apply(components);
                int componentsWithResources = components.Count(component => component.ResourceRegistry.Count > 0);

                TestContext.Progress.WriteLine(
                    $"{game}: components={components.Count}, resources={componentsWithResources}, generated={generated}, "
                    + $"unresolvedAfterArchives={result.UnresolvedComponents.Count}");
                foreach (string unresolved in result.UnresolvedComponents)
                {
                    TestContext.Progress.WriteLine($"{game}: unresolved-after-archives: {unresolved}");
                    ModComponent unresolvedComponent = components.First(component =>
                        string.Equals(component.Name, unresolved, StringComparison.Ordinal));
                    string actions = string.Join(", ", unresolvedComponent.Instructions.Select(i => i.Action)
                        .Concat(unresolvedComponent.Options.SelectMany(option => option.Instructions)
                            .Select(i => i.Action)));
                    TestContext.Progress.WriteLine(
                        $"{game}: unresolved-shape: {unresolved}; method={unresolvedComponent.InstallationMethod}; "
                        + $"root={unresolvedComponent.Instructions.Count}; options={unresolvedComponent.Options.Count}; actions=[{actions}]");
                }

                Assert.Multiple(() =>
                {
                    Assert.That(components, Has.Count.EqualTo(expectedComponents));
                    Assert.That(result.IsReady, Is.True,
                        "Selected guide components must all have executable actions after local archive generation. "
                        + string.Join(", ", result.UnresolvedComponents));
                });
            }
            finally
            {
                var config = MainConfig.Instance;
                config.sourcePath = previousSource;
                config.extractScratchPath = previousScratch;
                config.targetGame = previousTargetGame;
                MainConfig.AllComponents = previousComponents;
            }
        }

        [Test]
        public void Apply_PreservesWidescreenSelection_AndSkipsFourGb()
        {
            var normal = new ModComponent { Name = "Reviewed", IsSelected = true };
            normal.Instructions.Add(new Instruction { Action = Instruction.ActionType.DelDuplicate });
            var widescreen = new ModComponent { Name = "Wide", IsSelected = true, WidescreenOnly = true };
            widescreen.Instructions.Add(new Instruction { Action = Instruction.ActionType.DelDuplicate });
            var fourGb = new ModComponent { Name = "4GB Patcher", IsSelected = true };

            DirectMarkdownInstallPreflightResult result = DirectMarkdownInstallPreflight.Apply(
                new[] { normal, widescreen, fourGb });

            Assert.Multiple(() =>
            {
                Assert.That(result.IsReady, Is.True);
                Assert.That(result.SkippedWidescreen, Is.Zero);
                Assert.That(result.SkippedFourGb, Is.EqualTo(1));
                Assert.That(normal.IsSelected, Is.True);
                Assert.That(widescreen.IsSelected, Is.True);
                Assert.That(fourGb.IsSelected, Is.False);
            });
        }

        [Test]
        public void Apply_RejectsPreparationOnlyAndPartialPayloads()
        {
            var patcherNoOp = new ModComponent
            {
                Name = "Patcher no-op",
                IsSelected = true,
                InstallationMethod = "TSLPatcher",
            };
            patcherNoOp.Instructions.Add(new Instruction { Action = Instruction.ActionType.Extract });

            var looseNoOp = new ModComponent
            {
                Name = "Loose no-op",
                IsSelected = true,
                InstallationMethod = "Loose-File Mod",
            };
            looseNoOp.Instructions.Add(new Instruction { Action = Instruction.ActionType.Delete });
            looseNoOp.Instructions.Add(new Instruction { Action = Instruction.ActionType.Rename });

            var partialHybrid = new ModComponent
            {
                Name = "Partial hybrid",
                IsSelected = true,
                InstallationMethod = "Hybrid (TSLPatcher + Loose Files)",
            };
            partialHybrid.Instructions.Add(new Instruction { Action = Instruction.ActionType.Patcher });

            DirectMarkdownInstallPreflightResult result = DirectMarkdownInstallPreflight.Apply(
                new[] { patcherNoOp, looseNoOp, partialHybrid });

            Assert.That(result.UnresolvedComponents,
                Is.EquivalentTo(new[] { "Patcher no-op", "Loose no-op", "Partial hybrid" }));
        }

        [Test]
        public void Apply_AcceptsCompleteHybridPayload()
        {
            var hybrid = new ModComponent
            {
                Name = "Complete hybrid",
                IsSelected = true,
                InstallationMethod = "Hybrid (TSLPatcher + Loose Files)",
            };
            hybrid.Instructions.Add(new Instruction { Action = Instruction.ActionType.Extract });
            hybrid.Instructions.Add(new Instruction { Action = Instruction.ActionType.Patcher });
            hybrid.Instructions.Add(new Instruction { Action = Instruction.ActionType.Move });

            DirectMarkdownInstallPreflightResult result = DirectMarkdownInstallPreflight.Apply(
                new[] { hybrid });

            Assert.That(result.IsReady, Is.True);
        }

        [Test]
        [NonParallelizable]
        public void CanonicalComponent_WithLocalArchives_IsExecutableLongRunning()
        {
            string componentName = Environment.GetEnvironmentVariable("MODSYNC_AUDIT_COMPONENT");
            string game = Environment.GetEnvironmentVariable("MODSYNC_AUDIT_GAME");
            string archiveRoot = Environment.GetEnvironmentVariable("MODSYNC_ARCHIVE_ROOT");
            if (string.IsNullOrWhiteSpace(componentName)
                || string.IsNullOrWhiteSpace(game)
                || string.IsNullOrWhiteSpace(archiveRoot))
            {
                Assert.Ignore("Set MODSYNC_AUDIT_COMPONENT, MODSYNC_AUDIT_GAME, and MODSYNC_ARCHIVE_ROOT.");
            }

            MainConfig.Instance.sourcePath = new DirectoryInfo(archiveRoot);
            MainConfig.Instance.targetGame = string.Equals(game, "k1", StringComparison.OrdinalIgnoreCase)
                ? "K1"
                : "TSL";
            string guidePath = Path.Combine(FindRepoRoot(), "mod-builds", "content", game, "full.md");
            List<ModComponent> all = DirectMarkdownInstallPreflight.LoadGuideAsync(guidePath)
                .GetAwaiter()
                .GetResult();
            MainConfig.AllComponents = all;
            ModComponent component = all.Single(candidate =>
                string.Equals(candidate.Name, componentName, StringComparison.OrdinalIgnoreCase));
            component.IsSelected = true;

            GenerationResult generation = AutoInstructionGenerator
                .TryGenerateInstructionsFromArchiveDetailed(component);
            DirectMarkdownInstallPreflightResult preflight = DirectMarkdownInstallPreflight.Apply(
                new[] { component });
            string actions = string.Join(", ", component.Instructions.Select(i => i.Action)
                .Concat(component.Options.SelectMany(option => option.Instructions).Select(i => i.Action)));

            Assert.That(preflight.IsReady, Is.True,
                $"generated={generation.Success}; skip={generation.SkipReason}; archive={generation.ResolvedArchivePath}; "
                + $"tier={generation.ResolutionTier}; reason={generation.ResolutionReason}; "
                + $"method={component.InstallationMethod}; actions=[{actions}]");
        }

        private static string FindRepoRoot()
        {
            string configuredRoot = Environment.GetEnvironmentVariable("MODSYNC_REPO_ROOT");
            if (!string.IsNullOrWhiteSpace(configuredRoot)
                && File.Exists(Path.Combine(configuredRoot, "ModSync.sln")))
            {
                return Path.GetFullPath(configuredRoot);
            }

            string currentDirectory = Directory.GetCurrentDirectory();
            if (File.Exists(Path.Combine(currentDirectory, "ModSync.sln")))
            {
                return currentDirectory;
            }

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
