// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using JetBrains.Annotations;

using ModSync.Core.FileSystemUtils;
using ModSync.Core.Services.Checkpoints;
using ModSync.Core.Services.FileSystem;
using ModSync.Core.Utility;

namespace ModSync.Core.TSLPatcher
{
    /// <summary>
    /// A finished patcher run can log a "success" exit code and per-file "Copying X..." lines while the
    /// destination file never actually lands on disk (observed for K1 Ported Alien VO Replacements and the
    /// KOTOR Community Patch, both of which declare `[InstallList]` entries targeting `streamwaves`).
    /// The patcher exit code alone does not prove those files exist afterward. This verifier re-reads the
    /// same `changes.ini` (or namespaced `changes_N.ini`) the patcher was invoked against, and confirms every
    /// declared `[InstallList]` destination file outside `Override`/`modules` — i.e. one of
    /// <see cref="CheckpointPaths.ImmutableVanillaDirectoryNames"/> (streamwaves, streamsounds, streammusic,
    /// movies, data) — actually exists post-install. This is a visibility gate only: it never changes the
    /// patcher's exit code, and it does not attempt to fix why the file was not written.
    /// </summary>
    public static class InstallListDestinationVerifier
    {
        /// <summary>
        /// Returns a human-readable description ("streamwaves/AVO_NiktAngS2.wav") for every InstallList file
        /// that should have been written to a non-Override/non-modules destination but is missing. Returns an
        /// empty list when nothing is missing, when the InstallList declares no such destinations, or when the
        /// active changes.ini could not be resolved (a warning is logged for the resolution-failure case so
        /// the skip itself stays visible).
        /// </summary>
        [NotNull]
        [ItemNotNull]
        public static async Task<IReadOnlyList<string>> VerifyAsync(
            [NotNull] IFileSystemProvider fileSystemProvider,
            [NotNull] DirectoryInfo tslPatcherDirectory,
            [CanBeNull] string kotorDirectory,
            [CanBeNull] string namespaceArgument)
        {
            if (fileSystemProvider is null)
            {
                throw new ArgumentNullException(nameof(fileSystemProvider));
            }

            if (tslPatcherDirectory is null)
            {
                throw new ArgumentNullException(nameof(tslPatcherDirectory));
            }

            var missing = new List<string>();

            if (fileSystemProvider.IsDryRun || string.IsNullOrWhiteSpace(kotorDirectory))
            {
                return missing;
            }

            string changesIniPath = await ResolveActiveChangesIniPathAsync(fileSystemProvider, tslPatcherDirectory, namespaceArgument)
                .ConfigureAwait(false);
            if (changesIniPath is null)
            {
                await Logger.LogWarningAsync(
                        "[InstallList] Could not determine which changes.ini the patcher executed under"
                        + $" '{tslPatcherDirectory.FullName}'; InstallList destination verification skipped.")
                    .ConfigureAwait(false);
                return missing;
            }

            string iniText = await ReadTextAsync(fileSystemProvider, changesIniPath).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(iniText))
            {
                return missing;
            }

            Dictionary<string, Dictionary<string, string>> ini = TslPatcherVfsSimulator.ParseIni(iniText);
            if (!ini.TryGetValue("InstallList", out Dictionary<string, string> installList))
            {
                return missing;
            }

            string normalizedKotor = Path.GetFullPath(kotorDirectory);

            foreach (KeyValuePair<string, string> entry in installList)
            {
                if (!entry.Key.StartsWith("install_folder", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string targetFolder = entry.Value?.Trim();
                if (string.IsNullOrEmpty(targetFolder))
                {
                    continue;
                }

                string firstSegment = targetFolder
                    .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (!CheckpointPaths.IsImmutableVanillaDirectoryName(firstSegment))
                {
                    // Override/modules/anything else: not this bug class, not verified here.
                    continue;
                }

                if (!ini.TryGetValue(entry.Key, out Dictionary<string, string> folderSection))
                {
                    continue;
                }

                foreach (KeyValuePair<string, string> fileEntry in folderSection)
                {
                    if (fileEntry.Key.StartsWith("!", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    bool isFileKey = fileEntry.Key.StartsWith("File", StringComparison.OrdinalIgnoreCase)
                        || fileEntry.Key.StartsWith("Replace", StringComparison.OrdinalIgnoreCase);
                    if (!isFileKey)
                    {
                        continue;
                    }

                    string fileName = fileEntry.Value?.Trim();
                    if (string.IsNullOrEmpty(fileName))
                    {
                        continue;
                    }

                    string destinationPath = Path.GetFullPath(
                        Path.Combine(normalizedKotor, targetFolder.Replace('\\', Path.DirectorySeparatorChar), fileName));

                    if (MainConfig.CaseInsensitivePathing)
                    {
                        destinationPath = PathHelper.GetCaseSensitivePath(destinationPath, isFile: true).Item1;
                    }

                    if (!fileSystemProvider.FileExists(destinationPath))
                    {
                        missing.Add($"{targetFolder}/{fileName}");
                    }
                }
            }

            return missing;
        }

        [CanBeNull]
        private static async Task<string> ResolveActiveChangesIniPathAsync(
            [NotNull] IFileSystemProvider fileSystemProvider,
            [NotNull] DirectoryInfo tslPatcherDirectory,
            [CanBeNull] string namespaceArgument)
        {
            string root = tslPatcherDirectory.FullName;
            string namespacesIniPath = fileSystemProvider
                .GetFilesInDirectory(root, "namespaces.ini", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(namespacesIniPath)
                && int.TryParse(
                    (namespaceArgument ?? string.Empty).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int namespaceId))
            {
                string resolved = await ResolveFromNamespacesIniAsync(fileSystemProvider, namespacesIniPath, namespaceId)
                    .ConfigureAwait(false);
                if (!string.IsNullOrEmpty(resolved) && fileSystemProvider.FileExists(resolved))
                {
                    return resolved;
                }
            }

            return fileSystemProvider
                .GetFilesInDirectory(root, "changes.ini", SearchOption.AllDirectories)
                .OrderBy(path => path.IndexOf("tslpatchdata", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                .FirstOrDefault();
        }

        [CanBeNull]
        private static async Task<string> ResolveFromNamespacesIniAsync(
            [NotNull] IFileSystemProvider fileSystemProvider,
            [NotNull] string namespacesIniPath,
            int namespaceId)
        {
            string content = await ReadTextAsync(fileSystemProvider, namespacesIniPath).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            Dictionary<string, Dictionary<string, string>> ini = TslPatcherVfsSimulator.ParseIni(content);
            if (!ini.TryGetValue("Namespaces", out Dictionary<string, string> namespacesSection))
            {
                return null;
            }

            // "NamespaceN" keys are 1-based labels; --namespace-option-index is 0-based. Order by the
            // numeric suffix (not alphabetically) so Namespace10 does not sort before Namespace2.
            List<string> ordered = namespacesSection
                .Select(kv => (Index: ExtractNamespaceIndex(kv.Key), Value: kv.Value))
                .Where(kv => kv.Index.HasValue)
                .OrderBy(kv => kv.Index.Value)
                .Select(kv => kv.Value)
                .ToList();

            if (namespaceId < 0 || namespaceId >= ordered.Count)
            {
                return null;
            }

            string sectionName = ordered[namespaceId]?.Trim();
            if (string.IsNullOrEmpty(sectionName) || !ini.TryGetValue(sectionName, out Dictionary<string, string> section))
            {
                return null;
            }

            string iniName = section.TryGetValue("IniName", out string iniNameValue) && !string.IsNullOrWhiteSpace(iniNameValue)
                ? iniNameValue.Trim()
                : "changes.ini";
            string dataPath = section.TryGetValue("DataPath", out string dataPathValue) && !string.IsNullOrWhiteSpace(dataPathValue)
                ? dataPathValue.Trim()
                : sectionName;

            string namespacesDir = fileSystemProvider.GetDirectoryName(namespacesIniPath) ?? Path.GetDirectoryName(namespacesIniPath);
            if (string.IsNullOrEmpty(namespacesDir))
            {
                return null;
            }

            return Path.GetFullPath(Path.Combine(namespacesDir, dataPath.Replace('/', Path.DirectorySeparatorChar), iniName));
        }

        private static int? ExtractNamespaceIndex([CanBeNull] string key)
        {
            if (string.IsNullOrEmpty(key) || !key.StartsWith("Namespace", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string suffix = key.Substring("Namespace".Length);
            return int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out int label)
                ? label - 1
                : (int?)null;
        }

        private static async Task<string> ReadTextAsync([NotNull] IFileSystemProvider fileSystemProvider, [NotNull] string path)
        {
            try
            {
                return await fileSystemProvider.ReadFileAsync(path).ConfigureAwait(false);
            }
            catch
            {
                if (File.Exists(path))
                {
                    return await Utility.NetFrameworkCompatibility.ReadAllTextAsync(path).ConfigureAwait(false);
                }

                return string.Empty;
            }
        }
    }
}
