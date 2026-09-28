// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

using ModSync.Core;
using ModSync.Core.Services;

using NUnit.Framework;

namespace ModSync.Tests
{
    /// <summary>
    /// An archive may sit beside an already-extracted copy of itself. The archive is authoritative
    /// whenever it can be listed, because it is what actually gets extracted at install time and the
    /// only thing instruction paths are validated against. The extracted folder is a fallback for
    /// archives that cannot be read at all.
    /// <para>
    /// Directory EXISTENCE is not proof of content. The reference mod library contains folders left
    /// by an earlier session whose extraction failed part-way: the directory structure is present
    /// (an "Override" subfolder, for instance) with zero files in it. Generating from one of those
    /// emits instructions that copy nothing, every step reports success, and the finished build is
    /// silently missing thousands of files with no error raised anywhere.
    /// </para>
    /// <para>
    /// A folder can also hold files the archive does not — install residue, or a stray second
    /// executable — so preferring it produced Source paths present in no archive. These tests pin
    /// both the archive-wins rule and the recursive non-emptiness check.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class ExtractedFolderTrustTests
    {
        private string _testDirectory;
        private string _modDirectory;
        private MainConfig _config;

        [SetUp]
        public void SetUp()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), "ModSync_ExtractedFolderTrust_" + Guid.NewGuid());
            _modDirectory = Path.Combine(_testDirectory, "Mods");
            _ = Directory.CreateDirectory(_modDirectory);

            _config = new MainConfig
            {
                sourcePath = new DirectoryInfo(_modDirectory),
            };
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
            return CreateArchiveIn(_modDirectory, archiveName, entryPaths);
        }

        private static string CreateArchiveIn(string directory, string archiveName, params string[] entryPaths)
        {
            _ = Directory.CreateDirectory(directory);
            string archivePath = Path.Combine(directory, archiveName);
            using (ZipArchive zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                foreach (string entryPath in entryPaths)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(entryPath);
                    using (var writer = new StreamWriter(entry.Open()))
                    {
                        writer.Write("x");
                    }
                }
            }

            return archivePath;
        }

        private static ModComponent ComponentFor(string name, string archiveBaseName)
        {
            var component = new ModComponent { Name = name };
            component.ResourceRegistry[archiveBaseName] = new ResourceMetadata();
            return component;
        }

        private static string PatcherSource(ModComponent component)
        {
            return component.Instructions
                .Concat(component.Options.SelectMany(o => o.Instructions))
                .Where(i => i.Action == Instruction.ActionType.Patcher)
                .SelectMany(i => i.Source)
                .FirstOrDefault();
        }

        /// <summary>
        /// The exact shape of the failed-extraction residue in the library: directory tree present,
        /// zero files. The archive must be used instead.
        /// </summary>
        [Test]
        public void EmptyShellFolder_IsIgnored_AndArchiveIsUsedInstead()
        {
            _ = CreateArchive(
                "Ultimate Pack.zip",
                "INSTALL.exe",
                "tslpatchdata/changes.ini",
                "tslpatchdata/texture.tpc");

            // Failed extraction: structure only, no files anywhere beneath it.
            _ = Directory.CreateDirectory(Path.Combine(_modDirectory, "Ultimate Pack", "Override"));

            ModComponent component = ComponentFor("Ultimate Pack", "Ultimate Pack");

            Assert.That(AutoInstructionGenerator.TryGenerateInstructionsFromArchive(component), Is.True);
            Assert.That(
                PatcherSource(component),
                Is.EqualTo(@"<<modDirectory>>\Ultimate Pack\INSTALL.exe"),
                "A folder containing no files must never be treated as the mod's layout.");
        }

        /// <summary>
        /// A folder holding only empty subdirectories is still empty; recursion must not be fooled by
        /// nesting.
        /// </summary>
        [Test]
        public void FolderWithOnlyEmptySubdirectories_IsTreatedAsEmpty()
        {
            _ = CreateArchive(
                "Nested Pack.zip",
                "Install.exe",
                "tslpatchdata/changes.ini");

            _ = Directory.CreateDirectory(
                Path.Combine(_modDirectory, "Nested Pack", "Override", "deeper", "deeper still"));

            ModComponent component = ComponentFor("Nested Pack", "Nested Pack");

            Assert.That(AutoInstructionGenerator.TryGenerateInstructionsFromArchive(component), Is.True);
            Assert.That(
                PatcherSource(component),
                Is.EqualTo(@"<<modDirectory>>\Nested Pack\Install.exe"));
        }

        /// <summary>
        /// A partially extracted folder has real files but not all of them. The archive is the more
        /// complete manifest and must win.
        /// </summary>
        [Test]
        public void PartiallyExtractedFolder_IsRejectedInFavourOfTheArchive()
        {
            _ = CreateArchive(
                "Partial Pack.zip",
                "Install.exe",
                "tslpatchdata/changes.ini",
                "tslpatchdata/one.tpc",
                "tslpatchdata/two.tpc",
                "tslpatchdata/three.tpc");

            string folder = Path.Combine(_modDirectory, "Partial Pack", "tslpatchdata");
            _ = Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "changes.ini"), "x");
            File.WriteAllText(Path.Combine(folder, "one.tpc"), "x");
            // two.tpc and three.tpc never made it, and Install.exe is missing too.

            ModComponent component = ComponentFor("Partial Pack", "Partial Pack");

            Assert.That(AutoInstructionGenerator.TryGenerateInstructionsFromArchive(component), Is.True);
            Assert.That(
                PatcherSource(component),
                Is.EqualTo(@"<<modDirectory>>\Partial Pack\Install.exe"),
                "An incomplete extraction must not be trusted over the archive.");
        }

        /// <summary>
        /// The archive is authoritative whenever it can be listed, because it is what actually gets
        /// extracted and the only thing instruction paths are validated against.
        /// <para>
        /// Reproduces a real failure: `Darth_Malaks_Lightsaber_K1` on disk holds both
        /// "Darth Malak\'s Lightsaber.exe" (which is in the archive) and a stray "TSLPatcher.exe"
        /// (which is not). Naming the stray one produced a Source path present in no archive and
        /// failed validation.
        /// </para>
        /// </summary>
        [Test]
        public void ArchiveWinsOverFolderHoldingAnExecutableTheArchiveDoesNotContain()
        {
            _ = CreateArchive(
                "Darth_Malaks_Lightsaber_K1.zip",
                "Darth Malak\'s Lightsaber.exe",
                "tslpatchdata/changes.ini");

            string root = Path.Combine(_modDirectory, "Darth_Malaks_Lightsaber_K1");
            _ = Directory.CreateDirectory(Path.Combine(root, "tslpatchdata"));
            File.WriteAllText(Path.Combine(root, "Darth Malak\'s Lightsaber.exe"), "x");
            File.WriteAllText(Path.Combine(root, "TSLPatcher.exe"), "x");
            File.WriteAllText(Path.Combine(root, "tslpatchdata", "changes.ini"), "x");

            ModComponent component = ComponentFor("Darth Malak\'s Lightsaber", "Darth_Malaks_Lightsaber_K1");

            Assert.That(AutoInstructionGenerator.TryGenerateInstructionsFromArchive(component), Is.True);
            Assert.That(
                PatcherSource(component),
                Is.EqualTo(@"<<modDirectory>>\Darth_Malaks_Lightsaber_K1\Darth Malak's Lightsaber.exe"),
                "The executable must come from the archive, not from a file that only exists on disk.");
        }

        /// <summary>
        /// Install residue that TSLPatcher writes (backup/uninstall trees full of the game files a
        /// previous run displaced) must never be mistaken for mod content, or the generator would
        /// emit Move instructions copying a prior installation's originals into Override.
        /// </summary>
        [Test]
        public void InstallResidue_DoesNotBecomeModContent()
        {
            _ = CreateArchive(
                "Residue Mod.zip",
                "Install.exe",
                "tslpatchdata/changes.ini");

            string root = Path.Combine(_modDirectory, "Residue Mod");
            _ = Directory.CreateDirectory(Path.Combine(root, "tslpatchdata"));
            _ = Directory.CreateDirectory(Path.Combine(root, "backup", "2026-07-30_15.33.47"));
            File.WriteAllText(Path.Combine(root, "Install.exe"), "x");
            File.WriteAllText(Path.Combine(root, "tslpatchdata", "changes.ini"), "x");
            File.WriteAllText(Path.Combine(root, "installlog.txt"), "x");
            File.WriteAllText(Path.Combine(root, "backup", "2026-07-30_15.33.47", "appearance.2da"), "x");

            ModComponent component = ComponentFor("Residue Mod", "Residue Mod");

            Assert.That(AutoInstructionGenerator.TryGenerateInstructionsFromArchive(component), Is.True);

            bool movesResidue = component.Instructions
                .Concat(component.Options.SelectMany(o => o.Instructions))
                .SelectMany(i => i.Source ?? new System.Collections.Generic.List<string>())
                .Any(s => s.IndexOf("backup", StringComparison.OrdinalIgnoreCase) >= 0
                    || s.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0);

            Assert.That(movesResidue, Is.False, "backup/ and uninstall/ trees are install residue, not mod content.");
        }

        /// <summary>
        /// Deciding that the archive wins requires listing it, and that listing is handed to the
        /// generator so the archive is opened and enumerated once per component instead of twice.
        /// Nearly every archive in the reference library has a sibling extracted folder, so the
        /// second read was being paid for almost every component.
        /// <para>
        /// Reuse is only safe while the analysis built from a precomputed listing matches the one
        /// built by walking the open archive. These cases pin that: the same archive must generate
        /// the same instructions whether or not a sibling folder exists to trigger the reuse path.
        /// </para>
        /// </summary>
        [Test]
        public void ReusedArchiveListing_GeneratesTheSameInstructionsAsReadingTheArchiveAgain(
            [ValueSource(nameof(ArchiveShapes))] string[] entryPaths)
        {
            const string archiveName = "Listing Reuse.zip";
            string withFolder = Path.Combine(_testDirectory, "WithSiblingFolder");
            string withoutFolder = Path.Combine(_testDirectory, "WithoutSiblingFolder");

            _ = CreateArchiveIn(withFolder, archiveName, entryPaths);
            _ = CreateArchiveIn(withoutFolder, archiveName, entryPaths);

            // A complete extraction beside the archive. The archive is readable, so it stays
            // authoritative - but the folder's presence is what makes the trust check list it.
            string extracted = Path.Combine(withFolder, "Listing Reuse");
            foreach (string entryPath in entryPaths)
            {
                string filePath = Path.Combine(extracted, entryPath.Replace('/', Path.DirectorySeparatorChar));
                _ = Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                File.WriteAllText(filePath, "x");
            }

            string reusedListing = GenerateAndSummarize(withFolder, archiveName);
            string freshRead = GenerateAndSummarize(withoutFolder, archiveName);

            Assert.That(reusedListing, Is.Not.Empty, "The archive should have produced instructions.");
            Assert.That(
                reusedListing,
                Is.EqualTo(freshRead),
                "Generating from the listing the trust check already read must match reading the archive again.");
        }

        private static readonly string[][] ArchiveShapes =
        {
            // TSLPatcher mod: installer executable plus tslpatchdata.
            new[] { "Install.exe", "tslpatchdata/changes.ini", "tslpatchdata/appearance.2da" },

            // Loose files spread across folders, which drives the multi-folder Choose branch.
            new[] { "Override/one.tpc", "Override/two.tpc", "Alternative/one.tpc", "readme.txt" },

            // Flat loose files at the archive root.
            new[] { "one.tpc", "two.2da" },

            // Both a patcher and loose files.
            new[] { "Install.exe", "tslpatchdata/changes.ini", "Override/extra.tpc" },
        };

        /// <summary>
        /// Generates a component's instructions from the archive in <paramref name="sourceDirectory"/>
        /// and renders them into a comparable form. Option and instruction GUIDs are freshly minted on
        /// every run, so a Choose instruction is rendered by how many options it selects between rather
        /// than by the GUIDs it holds.
        /// </summary>
        private string GenerateAndSummarize(string sourceDirectory, string archiveName)
        {
            _config.sourcePath = new DirectoryInfo(sourceDirectory);

            ModComponent component = ComponentFor("Listing Reuse", Path.GetFileNameWithoutExtension(archiveName));
            _ = AutoInstructionGenerator.TryGenerateInstructionsFromArchive(component);

            var lines = component.Instructions.Select(Render).ToList();
            lines.AddRange(component.Options
                .OrderBy(o => o.Name, StringComparer.Ordinal)
                .Select(o => $"option {o.Name}: " + string.Join(" ; ", o.Instructions.Select(Render))));

            return string.Join(Environment.NewLine, lines);
        }

        private static string Render(Instruction instruction)
        {
            string sources = instruction.Action == Instruction.ActionType.Choose
                ? $"{instruction.Source.Count} option(s)"
                : string.Join(", ", instruction.Source ?? new System.Collections.Generic.List<string>());

            return $"{instruction.Action} [{sources}] -> {instruction.Destination ?? "(none)"} "
                + $"args={instruction.Arguments ?? "(none)"} overwrite={instruction.Overwrite}";
        }

        [TestCase(@"<<modDirectory>>\to override")]
        [TestCase(@"<<modDirectory>>\installer")]
        [TestCase(@"<<modDirectory>>\f\*")]
        [TestCase("f*")]
        [TestCase(@"<<modDirectory>>\patcher")]
        [TestCase(@"<<modDirectory>>\the\*")]
        [TestCase(@"<<modDirectory>>\override\*")]
        [TestCase(@"<<modDirectory>>\Option 5\*")]
        [TestCase(@"<<modDirectory>>\files from ""Jedi Robes Override\*")]
        public void SourceIsUngroundedProseFragment_DetectsRun9Failures(string source)
        {
            Assert.That(AutoInstructionGenerator.SourceIsUngroundedProseFragment(source), Is.True);
        }

        [Test]
        public void SourceIsUngroundedProseFragment_AcceptsRealPathsAndChooseGuids()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    AutoInstructionGenerator.SourceIsUngroundedProseFragment(@"<<modDirectory>>\K1_Community_Patch_v1.10.0.zip"),
                    Is.False);
                Assert.That(
                    AutoInstructionGenerator.SourceIsUngroundedProseFragment(@"<<kotorDirectory>>\Override"),
                    Is.False);
                Assert.That(
                    AutoInstructionGenerator.SourceIsUngroundedProseFragment(Guid.NewGuid().ToString()),
                    Is.False);
            });
        }

        [Test]
        public void UngroundedNlpDraft_IsReplacedFromArchive()
        {
            _ = CreateArchive(
                "Yavin Station Hangar.zip",
                "INSTALL.exe",
                "tslpatchdata/changes.ini",
                "tslpatchdata/info.rtf");

            ModComponent component = ComponentFor("Yavin Station Hangar", "Yavin Station Hangar");
            component.Instructions.Add(new Instruction
            {
                Action = Instruction.ActionType.Patcher,
                Source = new System.Collections.Generic.List<string> { @"<<modDirectory>>\installer" },
            });

            bool generated = AutoInstructionGenerator.TryGenerateInstructionsFromArchive(component);

            Assert.That(generated, Is.True);
            Assert.That(component.Instructions.Any(i => i.Action == Instruction.ActionType.Extract), Is.True);
            Assert.That(
                component.Instructions.SelectMany(i => i.Source ?? Enumerable.Empty<string>()),
                Has.None.EqualTo(@"<<modDirectory>>\installer"));
        }

        [Test]
        public void GroundedInstructions_AreNotReplaced()
        {
            _ = CreateArchive(
                "Already Drafted.zip",
                "INSTALL.exe",
                "tslpatchdata/changes.ini");

            ModComponent component = ComponentFor("Already Drafted", "Already Drafted");
            var existing = new Instruction
            {
                Action = Instruction.ActionType.Extract,
                Source = new System.Collections.Generic.List<string> { @"<<modDirectory>>\Already Drafted.zip" },
            };
            component.Instructions.Add(existing);

            bool generated = AutoInstructionGenerator.TryGenerateInstructionsFromArchive(component);

            Assert.That(generated, Is.False);
            Assert.That(component.Instructions, Has.Count.EqualTo(1));
            Assert.That(component.Instructions[0], Is.SameAs(existing));
        }

        [Test]
        public void UngroundedNlpDraft_KeepsGroundedExclusions()
        {
            _ = CreateArchive(
                "JC's Minor Fixes for K1 v1.1.zip",
                "Straight Fixes/man26.tga",
                "Bug Fixes/skip.tga");

            ModComponent component = ComponentFor("JC's Minor Fixes", "JC's Minor Fixes for K1 v1.1");
            var exclusion = new Instruction
            {
                Action = Instruction.ActionType.Delete,
                Source = new System.Collections.Generic.List<string> { @"<<kotorDirectory>>\Override\N_SithComF.mdl" },
            };
            component.Instructions.Add(exclusion);
            component.Instructions.Add(new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new System.Collections.Generic.List<string> { @"<<modDirectory>>\the\*" },
            });

            bool generated = AutoInstructionGenerator.TryGenerateInstructionsFromArchive(component);

            Assert.That(generated, Is.True);
            Assert.That(component.Instructions, Does.Contain(exclusion));
            Assert.That(
                component.Instructions.SelectMany(i => i.Source ?? Enumerable.Empty<string>()),
                Has.None.EqualTo(@"<<modDirectory>>\the\*"));
            Assert.That(component.Instructions.Any(i => i.Action == Instruction.ActionType.Extract), Is.True);
        }

        [Test]
        public void NestedOverrideFolder_MoveTargetsTheDirectoryThatHoldsTheFiles()
        {
            _ = CreateArchive(
                "Ultimate Korriban High Resolution.zip",
                "Korriban HR/Override/LKO_wall.tpc",
                "Korriban HR/Override/LKO_floor.tpc");

            ModComponent component = ComponentFor(
                "Ultimate Korriban High Resolution",
                "Ultimate Korriban High Resolution");

            Assert.That(AutoInstructionGenerator.TryGenerateInstructionsFromArchive(component), Is.True);

            string[] moveSources = component.Instructions
                .Where(i => i.Action == Instruction.ActionType.Move)
                .SelectMany(i => i.Source)
                .ToArray();

            Assert.That(moveSources, Has.Some.EqualTo(
                @"<<modDirectory>>\Ultimate Korriban High Resolution\Korriban HR\Override\*"));
            Assert.That(moveSources, Has.None.EqualTo(
                @"<<modDirectory>>\Ultimate Korriban High Resolution\Korriban HR\*"));
        }

        [Test]
        public void MissingLibraryRootFile_IsTreatedAsUngrounded()
        {
            Assert.That(
                AutoInstructionGenerator.SourceIsMissingFromLibrary(@"<<modDirectory>>\P_CandH01.tga"),
                Is.True);
            Assert.That(
                AutoInstructionGenerator.SourceIsMissingFromLibrary(@"<<kotorDirectory>>\Override\N_SithComF.mdl"),
                Is.False);
        }

        [Test]
        public void RemoveDuplicateTgaTpc_GetsAnOverrideDestination()
        {
            _ = CreateArchive("Remove Duplicate TGA TPC.zip", "readme.txt");

            ModComponent component = ComponentFor("Remove Duplicate TGA/TPC", "Remove Duplicate TGA TPC");

            Assert.That(AutoInstructionGenerator.TryGenerateInstructionsFromArchive(component), Is.True);

            Instruction del = component.Instructions.Single(i => i.Action == Instruction.ActionType.DelDuplicate);
            Assert.That(del.Destination, Is.EqualTo(@"<<kotorDirectory>>\Override"));
            Assert.That(del.Arguments, Is.EqualTo(".tpc"));
        }
    }
}
