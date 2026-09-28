// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ModSync.Tests.Fixtures
{
    /// <summary>Paths of the generated synthetic mod archives.</summary>
    public sealed class MockModArchiveSet
    {
        public MockModArchiveSet(string modDirectory, string looseFilesArchive, string tslPatcherArchive, string namespaceArchive)
        {
            ModDirectory = modDirectory;
            LooseFilesArchive = looseFilesArchive;
            TslPatcherArchive = tslPatcherArchive;
            NamespaceArchive = namespaceArchive;
        }

        /// <summary>Directory holding the archives - the ModSync "mod directory".</summary>
        public string ModDirectory { get; }

        /// <summary>Flat archive of loose override files. No installer, no tslpatchdata.</summary>
        public string LooseFilesArchive { get; }

        /// <summary>TSLPatcher-style archive whose installer is deliberately not named TSLPatcher.exe.</summary>
        public string TslPatcherArchive { get; }

        /// <summary>Multi-namespace archive whose root folder is doubled inside the zip.</summary>
        public string NamespaceArchive { get; }
    }

    /// <summary>
    /// Builds the synthetic mod archives the mock-install checks run against.
    /// <para>
    /// Each archive reproduces a packaging shape that has caused a real ModSync defect, so a
    /// regression puts the workflow red instead of needing the 143 GB archive library:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="MockModArchiveSet.TslPatcherArchive"/> names its installer
    /// <c>Mock Mod Installer.exe</c>. Code that falls back to a hardcoded <c>TSLPatcher.exe</c>
    /// produces a Source path that exists in no archive.
    /// </description></item>
    /// <item><description>
    /// <see cref="MockModArchiveSet.NamespaceArchive"/> carries its own root folder, so extraction
    /// into the mod directory yields <c>MockNamespaceMod/MockNamespaceMod/...</c>. Code that assumed
    /// a single level produced Source paths matching no file on disk.
    /// </description></item>
    /// <item><description>
    /// That same archive carries a <c>namespaces.ini</c> with two options. Deriving a patcher path
    /// from the namespace name yields the fabricated <c>&lt;namespace&gt;/&lt;namespace&gt;.exe</c>;
    /// the only executable here sits at the archive root.
    /// </description></item>
    /// </list>
    /// </summary>
    public static class MockModArchives
    {
        /// <summary>Installer filename that is deliberately not "TSLPatcher.exe".</summary>
        public const string TslPatcherInstallerName = "Mock Mod Installer.exe";

        /// <summary>Installer filename inside the doubled-root namespace archive.</summary>
        public const string NamespaceInstallerName = "Install Mod.exe";

        /// <summary>File the loose-files mod drops into the override directory.</summary>
        public const string LooseFileMarker = "mock_loose_marker.txt";

        /// <summary>File the TSLPatcher mod's [InstallList] copies into the override directory.</summary>
        public const string TslPatcherInstalledMarker = "mock_tsl_installed.txt";

        /// <summary>2DA the TSLPatcher mod appends a row to. Shipped by the mock install too.</summary>
        public const string PatchedTableName = "mock_baseline.2da";

        /// <summary>Row label the TSLPatcher mod's [2DAList] appends.</summary>
        public const string AppendedRowLabel = "mock_added_row";

        /// <summary>Marker each namespace option installs, indexed by namespace order.</summary>
        public static readonly string[] NamespaceMarkers = { "mock_namespace_a.txt", "mock_namespace_b.txt" };

        /// <summary>Strings the TSLPatcher mod appends to dialog.tlk via [TLKList].</summary>
        public static readonly string[] AppendedTlkStrings =
        {
            "ModSync mock appended string A",
            "ModSync mock appended string B",
        };

        private static readonly DateTimeOffset FixedTimestamp = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        /// <summary>
        /// Writes all three archives into <paramref name="modDirectory"/>, replacing anything there.
        /// </summary>
        public static MockModArchiveSet Create(string modDirectory)
        {
            if (string.IsNullOrWhiteSpace(modDirectory))
            {
                throw new ArgumentException("A mod directory is required.", nameof(modDirectory));
            }

            string root = Path.GetFullPath(modDirectory);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            Directory.CreateDirectory(root);

            // Staging lives outside the mod directory: ModSync scans that directory for archives
            // and loose mod folders, so leaving build scratch behind would change what it sees.
            string staging = Path.Combine(Path.GetTempPath(), "ModSync_MockMods_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);

            try
            {
                string loose = BuildLooseFilesArchive(root, Path.Combine(staging, "loose"));
                string patcher = BuildTslPatcherArchive(root, Path.Combine(staging, "tslpatcher"));
                string namespaced = BuildNamespaceArchive(root, Path.Combine(staging, "namespaced"));

                return new MockModArchiveSet(root, loose, patcher, namespaced);
            }
            finally
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (IOException)
                {
                    // Scratch only; leaving it behind is harmless.
                }
            }
        }

        /// <summary>
        /// A flat archive: entries sit at the archive root with no wrapping folder, the shape that
        /// forces ModSync to invent a folder name on extraction.
        /// </summary>
        private static string BuildLooseFilesArchive(string modDirectory, string staging)
        {
            string archivePath = Path.Combine(modDirectory, "MockLooseFilesMod.zip");
            Directory.CreateDirectory(staging);

            File.WriteAllText(
                Path.Combine(staging, LooseFileMarker),
                "Installed by the ModSync mock loose-files fixture.\n");

            MockKotorBinaries.Write2da(
                Path.Combine(staging, "mock_loose.2da"),
                new[] { "label", "value" },
                new IReadOnlyList<string>[] { new[] { "loose_row", "7" } });

            ZipDirectory(staging, archivePath, entryPrefix: string.Empty);
            return archivePath;
        }

        /// <summary>
        /// A TSLPatcher-style archive with the installer beside <c>tslpatchdata</c> at the archive
        /// root, and a changes.ini that touches all three surfaces a real mod does: dialog.tlk, a
        /// 2DA and a loose file copy.
        /// <para>
        /// The archive is flat rather than self-rooted so the extracted installer sits at exactly
        /// one predictable path. Extraction always creates a folder named after the archive, so a
        /// self-rooted archive lands at <c>&lt;name&gt;/&lt;name&gt;/</c> - a real and supported
        /// shape, kept by <see cref="MockModArchiveSet.NamespaceArchive"/> instead.
        /// </para>
        /// </summary>
        private static string BuildTslPatcherArchive(string modDirectory, string staging)
        {
            const string ArchiveName = "MockTslPatcherMod";

            string archivePath = Path.Combine(modDirectory, ArchiveName + ".zip");
            string patchData = Path.Combine(staging, "tslpatchdata");
            Directory.CreateDirectory(patchData);

            // The installer name is the point of this fixture: never "TSLPatcher.exe".
            File.WriteAllText(
                Path.Combine(staging, TslPatcherInstallerName),
                "ModSync synthetic placeholder: TSLPatcher-style installer. Never executed.\n");

            File.WriteAllText(
                Path.Combine(patchData, TslPatcherInstalledMarker),
                "Copied by the ModSync mock TSLPatcher fixture [InstallList].\n");

            // The 2DA the patcher edits. It also ships in the mock install's override directory,
            // so the patch applies to an existing table rather than needing BIF-backed game data.
            MockKotorBinaries.Write2da(
                Path.Combine(patchData, PatchedTableName),
                new[] { "label", "value" },
                new IReadOnlyList<string>[]
                {
                    new[] { "baseline_first", "1" },
                    new[] { "baseline_second", "2" },
                });

            MockKotorBinaries.WriteTlk(Path.Combine(patchData, "append.tlk"), AppendedTlkStrings);

            File.WriteAllText(Path.Combine(patchData, "changes.ini"), BuildChangesIni(), new UTF8Encoding(false));

            ZipDirectory(staging, archivePath, entryPrefix: string.Empty);
            return archivePath;
        }

        /// <summary>
        /// A multi-namespace, self-rooted archive: the zip wraps everything in
        /// <c>MockNamespaceMod/</c>, so extraction into the mod directory lands the installer at
        /// <c>MockNamespaceMod/MockNamespaceMod/</c>. Its single installer sits at that root, not
        /// inside either namespace folder.
        /// </summary>
        private static string BuildNamespaceArchive(string modDirectory, string staging)
        {
            const string RootFolder = "MockNamespaceMod";

            string archivePath = Path.Combine(modDirectory, RootFolder + ".zip");
            string patchData = Path.Combine(staging, "tslpatchdata");
            Directory.CreateDirectory(patchData);

            File.WriteAllText(
                Path.Combine(staging, NamespaceInstallerName),
                "ModSync synthetic placeholder: multi-namespace installer. Never executed.\n");

            File.WriteAllText(Path.Combine(patchData, "namespaces.ini"), BuildNamespacesIni(), new UTF8Encoding(false));

            string[] dataPaths = { "OptionA", "OptionB" };
            for (int i = 0; i < dataPaths.Length; i++)
            {
                string optionDir = Path.Combine(patchData, dataPaths[i]);
                Directory.CreateDirectory(optionDir);

                File.WriteAllText(
                    Path.Combine(optionDir, NamespaceMarkers[i]),
                    "Installed by ModSync mock namespace option " + dataPaths[i] + ".\n");

                File.WriteAllText(
                    Path.Combine(optionDir, "changes.ini"),
                    BuildNamespaceChangesIni(NamespaceMarkers[i]),
                    new UTF8Encoding(false));
            }

            ZipDirectory(staging, archivePath, entryPrefix: RootFolder + "/");
            return archivePath;
        }

        private static string BuildChangesIni()
        {
            var sb = new StringBuilder();
            sb.Append("[Settings]\n");
            sb.Append("FileExists=1\n");
            sb.Append("ConfirmMessage=N/A\n");
            sb.Append("LogLevel=3\n");
            sb.Append("InstallerMode=1\n");
            sb.Append("BackupFiles=1\n");
            sb.Append("PlaintextLog=1\n");
            sb.Append("LookupGameFolder=0\n");
            sb.Append("LookupGameNumber=1\n");
            sb.Append("SaveProcessedScripts=0\n");
            sb.Append('\n');

            // Appends both append.tlk entries to dialog.tlk. This is what makes dialog.tlk's
            // size and hash change, which is the workflow's proof the patcher really ran.
            sb.Append("[TLKList]\n");
            sb.Append("StrRef0=0\n");
            sb.Append("StrRef1=1\n");
            sb.Append('\n');

            // "Override" with a capital O on purpose: on Linux the K2 mock's directory is
            // lowercase, so this only lands if case-insensitive path resolution works.
            sb.Append("[InstallList]\n");
            sb.Append("install_folder0=Override\n");
            sb.Append('\n');
            sb.Append("[install_folder0]\n");
            sb.Append("File0=").Append(TslPatcherInstalledMarker).Append('\n');
            sb.Append('\n');

            sb.Append("[2DAList]\n");
            sb.Append("Table0=").Append(PatchedTableName).Append('\n');
            sb.Append('\n');
            sb.Append('[').Append(PatchedTableName).Append("]\n");
            sb.Append("AddRow0=mock_add_row\n");
            sb.Append('\n');
            sb.Append("[mock_add_row]\n");
            sb.Append("label=").Append(AppendedRowLabel).Append('\n');
            sb.Append("value=42\n");

            return sb.ToString();
        }

        private static string BuildNamespacesIni()
        {
            var sb = new StringBuilder();
            sb.Append("[Namespaces]\n");
            sb.Append("Namespace1=option_a\n");
            sb.Append("Namespace2=option_b\n");
            sb.Append('\n');
            sb.Append("[option_a]\n");
            sb.Append("Name=Mock Option A\n");
            sb.Append("Description=First synthetic namespace.\n");
            sb.Append("DataPath=OptionA\n");
            sb.Append("IniName=changes.ini\n");
            sb.Append('\n');
            sb.Append("[option_b]\n");
            sb.Append("Name=Mock Option B\n");
            sb.Append("Description=Second synthetic namespace.\n");
            sb.Append("DataPath=OptionB\n");
            sb.Append("IniName=changes.ini\n");

            return sb.ToString();
        }

        private static string BuildNamespaceChangesIni(string markerFileName)
        {
            var sb = new StringBuilder();
            sb.Append("[Settings]\n");
            sb.Append("FileExists=1\n");
            sb.Append("ConfirmMessage=N/A\n");
            sb.Append("LogLevel=3\n");
            sb.Append("InstallerMode=1\n");
            sb.Append("BackupFiles=1\n");
            sb.Append("PlaintextLog=1\n");
            sb.Append("LookupGameFolder=0\n");
            sb.Append("LookupGameNumber=1\n");
            sb.Append("SaveProcessedScripts=0\n");
            sb.Append('\n');
            sb.Append("[InstallList]\n");
            sb.Append("install_folder0=Override\n");
            sb.Append('\n');
            sb.Append("[install_folder0]\n");
            sb.Append("File0=").Append(markerFileName).Append('\n');

            return sb.ToString();
        }

        /// <summary>
        /// Command-line front end, reached through <c>ModSync.Tests</c>'s <c>make-mock-mods</c> verb
        /// so CI can build the archives without running a test.
        /// </summary>
        public static int RunCli(string[] args)
        {
            string outputDirectory = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--out":
                    case "-o":
                        outputDirectory = i + 1 < args.Length ? args[++i] : null;
                        break;
                    case "-h":
                    case "--help":
                        PrintUsage();
                        return 0;
                    default:
                        Console.Error.WriteLine("make-mock-mods: unknown argument '" + args[i] + "'.");
                        PrintUsage();
                        return 2;
                }
            }

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                Console.Error.WriteLine("make-mock-mods: --out is required.");
                PrintUsage();
                return 2;
            }

            MockModArchiveSet set = Create(outputDirectory);
            Console.WriteLine("modDirectory=" + set.ModDirectory);
            Console.WriteLine("looseFilesArchive=" + set.LooseFilesArchive);
            Console.WriteLine("tslPatcherArchive=" + set.TslPatcherArchive);
            Console.WriteLine("namespaceArchive=" + set.NamespaceArchive);
            return 0;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: make-mock-mods --out <directory>");
            Console.WriteLine();
            Console.WriteLine("Generates three synthetic mod archives. Contains no third-party mod data.");
            Console.WriteLine("The output directory is deleted and recreated.");
        }

        /// <summary>
        /// Zips <paramref name="sourceDirectory"/> with every entry under
        /// <paramref name="entryPrefix"/>. Entry order and timestamps are fixed so the archive
        /// bytes are reproducible.
        /// </summary>
        private static void ZipDirectory(string sourceDirectory, string archivePath, string entryPrefix)
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            List<string> files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            using (FileStream stream = File.Create(archivePath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (string file in files)
                {
                    string relative = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
                    ZipArchiveEntry entry = archive.CreateEntry(entryPrefix + relative, CompressionLevel.Optimal);
                    entry.LastWriteTime = FixedTimestamp;

                    using (Stream entryStream = entry.Open())
                    using (FileStream source = File.OpenRead(file))
                    {
                        source.CopyTo(entryStream);
                    }
                }
            }
        }
    }
}
