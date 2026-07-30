// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ModSync.Core;
using ModSync.Core.CLI;
using ModSync.Core.Parsing;
using ModSync.Core.Ports.Guides;
using ModSync.Core.Services;

using NUnit.Framework;

namespace ModSync.Tests
{
    /// <summary>
    /// Tests for the guide-ingestion slice: natural-language draft instructions
    /// (<see cref="DraftInstructionService"/>), paste-cascade format sniffing
    /// (<see cref="ModComponentSerializationService.DetectFormatFromContent"/>), and CLI parity
    /// (convert --stdin --parse-directions).
    /// </summary>
    [TestFixture]
    public sealed class GuideIngestionTests
    {
        private const string ModDirectoryPlaceholder = "<<modDirectory>>";
        private const string KotorDirectoryPlaceholder = "<<kotorDirectory>>";

        // Real prose taken from mod-builds/content/k1/full.md (KOTOR 1 Community Patch section style).
        private const string MoveFoldersProse =
            "Move everything from the Straight Fixes, Resolution Fixes, and Aesthetic Improvements folders to your Override.";

        private const string PatcherProse =
            "Run the installer, then move the files from the patch to your override.";

        private const string DeleteBeforeMoveProse =
            "Make sure to delete LSI_win01.tpc and LSI_box01.tpc **before** moving to override.";

        private const string BeforeMovingDeleteProse =
            "Before moving the files to the override folder, be sure to delete the following: PFBI01 through PFBI04, and PMBI01 through PMBI04.";

        private const string RerunPatcherProse =
            "Install the main mod, then re-run the patcher and select the K1CP compatibility install option and install it as well, if using K1CP.";

        private const string MoveExceptProse =
            "The file has the wrong readme; move all the files in the Creatures folder, except for the readme and Gizka.jpg (any .jpg/.png files are always previews and can be deleted), to the override.";

        private const string MarkdownGuide = @"### Guide Ingestion Test Mod

**Name:** [Guide Ingestion Test Mod](https://example.com/guide-ingestion-test-mod.zip)

**Author:** Test Author

**Description:** Synthetic mod for guide ingestion tests.

**Category & Tier:** Immersion / 1 - Essential

**Installation Method:** Loose-File Mod

**Installation Instructions:** Move everything from the Straight Fixes, Resolution Fixes, and Aesthetic Improvements folders to your Override.

___
";

        private string _testDirectory;
        private MainConfig _previousMainConfig;

        [SetUp]
        public void SetUp()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), "ModSync_GuideIngestion_" + Guid.NewGuid());
            Directory.CreateDirectory(_testDirectory);
            _previousMainConfig = MainConfig.Instance;
            MainConfig.Instance = new MainConfig();
        }

        [TearDown]
        public void TearDown()
        {
            MainConfig.Instance = _previousMainConfig;

            try
            {
                if (Directory.Exists(_testDirectory))
                {
                    Directory.Delete(_testDirectory, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        private static ModComponent CreateComponent(string directions)
        {
            return new ModComponent
            {
                Name = "Guide Ingestion Component",
                Guid = Guid.NewGuid(),
                Directions = directions,
            };
        }

        private static void AssertInstructionIsSandboxed(Instruction instruction)
        {
            foreach (string source in instruction.Source)
            {
                Assert.That(DraftInstructionService.IsSandboxedPath(source), Is.True,
                    $"Source '{source}' must be confined to a placeholder root without '..' / rooted escapes");
                Assert.That(source, Does.Not.Contain("<<gameDirectory>>"), "Legacy placeholder must be normalized away");
                Assert.That(source.Replace('\\', '/').Split('/'), Does.Not.Contain(".."),
                    $"Source '{source}' must not contain '..' segments");
            }

            if (!string.IsNullOrEmpty(instruction.Destination))
            {
                Assert.That(DraftInstructionService.IsSandboxedPath(instruction.Destination), Is.True,
                    $"Destination '{instruction.Destination}' must be confined to a placeholder root without '..' / rooted escapes");
                Assert.That(instruction.Destination, Does.Not.Contain("<<gameDirectory>>"), "Legacy placeholder must be normalized away");
                Assert.That(instruction.Destination.Replace('\\', '/').Split('/'), Does.Not.Contain(".."),
                    $"Destination '{instruction.Destination}' must not contain '..' segments");
            }
        }

        #region NL parser / draft service

        [Test]
        public void DraftInstructions_MoveFoldersProse_ProducesSandboxedMoveInstructions()
        {
            ModComponent component = CreateComponent(MoveFoldersProse);

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.Multiple(() =>
            {
                Assert.That(results, Has.Count.EqualTo(1), "Component with parseable prose should receive drafts");
                Assert.That(component.Instructions, Is.Not.Empty, "Draft instructions should be attached to the component");
                Assert.That(results[0].DraftInstructionCount, Is.EqualTo(component.Instructions.Count));
                Assert.That(results[0].UnparsedGaps, Is.Empty, "Prose that fully matches known patterns has zero gaps");
            });

            Assert.That(component.Instructions.Any(i => i.Action == Instruction.ActionType.Move), Is.True,
                "Prose describing a folder move should draft at least one Move instruction");

            foreach (Instruction instruction in component.Instructions)
            {
                AssertInstructionIsSandboxed(instruction);
                Assert.That(instruction.GetParentComponent(), Is.SameAs(component));
            }
        }

        [Test]
        public void DraftInstructions_PatcherProse_ProducesSandboxedDrafts()
        {
            ModComponent component = CreateComponent(PatcherProse);

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(component.Instructions, Is.Not.Empty);

            foreach (Instruction instruction in component.Instructions)
            {
                AssertInstructionIsSandboxed(instruction);
            }
        }

        [Test]
        public void DraftInstructions_DeleteBeforeMoveProse_ProducesSandboxedDelete()
        {
            ModComponent component = CreateComponent(DeleteBeforeMoveProse);

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.That(results, Has.Count.EqualTo(1), "Delete-before-move prose from mod-builds should draft");
            Assert.That(component.Instructions.Any(i => i.Action == Instruction.ActionType.Delete), Is.True,
                "Prose that deletes files before moving should draft a Delete instruction");

            foreach (Instruction instruction in component.Instructions)
            {
                AssertInstructionIsSandboxed(instruction);
            }
        }

        [Test]
        public void DraftInstructions_BeforeMovingDeleteProse_ProducesSandboxedDelete()
        {
            ModComponent component = CreateComponent(BeforeMovingDeleteProse);

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(component.Instructions.Any(i => i.Action == Instruction.ActionType.Delete), Is.True);

            foreach (Instruction instruction in component.Instructions)
            {
                AssertInstructionIsSandboxed(instruction);
            }
        }

        [Test]
        public void DraftInstructions_RerunPatcherProse_ProducesSandboxedPatcher()
        {
            ModComponent component = CreateComponent(RerunPatcherProse);

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(component.Instructions.Any(i => i.Action == Instruction.ActionType.Patcher), Is.True,
                "Re-run patcher / compatibility option prose should draft a Patcher instruction");

            foreach (Instruction instruction in component.Instructions)
            {
                AssertInstructionIsSandboxed(instruction);
            }
        }

        [Test]
        public void DraftInstructions_MoveExceptProse_ProducesSandboxedMove()
        {
            ModComponent component = CreateComponent(MoveExceptProse);

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(component.Instructions.Any(i => i.Action == Instruction.ActionType.Move), Is.True);

            foreach (Instruction instruction in component.Instructions)
            {
                AssertInstructionIsSandboxed(instruction);
            }
        }

        [Test]
        public void DraftInstructions_ComponentWithExistingInstructions_IsNeverTouched()
        {
            ModComponent component = CreateComponent(MoveFoldersProse);
            var authored = new Instruction { Action = Instruction.ActionType.Patcher };
            authored.SetParentComponent(component);
            component.Instructions.Add(authored);

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.Multiple(() =>
            {
                Assert.That(results, Is.Empty, "Authored instructions must never be overwritten or extended by drafts");
                Assert.That(component.Instructions, Has.Count.EqualTo(1));
                Assert.That(component.Instructions[0], Is.SameAs(authored));
            });
        }

        [Test]
        public void DraftInstructions_K2CPHDVisasNestedConditional_DecomposesIntoDistinctInstructions()
        {
            // AE8: an unconditional deletion followed by "if also using HD Visas, additionally delete
            // these three more" must decompose into two distinct instructions - the conditional half must
            // never merge into the unconditional one, nor be silently dropped from it.
            ModComponent component = CreateComponent(
                "Delete portraits001.tga and portraits002.tga before moving to override, " +
                "and if also using HD Visas, additionally delete hdvisas01.tga, hdvisas02.tga, and hdvisas03.tga.");

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.Multiple(() =>
            {
                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].DraftInstructionCount, Is.EqualTo(2),
                    "The unconditional and HD-Visas-conditional deletions must be captured as distinct instructions");
                Assert.That(component.Instructions.Count(i => i.Action == Instruction.ActionType.Delete), Is.EqualTo(2));
                Assert.That(results[0].HasConditionalDrafts, Is.True,
                    "The HD-Visas-conditional deletion must be flagged as conditional, not applied unconditionally");
                Assert.That(results[0].ConditionalDrafts.Count, Is.EqualTo(1));
                Assert.That(results[0].ConditionalDrafts[0], Does.Contain("HD Visas"));
                Assert.That(component.InstallationWarning, Does.Contain("HD Visas"),
                    "The conditional note should be surfaced on the component for review");
            });

            foreach (Instruction instruction in component.Instructions)
            {
                AssertInstructionIsSandboxed(instruction);
            }
        }

        [Test]
        public void DraftInstructions_NestedConditional_SemicolonVariant_StillTagsCondition()
        {
            // Same AE8 shape, but the guide phrases the conditional clause after a semicolon rather than
            // "and if" - the existing semicolon splitter already separates the two clauses, so this
            // guards the "bare" conditional-clause form (no unconditional prefix in the same fragment).
            ModComponent component = CreateComponent(
                "Delete these files before moving to override; " +
                "if also using HD Visas, additionally delete these three more files.");

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.Multiple(() =>
            {
                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].DraftInstructionCount, Is.EqualTo(2));
                Assert.That(results[0].HasConditionalDrafts, Is.True);
                Assert.That(results[0].ConditionalDrafts[0], Does.Contain("HD Visas"));
            });
        }

        [Test]
        public void DraftInstructions_NonConditionalProse_UnaffectedByConditionalClauseSplitter()
        {
            // Regression guard: ordinary prose with no "if also using" clause must parse exactly as before.
            ModComponent component = CreateComponent(MoveFoldersProse);

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.Multiple(() =>
            {
                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].HasConditionalDrafts, Is.False);
                Assert.That(component.Instructions.Any(i => i.Action == Instruction.ActionType.Move), Is.True);
            });
        }

        [Test]
        public void DraftInstructions_RedrobCleanlistWithNamedFile_DraftsCleanListInstruction()
        {
            // AE7: a redrob-style per-mod deletion driven by cleanlist_k1.txt cannot be enumerated from the
            // guide text. Before this fix the parser silently resolved it to a nonsensical fixed file list
            // (e.g. deleting a literal path made of prose words) instead of never guessing.
            ModComponent component = CreateComponent(
                "Delete the files listed in cleanlist_k1.txt for your installed mods before running the patcher.");

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.Multiple(() =>
            {
                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].DraftInstructionCount, Is.EqualTo(1));
                Assert.That(results[0].UnparsedGaps, Is.Empty);
                Assert.That(component.Instructions, Has.Count.EqualTo(1));
                Assert.That(component.Instructions[0].Action, Is.EqualTo(Instruction.ActionType.CleanList));
                Assert.That(component.Instructions[0].Source.Any(s => s.Contains("cleanlist_k1.txt")), Is.True);
            });

            AssertInstructionIsSandboxed(component.Instructions[0]);
        }

        [Test]
        public void DraftInstructions_RedrobCleanlistWithoutNamedFile_SurfacesAsGapNotWrongFixedList()
        {
            // AE7 edge case: a bare "cleanlist" mention with no nameable file must never be resolved to a
            // fabricated fixed file list - it surfaces as a reviewable gap instead.
            ModComponent component = CreateComponent(
                "Before installing, delete any files listed in the cleanlist that correspond to mods you already have installed.");

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.Multiple(() =>
            {
                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].DraftInstructionCount, Is.EqualTo(0));
                Assert.That(results[0].UnparsedGaps, Is.Not.Empty);
                Assert.That(component.Instructions, Is.Empty,
                    "Must never fabricate a fixed Delete file list from an unnameable cleanlist reference");
            });
        }

        [Test]
        public void DraftInstructions_LiteralFileDeletion_UnaffectedByCleanlistDetection()
        {
            // Regression guard: an ordinary, literal file-deletion entry (no cleanlist reference) drafts
            // exactly as before.
            ModComponent component = CreateComponent(DeleteBeforeMoveProse);

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(component.Instructions.Any(i => i.Action == Instruction.ActionType.Delete), Is.True);
            Assert.That(component.Instructions.Any(i => i.Action == Instruction.ActionType.CleanList), Is.False);
        }

        [Test]
        public void DraftInstructions_HQBlasters_DeleteBeforePatcherRun_PreservesFullFilenameWithExtension()
        {
            // AE6: the HQ Blasters sequence deletes keblastore.utm to force an intentional single
            // TSLPatcher error before running the patcher. Before this fix, the Delete patterns' shared
            // "end of clause" boundary used a bare '.' that also matched the extension separator inside
            // the filename itself, truncating the capture to "keblastore" and silently dropping ".utm".
            ModComponent component = CreateComponent(
                "Delete keblastore.utm from the TSLPatchdata folder before running the patcher.");

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.Multiple(() =>
            {
                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(component.Instructions, Has.Count.EqualTo(1));
                Assert.That(component.Instructions[0].Action, Is.EqualTo(Instruction.ActionType.Delete));
                Assert.That(component.Instructions[0].Source.Any(s => s.EndsWith("keblastore.utm", StringComparison.OrdinalIgnoreCase)),
                    Is.True, "The full filename including its extension must survive, not be truncated at the extension separator");
            });
        }

        [Test]
        public void DraftInstructions_HQBlasters_MultiFileDeleteList_PreservesAllExtensions()
        {
            // AE6's post-patcher cleanup deletes several more files by name; every file's extension must
            // survive, not just the first item's (the same extension-separator boundary bug affected list
            // patterns too, truncating mid-list as soon as it hit any filename's own '.').
            ModComponent component = CreateComponent(
                "Delete LSI_win01.tpc and LSI_box01.tpc before moving to override.");

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.Multiple(() =>
            {
                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(component.Instructions, Has.Count.EqualTo(1));
                Assert.That(component.Instructions[0].Source, Has.Count.EqualTo(2));
                Assert.That(component.Instructions[0].Source.Any(s => s.EndsWith("LSI_win01.tpc", StringComparison.OrdinalIgnoreCase)), Is.True);
                Assert.That(component.Instructions[0].Source.Any(s => s.EndsWith("LSI_box01.tpc", StringComparison.OrdinalIgnoreCase)), Is.True);
            });
        }

        [Test]
        public void DraftInstructions_HQBlasters_WildcardPrefixRename_SurfacesAsGapNotWrongRename()
        {
            // AE6's rename step ("rename w_ionrfl_04.* files to w_ionrfl_004.*") is a bulk prefix rename,
            // not an enumerable exact-file pair. No pattern models this yet; it must surface as an explicit
            // reviewable gap rather than draft a wrong single-file rename or silently disappear.
            ModComponent component = CreateComponent("Rename all w_ionrfl_04.* files to w_ionrfl_004.*.");

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.Multiple(() =>
            {
                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].DraftInstructionCount, Is.EqualTo(0));
                Assert.That(results[0].UnparsedGaps, Is.Not.Empty,
                    "An undraftable bulk-rename step must surface as a gap, never silently dropped or misapplied");
                Assert.That(component.Instructions.Any(i => i.Action == Instruction.ActionType.Rename), Is.False);
            });
        }

        [Test]
        public void DraftInstructions_UnparseableProse_DegradesGracefullyToNoDrafts()
        {
            // Pure commentary (no action verb): recognized as informational, not a gap.
            ModComponent component = CreateComponent("A fan favorite retexture bundle. Many enjoy this excellent work.");

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.Multiple(() =>
            {
                Assert.That(results, Has.Count.EqualTo(1),
                    "A component with Directions prose should still appear in results even when nothing drafts");
                Assert.That(results[0].DraftInstructionCount, Is.EqualTo(0));
                Assert.That(results[0].UnparsedGaps, Is.Empty, "Pure commentary is not an unparsed gap");
                Assert.That(component.Instructions, Is.Empty);
            });
        }

        [Test]
        public void DraftInstructions_ActionableButUnmatchedProse_SurfacesAsUnparsedGap()
        {
            // Contains an action verb ("select") but is phrased in a way no pattern in the list matches
            // (no file/folder/version noun for the "select" patterns to anchor on).
            ModComponent component = CreateComponent("Select whichever seems best for your taste, honestly.");

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.Multiple(() =>
            {
                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].DraftInstructionCount, Is.EqualTo(0));
                Assert.That(results[0].UnparsedGaps, Is.Not.Empty,
                    "Actionable prose that matches no pattern must surface as a reviewable gap, not silently drop");
                Assert.That(results[0].HasUnparsedGaps, Is.True);
                Assert.That(component.Instructions, Is.Empty);
            });
        }

        [Test]
        public void DraftInstructions_EmptyDirections_ProducesNoDrafts()
        {
            ModComponent component = CreateComponent(string.Empty);

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.That(results, Is.Empty);
            Assert.That(component.Instructions, Is.Empty);
        }

        [Test]
        public void TrySanitizeInstruction_NormalizesLegacyGameDirectoryPlaceholder()
        {
            var instruction = new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string> { @"<<modDirectory>>\textures\*" },
                Destination = @"<<gameDirectory>>\Override",
            };

            bool kept = DraftInstructionService.TrySanitizeInstruction(instruction);

            Assert.Multiple(() =>
            {
                Assert.That(kept, Is.True);
                Assert.That(instruction.Destination, Is.EqualTo(@"<<kotorDirectory>>\Override"));
                Assert.That(instruction.Source[0], Is.EqualTo(@"<<modDirectory>>\textures\*"));
            });
        }

        [Test]
        public void TrySanitizeInstruction_RejectsNonSandboxedPaths()
        {
            var absoluteSource = new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string> { @"C:\Windows\System32\evil.dll" },
                Destination = @"<<kotorDirectory>>\Override",
            };

            var absoluteDestination = new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string> { @"<<modDirectory>>\file.tga" },
                Destination = @"C:\Windows\System32",
            };

            Assert.Multiple(() =>
            {
                Assert.That(DraftInstructionService.TrySanitizeInstruction(absoluteSource), Is.False,
                    "A Move whose only source escapes the sandbox must be dropped");
                Assert.That(DraftInstructionService.TrySanitizeInstruction(absoluteDestination), Is.False,
                    "A Move whose destination escapes the sandbox must be dropped");
            });
        }

        [Test]
        public void TrySanitizeInstruction_RejectsParentDirectoryTraversalAfterPlaceholder()
        {
            var traversalSource = new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string> { "<<modDirectory>>/../outside" },
                Destination = "<<kotorDirectory>>/Override",
            };

            var traversalDestination = new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string> { @"<<modDirectory>>\textures\*" },
                Destination = @"<<kotorDirectory>>\..\Windows",
            };

            var gluedTraversal = new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string> { "<<modDirectory>>../outside" },
                Destination = "<<kotorDirectory>>/Override",
            };

            var driveAfterPlaceholder = new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string> { @"<<modDirectory>>\C:\Windows\evil.dll" },
                Destination = @"<<kotorDirectory>>\Override",
            };

            Assert.Multiple(() =>
            {
                Assert.That(DraftInstructionService.IsSandboxedPath("<<modDirectory>>/../outside"), Is.False);
                Assert.That(DraftInstructionService.IsSandboxedPath(@"<<kotorDirectory>>\..\Windows"), Is.False);
                Assert.That(DraftInstructionService.IsSandboxedPath("<<modDirectory>>../outside"), Is.False);
                Assert.That(DraftInstructionService.IsSandboxedPath(@"<<modDirectory>>\C:\Windows\evil.dll"), Is.False);
                Assert.That(DraftInstructionService.IsSandboxedPath("<<modDirectory>>/textures/*"), Is.True);
                Assert.That(DraftInstructionService.TrySanitizeInstruction(traversalSource), Is.False,
                    "Source with '..' after placeholder must be dropped");
                Assert.That(DraftInstructionService.TrySanitizeInstruction(traversalDestination), Is.False,
                    "Destination with '..' after placeholder must be dropped");
                Assert.That(DraftInstructionService.TrySanitizeInstruction(gluedTraversal), Is.False,
                    "Missing separator before '..' must be dropped");
                Assert.That(DraftInstructionService.TrySanitizeInstruction(driveAfterPlaceholder), Is.False,
                    "Drive letter segment after placeholder must be dropped");
            });
        }

        [Test]
        public void DraftInstructions_TraversalProse_DoesNotKeepEscapingPaths()
        {
            ModComponent escapeComponent = CreateComponent(
                "Move everything from <<modDirectory>>/../outside to your Override.");
            ModComponent bareDotDot = CreateComponent(
                "Move .. to override.");

            IReadOnlyList<DraftInstructionResult> escapeResults =
                DraftInstructionService.GenerateDraftInstructions(new[] { escapeComponent });
            IReadOnlyList<DraftInstructionResult> bareResults =
                DraftInstructionService.GenerateDraftInstructions(new[] { bareDotDot });

            Assert.Multiple(() =>
            {
                foreach (Instruction instruction in escapeComponent.Instructions)
                {
                    AssertInstructionIsSandboxed(instruction);
                }

                foreach (Instruction instruction in bareDotDot.Instructions)
                {
                    AssertInstructionIsSandboxed(instruction);
                }

                if (escapeResults.Count > 0)
                {
                    Assert.That(escapeComponent.InstallationWarning, Does.Contain(DraftInstructionService.ReviewFlagMessage));
                }

                if (bareResults.Count > 0)
                {
                    Assert.That(bareDotDot.InstallationWarning, Does.Contain(DraftInstructionService.ReviewFlagMessage));
                }
            });
        }

        [Test]
        public void DraftInstructions_SuccessfulDraft_AppliesReviewFlagMessage()
        {
            ModComponent component = CreateComponent(MoveFoldersProse);

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(new[] { component });

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(component.InstallationWarning, Is.EqualTo(DraftInstructionService.ReviewFlagMessage));
        }

        #endregion

        #region Paste cascade format sniffing

        [Test]
        public void DetectFormatFromContent_TomlContent_ReturnsToml()
        {
            const string toml = @"[[thisMod]]
Guid = ""a1b2c3d4-e5f6-7890-abcd-ef1234567890""
Name = ""Paste Cascade Toml Mod""
";

            Assert.That(ModComponentSerializationService.DetectFormatFromContent(toml), Is.EqualTo("toml"));
        }

        [Test]
        public void DetectFormatFromContent_MarkdownGuide_ReturnsMarkdown()
        {
            Assert.That(ModComponentSerializationService.DetectFormatFromContent(MarkdownGuide), Is.EqualTo("markdown"));
        }

        [Test]
        public void DetectFormatFromContent_UnrecognizedProse_ReturnsNull()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ModComponentSerializationService.DetectFormatFromContent("just some random prose about nothing in particular"), Is.Null);
                Assert.That(ModComponentSerializationService.DetectFormatFromContent("   \r\n\t  "), Is.Null);
            });
        }

        [Test]
        public void PasteCascade_MarkdownGuideString_DeserializesComponentWithDirections()
        {
            IReadOnlyList<ModComponent> components = ModComponentSerializationService.DeserializeModComponentFromString(MarkdownGuide);

            Assert.That(components, Has.Count.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(components[0].Name, Is.EqualTo("Guide Ingestion Test Mod"));
                Assert.That(components[0].Directions, Does.Contain("Move everything from the Straight Fixes"));
            });
        }

        #endregion

        #region Real guide (mod-builds)

        // U9 measurement finding: MarkdownParser's per-field regex scanning scales superlinearly
        // (roughly cubic) with document size - confirmed pre-existing on the pre-branch baseline, not
        // introduced by this plan's changes. A full guide (~150KB, e.g. k1/full.md) takes on the order
        // of tens of minutes to parse, which is impractical for default CI/local runs. Tagged Slow
        // (excluded by ModSync.Tests.runsettings' default TestCategory!=Slow filter) until the
        // underlying scan-scoping performance issue gets its own dedicated fix.
        [Category("Slow")]
        [CancelAfter(300_000)]
        [TestCase("k1", "full.md")]
        [TestCase("k2", "full.md")]
        [TestCase("k1", "spoiler-free.md")]
        [TestCase("k2", "spoiler-free.md")]
        [TestCase("k1", "full_mobile.md")]
        public void RealGuide_ModBuildsMarkdown_DraftedInstructionsAreAllSandboxed(string gameFolder, string guideFile)
        {
            string repoRoot = ResolveRepoRoot();
            string markdownPath = Path.Combine(repoRoot, "mod-builds", "content", gameFolder, guideFile);
            if (!File.Exists(markdownPath))
            {
                Assert.Ignore($"mod-builds guide not found: {markdownPath}");
            }

            List<ModComponent> components;
            try
            {
                components = FileLoadingService.LoadFromFile(markdownPath).ToList();
            }
            catch (InvalidDataException ex)
            {
                Assert.Ignore($"Guide is not a component markdown list ({gameFolder}/{guideFile}): {ex.Message}");
                return;
            }

            Assert.That(components, Is.Not.Empty, $"Expected components from {gameFolder}/{guideFile}");

            IReadOnlyList<DraftInstructionResult> results = DraftInstructionService.GenerateDraftInstructions(components);

            Assert.That(results, Is.Not.Empty, $"Real guide prose ({gameFolder}/{guideFile}) should draft instructions for at least one component");
            Assert.That(results.Any(r => r.DraftInstructionCount > 0), Is.True,
                $"Real guide prose ({gameFolder}/{guideFile}) should draft instructions for at least one component");

            foreach (DraftInstructionResult result in results)
            {
                foreach (Instruction instruction in result.Component.Instructions)
                {
                    AssertInstructionIsSandboxed(instruction);
                }
            }
        }

        #endregion

        #region K2 Full site fixture (neocities plain-field markdown)

        [Test]
        public void K2FullGuideFixture_PlainFieldMarkdown_ParsesManyComponents()
        {
            string fixturePath = Path.Combine(ResolveRepoRoot(), "src", "ModSync.Tests", "Fixtures", "k2_full_guide.md");
            Assert.That(File.Exists(fixturePath), Is.True, $"Expected fixture at {fixturePath}");

            string markdown = File.ReadAllText(fixturePath);
            IReadOnlyList<ModComponent> components = ModComponentSerializationService.DeserializeModComponentFromString(markdown, "markdown");

            // Fixture has ~124 plain "Name:" lines and ~169 ### headings; Mod List sections must not collapse to 1.
            Assert.That(components.Count, Is.GreaterThanOrEqualTo(100),
                $"Expected >=100 components from site-scraped K2 Full guide, got {components.Count}");

            Assert.That(components.Any(c => c.Name.IndexOf("Silent Sion", StringComparison.OrdinalIgnoreCase) >= 0), Is.True);
            Assert.That(components.Any(c =>
                !string.IsNullOrWhiteSpace(c.Directions)
                && c.Directions.IndexOf("153sion.dlg", StringComparison.OrdinalIgnoreCase) >= 0), Is.True,
                "Plain 'Installation Instructions' blocks should populate Directions");

            ModComponent withAuthor = components.FirstOrDefault(c =>
                !string.IsNullOrWhiteSpace(c.Author)
                && c.Name.IndexOf("TSLRCM", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.That(withAuthor, Is.Not.Null, "Plain Author: fields should populate Author");
        }

        [Test]
        public void K2FullGuideFixture_ParseDirections_DraftsSandboxedInstructions()
        {
            string fixturePath = Path.Combine(ResolveRepoRoot(), "src", "ModSync.Tests", "Fixtures", "k2_full_guide.md");
            Assert.That(File.Exists(fixturePath), Is.True);

            string markdown = File.ReadAllText(fixturePath);
            GuideIngestResult ingested = GuideIngestService.Instance.IngestFromText(markdown, formatHint: "markdown", parseDirections: true);

            Assert.That(ingested.Components.Count, Is.GreaterThanOrEqualTo(100));
            Assert.That(ingested.DraftResults, Is.Not.Empty, "NLP should draft instructions for at least one K2 Full component");

            foreach (DraftInstructionResult draft in ingested.DraftResults)
            {
                // DraftResults now includes components whose Directions produced zero drafts (U2 gap
                // reporting) - only components that actually drafted instructions get the review flag.
                if (draft.DraftInstructionCount > 0)
                {
                    Assert.That(draft.Component.InstallationWarning, Does.Contain(DraftInstructionService.ReviewFlagMessage));
                }

                foreach (Instruction instruction in draft.Component.Instructions)
                {
                    AssertInstructionIsSandboxed(instruction);
                }
            }

            Assert.That(
                ingested.DraftResults.Any(d => d.Component.Instructions.Any(i =>
                    i.Action == Instruction.ActionType.Move
                    || i.Action == Instruction.ActionType.Delete
                    || i.Action == Instruction.ActionType.Extract
                    || i.Action == Instruction.ActionType.Patcher)),
                Is.True,
                "Expected Move/Delete/Extract/Patcher drafts from K2 Full installation prose");
        }

        [Test]
        public void ModBuildsK2Full_BoldFieldMarkdown_StillParsesHighComponentCount()
        {
            string repoRoot = ResolveRepoRoot();
            string markdownPath = Path.Combine(repoRoot, "mod-builds", "content", "k2", "full.md");
            if (!File.Exists(markdownPath))
            {
                // Sibling checkout used by local agents when worktree lacks mod-builds submodule content.
                string sibling = Path.GetFullPath(Path.Combine(repoRoot, "..", "ModSync", "mod-builds", "content", "k2", "full.md"));
                if (File.Exists(sibling))
                {
                    markdownPath = sibling;
                }
                else
                {
                    Assert.Ignore($"mod-builds K2 full.md not found at {markdownPath}");
                }
            }

            IReadOnlyList<ModComponent> components =
                ModComponentSerializationService.DeserializeModComponentFromString(File.ReadAllText(markdownPath), "markdown");

            Assert.That(components.Count, Is.GreaterThanOrEqualTo(100),
                $"Bold **Name:** path must keep working for mod-builds K2 full (got {components.Count})");
        }

        #endregion

        #region Guide ingest port (IGuideIngestService / GuideIngestResult content sections)

        [Test]
        public void IngestFromText_MarkdownWithPreambleEpilogueWidescreen_PopulatesPortContentSections()
        {
            // U6: GuideIngestService now calls MarkdownParser directly for markdown content instead of the
            // generic deserializer, so preamble/epilogue/widescreen/trace survive onto GuideIngestResult
            // instead of being dropped at the port boundary.
            const string markdown = @"This is the preamble text before the mod list.

## Mod List

### First Mod
**Name:** First Mod
**Author:** TestAuthor
**Description:** A basic mod.

___

## Optional Widescreen

This section describes widescreen-only fixes.

### Widescreen Mod
**Name:** Widescreen Mod
**Author:** TestAuthor
**Description:** A widescreen-only fix.

___

## Misc. Basegame Issues & Fixes

This is epilogue content after the widescreen section.
";

            GuideIngestResult result = GuideIngestService.Instance.IngestFromText(markdown, formatHint: null, parseDirections: false);

            Assert.Multiple(() =>
            {
                Assert.That(result.Components, Is.Not.Empty);
                Assert.That(result.DetectedFormat, Is.EqualTo("markdown"));
                Assert.That(result.PreambleContent, Does.Contain("preamble text before the mod list"));
                Assert.That(result.EpilogueContent, Does.Contain("Misc. Basegame Issues"));
                Assert.That(result.WidescreenWarningContent, Does.Contain("Optional Widescreen"));
                Assert.That(result.Trace, Is.Not.Null, "Markdown ingest should populate a parse trace");
            });
        }

        [Test]
        public void IngestFromText_MarkdownWithUndraftableDirections_SurfacesGapsOnPortResult()
        {
            // Integration: U2's unparsed-gap reporting must surface through the port's DraftResults too,
            // not only when calling MarkdownParser/DraftInstructionService directly.
            const string markdown = @"### Gap Mod
**Name:** Gap Mod
**Author:** TestAuthor
**Description:** A mod whose directions cannot be fully drafted.
**Installation Instructions:** Select whichever seems best for your taste, honestly.

___";

            GuideIngestResult result = GuideIngestService.Instance.IngestFromText(markdown, formatHint: "markdown", parseDirections: true);

            Assert.Multiple(() =>
            {
                Assert.That(result.Components, Has.Count.EqualTo(1));
                Assert.That(result.DraftResults, Has.Count.EqualTo(1));
                Assert.That(result.DraftResults[0].UnparsedGaps, Is.Not.Empty,
                    "The port must surface unparsed gaps, not only direct MarkdownParser/DraftInstructionService callers");
            });
        }

        [Test]
        public void IngestFromText_NonMarkdownFormat_ContentSectionsRemainNullAndComponentsUnaffected()
        {
            // Edge case: the markdown-specific rewiring must not regress other formats - TOML ingest
            // continues through the generic deserializer exactly as before, with no content sections
            // (those have no TOML equivalent).
            var component = new ModComponent
            {
                Guid = Guid.NewGuid(),
                Name = "TOML Round Trip Mod",
                Author = "TestAuthor",
                Description = "A TOML-only component.",
            };

            string toml = ModComponentSerializationService.SerializeModComponentAsTomlString(new[] { component });

            GuideIngestResult result = GuideIngestService.Instance.IngestFromText(toml, formatHint: "toml", parseDirections: false);

            Assert.Multiple(() =>
            {
                Assert.That(result.Components, Has.Count.EqualTo(1));
                Assert.That(result.Components[0].Name, Is.EqualTo("TOML Round Trip Mod"));
                Assert.That(result.DetectedFormat, Is.EqualTo("toml"));
                Assert.That(result.PreambleContent, Is.Null);
                Assert.That(result.EpilogueContent, Is.Null);
                Assert.That(result.WidescreenWarningContent, Is.Null);
                Assert.That(result.AspyrExclusiveWarningContent, Is.Null);
                Assert.That(result.Trace, Is.Null, "Non-markdown formats never go through MarkdownParser, so there is no trace");
            });
        }

        #endregion

        #region Guide emission (GenerateModDocumentation)

        [Test]
        public void GenerateModDocumentation_AfterDraftingGuide_RoundTripsComponentNameAndDirections()
        {
            IReadOnlyList<ModComponent> components = ModComponentSerializationService.DeserializeModComponentFromString(MarkdownGuide);
            Assert.That(components, Has.Count.EqualTo(1));

            DraftInstructionService.GenerateDraftInstructions(components);

            string emitted = ModComponentSerializationService.GenerateModDocumentation(components.ToList());
            Assert.That(emitted, Does.Contain("Guide Ingestion Test Mod"));
            Assert.That(emitted, Does.Contain("Move everything from the Straight Fixes"));

            IReadOnlyList<ModComponent> reparsed = ModComponentSerializationService.DeserializeModComponentFromString(emitted);
            Assert.That(reparsed, Has.Count.EqualTo(1));
            Assert.That(reparsed[0].Name, Is.EqualTo("Guide Ingestion Test Mod"));
            Assert.That(reparsed[0].Directions, Does.Contain("Move everything from the Straight Fixes"));
        }

        #endregion

        #region CLI parity (convert --stdin --parse-directions)

        [Test]
        public void CliConvert_StdinWithParseDirections_EmitsReviewFlaggedTomlWithDraftInstructions()
        {
            string outputToml = Path.Combine(_testDirectory, "ingested.toml");

            TextReader previousIn = Console.In;
            try
            {
                Console.SetIn(new StringReader(MarkdownGuide));

                int exitCode = ModBuildConverter.Run(new[]
                {
                    "convert",
                    "--stdin",
                    "--parse-directions",
                    "-f", "toml",
                    "-o", outputToml,
                    "--plaintext",
                });

                Assert.That(exitCode, Is.EqualTo(0), "convert --stdin --parse-directions should succeed");
            }
            finally
            {
                Console.SetIn(previousIn);
            }

            Assert.That(File.Exists(outputToml), Is.True);
            string tomlOutput = File.ReadAllText(outputToml);

            Assert.Multiple(() =>
            {
                Assert.That(tomlOutput, Does.Contain("# VALIDATION ISSUES:"), "Drafted components must be flagged for review in the output");
                Assert.That(tomlOutput, Does.Contain(DraftInstructionService.ReviewFlagMessage));
            });

            var reloaded = ModComponentSerializationService
                .DeserializeModComponentFromString(tomlOutput, "toml")
                .ToList();

            Assert.That(reloaded, Has.Count.EqualTo(1));
            Assert.That(reloaded[0].Instructions, Is.Not.Empty, "Drafted instructions should survive TOML round-trip");

            foreach (Instruction instruction in reloaded[0].Instructions)
            {
                AssertInstructionIsSandboxed(instruction);
            }
        }

        [Test]
        public void CliConvert_FileInputWithParseDirections_EmitsReviewFlaggedToml()
        {
            string inputMd = Path.Combine(_testDirectory, "guide.md");
            string outputToml = Path.Combine(_testDirectory, "from-file.toml");
            File.WriteAllText(inputMd, MarkdownGuide);

            int exitCode = ModBuildConverter.Run(new[]
            {
                "convert",
                "--input", inputMd,
                "--parse-directions",
                "-f", "toml",
                "-o", outputToml,
                "--plaintext",
            });

            Assert.That(exitCode, Is.EqualTo(0), "convert -i guide.md --parse-directions should succeed");
            Assert.That(File.Exists(outputToml), Is.True);
            string tomlOutput = File.ReadAllText(outputToml);
            Assert.That(tomlOutput, Does.Contain(DraftInstructionService.ReviewFlagMessage));

            var reloaded = ModComponentSerializationService
                .DeserializeModComponentFromString(tomlOutput, "toml")
                .ToList();
            Assert.That(reloaded[0].Instructions, Is.Not.Empty);
            foreach (Instruction instruction in reloaded[0].Instructions)
            {
                AssertInstructionIsSandboxed(instruction);
            }
        }

        [Test]
        public void CliConvert_StdinCombinedWithInput_Fails()
        {
            TextReader previousIn = Console.In;
            try
            {
                Console.SetIn(new StringReader(MarkdownGuide));

                int exitCode = ModBuildConverter.Run(new[]
                {
                    "convert",
                    "--stdin",
                    "--input", Path.Combine(_testDirectory, "does-not-matter.toml"),
                    "--plaintext",
                });

                Assert.That(exitCode, Is.EqualTo(1), "--stdin combined with --input should be rejected");
            }
            finally
            {
                Console.SetIn(previousIn);
            }
        }

        #endregion

        private static string ResolveRepoRoot()
        {
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..")),
                Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..")),
                Path.GetFullPath(Environment.CurrentDirectory),
            };

            foreach (string candidate in candidates.Distinct(StringComparer.Ordinal))
            {
                if (File.Exists(Path.Combine(candidate, "ModSync.sln")))
                {
                    return candidate;
                }
            }

            // Walk up from the test directory (covers git worktrees and nested bin layouts).
            DirectoryInfo dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ModSync.sln")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root containing ModSync.sln");
        }
    }
}
