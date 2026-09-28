// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

using ModSync.Core;
using ModSync.Core.Services;

using NUnit.Framework;

namespace ModSync.Tests
{
    /// <summary>
    /// A TSLPatcher mod's installer is the executable sitting BESIDE the tslpatchdata folder, never
    /// inside it, and its name is not predictable: of the 67 already-extracted TSLPatcher mods in the
    /// reference library only 24 are named "TSLPatcher.exe", the rest use "INSTALL.exe",
    /// "installer.exe" or the mod's own title ("Juhani Appearance Overhaul.exe").
    /// <para>
    /// The generator used to look for the executable only INSIDE tslpatchdata, so it almost never
    /// found one and fell back to a hardcoded "TSLPatcher.exe". When it did find one it picked
    /// nwnnsscomp.exe, the bundled NWScript compiler. Both produced Source paths that exist in no
    /// archive ("Juhani Appearance Overhaul/TSLPatcher.exe", "TSL/nwnnsscomp.exe") and failed the
    /// entire install at validation.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class PatcherExecutableDetectionTests
    {
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "modsync-patcher-exe-" + Path.GetRandomFileName());
            _ = Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }

        private string CreateArchive(string archiveName, params string[] entryPaths)
        {
            string archivePath = Path.Combine(_tempDir, archiveName);
            using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
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

        private static List<string> PatcherSources(ModComponent component)
        {
            var sources = new List<string>();

            foreach (Instruction instruction in component.Instructions
                .Where(i => i.Action == Instruction.ActionType.Patcher))
            {
                sources.AddRange(instruction.Source);
            }

            foreach (Option option in component.Options)
            {
                foreach (Instruction instruction in option.Instructions
                    .Where(i => i.Action == Instruction.ActionType.Patcher))
                {
                    sources.AddRange(instruction.Source);
                }
            }

            return sources;
        }

        /// <summary>
        /// The exact shape that produced "Juhani Appearance Overhaul/TSLPatcher.exe". The real archive
        /// is flat (verified against the library copy: entries begin at "tslpatchdata/"), so extraction
        /// creates one folder named after the archive and the installer sits directly inside it.
        /// </summary>
        [Test]
        public void FlatArchive_UsesRealExecutableName_NotTslPatcherExe()
        {
            string archivePath = CreateArchive(
                "Juhani Appearance Overhaul.zip",
                "Juhani Appearance Overhaul.exe",
                "tslpatchdata/changes.ini",
                "tslpatchdata/juhani.tpc");

            var component = new ModComponent { Name = "Juhani Appearance Overhaul" };

            Assert.That(AutoInstructionGenerator.GenerateInstructions(component, archivePath), Is.True);

            List<string> sources = PatcherSources(component);
            Assert.That(sources, Has.Count.EqualTo(1));
            Assert.That(
                sources[0],
                Is.EqualTo(@"<<modDirectory>>\Juhani Appearance Overhaul\Juhani Appearance Overhaul.exe"));
            Assert.That(sources[0], Does.Not.Contain("TSLPatcher.exe"));
        }

        /// <summary>
        /// An archive carrying its own top-level folder nests one level deeper once extracted, because
        /// extraction always creates a folder named after the archive first. Verified on disk:
        /// "Character Start Up Changes.zip" is present as
        /// "Character Start Up Changes/Character Start Up Changes/TSLPatcher.exe".
        /// <para>
        /// The generator used to emit only the inner segment, pointing one level too shallow at a file
        /// that does not exist. Validation missed it because the archive-entry lookup accepts an entry
        /// either as stored or prefixed with the archive name.
        /// </para>
        /// </summary>
        [Test]
        public void SelfRootedArchive_KeepsBothTheExtractionFolderAndTheInnerFolder()
        {
            string archivePath = CreateArchive(
                "Character Start Up Changes.zip",
                "Character Start Up Changes/TSLPatcher.exe",
                "Character Start Up Changes/tslpatchdata/changes.ini");

            var component = new ModComponent { Name = "Character Startup Changes" };

            Assert.That(AutoInstructionGenerator.GenerateInstructions(component, archivePath), Is.True);

            List<string> sources = PatcherSources(component);
            Assert.That(sources, Has.Count.EqualTo(1));
            Assert.That(
                sources[0],
                Is.EqualTo(@"<<modDirectory>>\Character Start Up Changes\Character Start Up Changes\TSLPatcher.exe"));
        }

        /// <summary>
        /// A flat archive has no top-level folder of its own; extraction creates one named after the
        /// archive, so the patcher path is the archive base name and the executable name still comes
        /// from the archive rather than a guess.
        /// </summary>
        [Test]
        public void FlatArchive_UsesRealExecutableName_AndArchiveNameAsFolder()
        {
            string archivePath = CreateArchive(
                "Rebalanced Grenades v1.0.zip",
                "Install Rebalanced Grenades.exe",
                "tslpatchdata/changes.ini",
                "tslpatchdata/grenade.uti");

            var component = new ModComponent { Name = "Rebalanced Grenades" };

            Assert.That(AutoInstructionGenerator.GenerateInstructions(component, archivePath), Is.True);

            List<string> sources = PatcherSources(component);
            Assert.That(sources, Has.Count.EqualTo(1));
            Assert.That(
                sources[0],
                Is.EqualTo(@"<<modDirectory>>\Rebalanced Grenades v1.0\Install Rebalanced Grenades.exe"));
        }

        /// <summary>
        /// nwnnsscomp.exe is the NWScript compiler bundled inside tslpatchdata, not the installer.
        /// Selecting it produced sources such as "TSL/nwnnsscomp.exe".
        /// </summary>
        [Test]
        public void NwnnsscompInsideTslPatchData_IsNeverSelectedAsInstaller()
        {
            string archivePath = CreateArchive(
                "Alignment Affects Force Powers.zip",
                "INSTALL.exe",
                "tslpatchdata/nwnnsscomp.exe",
                "tslpatchdata/changes.ini");

            var component = new ModComponent { Name = "Alignment Affects Force Powers" };

            Assert.That(AutoInstructionGenerator.GenerateInstructions(component, archivePath), Is.True);

            List<string> sources = PatcherSources(component);
            Assert.That(sources, Has.Count.EqualTo(1));
            Assert.That(sources[0], Does.Not.Contain("nwnnsscomp"));
            Assert.That(
                sources[0],
                Is.EqualTo(@"<<modDirectory>>\Alignment Affects Force Powers\INSTALL.exe"));
        }

        /// <summary>
        /// With no installer beside tslpatchdata there is nothing to resolve. Emitting a guessed name
        /// creates a Source path that exists in no archive and fails the whole install at validation,
        /// so no Patcher instruction may be produced at all.
        /// </summary>
        [Test]
        public void NoExecutableBesideTslPatchData_GeneratesNoPatcherInstruction()
        {
            string archivePath = CreateArchive(
                "Toolless Mod.zip",
                "tslpatchdata/nwnnsscomp.exe",
                "tslpatchdata/changes.ini");

            var component = new ModComponent { Name = "Toolless Mod" };

            _ = AutoInstructionGenerator.GenerateInstructions(component, archivePath);

            Assert.That(PatcherSources(component), Is.Empty);
        }

        /// <summary>
        /// A namespaced mod ships one installer at the root and selects the namespace by index, so
        /// every namespace option must point at that same real executable.
        /// </summary>
        [Test]
        public void NamespacedArchive_AllOptionsUseTheSameRealExecutable()
        {
            string archivePath = CreateArchive(
                "Sith Soldier Texture Restoration-v2.4.zip",
                "Install.exe",
                "tslpatchdata/namespaces.ini",
                "tslpatchdata/Main/changes.ini",
                "tslpatchdata/Alt/changes.ini");

            var component = new ModComponent { Name = "Sith Soldier Texture Restoration" };

            _ = AutoInstructionGenerator.GenerateInstructions(component, archivePath);

            List<string> sources = PatcherSources(component);
            foreach (string source in sources)
            {
                Assert.That(
                    source,
                    Is.EqualTo(@"<<modDirectory>>\Sith Soldier Texture Restoration-v2.4\Install.exe"),
                    "Namespace options must all point at the single real installer at the archive root.");
            }
        }
    }
}
