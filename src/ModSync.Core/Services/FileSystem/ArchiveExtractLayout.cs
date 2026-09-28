// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.IO;

namespace ModSync.Core.Services.FileSystem
{
    /// <summary>
    /// Single source of truth for the directory an archive's entries land in.
    /// <para>
    /// Dry-run validation and the real install MUST agree on this path. When they disagreed, a
    /// component whose <c>Extract</c> writes to the scratch directory and whose <c>Move</c> reads
    /// <c>&lt;&lt;modDirectory&gt;&gt;\Foo\*</c> (remapped to <c>scratch\Foo\*</c>) reported a
    /// missing source during validation and then installed perfectly during the real phase: the
    /// virtual provider appended an extra <c>Foo\</c> segment the real provider did not, so 84 of
    /// 146 components in a K2 build were flagged with false "missing archive" errors and the
    /// install aborted before it started.
    /// </para>
    /// </summary>
    internal static class ArchiveExtractLayout
    {
        /// <summary>
        /// Where entries of <paramref name="archivePath"/> are written when extracting to
        /// <paramref name="destinationPath"/>.
        /// <para>
        /// An explicitly chosen destination (anything other than the archive's own directory) is
        /// used verbatim; the default destination gets an archive-name subfolder so two archives
        /// unpacked side by side cannot collide. The result is then moved off the archive store
        /// when a scratch directory is configured, because the archive store may be read-only or a
        /// slow removable volume.
        /// </para>
        /// </summary>
        /// <param name="archivePath">Full path of the archive being extracted.</param>
        /// <param name="destinationPath">Requested destination directory.</param>
        /// <param name="createDirectories">
        /// True for the real filesystem, where the scratch directory must exist before writing.
        /// False for validation, which must not touch disk.
        /// </param>
        public static string ResolveExtractRoot(
            string archivePath,
            string destinationPath,
            bool createDirectories)
        {
            if (string.IsNullOrEmpty(archivePath) || string.IsNullOrEmpty(destinationPath))
            {
                return destinationPath;
            }

            string archiveDirectory;
            string normalizedDestPath;
            string normalizedArchiveDir;
            try
            {
                archiveDirectory = Path.GetDirectoryName(Path.GetFullPath(archivePath)) ?? string.Empty;
                normalizedDestPath = Path.GetFullPath(destinationPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                normalizedArchiveDir = Path.GetFullPath(archiveDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is NotSupportedException)
            {
                return destinationPath;
            }

            bool isExplicitDestination = !string.Equals(
                normalizedDestPath,
                normalizedArchiveDir,
                StringComparison.OrdinalIgnoreCase);

            string extractRoot = isExplicitDestination
                ? normalizedDestPath
                : Path.GetFullPath(Path.Combine(destinationPath, Path.GetFileNameWithoutExtension(archivePath)))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return RemapOffArchiveStore(extractRoot, createDirectories);
        }

        /// <summary>
        /// Rebases an extract root that sits inside the archive store onto the scratch directory.
        /// </summary>
        public static string RemapOffArchiveStore(string extractRoot, bool createDirectories)
        {
            if (MainConfig.ExtractScratchPath is null
                || MainConfig.SourcePath is null
                || string.IsNullOrEmpty(extractRoot))
            {
                return extractRoot;
            }

            string sourceRoot;
            string full;
            try
            {
                sourceRoot = Path.GetFullPath(MainConfig.SourcePath.FullName)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                full = Path.GetFullPath(extractRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is NotSupportedException)
            {
                return extractRoot;
            }

            if (!full.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(sourceRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return extractRoot;
            }

            string relative = full.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : full.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string remapped = string.IsNullOrEmpty(relative)
                ? MainConfig.ExtractScratchPath.FullName
                : Path.Combine(MainConfig.ExtractScratchPath.FullName, relative);

            if (createDirectories)
            {
                _ = Directory.CreateDirectory(remapped);
            }

            return remapped;
        }
    }
}
