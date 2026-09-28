// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.IO;
using System.Text.RegularExpressions;

using JetBrains.Annotations;

namespace ModSync.Core.Services
{
    /// <summary>
    /// Works out which game a build targets when no serialized <c>game</c> field says so.
    /// <para>
    /// Measured: every component in a <c>convert --auto-generate-local</c> run resolved with
    /// <c>targetGame=None</c>, because <see cref="MainConfig.TargetGame"/> is only populated from a
    /// serialized instruction file and <see cref="MainConfig.DestinationPath"/> is null during a
    /// conversion. With None, EVERY wrong-game guard in the resolver is inert -- the K1 component
    /// "Trandoshans Rescaled" was handed the KOTOR 2 mod "Rescaled Trandoshans.zip".
    /// </para>
    /// <para>
    /// The document's own title is preferred over its path: "# KOTOR 1 Full Build" is content, while
    /// a <c>content/k1/</c> directory is a convention that breaks the moment someone renames it.
    /// </para>
    /// </summary>
    public static class BuildTargetGameInference
    {
        /// <summary>
        /// Returns <c>"K1"</c>, <c>"TSL"</c>, or an empty string when nothing decides it.
        /// </summary>
        [NotNull]
        public static string Infer([CanBeNull] string documentText, [CanBeNull] string inputPath)
        {
            string fromTitle = FromTitle(documentText);
            if (!string.IsNullOrEmpty(fromTitle))
            {
                return fromTitle;
            }

            return FromPath(inputPath);
        }

        /// <summary>
        /// Reads the first Markdown heading, or the first non-empty line of any other format, and
        /// decides from that alone. A body mentioning both games decides nothing.
        /// </summary>
        [NotNull]
        public static string FromTitle([CanBeNull] string documentText)
        {
            if (string.IsNullOrWhiteSpace(documentText))
            {
                return string.Empty;
            }

            foreach (string rawLine in documentText.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                // Only the title line is consulted. Prose below it discusses both games constantly
                // ("this mod is a port of the TSL version"), which is why the whole document is not
                // scanned.
                return FromPhrase(line.TrimStart('#', ' ', '\t'));
            }

            return string.Empty;
        }

        /// <summary>Last resort: a <c>k1</c> / <c>k2</c> / <c>tsl</c> segment in the file path.</summary>
        [NotNull]
        public static string FromPath([CanBeNull] string inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                return string.Empty;
            }

            string normalized;
            try
            {
                normalized = inputPath.Replace('\\', '/');
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }

            bool k1 = Regex.IsMatch(normalized, @"(^|[/_\-.])(k1|kotor1|kotor_1)([/_\-.]|$)",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
            bool k2 = Regex.IsMatch(normalized, @"(^|[/_\-.])(k2|kotor2|kotor_2|tsl)([/_\-.]|$)",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

            if (k1 == k2)
            {
                return string.Empty;
            }

            return k1 ? MainConfig.ValidTargetGames.K1 : MainConfig.ValidTargetGames.TSL;
        }

        /// <summary>Decides from a single line of text. Naming both games decides nothing.</summary>
        [NotNull]
        public static string FromPhrase([CanBeNull] string phrase)
        {
            if (string.IsNullOrWhiteSpace(phrase))
            {
                return string.Empty;
            }

            bool k2 = Regex.IsMatch(phrase, @"\b(kotor\s*(2|ii)|k2|tsl|sith\s+lords)\b",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
            bool k1 = Regex.IsMatch(phrase, @"\b(kotor\s*(1|i)|k1|knights\s+of\s+the\s+old\s+republic\s*(1|i)?)\b",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

            // "Knights of the Old Republic II" matches the KOTOR-1 pattern's optional-numeral arm as
            // well, so a line naming both is treated as naming neither.
            if (k1 && k2)
            {
                return string.Empty;
            }

            if (k2)
            {
                return MainConfig.ValidTargetGames.TSL;
            }

            return k1 ? MainConfig.ValidTargetGames.K1 : string.Empty;
        }
    }
}
