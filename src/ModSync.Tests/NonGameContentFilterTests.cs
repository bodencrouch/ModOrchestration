// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using ModSync.Core;
using ModSync.Core.Services;
using ModSync.Core.Services.FileSystem;

using NUnit.Framework;

namespace ModSync.Tests
{
    /// <summary>
    /// A generated <c>folder\*</c> sweep must carry game content into Override and nothing else.
    /// Completed K1 and K2 installs had macOS resource forks, .DS_Store, a LibreOffice lock file,
    /// desktop.ini, installlog.txt, screenshots, readmes and a stray TSLPatcher.exe in Override;
    /// the hand-built reference builds have zero of those.
    /// </summary>
    [TestFixture]
    public class NonGameContentFilterTests
    {
        private string _testDirectory;
        private string _modDirectory;
        private string _kotorDirectory;
        private MainConfig _config;

        [SetUp]
        public void SetUp()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), "ModSync_NonGameContent_" + Guid.NewGuid());
            _modDirectory = Path.Combine(_testDirectory, "Mods");
            _kotorDirectory = Path.Combine(_testDirectory, "KOTOR");
            _ = Directory.CreateDirectory(_modDirectory);
            _ = Directory.CreateDirectory(Path.Combine(_kotorDirectory, "Override"));

            _config = new MainConfig
            {
                sourcePath = new DirectoryInfo(_modDirectory),
                destinationPath = new DirectoryInfo(_kotorDirectory),
            };
            MainConfig.ExtractScratchPath = null;
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
                // Ignore cleanup errors.
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore cleanup errors.
            }
        }

        [TestCase("._p_attnh1.tga")]
        [TestCase("._read-me.txt")]
        [TestCase("._.ds_store")]
        [TestCase(".DS_Store")]
        [TestCase(".ds_store")]
        [TestCase(".~lock.silent sion restoration readme.doc#")]
        [TestCase("desktop.ini")]
        [TestCase("Thumbs.db")]
        [TestCase("installlog.txt")]
        [TestCase("readme.txt")]
        [TestCase("notes.rtf")]
        [TestCase("manual.pdf")]
        [TestCase("readme.doc")]
        [TestCase("readme.docx")]
        [TestCase("README.md")]
        [TestCase("index.html")]
        [TestCase("page.htm")]
        [TestCase("link.url")]
        [TestCase("changes.ini")]
        [TestCase("preview.jpg")]
        [TestCase("preview.jpeg")]
        [TestCase("screenshot.png")]
        [TestCase("shot.bmp")]
        [TestCase("anim.gif")]
        [TestCase("source.psd")]
        [TestCase("TSLPatcher.exe")]
        [TestCase("install.bat")]
        [TestCase("helper.dll")]
        [TestCase("run.cmd")]
        [TestCase("install.sh")]
        [TestCase(@"some\folder\readme.txt")]
        public void IsNonGameContent_RejectsPackagingDebris(string fileName)
        {
            Assert.That(NonGameContentFilter.IsNonGameContent(fileName), Is.True, fileName);
        }

        [TestCase(".tga")]
        [TestCase(".tpc")]
        [TestCase(".dds")]
        [TestCase(".txi")]
        [TestCase(".mdl")]
        [TestCase(".mdx")]
        [TestCase(".ncs")]
        [TestCase(".nss")]
        [TestCase(".utc")]
        [TestCase(".utp")]
        [TestCase(".uti")]
        [TestCase(".utd")]
        [TestCase(".utm")]
        [TestCase(".utt")]
        [TestCase(".utw")]
        [TestCase(".ute")]
        [TestCase(".uts")]
        [TestCase(".dlg")]
        [TestCase(".are")]
        [TestCase(".git")]
        [TestCase(".pth")]
        [TestCase(".ifo")]
        [TestCase(".2da")]
        [TestCase(".tlk")]
        [TestCase(".mod")]
        [TestCase(".rim")]
        [TestCase(".erf")]
        [TestCase(".bik")]
        [TestCase(".wav")]
        [TestCase(".mp3")]
        [TestCase(".lip")]
        [TestCase(".gui")]
        [TestCase(".ssf")]
        public void IsNonGameContent_NeverRejectsGameResources(string extension)
        {
            Assert.That(
                NonGameContentFilter.IsNonGameContent("p_attnh1" + extension),
                Is.False,
                extension);
        }

        [Test]
        public void IsNonGameContent_IsCaseInsensitiveOnExtensions()
        {
            Assert.Multiple(() =>
            {
                Assert.That(NonGameContentFilter.IsNonGameContent("READ ME.TXT"), Is.True);
                Assert.That(NonGameContentFilter.IsNonGameContent("P_ATTNH1.TGA"), Is.False);
            });
        }

        [Test]
        public async Task GeneratedWildcardMove_SweepsGameFilesOnly()
        {
            string folder = Path.Combine(_modDirectory, "Trandoshans");
            _ = Directory.CreateDirectory(folder);
            WriteAll(
                folder,
                "p_attnh1.tga",
                "n_trandoshan.mdl",
                "._p_attnh1.tga",
                ".DS_Store",
                "desktop.ini",
                "installlog.txt",
                "read-me.txt",
                "preview.jpg",
                "TSLPatcher.exe");

            Instruction move = BuildWildcardMove(generated: true);
            ModComponent component = Wrap(move);

            var provider = new RealFileSystemProvider();
            move.SetFileSystemProvider(provider);
            Instruction.ActionExitCode exitCode = await component.ExecuteSingleInstructionAsync(
                move, 0, new List<ModComponent> { component }, provider).ConfigureAwait(false);

            string overrideDir = Path.Combine(_kotorDirectory, "Override");
            List<string> landed = Directory.GetFiles(overrideDir)
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(Instruction.ActionExitCode.Success));
                Assert.That(landed, Is.EquivalentTo(new[] { "n_trandoshan.mdl", "p_attnh1.tga" }));
            });
        }

        /// <summary>
        /// A hand-authored instruction that names a specific documentation file still installs it.
        /// The filter describes how a path was produced, not what the author asked for.
        /// </summary>
        [Test]
        public async Task HandAuthoredLiteralSource_StillInstallsDocumentation()
        {
            WriteAll(_modDirectory, "read-me.txt");

            var move = new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string> { @"<<modDirectory>>\read-me.txt" },
                Destination = @"<<gameDirectory>>\Override",
                Overwrite = true,
            };
            ModComponent component = Wrap(move);

            var provider = new RealFileSystemProvider();
            move.SetFileSystemProvider(provider);
            Instruction.ActionExitCode exitCode = await component.ExecuteSingleInstructionAsync(
                move, 0, new List<ModComponent> { component }, provider).ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(Instruction.ActionExitCode.Success));
                Assert.That(
                    File.Exists(Path.Combine(_kotorDirectory, "Override", "read-me.txt")),
                    Is.True,
                    "An explicitly named file must still be installed.");
            });
        }

        /// <summary>
        /// A wildcard the generator did NOT produce is left alone, so this change cannot silently
        /// alter existing hand-written TOML builds.
        /// </summary>
        [Test]
        public async Task UnmarkedWildcardMove_IsNotFiltered()
        {
            string folder = Path.Combine(_modDirectory, "Trandoshans");
            _ = Directory.CreateDirectory(folder);
            WriteAll(folder, "p_attnh1.tga", "read-me.txt");

            Instruction move = BuildWildcardMove(generated: false);
            ModComponent component = Wrap(move);

            var provider = new RealFileSystemProvider();
            move.SetFileSystemProvider(provider);
            _ = await component.ExecuteSingleInstructionAsync(
                move, 0, new List<ModComponent> { component }, provider).ConfigureAwait(false);

            Assert.That(
                File.Exists(Path.Combine(_kotorDirectory, "Override", "read-me.txt")),
                Is.True,
                "Only generated sweeps are filtered.");
        }

        /// <summary>
        /// A folder holding only excluded files keeps its unfiltered list, so the component reports
        /// "installed nothing useful" instead of "missing source files".
        /// </summary>
        [Test]
        public async Task GeneratedWildcardMove_WithOnlyDebris_DoesNotFailTheComponent()
        {
            string folder = Path.Combine(_modDirectory, "Trandoshans");
            _ = Directory.CreateDirectory(folder);
            WriteAll(folder, "read-me.txt", "preview.jpg");

            Instruction move = BuildWildcardMove(generated: true);
            ModComponent component = Wrap(move);

            var provider = new RealFileSystemProvider();
            move.SetFileSystemProvider(provider);
            Instruction.ActionExitCode exitCode = await component.ExecuteSingleInstructionAsync(
                move, 0, new List<ModComponent> { component }, provider).ConfigureAwait(false);

            Assert.That(exitCode, Is.EqualTo(Instruction.ActionExitCode.Success));
        }

        private static Instruction BuildWildcardMove(bool generated)
        {
            return new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string> { @"<<modDirectory>>\Trandoshans\*" },
                Destination = @"<<gameDirectory>>\Override",
                Overwrite = true,
                ExcludeNonGameContent = generated,
            };
        }

        private static ModComponent Wrap(Instruction instruction)
        {
            var component = new ModComponent
            {
                Name = "Trandoshans Rescaled",
                Guid = Guid.NewGuid(),
                IsSelected = true,
            };
            component.Instructions.Add(instruction);
            instruction.SetParentComponent(component);
            return component;
        }

        private static void WriteAll(string directory, params string[] fileNames)
        {
            foreach (string name in fileNames)
            {
                File.WriteAllText(Path.Combine(directory, name), "x");
            }
        }
    }
}
