// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;

using JetBrains.Annotations;

using ModSync.Core.Services.Interpretation;

namespace ModSync.Core.Services
{
    /// <summary>
    /// Decides whether a file swept up by a GENERATED wildcard Move belongs in the game directory.
    /// <para>
    /// Auto-generated instructions copy whole extracted folders with <c>folder\*</c>, so everything
    /// the mod author happened to zip up lands in <c>Override</c>. Completed installs ended up with
    /// macOS resource forks (<c>._p_attnh1.tga</c>), <c>.DS_Store</c>, a LibreOffice lock file,
    /// <c>desktop.ini</c>, <c>installlog.txt</c>, preview screenshots, ten readmes, and a stray
    /// <c>TSLPatcher.exe</c> in the game's Override folder. The hand-built reference builds contain
    /// none of those.
    /// </para>
    /// <para>
    /// This is a DENYLIST on purpose. An allowlist of known game extensions would silently drop
    /// legitimate files whose extension nobody thought of, which is the worse failure. It is applied
    /// only where ModSync itself synthesized the path -- a human who names a specific <c>.txt</c> in
    /// an instruction still gets that file installed.
    /// </para>
    /// </summary>
    internal static class NonGameContentFilter
    {
        /// <summary>
        /// Documentation, artwork, tooling, and OS bookkeeping. Never game content.
        /// </summary>
        internal static readonly HashSet<string> ExcludedExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".txt", ".rtf", ".pdf", ".doc", ".docx", ".md",
                ".html", ".htm", ".url", ".ini",
                ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".psd",
                ".exe", ".bat", ".dll", ".cmd", ".sh",
            };

        /// <summary>
        /// Exact filenames that are never game content regardless of extension.
        /// </summary>
        internal static readonly HashSet<string> ExcludedFileNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".ds_store",
                "desktop.ini",
                "thumbs.db",
                "installlog.txt",
            };

        /// <summary>
        /// Real KOTOR resource extensions. A file with one of these is content even if some other
        /// rule would have rejected it, so the denylist can never grow a hole in the game data.
        /// </summary>
        internal static readonly HashSet<string> ProtectedGameExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".tga", ".tpc", ".dds", ".txi", ".mdl", ".mdx", ".ncs", ".nss",
                ".utc", ".utp", ".uti", ".utd", ".utm", ".utt", ".utw", ".ute", ".uts",
                ".dlg", ".are", ".git", ".pth", ".ifo", ".2da", ".tlk",
                ".mod", ".rim", ".erf", ".bik", ".wav", ".mp3", ".lip", ".gui", ".ssf",
                ".jrl", ".lyt", ".vis", ".ncs",
            };

        /// <summary>
        /// True when <paramref name="path"/> is packaging debris rather than game content.
        /// </summary>
        public static bool IsNonGameContent([CanBeNull] string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string fileName;
            try
            {
                fileName = Path.GetFileName(path.Replace('\\', Path.DirectorySeparatorChar));
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (string.IsNullOrEmpty(fileName))
            {
                return false;
            }

            string extension = Path.GetExtension(fileName);
            GuideInterpretationPolicy.FilterRules filters = GuideInterpretationPolicyStore.Current.Filters;
            HashSet<string> protectedExt = ToSet(filters.ProtectedGameExtensions, ProtectedGameExtensions);
            HashSet<string> excludedExt = ToSet(filters.NonGameExtensions, ExcludedExtensions);
            HashSet<string> excludedNames = ToSet(filters.NonGameFileNames, ExcludedFileNames);
            string appleDouble = string.IsNullOrEmpty(filters.AppleDoublePrefix)
                ? "._"
                : filters.AppleDoublePrefix;
            string officeLock = string.IsNullOrEmpty(filters.OfficeLockPrefix)
                ? ".~lock"
                : filters.OfficeLockPrefix;

            // A real resource always wins, whatever its name looks like.
            if (!string.IsNullOrEmpty(extension) && protectedExt.Contains(extension))
            {
                // AppleDouble sidecars carry the resource's own extension ("._p_attnh1.tga"), so
                // they must still be rejected.
                return fileName.StartsWith(appleDouble, StringComparison.Ordinal);
            }

            if (fileName.StartsWith(appleDouble, StringComparison.Ordinal))
            {
                return true;
            }

            if (fileName.StartsWith(officeLock, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (excludedNames.Contains(fileName))
            {
                return true;
            }

            return !string.IsNullOrEmpty(extension) && excludedExt.Contains(extension);
        }

        [NotNull]
        private static HashSet<string> ToSet(
            [CanBeNull] IReadOnlyList<string> configured,
            [NotNull] HashSet<string> fallback)
        {
            if (configured == null || configured.Count == 0)
            {
                return fallback;
            }

            return new HashSet<string>(configured, StringComparer.OrdinalIgnoreCase);
        }
    }
}
