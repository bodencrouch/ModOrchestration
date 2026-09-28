// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using ModSync.Core;
using ModSync.Core.Services;
using ModSync.Core.Utility;

using NUnit.Framework;

using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace ModSync.Tests
{
    [TestFixture]
    public class AutoInstructionGeneratorTests
    {
        private string _testDirectory;

        [SetUp]
        public void SetUp()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), "ModSync_AutoInstructionTests_" + Guid.NewGuid());
            Directory.CreateDirectory(_testDirectory);

            var mainConfig = new MainConfig();
            mainConfig.sourcePath = new DirectoryInfo(_testDirectory);
            mainConfig.destinationPath = new DirectoryInfo(Path.Combine(_testDirectory, "KOTOR"));
            Directory.CreateDirectory(mainConfig.destinationPath.FullName);
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
            catch
            {

            }
        }

        [Test]
        public void GenerateInstructions_TSLPatcherWithChangesIni_CreatesPatcherInstruction()
        {

            string archivePath = CreateTestArchive("tslpatcher_simple.zip", archive =>
            {

                AddTextFileToArchive(archive, "tslpatchdata/changes.ini", "[Settings]\nLookupGameFolder=1");
                AddTextFileToArchive(archive, "TSLPatcher.exe", "fake exe");
                AddTextFileToArchive(archive, "example.2da", "2DA V2.0");
            });

            var component = new ModComponent { Name = "Test Mod", Guid = Guid.NewGuid() };

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True, "Should successfully generate instructions");
                Assert.That(component.Instructions, Has.Count.EqualTo(3), "Should have Extract + Patcher + Move instructions");
            });

            Assert.Multiple(() =>
            {
                Assert.That(component.Instructions[0].Action, Is.EqualTo(Instruction.ActionType.Extract));
                Assert.That(component.Instructions[0].Source[0], Does.Contain("tslpatcher_simple.zip"));

                Assert.That(component.Instructions[1].Action, Is.EqualTo(Instruction.ActionType.Patcher));
                Assert.That(component.Instructions[2].Action, Is.EqualTo(Instruction.ActionType.Move));
                Assert.That(component.InstallationMethod, Is.EqualTo("Hybrid (TSLPatcher + Loose Files)"));
            });
        }

        [Test]
        public void GenerateInstructions_TSLPatcherWithNamespacesIni_CreatesChooseWithOptions()
        {

            string archivePath = CreateTestArchive("tslpatcher_namespaces.zip", archive =>
            {

                AddTextFileToArchive(archive, "tslpatchdata/namespaces.ini",
                    "[Namespaces]\n1=Option1\n\n[Option1]\nName=First Option\nDescription=First option description\nIniName=changes1.ini");
                AddTextFileToArchive(archive, "tslpatchdata/changes1.ini", "[Settings]\nLookupGameFolder=1");
                AddTextFileToArchive(archive, "TSLPatcher.exe", "fake exe");
            });

            var component = new ModComponent { Name = "Test Mod", Guid = Guid.NewGuid() };

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(component.Instructions, Has.Count.EqualTo(2), "Should have Extract + Choose instructions");
            });
            Assert.Multiple(() =>
            {
                Assert.That(component.Instructions[0].Action, Is.EqualTo(Instruction.ActionType.Extract));
                Assert.That(component.Instructions[1].Action, Is.EqualTo(Instruction.ActionType.Choose));
                Assert.That(component.Options, Has.Count.EqualTo(1), "Should create one option");
            });
            Assert.That(component.Options[0].Instructions, Has.Count.EqualTo(1), "Option should have Patcher instruction");
            Assert.That(component.Options[0].Instructions[0].Action, Is.EqualTo(Instruction.ActionType.Patcher));
        }

        [Test]
        public void GenerateInstructions_HybridWithTSLPatcherAndLooseFiles_TSLPatcherComesFirst()
        {

            string archivePath = CreateTestArchive("hybrid_mod.zip", archive =>
            {

                AddTextFileToArchive(archive, "tslpatchdata/changes.ini", "[Settings]\nLookupGameFolder=1");
                AddTextFileToArchive(archive, "TSLPatcher.exe", "fake exe");

                AddTextFileToArchive(archive, "Override1/appearance.2da", "2DA");
                AddTextFileToArchive(archive, "Override2/dialog.dlg", "DLG");
            });

            var component = new ModComponent { Name = "Hybrid Mod", Guid = Guid.NewGuid() };

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(component.InstallationMethod, Is.EqualTo("Hybrid (TSLPatcher + Loose Files)"));

                Assert.That(component.Instructions[0].Action, Is.EqualTo(Instruction.ActionType.Extract), "First should be Extract");
                Assert.That(component.Instructions[1].Action, Is.EqualTo(Instruction.ActionType.Patcher), "Second should be Patcher (TSLPatcher before Move)");
                Assert.That(component.Instructions.Skip(2), Has.All.Property(nameof(Instruction.Action))
                    .EqualTo(Instruction.ActionType.Move), "Loose payload folders must run after the patcher");
                Assert.That(component.Instructions, Has.Count.EqualTo(4));
            });
        }

        [Test]
        public void GenerateInstructions_RootedHybrid_DoesNotDiscardLooseSiblingOfTslpatchdata()
        {
            string archivePath = CreateTestArchive("rooted_hybrid.zip", archive =>
            {
                AddTextFileToArchive(archive, "Rooted Mod/tslpatchdata/changes.ini", "[Settings]\nLookupGameFolder=1");
                AddTextFileToArchive(archive, "Rooted Mod/Install.exe", "fake exe");
                AddTextFileToArchive(archive, "Rooted Mod/OPTIONAL/extra.ncs", "NCS");
            });

            var component = new ModComponent { Name = "Rooted Hybrid", Guid = Guid.NewGuid() };

            Assert.That(AutoInstructionGenerator.GenerateInstructions(component, archivePath), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(component.InstallationMethod, Is.EqualTo("Hybrid (TSLPatcher + Loose Files)"));
                Assert.That(component.Instructions, Has.Some.Property(nameof(Instruction.Action))
                    .EqualTo(Instruction.ActionType.Patcher));
                Assert.That(component.Instructions, Has.Some.Property(nameof(Instruction.Action))
                    .EqualTo(Instruction.ActionType.Move));
                Assert.That(component.Instructions.Single(i => i.Action == Instruction.ActionType.Move).Source[0],
                    Does.Contain("OPTIONAL"));
            });
        }

        [Test]
        public void GenerateInstructions_RootedNamespaces_CreatesSelectedPatcherOption()
        {
            string archivePath = CreateTestArchive("rooted_namespaces.zip", archive =>
            {
                AddTextFileToArchive(archive, "Rooted Mod/tslpatchdata/namespaces.ini",
                    "[Namespaces]\n1=Main\n\n[Main]\nName=Main Installation\nIniName=changes.ini");
                AddTextFileToArchive(archive, "Rooted Mod/tslpatchdata/changes.ini", "[Settings]\nLookupGameFolder=1");
                AddTextFileToArchive(archive, "Rooted Mod/Install.exe", "fake exe");
            });

            var component = new ModComponent { Name = "Rooted Namespaces", Guid = Guid.NewGuid() };

            Assert.That(AutoInstructionGenerator.GenerateInstructions(component, archivePath), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(component.Instructions, Has.Some.Property(nameof(Instruction.Action))
                    .EqualTo(Instruction.ActionType.Choose));
                Assert.That(component.Options, Has.Count.EqualTo(1));
                Assert.That(component.Options[0].IsSelected, Is.True);
                Assert.That(component.Options[0].Instructions, Has.Some.Property(nameof(Instruction.Action))
                    .EqualTo(Instruction.ActionType.Patcher));
            });
        }

        [Test]
        public void GenerateInstructions_MultipleTslPatchDataTrees_BindsPatcherToDirectoryOwningSelectedOption()
        {
            // Reproduces an archive shape where two independent tslpatchdata trees each ship their
            // own namespaces.ini (e.g. a mod's "Main Install" plus an unrelated compat-patch
            // subtree for a different mod entirely). The generated Patcher instruction must point
            // at the tslpatchdata tree whose OWN namespaces.ini actually defines the selected
            // option, not whichever tree happens to be discovered last while scanning the archive.
            string archivePath = CreateTestArchive("multi_tslpatchdata_trees.zip", archive =>
            {
                // First subtree, encountered first in the archive: the real, selected mod.
                AddTextFileToArchive(archive, "Main Install/tslpatchdata/namespaces.ini",
                    "[Namespaces]\n1=Primary\n\n[Primary]\nName=Primary Option\n"
                    + "Description=The default installation for the mod.\nIniName=changes.ini");
                AddTextFileToArchive(archive, "Main Install/tslpatchdata/changes.ini", "[Settings]\nLookupGameFolder=1");
                AddTextFileToArchive(archive, "Main Install/TSLPatcher.exe", "fake exe - main");

                // Second, independent subtree encountered later in the archive: an unrelated
                // compat-patch for a different mod, with its own differently-named option(s).
                AddTextFileToArchive(archive, "Patch - Unrelated Compat/tslpatchdata/namespaces.ini",
                    "[Namespaces]\n1=CompatOption\n\n[CompatOption]\nName=Compat Patch Option\n"
                    + "Description=Only if using Unrelated Compat.\nIniName=changescompat.ini");
                AddTextFileToArchive(archive, "Patch - Unrelated Compat/tslpatchdata/changescompat.ini", "[Settings]\nLookupGameFolder=1");
                AddTextFileToArchive(archive, "Patch - Unrelated Compat/TSLPatcher.exe", "fake exe - compat");
            });

            var component = new ModComponent { Name = "Multi Tree Mod", Guid = Guid.NewGuid() };

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);

            Assert.That(result, Is.True);
            Assert.That(component.Options, Has.Count.EqualTo(1),
                "Only the first tslpatchdata tree's namespaces.ini should be read into options - the "
                + "unrelated compat-patch tree's namespaces.ini is never consulted.");

            Option option = component.Options[0];
            Instruction patcher = option.Instructions.Single(i => i.Action == Instruction.ActionType.Patcher);

            Assert.Multiple(() =>
            {
                Assert.That(patcher.Source[0], Does.Contain("Main Install"),
                    "Patcher instruction must be bound to the tslpatchdata tree whose OWN namespaces.ini "
                    + "defines the selected option, not an unrelated tslpatchdata tree found later in the archive.");
                Assert.That(patcher.Source[0], Does.Not.Contain("Patch - Unrelated Compat"));
            });
        }

        [Test]
        public void GenerateInstructions_NamespacesIniTreeAfterChangesIniOnlyTree_NamespacesIniStillWinsThePath()
        {
            // Discriminates a narrower variant of the same bug class: a namespaces.ini found
            // LATER in the archive must still win the TslPatcherPath over a changes.ini-only tree
            // found EARLIER - "first namespaces.ini wins" must not degrade into "first
            // tslpatchdata entry of any kind wins". A guard keyed on TslPatcherPath already being
            // non-empty (instead of on whether a namespaces.ini has already been seen) would wrongly
            // let the earlier changes.ini-only tree's path stick.
            string archivePath = CreateTestArchive("changes_then_namespaces_trees.zip", archive =>
            {
                // First subtree, encountered first: only ships changes.ini, no namespaces.ini.
                AddTextFileToArchive(archive, "Changes Only Mod/tslpatchdata/changes.ini", "[Settings]\nLookupGameFolder=1");
                AddTextFileToArchive(archive, "Changes Only Mod/OldPatcher.exe", "fake exe - changes only");

                // Second, independent subtree encountered later: the actual namespaced mod.
                AddTextFileToArchive(archive, "Namespaced Mod/tslpatchdata/namespaces.ini",
                    "[Namespaces]\n1=OnlyOption\n\n[OnlyOption]\nName=Only Option\n"
                    + "Description=The default installation for the mod.\nIniName=changesB.ini");
                AddTextFileToArchive(archive, "Namespaced Mod/tslpatchdata/changesB.ini", "[Settings]\nLookupGameFolder=1");
                AddTextFileToArchive(archive, "Namespaced Mod/NewPatcher.exe", "fake exe - namespaced");
            });

            var component = new ModComponent { Name = "Changes Then Namespaces Mod", Guid = Guid.NewGuid() };

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);

            Assert.That(result, Is.True);
            Assert.That(component.Options, Has.Count.EqualTo(1));

            Instruction patcher = component.Options[0].Instructions.Single(i => i.Action == Instruction.ActionType.Patcher);

            Assert.Multiple(() =>
            {
                Assert.That(patcher.Source[0], Does.Contain("Namespaced Mod"),
                    "A namespaces.ini found later in the archive must still win the patcher path over "
                    + "an earlier tree that only has a changes.ini.");
                Assert.That(patcher.Source[0], Does.Not.Contain("Changes Only Mod"));
                Assert.That(patcher.Source[0], Does.Contain("NewPatcher.exe"));
            });
        }

        [Test]
        public void TryGenerateInstructions_RemoveDuplicateStep_DoesNotRequireAnArchive()
        {
            var component = new ModComponent
            {
                Name = "Remove Duplicate TGA/TPC",
                Guid = Guid.NewGuid(),
            };
            component.Instructions.Add(new Instruction
            {
                Action = Instruction.ActionType.Extract,
                Source = new List<string> { @"<<modDirectory>>\draft.zip" },
            });

            Assert.That(AutoInstructionGenerator.TryGenerateInstructionsFromArchive(component), Is.True);
            Assert.That(component.Instructions, Has.One.Property(nameof(Instruction.Action))
                .EqualTo(Instruction.ActionType.DelDuplicate));
        }

        [Test]
        public void GenerateInstructions_MultipleFolders_CreatesMoveForEveryPayloadFolder()
        {

            string archivePath = CreateTestArchive("multi_folder.zip", archive =>
            {
                AddTextFileToArchive(archive, "Folder1/appearance.2da", "2DA");
                AddTextFileToArchive(archive, "Folder2/dialog.dlg", "DLG");
                AddTextFileToArchive(archive, "Folder3/heads.2da", "2DA");
            });

            var component = new ModComponent { Name = "Multi Folder Mod", Guid = Guid.NewGuid() };

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(component.Instructions[0].Action, Is.EqualTo(Instruction.ActionType.Extract));
                Assert.That(component.Instructions.Skip(1), Has.All.Property(nameof(Instruction.Action))
                    .EqualTo(Instruction.ActionType.Move));
                Assert.That(component.Instructions, Has.Count.EqualTo(4));
                Assert.That(component.Options, Is.Empty,
                    "Loose payload folders are cumulative; unselected Choose options would silently install nothing");
            });
        }

        [Test]
        public void GenerateInstructions_SingleFolder_CreatesSimpleMoveInstruction()
        {

            string archivePath = CreateTestArchive("single_folder.zip", archive =>
            {
                AddTextFileToArchive(archive, "Override/appearance.2da", "2DA");
                AddTextFileToArchive(archive, "Override/dialog.dlg", "DLG");
            });

            var component = new ModComponent { Name = "Single Folder Mod", Guid = Guid.NewGuid() };

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(component.Instructions, Has.Count.EqualTo(2), "Should have Extract + Move");
            });
            Assert.Multiple(() =>
            {
                Assert.That(component.Instructions[0].Action, Is.EqualTo(Instruction.ActionType.Extract));
                Assert.That(component.Instructions[1].Action, Is.EqualTo(Instruction.ActionType.Move));
                Assert.That(component.Instructions[1].Destination, Does.Contain("Override"));
            });
        }

        [Test]
        public void GenerateInstructions_FlatFiles_CreatesSimpleMoveInstruction()
        {

            string archivePath = CreateTestArchive("flat_files.zip", archive =>
            {
                AddTextFileToArchive(archive, "appearance.2da", "2DA");
                AddTextFileToArchive(archive, "dialog.dlg", "DLG");
            });

            var component = new ModComponent { Name = "Flat Files Mod", Guid = Guid.NewGuid() };

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(component.Instructions, Has.Count.EqualTo(2), "Should have Extract + Move");
            });
            Assert.That(component.Instructions[1].Action, Is.EqualTo(Instruction.ActionType.Move));
        }

        [Test]
        public void GenerateInstructions_MultipleArchives_DoesNotDuplicate()
        {

            string archive1Path = CreateTestArchive("archive1.zip", archive =>
            {
                AddTextFileToArchive(archive, "file1.2da", "2DA");
            });

            string archive2Path = CreateTestArchive("archive2.zip", archive =>
            {
                AddTextFileToArchive(archive, "file2.dlg", "DLG");
            });

            var component = new ModComponent { Name = "Multi Archive Mod", Guid = Guid.NewGuid() };

            bool result1 = AutoInstructionGenerator.GenerateInstructions(component, archive1Path);
            int instructionsAfterFirst = component.Instructions.Count;

            bool result2 = AutoInstructionGenerator.GenerateInstructions(component, archive2Path);
            int instructionsAfterSecond = component.Instructions.Count;

            Assert.Multiple(() =>
            {
                Assert.That(result1, Is.True);
                Assert.That(result2, Is.True);
                Assert.That(instructionsAfterFirst, Is.EqualTo(2), "First archive should create 2 instructions");
                Assert.That(instructionsAfterSecond, Is.EqualTo(4), "Second archive should ADD 2 more (not replace)");
            });

            var archive1Instructions = component.Instructions.Where(i =>
                i.Source != null && i.Source.Any(s => NetFrameworkCompatibility.Contains(s, "archive1", StringComparison.Ordinal))).ToList();
            var archive2Instructions = component.Instructions.Where(i =>
                i.Source != null && i.Source.Any(s => NetFrameworkCompatibility.Contains(s, "archive2", StringComparison.Ordinal))).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(archive1Instructions, Has.Count.EqualTo(2), "Archive1 instructions should still exist");
                Assert.That(archive2Instructions, Has.Count.EqualTo(2), "Archive2 instructions should be added");
            });
        }

        /// <summary>
        /// Measured 2026-08-31 on both K1 Holo and K1 Bio: the zip has real cards at the
        /// root plus optional green/ and AppleDouble debris in __MACOSX/. The generator
        /// walked only subfolders, so Override got ._lbl_*.tga and missed lbl_cardback.tga.
        /// </summary>
        [Test]
        public void HdPazaakCards_MovesRootLooseFiles_NotMacosxOrOptionalGreen()
        {
            string archivePath = CreateTestArchive("HD_Pazaak_Cards.zip", archive =>
            {
                AddTextFileToArchive(archive, "lbl_cardback.tga", "REAL-CARDBACK");
                AddTextFileToArchive(archive, "lbl_cardstand.tga", "REAL-STAND");
                AddTextFileToArchive(archive, "green/lbl_cardback.tga", "GREEN-CARDBACK");
                AddTextFileToArchive(archive, "__MACOSX/._lbl_cardback.tga", "APPLEDOUBLE");
            });

            var component = new ModComponent
            {
                Name = "HD Pazaak Cards",
                Guid = Guid.NewGuid(),
                Directions =
                    "Move all the loose files to the Override. If you'd like KOTOR 2-style "
                    + "specialty cards (green-colored), move the files from the \"green\" folder "
                    + "to the override folder as well.",
            };

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);
            List<string> moveSources = component.Instructions
                .Where(i => i.Action == Instruction.ActionType.Move)
                .SelectMany(i => i.Source ?? new List<string>())
                .ToList();

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(
                    moveSources,
                    Has.Some.Contains("lbl_cardback.tga")
                        .Or.Some.Match("HD_Pazaak_Cards\\\\\\*$")
                        .Or.Contains(@"HD_Pazaak_Cards\*"),
                    "Root loose cards must be moved. Sources: " + string.Join(" | ", moveSources));
                Assert.That(
                    moveSources,
                    Has.Some.Contains("lbl_cardstand.tga")
                        .Or.Some.Match("HD_Pazaak_Cards\\\\\\*$")
                        .Or.Contains(@"HD_Pazaak_Cards\*"),
                    "Root stand texture must be moved. Sources: " + string.Join(" | ", moveSources));
                Assert.That(
                    moveSources,
                    Has.None.Contains("__MACOSX"),
                    "AppleDouble tree must not become a Move. Sources: " + string.Join(" | ", moveSources));
                Assert.That(
                    moveSources,
                    Has.None.Contains("green"),
                    "Guide marks green as optional. Sources: " + string.Join(" | ", moveSources));
            });
        }

        /// <summary>
        /// Guide prose names folders to copy, a folder to skip, and EXCEPT filenames.
        /// Measured 2026-08-31: the generator walked every archive folder, so both K1
        /// autos installed Bug Fixes plus the six Sith-uniform models the guide excludes.
        /// </summary>
        [Test]
        public void LooseFolders_HonorsGuideSkipFolderAndExceptFilenames()
        {
            string archivePath = CreateTestArchive("minor_fixes.zip", archive =>
            {
                AddTextFileToArchive(archive, "Straight Fixes/man26_ok.dlg", "KEEP");
                AddTextFileToArchive(archive, "Bug Fixes/later.2da", "SKIP-FOLDER");
                AddTextFileToArchive(archive, "Things What Bother Me Fixes/man26_keep.dlg", "KEEP-DLG");
                AddTextFileToArchive(archive, "Things What Bother Me Fixes/plc_kiosk2.tga", "KEEP-TGA");
                AddTextFileToArchive(archive, "Things What Bother Me Fixes/N_AdmrlSaulKar.mdl", "EXCEPT-MDL");
                AddTextFileToArchive(archive, "Things What Bother Me Fixes/N_AdmrlSaulKar.mdx", "EXCEPT-MDX");
                AddTextFileToArchive(archive, "Things What Bother Me Fixes/N_SithComF.mdl", "EXCEPT-F");
                AddTextFileToArchive(archive, "Things What Bother Me Fixes/N_SithComF.mdx", "EXCEPT-FMDX");
                AddTextFileToArchive(archive, "Things What Bother Me Fixes/N_SithComM.mdl", "EXCEPT-M");
                AddTextFileToArchive(archive, "Things What Bother Me Fixes/N_SithComM.mdx", "EXCEPT-MMDX");
            });

            var component = new ModComponent
            {
                Name = "Minor Fixes",
                Guid = Guid.NewGuid(),
                Directions =
                    "Move everything from the Straight Fixes, Resolution Fixes, and Aesthetic "
                    + "Improvements folders to your Override. Move everything from the "
                    + "\"Things what bother me\" folder as well, EXCEPT the files for the Sith "
                    + "uniform changes: N_AdmrlSaulKar.mdl, N_AdmrlSaulKar.mdx, N_SithComF.mdl, "
                    + "N_SithComF.mdx, N_SithComM.mdl, and N_SithComM.mdx (in other words, move "
                    + "all \"MAN26\" files and the two \"plc_kiosk\" files at the bottom). The "
                    + "fix in the Bugfix folder will be applied by a later mod, so you can also skip it.",
            };

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);
            List<string> moveSources = component.Instructions
                .Where(i => i.Action == Instruction.ActionType.Move)
                .SelectMany(i => i.Source ?? new List<string>())
                .ToList();
            string joined = string.Join(" | ", moveSources);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(moveSources, Has.Some.Contains("Straight Fixes"), joined);
                Assert.That(moveSources, Has.None.Contains("Bug Fixes"), "Guide says skip Bugfix. " + joined);
                Assert.That(moveSources, Has.None.Contains("N_AdmrlSaulKar"), joined);
                Assert.That(moveSources, Has.None.Contains("N_SithComF"), joined);
                Assert.That(moveSources, Has.None.Contains("N_SithComM"), joined);
                Assert.That(
                    moveSources,
                    Has.Some.Contains("man26_keep.dlg").Or.Contains("Things What Bother Me Fixes"),
                    "Non-excepted files in that folder must still move. " + joined);
            });
        }

        /// <summary>
        /// Guide: delete named files from the payload before the Override copy.
        /// Measured 2026-08-31: NLP kept the Delete, then BindGuideDeleteSource saw
        /// "override" and pointed it at Override. Delete ran first (files not there
        /// yet), then Move copied LSI_* / LUN_* in anyway.
        /// </summary>
        [Test]
        public void DeleteBeforeMove_ExcludesPayloadFiles_AndBindsDeleteToExtract()
        {
            string archivePath = CreateTestArchive("scene_hr.zip", archive =>
            {
                AddTextFileToArchive(archive, "Scene HR/Override/keep.tpc", "KEEP");
                AddTextFileToArchive(archive, "Scene HR/Override/drop_win.tpc", "DROP-WIN");
                AddTextFileToArchive(archive, "Scene HR/Override/drop_box.tpc", "DROP-BOX");
            });

            var component = new ModComponent
            {
                Name = "Scene High Resolution",
                Guid = Guid.NewGuid(),
                Directions =
                    "Download the .tpc variant of the mod. Make sure to delete drop_win.tpc "
                    + "and drop_box.tpc before moving to override.",
            };
            var draftedDelete = new Instruction
            {
                Action = Instruction.ActionType.Delete,
                Source = new List<string>
                {
                    @"<<modDirectory>>\drop_win.tpc",
                    @"<<modDirectory>>\drop_box.tpc",
                },
            };
            draftedDelete.SetParentComponent(component);
            component.Instructions.Add(draftedDelete);

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);
            List<string> moveSources = component.Instructions
                .Where(i => i.Action == Instruction.ActionType.Move)
                .SelectMany(i => i.Source ?? new List<string>())
                .ToList();
            List<string> deleteSources = component.Instructions
                .Where(i => i.Action == Instruction.ActionType.Delete)
                .SelectMany(i => i.Source ?? new List<string>())
                .ToList();
            string moves = string.Join(" | ", moveSources);
            string deletes = string.Join(" | ", deleteSources);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(moveSources, Has.None.Contains("drop_win.tpc"), moves);
                Assert.That(moveSources, Has.None.Contains("drop_box.tpc"), moves);
                Assert.That(
                    moveSources,
                    Has.None.Match(@"Override\\\\\\*$"),
                    "Must not wildcard a folder that still holds the dropped files. " + moves);
                Assert.That(
                    moveSources,
                    Has.Some.Contains("keep.tpc"),
                    "The rest of the pack must still move. " + moves);
                Assert.That(deleteSources, Has.None.Contains("<<kotorDirectory>>"), deletes);
                Assert.That(
                    deleteSources,
                    Has.All.Contains("<<modDirectory>>"),
                    "Delete-before-move must hit the extract, not Override. " + deletes);
            });
        }

        /// <summary>
        /// Guide names the folders to copy and forbids the archive root. Measured
        /// 2026-08-31 on Ajunta 1.1: generator emitted Move * and dumped heads.2da,
        /// clear_effects.ncs, utc/dlg plus both skin trees.
        /// </summary>
        [Test]
        public void AllowlistedFolders_SkipsRootAndUnnamedSiblings_PicksOneOf()
        {
            string archivePath = CreateTestArchive("unique_look_1.1.zip", archive =>
            {
                AddTextFileToArchive(archive, "heads.2da", "ROOT-2DA");
                AddTextFileToArchive(archive, "leftover.utc", "ROOT-UTC");
                AddTextFileToArchive(archive, "Transparent Skins/hero.tga", "TRANS");
                AddTextFileToArchive(archive, "Transparent Skins/Sith Eyes/hero.tga", "EYES");
                AddTextFileToArchive(archive, "Non-Transparent Skins/hero.tga", "OPAQUE");
            });

            var component = new ModComponent
            {
                Name = "Unique Look",
                Guid = Guid.NewGuid(),
                Directions =
                    "Run the patch first. ONLY look at the Transparent/Non-Transparent folders "
                    + "within the main file; move your preferred textures from one of those "
                    + "folders to your override, and optionally also move the contents of the "
                    + "sub-folders for Sith eyes if desired. Do NOT move any of the files in "
                    + "the main mod folder!",
            };

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);
            List<string> moveSources = component.Instructions
                .Where(i => i.Action == Instruction.ActionType.Move)
                .SelectMany(i => i.Source ?? new List<string>())
                .ToList();
            string joined = string.Join(" | ", moveSources);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(moveSources, Has.None.Contains("heads.2da"), joined);
                Assert.That(moveSources, Has.None.Contains("leftover.utc"), joined);
                Assert.That(moveSources, Has.None.Contains("Non-Transparent"), joined);
                Assert.That(moveSources, Has.None.Contains("Sith Eyes"), joined);
                Assert.That(
                    moveSources,
                    Has.Some.Contains("hero.tga").Or.Contains("Transparent Skins"),
                    joined);
                Assert.That(
                    moveSources,
                    Has.None.Match(@"unique_look_1\.1\\\\\\*$"),
                    "Must not wildcard the archive root. " + joined);
            });
        }

        /// <summary>
        /// Guide: enter one folder, copy the files in it, skip the optional subtree
        /// and every unnamed sibling. Measured 2026-08-31: NPC Replacement\* covered
        /// OPTIONAL at install time, and Player Bodies was walked as a top-level folder.
        /// </summary>
        [Test]
        public void NamedFolder_SkipsNestedOptionalAndUnnamedSiblings()
        {
            string archivePath = CreateTestArchive("boots_resource.zip", archive =>
            {
                AddTextFileToArchive(archive, "boots_resource/NPC Replacement/N_Keep.mdl", "KEEP-MDL");
                AddTextFileToArchive(archive, "boots_resource/NPC Replacement/N_Keep.mdx", "KEEP-MDX");
                AddTextFileToArchive(archive, "boots_resource/NPC Replacement/OPTIONAL/DP_Skip.mdl", "SKIP-MDL");
                AddTextFileToArchive(archive, "boots_resource/NPC Replacement/OPTIONAL/DP_Skip.mdx", "SKIP-MDX");
                AddTextFileToArchive(archive, "boots_resource/Player Bodies/P_Extra.mdl", "EXTRA");
            });

            var component = new ModComponent
            {
                Name = "Boots Resource",
                Guid = Guid.NewGuid(),
                Directions =
                    "Unzip the mod, enter the NPC Replacement folder, and move the six files "
                    + "within (NOT including the optional folder or its contents) to the override.",
            };

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);
            List<string> moveSources = component.Instructions
                .Where(i => i.Action == Instruction.ActionType.Move)
                .SelectMany(i => i.Source ?? new List<string>())
                .ToList();
            string joined = string.Join(" | ", moveSources);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(moveSources, Has.Some.Contains("N_Keep.mdl").Or.Contains("N_Keep.mdx"), joined);
                Assert.That(moveSources, Has.None.Contains("OPTIONAL"), joined);
                Assert.That(moveSources, Has.None.Contains("DP_Skip"), joined);
                Assert.That(moveSources, Has.None.Contains("Player Bodies"), joined);
                Assert.That(moveSources, Has.None.Contains("P_Extra"), joined);
            });
        }

        /// <summary>
        /// full.md bold-wraps "before" (`**before** moving`). Measured 2026-08-31
        /// on Ultimate Taris: the clause missed, Delete bound to Override (files
        /// not there yet), then Move Override\* copied LSI_* back in.
        /// </summary>
        [Test]
        public void DeleteBeforeMove_MarkdownBoldBefore_ExcludesPayloadFiles()
        {
            string archivePath = CreateTestArchive("taris_hr.zip", archive =>
            {
                AddTextFileToArchive(archive, "Taris HR/Override/keep.tpc", "KEEP");
                AddTextFileToArchive(archive, "Taris HR/Override/LSI_win01.tpc", "DROP-WIN");
                AddTextFileToArchive(archive, "Taris HR/Override/LSI_box01.tpc", "DROP-BOX");
            });

            var component = new ModComponent
            {
                Name = "Ultimate Taris High Resolution",
                Guid = Guid.NewGuid(),
                Directions =
                    "Download the .tpc variant of the mod. Make sure to delete "
                    + "LSI_win01.tpc and LSI_box01.tpc **before** moving to override.",
            };
            var draftedDelete = new Instruction
            {
                Action = Instruction.ActionType.Delete,
                Source = new List<string>
                {
                    @"<<modDirectory>>\LSI_win01.tpc",
                    @"<<modDirectory>>\LSI_box01.tpc",
                },
            };
            draftedDelete.SetParentComponent(component);
            component.Instructions.Add(draftedDelete);

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);
            List<string> moveSources = component.Instructions
                .Where(i => i.Action == Instruction.ActionType.Move)
                .SelectMany(i => i.Source ?? new List<string>())
                .ToList();
            List<string> deleteSources = component.Instructions
                .Where(i => i.Action == Instruction.ActionType.Delete)
                .SelectMany(i => i.Source ?? new List<string>())
                .ToList();
            string moves = string.Join(" | ", moveSources);
            string deletes = string.Join(" | ", deleteSources);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(moveSources, Has.None.Contains("LSI_win01.tpc"), moves);
                Assert.That(moveSources, Has.None.Contains("LSI_box01.tpc"), moves);
                Assert.That(moveSources, Has.Some.Contains("keep.tpc"), moves);
                Assert.That(deleteSources, Has.None.Contains("<<kotorDirectory>>"), deletes);
                Assert.That(deleteSources, Has.All.Contains("<<modDirectory>>"), deletes);
            });
        }

        /// <summary>
        /// Guide deletes named files then says "move all files" with no "before".
        /// Measured 2026-08-31 on NPC Clothing M: Delete bound to Override, then
        /// n_commm07.tga / N_CommMD01.tga landed anyway.
        /// </summary>
        [Test]
        public void DeleteThenMoveAll_ExcludesNamedPayloadFiles()
        {
            string archivePath = CreateTestArchive("npc_clothes.zip", archive =>
            {
                AddTextFileToArchive(archive, "N_CommM04.tga", "KEEP");
                AddTextFileToArchive(archive, "n_commm07.tga", "DROP-07");
                AddTextFileToArchive(archive, "N_CommMD01.tga", "DROP-D");
                AddTextFileToArchive(archive, "N_CommM08.tga", "DROP-08");
                AddTextFileToArchive(archive, "N_CommM0801.tga", "RENAME-SRC");
            });

            var component = new ModComponent
            {
                Name = "NPC Clothing M",
                Guid = Guid.NewGuid(),
                Directions =
                    "Delete n_commm07.tga and N_CommMD01.tga. Delete N_CommM08.tga, then "
                    + "make a copy of N_CommM0801 and paste it in the same directory. "
                    + "This should create a duplicate file; rename that duplicate file to "
                    + "\"N_CommM08.tga\" and then move all files to override.",
            };
            var draftedDelete = new Instruction
            {
                Action = Instruction.ActionType.Delete,
                Source = new List<string>
                {
                    @"<<modDirectory>>\n_commm07.tga",
                    @"<<modDirectory>>\N_CommMD01.tga",
                    @"<<modDirectory>>\N_CommM08.tga",
                },
            };
            draftedDelete.SetParentComponent(component);
            component.Instructions.Add(draftedDelete);

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);
            List<string> moveSources = component.Instructions
                .Where(i => i.Action == Instruction.ActionType.Move)
                .SelectMany(i => i.Source ?? new List<string>())
                .ToList();
            List<string> deleteSources = component.Instructions
                .Where(i => i.Action == Instruction.ActionType.Delete)
                .SelectMany(i => i.Source ?? new List<string>())
                .ToList();
            string moves = string.Join(" | ", moveSources);
            string deletes = string.Join(" | ", deleteSources);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(moveSources, Has.None.Contains("n_commm07.tga"), moves);
                Assert.That(moveSources, Has.None.Contains("N_CommMD01.tga"), moves);
                Assert.That(moveSources, Has.None.Contains("N_CommM08.tga"), moves);
                Assert.That(moveSources, Has.Some.Contains("N_CommM04.tga"), moves);
                Assert.That(deleteSources, Has.None.Contains("<<kotorDirectory>>"), deletes);
            });
        }

        /// <summary>
        /// Guide: only move files from one named folder. Measured 2026-08-31 on
        /// Robes with Shadows: generator also Moved Hybrid / Party Robes Override,
        /// dumping JC_*BI party models the finished manual never has.
        /// </summary>
        [Test]
        public void OnlyMoveFromQuotedFolder_SkipsSiblingOverrideFolders()
        {
            string archivePath = CreateTestArchive("robes_shadows.zip", archive =>
            {
                AddTextFileToArchive(archive, "Jedi Robes Override/PMBIL.mdl", "JEDI");
                AddTextFileToArchive(archive, "Party Robes Override/JC_BastilaBI.mdl", "PARTY");
                AddTextFileToArchive(archive, "Hybrid Robes Override/hybrid.mdl", "HYBRID");
            });

            var component = new ModComponent
            {
                Name = "Robes with Shadows for K1 (JC's Port)",
                Guid = Guid.NewGuid(),
                Directions = "Only move the files from \"Jedi Robes Override\".",
            };

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);
            List<string> moveSources = component.Instructions
                .Where(i => i.Action == Instruction.ActionType.Move)
                .SelectMany(i => i.Source ?? new List<string>())
                .ToList();
            string joined = string.Join(" | ", moveSources);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(moveSources, Has.Some.Contains("PMBIL.mdl").Or.Contains("Jedi Robes Override"), joined);
                Assert.That(moveSources, Has.None.Contains("JC_BastilaBI"), joined);
                Assert.That(moveSources, Has.None.Contains("Party Robes"), joined);
                Assert.That(moveSources, Has.None.Contains("Hybrid Robes"), joined);
            });
        }

        /// <summary>
        /// Guide: pick one of two named folders; ignore a named root file.
        /// Measured 2026-08-31 on HD Darth Malak: Move Malak\\* dumped
        /// N_DarthMalak01.tga (CineMalak owns that name) plus both eye folders.
        /// </summary>
        [Test]
        public void PreferredFolder_IgnoresNamedFile_PicksOneOf()
        {
            string archivePath = CreateTestArchive("malak.zip", archive =>
            {
                AddTextFileToArchive(archive, "N_DarthMalak01.tga", "BODY-IGNORE");
                AddTextFileToArchive(archive, "Malak (Red Eyes)/N_DarthMalakH01.tga", "RED");
                AddTextFileToArchive(archive, "Malak (Blue Eyes)/N_DarthMalakH01.tga", "BLUE");
            });

            var component = new ModComponent
            {
                Name = "HD Darth Malak",
                Guid = Guid.NewGuid(),
                Directions =
                    "If intending to use CineMalak below (recommended!), select your preferred "
                    + "head texture from the Malak (Red Eyes) or Malak (Blue Eyes) folders and "
                    + "move the files within to your override. You can ignore N_DarthMalak01.tga, "
                    + "unless you do not want to use CineMalak, in which case you should also "
                    + "move it to your override.",
            };

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);
            List<string> moveSources = component.Instructions
                .Where(i => i.Action == Instruction.ActionType.Move)
                .SelectMany(i => i.Source ?? new List<string>())
                .ToList();
            string joined = string.Join(" | ", moveSources);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(moveSources, Has.None.Contains("N_DarthMalak01.tga"), joined);
                Assert.That(moveSources, Has.None.Contains("Blue Eyes"), joined);
                Assert.That(
                    moveSources,
                    Has.Some.Contains("Red Eyes").Or.Contains("N_DarthMalakH01.tga"),
                    joined);
            });
        }

        /// <summary>
        /// Installer leftover backup/ trees are not payload. Measured 2026-08-31
        /// on K2 Swoops companion rar: Move backup\\...\\override\\* dumped
        /// lmg_jet*.tpc / v_rider01.tpc the finished manual never has.
        /// </summary>
        [Test]
        public void InstallerBackupTree_IsNotMoved()
        {
            string archivePath = CreateTestArchive("swoop_companion.zip", archive =>
            {
                AddTextFileToArchive(archive, "backup/2025-04-29_00.30.40/override/lmg_jet01.tpc", "JET");
                AddTextFileToArchive(archive, "backup/2025-04-29_00.30.40/override/v_rider01.tpc", "RIDER");
            });

            var component = new ModComponent
            {
                Name = "K2 Swoops to K1",
                Guid = Guid.NewGuid(),
                InstallationMethod = "HoloPatcher Mod",
                Directions = "Run the HoloPatcher installer.",
            };

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);
            List<string> moveSources = component.Instructions
                .Where(i => i.Action == Instruction.ActionType.Move)
                .SelectMany(i => i.Source ?? new List<string>())
                .ToList();
            string joined = string.Join(" | ", moveSources);

            Assert.That(moveSources, Is.Empty, "backup/override leftovers must not move. " + joined);
            Assert.That(result, Is.False);
        }

        /// <summary>
        /// Guide is installer-only. The archive still has a source/ tree of raw
        /// .dlg files the patcher reads. Measured 2026-08-31: Hybrid generation
        /// Moved source\* into Override after running the installer.
        /// </summary>
        [Test]
        public void PatcherOnlyGuide_DoesNotMoveSourceFolder()
        {
            string archivePath = CreateTestArchive("ported_vo.zip", archive =>
            {
                AddTextFileToArchive(archive, "tslpatchdata/namespaces.ini",
                    "[Namespaces]\n1=Main\n\n[Main]\nName=Main Installation\nIniName=changes.ini");
                AddTextFileToArchive(archive, "tslpatchdata/changes.ini", "[Settings]\nLookupGameFolder=1");
                AddTextFileToArchive(archive, "Installer.exe", "fake exe");
                AddTextFileToArchive(archive, "source/dan_extra.dlg", "RAW-DLG");
                AddTextFileToArchive(archive, "source/ebo_extra.dlg", "RAW-DLG-2");
            });

            var component = new ModComponent
            {
                Name = "Ported Voice",
                Guid = Guid.NewGuid(),
                InstallationMethod = "HoloPatcher Mod",
                Directions =
                    "Install the main mod, then re-run the patcher and select the "
                    + "compatibility install option and install it as well.",
            };

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);
            List<string> moveSources = component.Instructions
                .Where(i => i.Action == Instruction.ActionType.Move)
                .SelectMany(i => i.Source ?? new List<string>())
                .ToList();
            string joined = string.Join(" | ", moveSources);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(
                    component.Instructions,
                    Has.Some.Property(nameof(Instruction.Action)).EqualTo(Instruction.ActionType.Choose)
                        .Or.EqualTo(Instruction.ActionType.Patcher));
                Assert.That(moveSources, Is.Empty, "Installer-only guide must not dump source/. " + joined);
                Assert.That(
                    component.InstallationMethod,
                    Does.Contain("Patcher").IgnoreCase.And.Not.Contain("Loose"),
                    "Leftover source/ must not relabel a patcher-only guide as Hybrid.");
            });
        }

        [Test]
        public void GenerateInstructions_SameArchiveTwice_DoesNotCreateDuplicates()
        {

            string archivePath = CreateTestArchive("test.zip", archive =>
            {
                AddTextFileToArchive(archive, "file1.2da", "2DA");
            });

            var component = new ModComponent { Name = "Test Mod", Guid = Guid.NewGuid() };

            AutoInstructionGenerator.GenerateInstructions(component, archivePath);
            var firstGuids = component.Instructions.Select(i => i.GetHashCode()).ToList();

            AutoInstructionGenerator.GenerateInstructions(component, archivePath);
            var secondGuids = component.Instructions.Select(i => i.GetHashCode()).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(component.Instructions, Has.Count.EqualTo(2), "Should still have 2 instructions");
                Assert.That(firstGuids, Is.EqualTo(secondGuids), "Instructions should not be regenerated - same GUIDs expected");
            });
        }

        /// <summary>
        /// Regression for "Duros HD" (K2 full.md, component #62): the guide performs one copy-as
        /// clause ("make a copy of N_Duros02.tga ... Rename this file to N_Duros04.tga") followed by a
        /// "repeat this process" clause naming two more copies at once ("... with the file N_Duros03.tga,
        /// creating two copies and naming them N_Duros05.tga and N_Duros06.tga"). Before the fix:
        /// (1) <see cref="AutoInstructionGenerator"/>'s copy-as source matcher failed to rebind
        /// N_Duros04's source because N_Duros02.tga and its N_Duros02.txi sidecar shared a stem and
        /// were ambiguous, leaving a literal '&lt;&lt;modDirectory&gt;&gt;' placeholder in the Rename
        /// Destination; (2) the "repeat this process ... creating N copies and naming them A and B"
        /// clause matched no parser pattern at all, so N_Duros05/06 never got any instruction.
        /// </summary>
        [Test]
        public void RepeatCopyClause_ProducesResolvedNonDestructiveCopiesForAllNamedTargets()
        {
            string archivePath = CreateTestArchive("Duros HD.zip", archive =>
            {
                AddTextFileToArchive(archive, "N_Duros01.tga", new string('A', 32));
                AddTextFileToArchive(archive, "N_Duros01.txi", "TXI");
                AddTextFileToArchive(archive, "N_Duros02.tga", new string('A', 32));
                AddTextFileToArchive(archive, "N_Duros02.txi", "TXI");
                AddTextFileToArchive(archive, "N_Duros03.tga", new string('A', 32));
            });

            var component = new ModComponent
            {
                Name = "Duros HD",
                Guid = Guid.NewGuid(),
                Directions =
                    "Before moving the files to override, make a copy of N_Duros02.tga and paste it into the "
                    + "same location you extracted the mod to. On a Windows OS, this should create a file called "
                    + "\"N_Duros02 - Copy.tga\". Rename this file to \"N_Duros04.tga\". Repeat this process with "
                    + "the file N_Duros03.tga, creating two copies and naming them N_Duros05.tga and N_Duros06.tga. "
                    + "When you move all the files to the override, you should be moving eight total. Following "
                    + "this, download and install the patch.",
            };

            ModSync.Core.Parsing.DraftInstructionService.GenerateDraftInstructions(new[] { component });

            bool result = AutoInstructionGenerator.GenerateInstructions(component, archivePath);

            List<Instruction> renames = component.Instructions
                .Where(i => i.Action == Instruction.ActionType.Rename)
                .ToList();
            Dictionary<string, Instruction> renamesByDestination = renames
                .Where(i => !string.IsNullOrEmpty(i.Destination))
                .GroupBy(i => i.Destination, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var validator = new ComponentValidation(component);
            validator.Run();
            List<string> validationErrors = validator.GetErrors();

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(
                    validationErrors,
                    Has.None.Contains("Unresolved placeholder"),
                    "No Rename instruction should carry an unresolved '<<...>>' placeholder Destination: "
                    + string.Join(" | ", validationErrors));

                foreach (string expectedDestination in new[] { "N_Duros04.tga", "N_Duros05.tga", "N_Duros06.tga" })
                {
                    Assert.That(
                        renamesByDestination.ContainsKey(expectedDestination),
                        $"Expected a Rename instruction targeting '{expectedDestination}'.");
                    if (renamesByDestination.TryGetValue(expectedDestination, out Instruction rename))
                    {
                        Assert.That(rename.Destination, Does.Not.Contain("<<"), $"Destination for '{expectedDestination}' must be a bare filename.");
                        Assert.That(rename.Source, Has.Some.Contains("Override"), $"Source for '{expectedDestination}' must be grounded to a real Override path.");
                    }
                }

                // The originals must survive (non-destructive copy): they still land in Override via
                // the wildcard Move of the extracted payload, distinct from the renamed duplicates above.
                List<string> moveSources = component.Instructions
                    .Where(i => i.Action == Instruction.ActionType.Move)
                    .SelectMany(i => i.Source ?? new List<string>())
                    .ToList();
                Assert.That(moveSources, Has.Some.Contains("Duros HD"), "Original archive payload (including N_Duros02.tga/N_Duros03.tga) must still be moved to Override.");
            });
        }

        #region Helper Methods

        private string CreateTestArchive(string fileName, Action<SharpCompress.Archives.IWritableArchive<SharpCompress.Writers.Zip.ZipWriterOptions>> populateArchive)
        {
            if (_testDirectory is null)
            {
                throw new InvalidOperationException("Test directory is null");
            }
            string archivePath = Path.Combine(_testDirectory, fileName);

            using (var archive = ZipArchive.CreateArchive())
            {
                populateArchive(archive);
                archive.SaveTo(archivePath, CompressionType.Deflate);
            }

            return archivePath;
        }

        private static void AddTextFileToArchive(SharpCompress.Archives.IWritableArchive<SharpCompress.Writers.Zip.ZipWriterOptions> archive, string path, string content)
        {
            var memoryStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            archive.AddEntry(path, memoryStream, closeStream: true);
        }

        #endregion
    }
}
