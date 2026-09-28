// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ModSync.Core;
using ModSync.Core.Services.FileSystem;
using ModSync.Core.Services.Validation;

using NUnit.Framework;

using SharpCompress.Archives.Zip;
using SharpCompress.Common;


namespace ModSync.Tests
{
    /// <summary>
    /// Dry-run validation must model the post-extraction tree, not stat the scratch directory
    /// before the extraction that fills it has run. A K2 install aborted with 84 of 146 components
    /// flagged "missing archive" while every archive was present on disk: the virtual provider
    /// registered extracted entries one directory deeper than the real install writes them, so the
    /// component's own Move never saw the files its own Extract produces.
    /// </summary>
    [TestFixture]
    public class DryRunExtractThenMoveTests
    {
        private string _testDirectory;
        private string _modDirectory;
        private string _kotorDirectory;
        private string _scratchDirectory;
        private MainConfig _config;

        [SetUp]
        public void SetUp()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), "ModSync_DryRunExtractMove_" + Guid.NewGuid());
            _modDirectory = Path.Combine(_testDirectory, "Mods");
            _kotorDirectory = Path.Combine(_testDirectory, "KOTOR");
            _scratchDirectory = Path.Combine(_testDirectory, "k2_auto_extract");
            _ = Directory.CreateDirectory(_modDirectory);
            _ = Directory.CreateDirectory(_kotorDirectory);
            _ = Directory.CreateDirectory(Path.Combine(_kotorDirectory, "Override"));
            _ = Directory.CreateDirectory(_scratchDirectory);

            _config = new MainConfig
            {
                sourcePath = new DirectoryInfo(_modDirectory),
                destinationPath = new DirectoryInfo(_kotorDirectory),
            };
            MainConfig.ExtractScratchPath = new DirectoryInfo(_scratchDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            MainConfig.ExtractScratchPath = null;
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

        [Test]
        public async Task DryRun_ExtractThenMoveFromScratch_DoesNotReportMissingSource()
        {
            CreateZip(
                "PLC_Desk.zip",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "plc_desk.mdl", "model" },
                    { "plc_desk.mdx", "modelx" },
                });

            ModComponent component = BuildExtractThenMoveComponent();

            DryRunValidationResult result = await DryRunValidator.ValidateInstallationAsync(
                new List<ModComponent> { component },
                skipDependencyCheck: true,
                CancellationToken.None).ConfigureAwait(false);

            List<ValidationIssue> errors = result.Issues
                .Where(i => i.Severity == ValidationSeverity.Error)
                .ToList();
            List<ValidationIssue> skipped = result.Issues
                .Where(i => string.Equals(i.Category, "DryRunSkipped", StringComparison.Ordinal))
                .ToList();

            Assert.Multiple(() =>
            {
                Assert.That(
                    errors,
                    Is.Empty,
                    "Extract -> Move within one component must validate: "
                    + string.Join(" | ", errors.Select(i => i.Message)));
                Assert.That(
                    skipped,
                    Is.Empty,
                    "The component must not be silently skipped as 'sources unavailable': "
                    + string.Join(" | ", skipped.Select(i => i.Message)));
            });
        }

        /// <summary>
        /// Pins the exact regression: the virtual provider must register extracted entries at the
        /// same paths the real provider writes them to. It previously appended an extra
        /// archive-name segment whenever the destination was explicit.
        /// </summary>
        [Test]
        public async Task VirtualExtract_UsesSameRootAsRealExtract()
        {
            string zipPath = CreateZip(
                "PLC_Desk.zip",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "plc_desk.mdl", "model" },
                });

            string explicitDestination = Path.Combine(_scratchDirectory, "PLC_Desk");

            var vfs = new VirtualFileSystemProvider();
            await vfs.InitializeFromRealFileSystemAsync(_modDirectory).ConfigureAwait(false);
            List<string> virtualPaths = await vfs.ExtractArchiveAsync(zipPath, explicitDestination).ConfigureAwait(false);

            var real = new RealFileSystemProvider();
            List<string> realPaths = await real.ExtractArchiveAsync(zipPath, explicitDestination).ConfigureAwait(false);

            Assert.That(
                virtualPaths.Select(Path.GetFullPath).OrderBy(p => p, StringComparer.Ordinal),
                Is.EqualTo(realPaths.Select(Path.GetFullPath).OrderBy(p => p, StringComparer.Ordinal)),
                "Virtual and real extraction must agree on where entries land.");
        }

        /// <summary>
        /// The same component with its archive genuinely absent must still fail, so the fix above
        /// cannot be mistaken for "validation stopped checking".
        /// </summary>
        [Test]
        public async Task DryRun_ExtractThenMove_WithArchiveAbsent_StillReportsMissingSource()
        {
            ModComponent component = BuildExtractThenMoveComponent();

            DryRunValidationResult result = await DryRunValidator.ValidateInstallationAsync(
                new List<ModComponent> { component },
                skipDependencyCheck: true,
                CancellationToken.None).ConfigureAwait(false);

            Assert.That(
                result.Issues.Exists(i =>
                    i.Severity == ValidationSeverity.Error
                    || string.Equals(i.Category, "DryRunSkipped", StringComparison.Ordinal)),
                Is.True,
                "A component whose archive is not on disk must still be reported.");
        }

        private ModComponent BuildExtractThenMoveComponent()
        {
            var component = new ModComponent
            {
                Name = "PLC_Desk",
                Guid = Guid.NewGuid(),
                IsSelected = true,
            };

            var extract = new Instruction
            {
                Action = Instruction.ActionType.Extract,
                Source = new List<string> { @"<<modDirectory>>\PLC_Desk.zip" },
            };
            var move = new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string> { @"<<modDirectory>>\PLC_Desk\*" },
                Destination = @"<<gameDirectory>>\Override",
                Overwrite = true,
            };

            component.Instructions.Add(extract);
            component.Instructions.Add(move);
            extract.SetParentComponent(component);
            move.SetParentComponent(component);
            return component;
        }

        private string CreateZip(string fileName, Dictionary<string, string> entries)
        {
            string zipPath = Path.Combine(_modDirectory, fileName);
            using (var archive = ZipArchive.CreateArchive())
            {
                foreach (KeyValuePair<string, string> entry in entries)
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(entry.Value);
                    _ = archive.AddEntry(entry.Key, new MemoryStream(bytes), closeStream: true);
                }

                using (FileStream stream = File.Create(zipPath))
                {
                    archive.SaveTo(stream, new SharpCompress.Writers.Zip.ZipWriterOptions(CompressionType.None));
                }
            }

            return zipPath;
        }
    }
}
