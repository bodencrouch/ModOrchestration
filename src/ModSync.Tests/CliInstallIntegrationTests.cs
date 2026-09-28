// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;

using ModSync.Core;
using ModSync.Core.Services;

using NUnit.Framework;

namespace ModSync.Tests
{
    [TestFixture]
    public sealed class CliInstallIntegrationTests
    {
        private string _tempRoot = string.Empty;
        private string _modsDirectory = string.Empty;
        private string _gameDirectory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "ModSync_CliInstallTests", Guid.NewGuid().ToString("N"));
            _modsDirectory = Path.Combine(_tempRoot, "mods");
            _gameDirectory = Path.Combine(_tempRoot, "game");

            Directory.CreateDirectory(_modsDirectory);
            Directory.CreateDirectory(_gameDirectory);
            Directory.CreateDirectory(Path.Combine(_gameDirectory, "Override"));
        }

        [TearDown]
        public void TearDown()
        {
            // --best-effort / --no-checkpoint set process-wide MainConfig statics; don't leak them.
            if (MainConfig.Instance != null)
            {
                MainConfig.Instance.continueInstallOnMissingSources = false;
                MainConfig.Instance.continueInstallOnModFailure = false;
                MainConfig.Instance.noCheckpoint = false;
            }

            if (Directory.Exists(_tempRoot))
            {
                try
                {
                    Directory.Delete(_tempRoot, recursive: true);
                }
                catch
                {
                    // Best effort cleanup.
                }
            }
        }

        [Test]
        public void CliInstall_UsesSharedPipelineAndExtractsArchiveIntoGameDirectory()
        {
            string archivePath = Path.Combine(_modsDirectory, "cli_mod.zip");
            string outputFilePath = Path.Combine(_gameDirectory, "Override", "hello.txt");
            string tomlPath = Path.Combine(_tempRoot, "cli_install.toml");

            using (var archive = System.IO.Compression.ZipFile.Open(archivePath, System.IO.Compression.ZipArchiveMode.Create))
            {
                System.IO.Compression.ZipArchiveEntry entry = archive.CreateEntry("hello.txt");
                using (StreamWriter writer = new StreamWriter(entry.Open()))
                {
                    writer.Write("cli install content");
                }
            }

            string componentGuid = Guid.NewGuid().ToString();
            string instructionGuid = Guid.NewGuid().ToString();
            string tomlContents = new StringBuilder()
                .AppendLine("[metadata]")
                .AppendLine("fileFormatVersion = \"2.0\"")
                .AppendLine()
                .AppendLine("[[thisMod]]")
                .AppendLine($"Guid = \"{componentGuid}\"")
                .AppendLine("Name = \"CLI Install Test Mod\"")
                .AppendLine("IsSelected = true")
                .AppendLine("Category = [\"Test\"]")
                .AppendLine("Language = [\"YES\"]")
                .AppendLine()
                .AppendLine("[[thisMod.Instructions]]")
                .AppendLine($"Guid = \"{instructionGuid}\"")
                .AppendLine("Action = \"Extract\"")
                .AppendLine("Source = [\"<<modDirectory>>\\\\cli_mod.zip\"]")
                .AppendLine("Destination = \"<<kotorDirectory>>\\\\Override\"")
                .ToString();

            File.WriteAllText(tomlPath, tomlContents);

            var loadedComponents = FileLoadingService.LoadFromFile(tomlPath);
            Assert.That(loadedComponents, Has.Count.EqualTo(1), "Generated TOML should deserialize into a single component before CLI execution");

            int exitCode = ModSync.Core.Program.Main(new[]
            {
                "install",
                "-i", tomlPath,
                "-g", _gameDirectory,
                "-s", _modsDirectory,
                "--skip-validation",
                "--ignore-errors",
                "-y",
            });

            Assert.Multiple(() =>
            {
                // --skip-validation finishes completed-unverified (witness semantics): the archive is
                // still extracted, but the CLI must not report Success.
                Assert.That(
                    exitCode,
                    Is.EqualTo(ModSync.Core.CLI.ModBuildConverter.CompletedUnverifiedExitCode),
                    "Unverified CLI install should exit completed-unverified");
                Assert.That(File.Exists(outputFilePath), Is.True, "CLI install should extract archive contents into the game directory");
                Assert.That(File.ReadAllText(outputFilePath), Is.EqualTo("cli install content"));
            });
        }

        [Test]
        public void CliInstall_BestEffortWithMissingArchive_ExitsCompletedUnverifiedNotZero()
        {
            // Regression: --best-effort used to take an exit-0 shortcut when it skipped a missing
            // archive (MissingSourceFiles). Witness plan R9 / U4.3: it must finish completed-unverified.
            string archivePath = Path.Combine(_modsDirectory, "present_mod.zip");
            string outputFilePath = Path.Combine(_gameDirectory, "Override", "present.txt");
            string tomlPath = Path.Combine(_tempRoot, "best_effort_install.toml");

            using (var archive = System.IO.Compression.ZipFile.Open(archivePath, System.IO.Compression.ZipArchiveMode.Create))
            {
                System.IO.Compression.ZipArchiveEntry entry = archive.CreateEntry("present.txt");
                using (StreamWriter writer = new StreamWriter(entry.Open()))
                {
                    writer.Write("present content");
                }
            }

            string tomlContents = new StringBuilder()
                .AppendLine("[metadata]")
                .AppendLine("fileFormatVersion = \"2.0\"")
                .AppendLine()
                .Append(ExtractComponentToml("Present Mod", "present_mod.zip"))
                .AppendLine()
                .Append(ExtractComponentToml("Missing Mod", "missing_mod.zip"))
                .ToString();
            File.WriteAllText(tomlPath, tomlContents);

            var logLines = new ConcurrentQueue<string>();
            void Capture(string message) => logLines.Enqueue(message ?? string.Empty);
            Logger.Logged += Capture;
            int exitCode;
            try
            {
                exitCode = ModSync.Core.Program.Main(new[]
                {
                    "install",
                    "-i", tomlPath,
                    "-g", _gameDirectory,
                    "-s", _modsDirectory,
                    "--skip-validation",
                    "--no-checkpoint",
                    "--best-effort",
                    "--ignore-errors",
                    "-y",
                });
            }
            finally
            {
                Logger.Logged -= Capture;
            }

            Assert.Multiple(() =>
            {
                Assert.That(
                    exitCode,
                    Is.EqualTo(ModSync.Core.CLI.ModBuildConverter.CompletedUnverifiedExitCode),
                    "A best-effort run that skipped a missing archive must not exit 0");
                Assert.That(File.Exists(outputFilePath), Is.True, "The mod whose archive exists should still install");
                Assert.That(
                    logLines.Any(line => line.IndexOf("finished unverified, with", StringComparison.OrdinalIgnoreCase) >= 0),
                    Is.True,
                    "The skipped/failed components must be reported on their own unverified line");
            });
        }

        private static string ExtractComponentToml(string name, string archiveFileName)
        {
            return new StringBuilder()
                .AppendLine("[[thisMod]]")
                .AppendLine($"Guid = \"{Guid.NewGuid()}\"")
                .AppendLine($"Name = \"{name}\"")
                .AppendLine("IsSelected = true")
                .AppendLine("Category = [\"Test\"]")
                .AppendLine("Language = [\"YES\"]")
                .AppendLine()
                .AppendLine("[[thisMod.Instructions]]")
                .AppendLine($"Guid = \"{Guid.NewGuid()}\"")
                .AppendLine("Action = \"Extract\"")
                .AppendLine($"Source = [\"<<modDirectory>>\\\\{archiveFileName}\"]")
                .AppendLine("Destination = \"<<kotorDirectory>>\\\\Override\"")
                .ToString();
        }
    }
}
