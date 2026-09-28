// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using JetBrains.Annotations;

using ModSync.Core.FileSystemUtils;
using ModSync.Core.Utility;

using SharpCompress.Archives;
using SharpCompress.Readers;

namespace ModSync.Core.TSLPatcher
{
    public static class IniHelper
    {
        public static void ReplaceIniPattern([NotNull] DirectoryInfo directory, string pattern, string replacement)
        {
            if (directory is null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            FileInfo[] iniFiles = directory.GetFilesSafely(searchPattern: "*.ini", SearchOption.AllDirectories);
            if (iniFiles.Length == 0)
            {
                throw new InvalidOperationException("No .ini files found!");
            }

            foreach (FileInfo file in iniFiles)
            {
                string filePath = file.FullName;
                string fileContents = File.ReadAllText(filePath);

                fileContents = Regex.Replace(fileContents, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromSeconds(10));

                File.WriteAllText(filePath, fileContents);
            }
        }

        /// <summary>
        /// Holo 1.5.1 KeyErrors when a 2DA <c>ChangeRow</c>/<c>AddRow</c>/<c>Replace</c>
        /// names a section the ini never defines. Windows TSLPatcher skips those rows.
        /// JC's Blaster Adjustment <c>pistol_rifle.ini</c> ships this leftover
        /// (<c>ChangeRow12=repeating_blaster</c> with no <c>[repeating_blaster]</c>).
        /// </summary>
        public static int DropDangling2daRowReferences([NotNull] DirectoryInfo directory)
        {
            if (directory is null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            if (!directory.Exists)
            {
                return 0;
            }

            var rowKey = new Regex(
                @"^(ChangeRow|AddRow|Replace)\d+$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            int dropped = 0;
            FileInfo[] iniFiles;
            try
            {
                iniFiles = directory.GetFilesSafely(searchPattern: "*.ini", SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                return 0;
            }

            foreach (FileInfo file in iniFiles)
            {
                if (file.Name.Equals("namespaces.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string[] lines = File.ReadAllLines(file.FullName);
                var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in lines)
                {
                    string trimmed = raw.Trim();
                    if (trimmed.StartsWith("[", StringComparison.Ordinal)
                        && trimmed.EndsWith("]", StringComparison.Ordinal)
                        && trimmed.Length >= 2)
                    {
                        _ = sections.Add(trimmed.Substring(1, trimmed.Length - 2));
                    }
                }

                bool changed = false;
                string section = string.Empty;
                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i].Trim();
                    if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                    {
                        section = trimmed.Substring(1, trimmed.Length - 2);
                        continue;
                    }

                    if (!section.EndsWith(".2da", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int eq = trimmed.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    string key = trimmed.Substring(0, eq).Trim();
                    string value = trimmed.Substring(eq + 1).Trim();
                    if (!rowKey.IsMatch(key) || string.IsNullOrEmpty(value) || sections.Contains(value))
                    {
                        continue;
                    }

                    lines[i] = ";" + lines[i];
                    dropped++;
                    changed = true;
                }

                if (changed)
                {
                    File.WriteAllLines(file.FullName, lines);
                    Logger.LogVerbose($"[Patcher] Commented dangling 2DA row references in '{file.Name}'.");
                }
            }

            return dropped;
        }

        public static Dictionary<string, Dictionary<string, string>> ReadNamespacesIniFromArchive(
            [NotNull] string archivePath
        )
        {
            if (string.IsNullOrWhiteSpace(archivePath))
            {
                throw new ArgumentException(message: "Value cannot be null or whitespace.", nameof(archivePath));
            }

            (IArchive archive, FileStream thisStream) = ArchiveHelper.OpenArchive(archivePath);
            using (thisStream)
            {
                if (!(archive is null) && !(thisStream is null))
                {
                    return TraverseDirectories(archive.Entries);
                }
            }

            return null;
        }

        public static Dictionary<string, Dictionary<string, string>> ReadNamespacesIniFromArchive(
            [NotNull] Stream archiveStream
        )
        {
            if (archiveStream is null)
            {
                throw new ArgumentNullException(nameof(archiveStream));
            }

            try
            {
                using (IArchive archive = ArchiveFactory.OpenArchive(archiveStream, new ReaderOptions()))
                {
                    return TraverseDirectories(archive.Entries);
                }
            }
            catch (InvalidOperationException ex)
            {
                Logger.LogException(ex, "Failed to read namespaces.ini from archive stream due to invalid archive operation.");
                return null;
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "Unexpected error while reading namespaces.ini from archive stream.");
                return null;
            }
        }
        private static readonly char[] s_separator = new[] { '/', '\\' };

        private static Dictionary<string, Dictionary<string, string>> TraverseDirectories(
            IEnumerable<IArchiveEntry> entries
        )
        {
            IEnumerable<IArchiveEntry> archiveEntries = entries as IArchiveEntry[]
                ?? entries?.ToArray() ?? Array.Empty<IArchiveEntry>();
            foreach (IArchiveEntry entry in archiveEntries)
            {
                if (entry.IsDirectory)
                {
                    IEnumerable<IArchiveEntry> subDirectoryEntries = archiveEntries.Where(
                        e => e != null && (e.Key.StartsWith(entry.Key + "/", StringComparison.Ordinal) || e.Key.StartsWith(entry.Key + "\\", StringComparison.Ordinal))
                    );
                    Dictionary<string, Dictionary<string, string>> result = TraverseDirectories(
                        subDirectoryEntries
                    );
                    if (result != null)
                    {
                        return result;
                    }
                }
                else
                {
                    string directoryName = Path.GetDirectoryName(entry?.Key.Replace(oldChar: '\\', newChar: '/'));
                    string fileName = Path.GetFileName(entry?.Key);

                    bool isTslPatchDataFolder = directoryName?.Split(s_separator, StringSplitOptions.RemoveEmptyEntries)
                        .Any(dir => dir.Equals("tslpatchdata", StringComparison.OrdinalIgnoreCase)) ?? false;

                    if (!string.Equals(fileName, "namespaces.ini", StringComparison.OrdinalIgnoreCase) ||
                         !isTslPatchDataFolder)
                    {
                        continue;
                    }

                    using (var reader = new StreamReader(entry.OpenEntryStream()))
                    {
                        return ParseNamespacesIni(reader);
                    }
                }
            }

            return null;
        }

        public static Dictionary<string, Dictionary<string, string>> ParseNamespacesIni(StreamReader reader)
        {
            if (reader is null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> currentSection = null;

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    string sectionName = line.Substring(startIndex: 1, line.Length - 2);
                    currentSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections[sectionName] = currentSection;
                }
                else if (currentSection != null && NetFrameworkCompatibility.Contains(line, "=", StringComparison.Ordinal))
                {
                    string[] keyValue = line.Split('=');
                    if (keyValue.Length != 2)
                    {
                        continue;
                    }

                    string key = keyValue[0].Trim();
                    string value = keyValue[1].Trim();
                    currentSection[key] = value;
                }
            }

            return sections;
        }
    }
}
