// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using JetBrains.Annotations;

using ModSync.Core.Utility;

using Newtonsoft.Json.Linq;

namespace ModSync.Core.Services
{
    /// <summary>How a component's archive was identified.</summary>
    public enum ArchiveResolutionTier
    {
        /// <summary>Not resolved. The component must be reported, never guessed at.</summary>
        Unresolved = 0,

        /// <summary>ModSync's own download index mapped the guide URL straight to a filename.</summary>
        ResourceIndex,

        /// <summary>The Nexus mod id embedded in the download filename.</summary>
        NexusModId,

        /// <summary>Exact match of the normalized component name or URL slug.</summary>
        ExactName,

        /// <summary>Exactly one library entry contains the normalized component name.</summary>
        UniqueContainment,
    }

    /// <summary>The outcome of resolving one component to one archive, with its provenance.</summary>
    public sealed class ArchiveResolution
    {
        [CanBeNull] public FileInfo Archive { get; set; }

        public ArchiveResolutionTier Tier { get; set; }

        /// <summary>Human-readable account of how this resolved, or why it did not.</summary>
        [NotNull] public string Reason { get; set; } = string.Empty;

        /// <summary>The candidates considered, so an ambiguous result can be acted on by a human.</summary>
        [NotNull] public IReadOnlyList<string> Candidates { get; set; } = new List<string>();

        public bool IsResolved => Archive != null;
    }

    /// <summary>
    /// Resolves a guide component to exactly one archive on disk, using an explicit chain of keys
    /// ordered most-authoritative first.
    /// <para>
    /// The chain NARROWS, it never votes, and it never falls through to a scored best guess. If more
    /// than one candidate survives a tier the component is reported ambiguous. That rule is not
    /// defensive pedantry - it is driven by measured near-misses in the reference library, where a
    /// similarity score picks the wrong game's mod:
    /// "Thematic KOTOR Companions" matches `KOTOR1-Thematic-Companions_v1.0.1` and
    /// `KOTOR2-Thematic-Companions_v1.0.3` equally well, and "Repair Affects Stun Droid" matches both
    /// the `[K1]` and `[TSL]` archives. Installing the KOTOR 2 archive into a KOTOR 1 build would
    /// corrupt the build while every step reported success.
    /// </para>
    /// </summary>
    public static class ArchiveResolver
    {
        /// <summary>
        /// Which game an archive or component belongs to, when its name says so. Used to discard
        /// candidates that belong to the other game rather than to pick between them.
        /// </summary>
        public enum GameMarker
        {
            None = 0,
            Kotor1,
            Kotor2,
        }

        [NotNull]
        public static ArchiveResolution Resolve(
            [NotNull] string componentName,
            [NotNull] IReadOnlyList<string> componentUrls,
            [NotNull] IReadOnlyList<FileInfo> archives,
            GameMarker targetGame)
        {
            if (componentName is null)
            {
                throw new ArgumentNullException(nameof(componentName));
            }

            if (componentUrls is null)
            {
                throw new ArgumentNullException(nameof(componentUrls));
            }

            if (archives is null)
            {
                throw new ArgumentNullException(nameof(archives));
            }

            // Tier (a): ModSync's own index maps a normalized URL to the filename it downloaded.
            ArchiveResolution byIndex = ResolveByResourceIndex(componentUrls, archives);
            if (byIndex != null)
            {
                return byIndex;
            }

            // Tier (b): the Nexus mod id, pinned to its own delimited field in the download name.
            ArchiveResolution byModId = ResolveByNexusModId(componentName, componentUrls, archives, targetGame);
            if (byModId != null)
            {
                return byModId;
            }

            // Tiers (c) and (d): name equality, then unique containment.
            var searchTerms = new List<string> { componentName };
            searchTerms.AddRange(componentUrls.Select(UrlSlug).Where(t => !string.IsNullOrEmpty(t)));

            ArchiveResolution byName = ResolveByName(searchTerms, archives, targetGame);
            if (byName != null)
            {
                return byName;
            }

            return new ArchiveResolution
            {
                Tier = ArchiveResolutionTier.Unresolved,
                Reason = "No tier produced a unique match.",
            };
        }

        // ---------- tier (a): resource index ----------

        /// <summary>
        /// ModSync records every file it has downloaded under
        /// <c>sha1(UrlNormalizer.Normalize(url))</c>, so a guide URL resolves to a real filename with
        /// no network access and no name similarity involved. Authoritative wherever it hits, because
        /// the mapping was recorded at download time rather than inferred afterwards.
        /// </summary>
        [CanBeNull]
        private static ArchiveResolution ResolveByResourceIndex(
            [NotNull] IReadOnlyList<string> componentUrls,
            [NotNull] IReadOnlyList<FileInfo> archives)
        {
            Dictionary<string, List<string>> index = LoadResourceIndex();
            if (index.Count == 0)
            {
                return null;
            }

            foreach (string url in componentUrls)
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                string key = ContentKeyFor(url);
                if (key is null || !index.TryGetValue(key, out List<string> fileNames))
                {
                    continue;
                }

                foreach (string fileName in fileNames)
                {
                    FileInfo hit = archives.FirstOrDefault(
                        a => string.Equals(a.Name, fileName, StringComparison.OrdinalIgnoreCase));

                    if (hit != null)
                    {
                        return new ArchiveResolution
                        {
                            Archive = hit,
                            Tier = ArchiveResolutionTier.ResourceIndex,
                            Reason = $"Download index maps '{url}' to '{fileName}'.",
                        };
                    }
                }
            }

            return null;
        }

        [CanBeNull]
        private static string ContentKeyFor([NotNull] string url)
        {
            try
            {
                string normalized = UrlNormalizer.Normalize(url);
                byte[] hash = NetFrameworkCompatibility.HashDataSHA1(Encoding.UTF8.GetBytes(normalized));
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                Logger.LogVerbose($"[ArchiveResolver] Could not derive content key for '{url}': {ex.Message}");
                return null;
            }
        }

        private static Dictionary<string, List<string>> s_resourceIndexCache;

        [NotNull]
        private static Dictionary<string, List<string>> LoadResourceIndex()
        {
            if (s_resourceIndexCache != null)
            {
                return s_resourceIndexCache;
            }

            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string path = ResourceIndexPath();
                if (path != null && File.Exists(path))
                {
                    var root = JObject.Parse(File.ReadAllText(path));
                    if (root["entries"] is JObject entries)
                    {
                        foreach (KeyValuePair<string, JToken> entry in entries)
                        {
                            if (!(entry.Value?["Files"] is JObject files))
                            {
                                continue;
                            }

                            // "download" is the placeholder ModSync stores when it never learned the
                            // real filename; it names no file on disk and must not be matched.
                            List<string> names = files.Properties()
                                .Select(p => p.Name)
                                .Where(n => !string.IsNullOrWhiteSpace(n)
                                    && !n.Equals("download", StringComparison.OrdinalIgnoreCase))
                                .ToList();

                            if (names.Count > 0)
                            {
                                map[entry.Key] = names;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[ArchiveResolver] Could not read the download index: {ex.Message}");
            }

            s_resourceIndexCache = map;
            return map;
        }

        [CanBeNull]
        private static string ResourceIndexPath()
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(home))
                {
                    return null;
                }

                return Path.Combine(home, "ModSync", "resource-index.json");
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------- tier (b): nexus mod id ----------

        /// <summary>
        /// Nexus download names have the shape <c>&lt;title&gt;-&lt;modId&gt;-&lt;version
        /// fields&gt;-&lt;10-digit timestamp&gt;</c>, which pins the id to one delimited field.
        /// Substring-matching the bare id instead would also hit those digits inside the timestamp.
        /// <para>
        /// A mod page can host several files (a main download plus compatibility patches) that all
        /// carry the same id - id 1282 matches six archives - so when the id alone is ambiguous the
        /// component name is used to narrow, and the survivor must be unique.
        /// </para>
        /// </summary>
        [CanBeNull]
        private static ArchiveResolution ResolveByNexusModId(
            [NotNull] string componentName,
            [NotNull] IReadOnlyList<string> componentUrls,
            [NotNull] IReadOnlyList<FileInfo> archives,
            GameMarker targetGame)
        {
            foreach (string url in componentUrls)
            {
                string modId = NexusModId(url);
                if (modId is null)
                {
                    continue;
                }

                var matches = archives
                    .Where(a => Regex.IsMatch(
                        Path.GetFileNameWithoutExtension(a.Name),
                        $@"-{modId}-\d+(-\d+)*-\d{{9,}}$",
                        RegexOptions.None,
                        TimeSpan.FromSeconds(5)))
                    .ToList();

                if (matches.Count == 0)
                {
                    continue;
                }

                List<FileInfo> viable = DiscardWrongGame(matches, targetGame);

                if (viable.Count == 1)
                {
                    return new ArchiveResolution
                    {
                        Archive = viable[0],
                        Tier = ArchiveResolutionTier.NexusModId,
                        Reason = $"Nexus mod id {modId} matched one archive.",
                    };
                }

                if (viable.Count > 1)
                {
                    string normalizedComponent = Normalize(componentName);
                    List<FileInfo> narrowed = viable
                        .Where(a => Normalize(Path.GetFileNameWithoutExtension(a.Name))
                            .StartsWith(normalizedComponent, StringComparison.Ordinal))
                        .ToList();

                    if (narrowed.Count == 1)
                    {
                        return new ArchiveResolution
                        {
                            Archive = narrowed[0],
                            Tier = ArchiveResolutionTier.NexusModId,
                            Reason =
                                $"Nexus mod id {modId} matched {viable.Count} archives (the page hosts several "
                                + "files); the component name narrowed them to one.",
                        };
                    }

                    return new ArchiveResolution
                    {
                        Tier = ArchiveResolutionTier.Unresolved,
                        Reason =
                            $"Nexus mod id {modId} matched {viable.Count} archives and the component name did not "
                            + "narrow them to one. Ambiguous, so not guessed.",
                        Candidates = viable.Select(a => a.Name).ToList(),
                    };
                }
            }

            return null;
        }

        [CanBeNull]
        private static string NexusModId([CanBeNull] string url)
        {
            if (string.IsNullOrWhiteSpace(url)
                || url.IndexOf("nexusmods.com", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return null;
            }

            Match m = Regex.Match(url, @"/mods/(\d+)", RegexOptions.None, TimeSpan.FromSeconds(5));
            return m.Success ? m.Groups[1].Value : null;
        }

        // ---------- tiers (c) and (d): name ----------

        [CanBeNull]
        private static ArchiveResolution ResolveByName(
            [NotNull] IReadOnlyList<string> searchTerms,
            [NotNull] IReadOnlyList<FileInfo> archives,
            GameMarker targetGame)
        {
            // Archives and their extracted folders are the same logical mod; group so that a mod
            // present as both does not read as two competing candidates.
            List<IGrouping<string, FileInfo>> logical = archives
                .GroupBy(a => Normalize(Path.GetFileNameWithoutExtension(a.Name)), StringComparer.Ordinal)
                .ToList();

            foreach (string term in searchTerms)
            {
                string needle = Normalize(term);
                if (needle.Length < 4)
                {
                    continue;
                }

                var exact = logical.Where(g => string.Equals(g.Key, needle, StringComparison.Ordinal)).ToList();
                ArchiveResolution exactResult = SingleOrAmbiguous(
                    exact, targetGame, ArchiveResolutionTier.ExactName, $"Exact name match for '{term}'.");

                if (exactResult != null)
                {
                    return exactResult;
                }

                var contains = logical.Where(g => g.Key.IndexOf(needle, StringComparison.Ordinal) >= 0).ToList();
                ArchiveResolution containsResult = SingleOrAmbiguous(
                    contains, targetGame, ArchiveResolutionTier.UniqueContainment,
                    $"Exactly one archive name contains '{term}'.");

                if (containsResult != null)
                {
                    return containsResult;
                }
            }

            return null;
        }

        [CanBeNull]
        private static ArchiveResolution SingleOrAmbiguous(
            [NotNull] List<IGrouping<string, FileInfo>> groups,
            GameMarker targetGame,
            ArchiveResolutionTier tier,
            [NotNull] string reason)
        {
            if (groups.Count == 0)
            {
                return null;
            }

            List<IGrouping<string, FileInfo>> viable = groups
                .Where(g => !IsWrongGame(g.First().Name, targetGame))
                .ToList();

            if (viable.Count == 1)
            {
                return new ArchiveResolution
                {
                    Archive = viable[0].First(),
                    Tier = tier,
                    Reason = reason,
                };
            }

            if (viable.Count > 1)
            {
                return new ArchiveResolution
                {
                    Tier = ArchiveResolutionTier.Unresolved,
                    Reason =
                        $"{reason} but {viable.Count} distinct archives matched equally well. Ambiguous, so not "
                        + "guessed - picking one risks installing the wrong mod.",
                    Candidates = viable.Select(g => g.First().Name).ToList(),
                };
            }

            return null;
        }

        // ---------- game markers ----------

        [NotNull]
        private static List<FileInfo> DiscardWrongGame(
            [NotNull] IReadOnlyList<FileInfo> archives,
            GameMarker targetGame)
        {
            return archives.Where(a => !IsWrongGame(a.Name, targetGame)).ToList();
        }

        /// <summary>
        /// True when the name explicitly announces the OTHER game. Only an explicit contrary marker
        /// rejects a candidate; an unmarked name stays in play, because most archives say nothing
        /// about which game they target.
        /// </summary>
        public static bool IsWrongGame([CanBeNull] string name, GameMarker targetGame)
        {
            if (targetGame == GameMarker.None || string.IsNullOrEmpty(name))
            {
                return false;
            }

            GameMarker marker = MarkerOf(name);
            return marker != GameMarker.None && marker != targetGame;
        }

        /// <summary>
        /// Reads a game marker out of an archive name. Deliberately conservative: it looks for
        /// delimited tokens, so "K2" in "K2 Swoops to K1" or a stray "2" inside a version string does
        /// not flip the verdict. A name announcing BOTH games is treated as unmarked, since it is
        /// probably a cross-game patch and the caller should not discard it on a guess.
        /// </summary>
        public static GameMarker MarkerOf([CanBeNull] string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return GameMarker.None;
            }

            string padded = " " + Regex.Replace(name, @"[^A-Za-z0-9]+", " ", RegexOptions.None, TimeSpan.FromSeconds(5)) + " ";
            padded = padded.ToLowerInvariant();

            bool k2 = padded.Contains(" k2 ")
                || padded.Contains(" kotor2 ")
                || padded.Contains(" kotor 2 ")
                || padded.Contains(" tsl ")
                || padded.Contains(" tslrcm ");

            bool k1 = padded.Contains(" k1 ")
                || padded.Contains(" kotor1 ")
                || padded.Contains(" kotor 1 ");

            if (k1 && k2)
            {
                return GameMarker.None;
            }

            if (k2)
            {
                return GameMarker.Kotor2;
            }

            return k1 ? GameMarker.Kotor1 : GameMarker.None;
        }

        // ---------- helpers ----------

        /// <summary>
        /// Case- and separator-insensitive form used for comparison. Game markers are NOT stripped:
        /// removing them would collapse `KOTOR1-Thematic-Companions` and `KOTOR2-Thematic-Companions`
        /// into the same key and make the wrong game's archive look like a perfect match.
        /// </summary>
        [NotNull]
        public static string Normalize([CanBeNull] string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c))
                {
                    _ = sb.Append(char.ToLowerInvariant(c));
                }
            }

            return sb.ToString();
        }

        [NotNull]
        private static string UrlSlug([CanBeNull] string url)
        {
            if (string.IsNullOrWhiteSpace(url) || url.IndexOf("://", StringComparison.Ordinal) < 0)
            {
                return string.Empty;
            }

            try
            {
                var uri = new Uri(url);
                string last = uri.Segments.LastOrDefault()?.TrimEnd('/') ?? string.Empty;

                // A bare numeric segment is a Nexus mod id, which tier (b) owns; as a name it is
                // meaningless and would match digits anywhere.
                return Regex.IsMatch(last, @"^\d+$", RegexOptions.None, TimeSpan.FromSeconds(5))
                    ? string.Empty
                    : last;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
