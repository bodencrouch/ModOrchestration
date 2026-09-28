// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.IO;
using System.Linq;
using System.Text;

using ModSync.Core.Services;

using NUnit.Framework;

namespace ModSync.Tests
{
    /// <summary>
    /// TSLRCM, the mandatory foundation of the K2 build, is generated as an Execute against a
    /// Windows Inno Setup installer. On Linux that died with
    /// <c>Win32Exception ... Permission denied</c>, which tells the user nothing. These tests pin
    /// the routing decision and the asset copy; innoextract itself is never invoked here.
    /// </summary>
    [TestFixture]
    public class InnoSetupInstallerServiceTests
    {
        [TestCase(true, true, true, ExeExecutionPlan.ExecuteDirectly)]
        [TestCase(true, true, false, ExeExecutionPlan.ExecuteDirectly)]
        [TestCase(true, false, true, ExeExecutionPlan.ExecuteDirectly)]
        [TestCase(true, false, false, ExeExecutionPlan.ExecuteDirectly)]
        [TestCase(false, false, true, ExeExecutionPlan.ExecuteDirectly)]
        [TestCase(false, false, false, ExeExecutionPlan.ExecuteDirectly)]
        [TestCase(false, true, true, ExeExecutionPlan.InnoExtract)]
        [TestCase(false, true, false, ExeExecutionPlan.MissingInnoExtract)]
        public void PlanExeExecution_CoversEveryCombination(
            bool isWindows,
            bool isInnoSetup,
            bool innoExtractAvailable,
            ExeExecutionPlan expected)
        {
            Assert.That(
                InnoSetupInstallerService.PlanExeExecution(isWindows, isInnoSetup, innoExtractAvailable),
                Is.EqualTo(expected));
        }

        [Test]
        public void MissingToolMessage_NamesTheToolAndTheFile()
        {
            string message = InnoSetupInstallerService.MissingToolMessage("/mods/tslrcm2022.exe");

            Assert.Multiple(() =>
            {
                Assert.That(message, Does.Contain("innoextract"));
                Assert.That(message, Does.Contain("tslrcm2022.exe"));
                Assert.That(message, Does.Not.Contain("Win32Exception"));
            });
        }

        [Test]
        public void BufferLooksLikeInnoSetup_RecognisesTheSignature()
        {
            byte[] inno = Encoding.ASCII.GetBytes("MZ......Inno Setup Setup Data (6.0.5)......");
            byte[] loader = Encoding.ASCII.GetBytes("MZ......rDlPtS02......");
            byte[] sevenZipSfx = Encoding.ASCII.GetBytes("MZ......7z¼¯'......");

            Assert.Multiple(() =>
            {
                Assert.That(InnoSetupInstallerService.BufferLooksLikeInnoSetup(inno, inno.Length), Is.True);
                Assert.That(InnoSetupInstallerService.BufferLooksLikeInnoSetup(loader, loader.Length), Is.True);
                Assert.That(
                    InnoSetupInstallerService.BufferLooksLikeInnoSetup(sevenZipSfx, sevenZipSfx.Length),
                    Is.False,
                    "A 7-Zip SFX must keep its existing handling.");
                Assert.That(InnoSetupInstallerService.BufferLooksLikeInnoSetup(null, 0), Is.False);
                Assert.That(InnoSetupInstallerService.BufferLooksLikeInnoSetup(inno, 0), Is.False);
            });
        }

        [Test]
        public void IsInnoSetupInstaller_ReadsTheFileOnDisk()
        {
            string directory = NewTempDirectory();
            try
            {
                string innoPath = Path.Combine(directory, "tslrcm2022.exe");
                File.WriteAllBytes(innoPath, Encoding.ASCII.GetBytes("MZ padding Inno Setup Setup Data (6.0.5) more"));

                string plainPath = Path.Combine(directory, "helper.exe");
                File.WriteAllBytes(plainPath, Encoding.ASCII.GetBytes("MZ just an ordinary program"));

                Assert.Multiple(() =>
                {
                    Assert.That(InnoSetupInstallerService.IsInnoSetupInstaller(innoPath), Is.True);
                    Assert.That(InnoSetupInstallerService.IsInnoSetupInstaller(plainPath), Is.False);
                    Assert.That(
                        InnoSetupInstallerService.IsInnoSetupInstaller(Path.Combine(directory, "absent.exe")),
                        Is.False);
                });
            }
            finally
            {
                TryDelete(directory);
            }
        }

        [Test]
        public void LocatePayloadRoot_PrefersTheAppFolder()
        {
            string directory = NewTempDirectory();
            try
            {
                _ = Directory.CreateDirectory(Path.Combine(directory, "app"));
                Assert.That(
                    InnoSetupInstallerService.LocatePayloadRoot(directory).Name,
                    Is.EqualTo("app"));
            }
            finally
            {
                TryDelete(directory);
            }
        }

        [Test]
        public void LocatePayloadRoot_FallsBackToTheExtractionRoot()
        {
            string directory = NewTempDirectory();
            try
            {
                _ = Directory.CreateDirectory(Path.Combine(directory, "Override"));
                Assert.That(
                    InnoSetupInstallerService.LocatePayloadRoot(directory).FullName.TrimEnd(Path.DirectorySeparatorChar),
                    Is.EqualTo(directory.TrimEnd(Path.DirectorySeparatorChar)));
            }
            finally
            {
                TryDelete(directory);
            }
        }

        /// <summary>
        /// The TSLRCM payload shape: game folders plus dialog.tlk, and a Windows-only launcher that
        /// must stay behind.
        /// </summary>
        [Test]
        public void CopyGameContent_TakesGameFoldersAndDialogTlk_ButNotTheLauncher()
        {
            string root = NewTempDirectory();
            try
            {
                string payload = Path.Combine(root, "app");
                foreach (string folder in new[] { "override", "modules", "lips", "movies", "streammusic", "streamvoice" })
                {
                    _ = Directory.CreateDirectory(Path.Combine(payload, folder));
                    File.WriteAllText(Path.Combine(payload, folder, "content.dat"), "x");
                }

                _ = Directory.CreateDirectory(Path.Combine(payload, "launcher"));
                File.WriteAllText(Path.Combine(payload, "launcher", "launcher.exe"), "x");
                File.WriteAllText(Path.Combine(payload, "dialog.tlk"), "tlk");
                File.WriteAllText(Path.Combine(payload, "readme.txt"), "doc");

                string gameDirectory = Path.Combine(root, "KOTOR2");
                _ = Directory.CreateDirectory(gameDirectory);
                File.WriteAllText(Path.Combine(gameDirectory, "dialog.tlk"), "original");

                int copied = InnoSetupInstallerService.CopyGameContent(
                    new DirectoryInfo(payload),
                    new DirectoryInfo(gameDirectory));

                Assert.Multiple(() =>
                {
                    Assert.That(copied, Is.EqualTo(7), "Six folder files plus dialog.tlk.");
                    Assert.That(File.Exists(Path.Combine(gameDirectory, "override", "content.dat")), Is.True);
                    Assert.That(File.Exists(Path.Combine(gameDirectory, "streamvoice", "content.dat")), Is.True);
                    Assert.That(
                        File.ReadAllText(Path.Combine(gameDirectory, "dialog.tlk")),
                        Is.EqualTo("tlk"),
                        "dialog.tlk must be replaced by the mod's copy.");
                    Assert.That(
                        Directory.Exists(Path.Combine(gameDirectory, "launcher")),
                        Is.False,
                        "The Windows launcher is not game content.");
                    Assert.That(File.Exists(Path.Combine(gameDirectory, "readme.txt")), Is.False);
                });
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Test]
        public void CopyGameContent_CopiesNestedFolders()
        {
            string root = NewTempDirectory();
            try
            {
                string payload = Path.Combine(root, "app");
                string nested = Path.Combine(payload, "modules", "extra");
                _ = Directory.CreateDirectory(nested);
                File.WriteAllText(Path.Combine(nested, "deep.mod"), "x");

                string gameDirectory = Path.Combine(root, "KOTOR2");
                _ = Directory.CreateDirectory(gameDirectory);

                int copied = InnoSetupInstallerService.CopyGameContent(
                    new DirectoryInfo(payload),
                    new DirectoryInfo(gameDirectory));

                Assert.Multiple(() =>
                {
                    Assert.That(copied, Is.EqualTo(1));
                    Assert.That(
                        File.Exists(Path.Combine(gameDirectory, "modules", "extra", "deep.mod")),
                        Is.True);
                });
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Test]
        public void GameContentDirectories_DoNotIncludeTheLauncher()
        {
            Assert.That(
                InnoSetupInstallerService.GameContentDirectories.Contains("launcher"),
                Is.False);
        }

        private static string NewTempDirectory()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ModSync_Inno_" + Guid.NewGuid());
            _ = Directory.CreateDirectory(directory);
            return directory;
        }

        private static void TryDelete(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
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
    }
}
