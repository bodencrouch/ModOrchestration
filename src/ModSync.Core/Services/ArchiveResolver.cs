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

using ModSync.Core.Services.Interpretation;
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

        /// <summary>An explicit guide directive selected one archive variant.</summary>
        GuideDirective,

        /// <summary>Exact match of the normalized component name or URL slug.</summary>
        ExactName,

        /// <summary>Exactly one library entry contains the normalized component name.</summary>
        UniqueContainment,

        /// <summary>Exactly one library entry contains every significant word of the name.</summary>
        UniqueTokenSubset,

        /// <summary>The archive basename equals the initials of the component's significant words (USG).</summary>
        AcronymExact,

        /// <summary>Exactly one library entry contains a long (>=6) name or slug token.</summary>
        UniqueLongToken,

        /// <summary>DeadlyStream slug author prefix plus a later subject token, unique.</summary>
        SlugAuthorSubject,
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

        /// <summary>
        /// Extra archives that belong to the same resolution (a Nexus "patches" page that hosts
        /// several compatibility files). Empty for the normal one-archive case.
        /// </summary>
        [NotNull] public IReadOnlyList<FileInfo> AdditionalArchives { get; set; } = new List<FileInfo>();

        public bool IsResolved => Archive != null;
    }

    /// <summary>
    /// Immutable, vetted view of a cold archive library. Extracted twins, spent installer folders,
    /// and hollow wrapper folders are classified once per build rather than once per component.
    /// </summary>
    public sealed class ArchiveLibrarySnapshot
    {
        internal ArchiveLibrarySnapshot([NotNull] IReadOnlyList<FileInfo> candidates)
        {
            Candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        }

        [NotNull]
        internal IReadOnlyList<FileInfo> Candidates { get; }

        public int Count => Candidates.Count;
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
            return Resolve(componentName, componentUrls, archives, targetGame, extraSignals: null);
        }

        [NotNull]
        public static ArchiveResolution Resolve(
            [NotNull] string componentName,
            [NotNull] IReadOnlyList<string> componentUrls,
            [NotNull] IReadOnlyList<FileInfo> archives,
            GameMarker targetGame,
            [CanBeNull] IReadOnlyList<string> extraSignals)
        {
            return Resolve(componentName, componentUrls, archives, targetGame, extraSignals, author: null);
        }

        [NotNull]
        public static ArchiveResolution Resolve(
            [NotNull] string componentName,
            [NotNull] IReadOnlyList<string> componentUrls,
            [NotNull] IReadOnlyList<FileInfo> archives,
            GameMarker targetGame,
            [CanBeNull] IReadOnlyList<string> extraSignals,
            [CanBeNull] string author)
        {
            return ResolveCore(
                componentName, componentUrls, archives, targetGame, extraSignals, author,
                libraryIsPrepared: false);
        }

        [NotNull]
        public static ArchiveLibrarySnapshot CreateLibrarySnapshot(
            [NotNull] IReadOnlyList<FileInfo> archives)
        {
            if (archives is null)
            {
                throw new ArgumentNullException(nameof(archives));
            }

            return new ArchiveLibrarySnapshot(PrepareCandidates(archives));
        }

        [NotNull]
        internal static ArchiveResolution ResolvePrepared(
            [NotNull] string componentName,
            [NotNull] IReadOnlyList<string> componentUrls,
            [NotNull] ArchiveLibrarySnapshot library,
            GameMarker targetGame,
            [CanBeNull] IReadOnlyList<string> extraSignals,
            [CanBeNull] string author)
        {
            if (library is null)
            {
                throw new ArgumentNullException(nameof(library));
            }

            return ResolveCore(
                componentName,
                componentUrls,
                library.Candidates,
                targetGame,
                extraSignals,
                author,
                libraryIsPrepared: true);
        }

        [NotNull]
        private static ArchiveResolution ResolveCore(
            [NotNull] string componentName,
            [NotNull] IReadOnlyList<string> componentUrls,
            [NotNull] IReadOnlyList<FileInfo> archives,
            GameMarker targetGame,
            [CanBeNull] IReadOnlyList<string> extraSignals,
            [CanBeNull] string author,
            bool libraryIsPrepared)
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

            // Extracted folders sit beside their archives in the reference library
            // (`Foo-1367-1-2-1668960810.rar` next to `Foo-1367-1-2-1668960810`, or
            // `Foo-extracted`). They are the same logical mod. Counting them as a second
            // candidate turns every Nexus/name hit into an unresolvable tie (run12).
            // Hollow folders (empty, or only wrapping a nested archive) beat real archives on
            // ExactName/UniqueContainment — discard them so the .7z/.zip/.rar wins.
            IReadOnlyList<FileInfo> candidates = libraryIsPrepared
                ? archives
                : PrepareCandidates(archives);

            // Tier (a): ModSync's own index maps a normalized URL to the filename it downloaded.
            ArchiveResolution byIndex = ResolveByResourceIndex(componentUrls, candidates);
            if (byIndex != null)
            {
                return byIndex;
            }

            // The guide sometimes just says which file to download ("simply download the
            // 'HQSkyboxesII_K1.7z' file"). That is the author naming the archive, not a similarity
            // guess, so it outranks every name-matching tier below.
            ArchiveResolution byNamedFile = ResolveByFilenameNamedInProse(extraSignals, candidates);
            if (byNamedFile != null)
            {
                return byNamedFile;
            }

            // "Download only the Damaged version" names a product that does not share
            // tokens with the component title. That has to win before UniqueTokenSubset
            // keeps only "computer"+"panel" archives and drops the Damaged file.
            // Restricted to download-only/just (not "recommend") so this does not
            // re-run PreferExplicitGuideVariant over the whole library.
            ArchiveResolution byDownloadOnly = ResolveByDownloadOnlyVariant(
                extraSignals, candidates, targetGame);
            if (byDownloadOnly != null)
            {
                return byDownloadOnly;
            }

            // A Nexus id is the page's own files. Guide prose that "recommends" another
            // later mod (UCO Patches → Character Textures) must not outrank that id.
            ArchiveResolution byModId = ResolveByNexusModId(componentName, componentUrls, candidates, targetGame);
            if (byModId != null && byModId.IsResolved)
            {
                return byModId;
            }

            // Do not run PreferExplicitGuideVariant over the entire library. That treated
            // "Strongly recommend the 2x .tpc version" as a unique hit on Ultimate HR TPC
            // (and similarly stole Character Textures for PFHB02, load screens for Malak,
            // robe icons for Cloaked Jedi Robes). Variant narrowing belongs among
            // already-name-matched candidates in SingleOrAmbiguous.

            // Guide download notes such as "Strongly recommend the 2x .tpc version" name a
            // concrete archive variant. Resolve that before heading-name containment can latch
            // onto an empty or wrong-game folder with the same English title.
            ArchiveResolution byTextureRec = ResolveByRecommendedTextureVariant(
                componentName, extraSignals, candidates, targetGame);
            if (byTextureRec != null)
            {
                return byTextureRec;
            }

            // Tier (b) already ran before GuideDirective; if it was only an ambiguous
            // miss, keep that verdict rather than guessing by name.
            if (byModId != null)
            {
                return byModId;
            }

            // Tiers (c) and (d): name equality, then unique containment.
            // extraSignals (guide prose) may recommend Medium / mention a filename; they must
            // not become identity tokens. UniqueLongToken on "download" from a download
            // sentence resolved HD Darth Malak to 'Hawk Downloadable Map' in run12.
            var identityTerms = new List<string> { componentName };
            identityTerms.AddRange(componentUrls.Select(UrlSlug).Where(t => !string.IsNullOrEmpty(t)));

            var searchTerms = new List<string>(identityTerms);
            if (extraSignals != null)
            {
                searchTerms.AddRange(extraSignals.Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            ArchiveResolution byName = ResolveByName(searchTerms, candidates, targetGame);
            // A leftover extract folder whose name equals the guide heading is ExactName,
            // but it is not identity. The 2026-08-24 K1 Holo run latched onto
            // "Better Twi'lek Heads/" and applied that folder's stale 11-patch Slim ini
            // while `K1 Twi'lek Heads v1.3.3.7z` sat beside it. Keep the folder as a
            // fallback only when no later tier uniquely picks a real archive.
            if (byName != null && !IsFolderOnlyHit(byName))
            {
                return byName;
            }

            List<IGrouping<string, FileInfo>> logical = candidates
                .GroupBy(a => LogicalArchiveStem(a.Name), StringComparer.Ordinal)
                .ToList();

            // Drop Vurt / lightsaber / romance / new-clothes archives unless the component
            // itself names that product. Otherwise a later unique-match tier (slug author
            // "darth"+"malak") silently picks Darth_Malaks_Lightsaber over Malak.rar.
            logical = DiscardExtraProduct(logical, componentName);

            ArchiveResolution folderFallback = IsFolderOnlyHit(byName) ? byName : null;

            ArchiveResolution byAcronym = ResolveByAcronym(componentName, logical, targetGame);
            if (TryTakeArchiveHit(byAcronym, ref folderFallback, out ArchiveResolution acronymHit))
            {
                return acronymHit;
            }

            ArchiveResolution byAcronymContained = ResolveByAcronymContained(componentName, logical, targetGame);
            if (TryTakeArchiveHit(byAcronymContained, ref folderFallback, out ArchiveResolution acronymContainedHit))
            {
                return acronymContainedHit;
            }

            ArchiveResolution byExpandedAcronym = ResolveByExpandedAcronym(
                componentName, extraSignals, logical, targetGame);
            if (TryTakeArchiveHit(byExpandedAcronym, ref folderFallback, out ArchiveResolution expandedHit))
            {
                return expandedHit;
            }

            ArchiveResolution bySlugAuthor = ResolveBySlugAuthorSubject(componentUrls, logical, targetGame);
            if (TryTakeArchiveHit(bySlugAuthor, ref folderFallback, out ArchiveResolution slugHit))
            {
                return slugHit;
            }

            ArchiveResolution byAuthorPrefix = ResolveByAuthorPrefix(author, componentName, logical, targetGame);
            if (TryTakeArchiveHit(byAuthorPrefix, ref folderFallback, out ArchiveResolution authorHit))
            {
                return authorHit;
            }

            ArchiveResolution byLongToken = ResolveByUniqueLongToken(identityTerms, logical, targetGame);
            if (TryTakeArchiveHit(byLongToken, ref folderFallback, out ArchiveResolution longTokenHit))
            {
                return longTokenHit;
            }

            ArchiveResolution byLastToken = ResolveByLastTokenExact(componentName, logical, targetGame);
            if (TryTakeArchiveHit(byLastToken, ref folderFallback, out ArchiveResolution lastTokenHit))
            {
                return lastTokenHit;
            }

            ArchiveResolution byDropped = ResolveByDroppedTokenSubset(componentName, logical, targetGame, searchTerms);
            if (TryTakeArchiveHit(byDropped, ref folderFallback, out ArchiveResolution droppedHit))
            {
                return droppedHit;
            }

            ArchiveResolution byCompact = ResolveByCompactProductToken(componentName, logical, targetGame);
            if (TryTakeArchiveHit(byCompact, ref folderFallback, out ArchiveResolution compactHit))
            {
                return compactHit;
            }

            if (folderFallback != null)
            {
                return folderFallback;
            }

            return new ArchiveResolution
            {
                Tier = ArchiveResolutionTier.Unresolved,
                Reason = "No tier produced a unique match.",
            };
        }

        [NotNull]
        private static IReadOnlyList<FileInfo> PrepareCandidates(
            [NotNull] IReadOnlyList<FileInfo> archives) =>
            DiscardHollowFolders(DiscardSpentInstallFolders(DiscardExtractedFolderTwins(archives)));

        /// <summary>
        /// A bare filename in prose, e.g. <c>HQSkyboxesII_K1.7z</c>. Spaces are excluded on purpose:
        /// allowing them let the match start at "simply" and swallow
        /// "simply download the 'HQSkyboxesII_K1.7z", which matches nothing in the library.
        /// </summary>
        private static readonly Regex s_namedArchiveInProse = new Regex(
            @"[A-Za-z0-9][A-Za-z0-9_\-.&()\[\]]*\.(?:7z|zip|rar|tga|tpc)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromSeconds(5));

        /// <summary>A quoted filename, which may legitimately contain spaces.</summary>
        private static readonly Regex s_quotedArchiveInProse = new Regex(
            @"['""`]\s*([^'""`\r\n]{1,120}?\.(?:7z|zip|rar|tga|tpc))\s*['""`]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromSeconds(5));

        /// <summary>
        /// Resolves when the guide names an archive filename outright and that exact file is in the
        /// library.
        /// <para>
        /// "High Quality Skyboxes II" says: "simply download the 'HQSkyboxesII_K1.7z' file." Name
        /// similarity could never find it -- the archive spells "High Quality" as "HQ" with no
        /// separator -- and the similarity tiers instead offered a seven-file "model fixes" patch.
        /// </para>
        /// <para>
        /// Deliberately strict: it fires only when the prose names exactly ONE archive that exists,
        /// so a sentence mentioning a main file and a patch file resolves nothing here and falls
        /// through to the ordinary chain.
        /// </para>
        /// </summary>
        [CanBeNull]
        private static ArchiveResolution ResolveByFilenameNamedInProse(
            [CanBeNull] IReadOnlyList<string> extraSignals,
            [NotNull] IReadOnlyList<FileInfo> archives)
        {
            if (extraSignals is null || extraSignals.Count == 0)
            {
                return null;
            }

            var byNormalizedName = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
            foreach (FileInfo archive in archives)
            {
                if (!IsRealArchiveName(archive.Name) && !IsLooseGameFileName(archive.Name))
                {
                    continue;
                }

                string key = Normalize(archive.Name);
                if (!byNormalizedName.ContainsKey(key))
                {
                    byNormalizedName[key] = archive;
                }
            }

            var matched = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
            foreach (string signal in extraSignals)
            {
                if (string.IsNullOrWhiteSpace(signal))
                {
                    continue;
                }

                foreach (Match m in s_namedArchiveInProse.Matches(signal))
                {
                    if (ProseRejectsNamedFile(signal, m.Value))
                    {
                        continue;
                    }

                    Consider(m.Value, byNormalizedName, matched);
                }

                foreach (Match m in s_quotedArchiveInProse.Matches(signal))
                {
                    if (ProseRejectsNamedFile(signal, m.Groups[1].Value))
                    {
                        continue;
                    }

                    Consider(m.Groups[1].Value, byNormalizedName, matched);
                }
            }

            if (matched.Count != 1)
            {
                return null;
            }

            FileInfo only = matched.Values.First();
            return new ArchiveResolution
            {
                Archive = only,
                Tier = ArchiveResolutionTier.ExactName,
                Reason = $"The guide names '{only.Name}' as the file to download.",
            };
        }

        private static void Consider(
            [CanBeNull] string rawName,
            [NotNull] Dictionary<string, FileInfo> byNormalizedName,
            [NotNull] Dictionary<string, FileInfo> matched)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return;
            }

            string candidate = Normalize(rawName.Trim());
            if (candidate.Length >= 6 && byNormalizedName.TryGetValue(candidate, out FileInfo archive))
            {
                matched[candidate] = archive;
            }
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
                // Must mirror DownloadCacheService.GetCacheDirectory exactly, or this reads a
                // directory the downloader never writes to. On Linux the index lives under
                // ~/.local/share, which is SpecialFolder.LocalApplicationData - NOT
                // SpecialFolder.ApplicationData (~/.config). Getting this wrong is silent: the tier
                // simply never fires and every component falls through to the weaker name tiers.
                string appDataPath;
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                }
                else
                {
                    string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    appDataPath = Path.Combine(homeDir, ".local", "share");
                }

                return string.IsNullOrEmpty(appDataPath)
                    ? null
                    : Path.Combine(appDataPath, "ModSync", "resource-index.json");
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
                    .Where(a => NexusIdIsDelimitedField(Path.GetFileNameWithoutExtension(a.Name), modId))
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

                    if (narrowed.Count != 1)
                    {
                        // "Korriban Sith Art" vs "Sith Art-1632-..." — the archive title is
                        // contained in the component name. StartsWith cannot see that.
                        // Armor/armour is the same word (Nexus id 9: Darth Malak's Armor vs
                        // TSL_Darth_Malaks_Armour_…).
                        narrowed = viable
                            .Where(a =>
                            {
                                string title = Normalize(TitleBeforeNexusSuffix(a.Name));
                                if (title.Length < 4)
                                {
                                    return false;
                                }

                                if (normalizedComponent.IndexOf(title, StringComparison.Ordinal) >= 0)
                                {
                                    return true;
                                }

                                string foldedTitle = title.Replace("armour", "armor", StringComparison.Ordinal);
                                string foldedComponent = normalizedComponent.Replace(
                                    "armour",
                                    "armor",
                                    StringComparison.Ordinal);
                                return foldedComponent.IndexOf(foldedTitle, StringComparison.Ordinal) >= 0
                                    || foldedTitle.IndexOf(foldedComponent, StringComparison.Ordinal) >= 0;
                            })
                            .ToList();
                    }

                    if (narrowed.Count != 1)
                    {
                        narrowed = PreferNonCompatibilityPatch(viable, componentName);
                    }

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

                    // "Ultimate Character Overhaul Patches" is the patch *set* for id 1282, not the
                    // LITE main file. Take every compatibility-patch member and leave LITE out.
                    if (ComponentLooksLikeAPatchSet(componentName))
                    {
                        List<FileInfo> patchSet = viable
                            .Where(a => NameLooksLikeCompatibilityPatch(a.Name)
                                && !Normalize(a.Name).Contains("lite", StringComparison.Ordinal))
                            .ToList();
                        if (patchSet.Count >= 1)
                        {
                            return new ArchiveResolution
                            {
                                Archive = patchSet[0],
                                AdditionalArchives = patchSet.Skip(1).ToList(),
                                Tier = ArchiveResolutionTier.NexusModId,
                                Reason =
                                    $"Nexus mod id {modId} is a patch set; kept {patchSet.Count} compatibility-patch "
                                    + "file(s) and discarded the LITE/main download.",
                                Candidates = patchSet.Select(a => a.Name).ToList(),
                            };
                        }
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

        /// <summary>
        /// True when <paramref name="modId"/> is its own hyphen-delimited field followed by
        /// version-like fields. A title that ends in a digit (<c>G0-T0-1296-1-0-timestamp</c>)
        /// is accepted; a version field that merely equals the id (<c>-1282-4-1-timestamp</c>
        /// must not satisfy id 1) is not, because it has no version field after it before
        /// the timestamp. Digits inside a 9+ digit timestamp never form a field.
        /// </summary>
        private static bool NexusIdIsDelimitedField([CanBeNull] string baseName, [NotNull] string modId)
        {
            if (string.IsNullOrEmpty(baseName))
            {
                return false;
            }

            string[] fields = baseName.Split('-');
            for (int i = 0; i < fields.Length; i++)
            {
                if (!fields[i].Equals(modId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                List<string> rest = fields.Skip(i + 1).ToList();
                if (rest.Count == 0 || !rest.All(IsNexusVersionOrTimestampField))
                {
                    continue;
                }

                bool hasTimestamp = rest.Any(f => f.Length >= 9 && f.All(char.IsDigit));
                if (hasTimestamp)
                {
                    // Require at least one version field before the timestamp so a trailing
                    // "-1-<timestamp>" version slot cannot satisfy Nexus id 1.
                    if (rest.Count >= 2)
                    {
                        return true;
                    }

                    continue;
                }

                // Shorter "-id-v1-0" names with no timestamp.
                if (rest.Count >= 1 && rest[0].StartsWith("v", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsNexusVersionOrTimestampField([CanBeNull] string field)
        {
            return !string.IsNullOrEmpty(field)
                && Regex.IsMatch(field, @"^v?\d+$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
        }

        // ---------- tiers (c) and (d): name ----------

        [CanBeNull]
        private static ArchiveResolution ResolveByName(
            [NotNull] IReadOnlyList<string> searchTerms,
            [NotNull] IReadOnlyList<FileInfo> archives,
            GameMarker targetGame)
        {
            // Archives and their extracted folders are the same logical mod; group so that a mod
            // present as both does not read as two competing candidates. Use LogicalArchiveStem
            // rather than GetFileNameWithoutExtension: a folder named `… v1.1` would otherwise
            // lose `.1` as a fake extension and fail to group with `… v1.1.zip`.
            List<IGrouping<string, FileInfo>> logical = archives
                .GroupBy(a => LogicalArchiveStem(a.Name), StringComparer.Ordinal)
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
                    exact, targetGame, ArchiveResolutionTier.ExactName, $"Exact name match for '{term}'.",
                    searchTerms);

                if (exactResult != null)
                {
                    return exactResult;
                }

                var contains = logical.Where(g => g.Key.IndexOf(needle, StringComparison.Ordinal) >= 0).ToList();
                ArchiveResolution containsResult = SingleOrAmbiguous(
                    contains, targetGame, ArchiveResolutionTier.UniqueContainment,
                    $"Archive name contains '{term}'.", searchTerms);

                if (containsResult != null)
                {
                    return containsResult;
                }
            }

            // An archive that announces the build's OWN game is a harder signal than any word-order
            // coincidence, so it is offered the match before the reordering tier below.
            //
            // "Trandoshans Rescaled" (a K1 build) resolved to "Rescaled Trandoshans.zip": a
            // different mod, by a different author, FOR KOTOR 2. The archive the build actually
            // declares, "[K1]_Trandoshans_Rescale.7z", lost for one letter -- "rescaled" is not a
            // substring of "rescale" -- while the K2 archive carried no marker at all, so the
            // wrong-game discard could not see it. The install then died applying a TSL-schema
            // appearance.2da to a K1 game: KeyError 'driveanimrun_pc'.
            ArchiveResolution byTargetMarker = ResolveByTargetGameMarker(searchTerms, logical, targetGame);
            if (byTargetMarker != null)
            {
                return byTargetMarker;
            }

            // Contiguous containment is word-order sensitive, so a guide heading whose words are
            // rearranged in the filename never matches: "Thematic KOTOR Companions" does not occur
            // inside "KOTOR1-Thematic-Companions", and "Trandoshans Rescaled" does not occur inside
            // "Rescaled Trandoshans". Requiring every significant word to be present, in any order,
            // recovers those without loosening the safety rule - the survivor must still be unique
            // after wrong-game candidates are discarded, otherwise it is reported as ambiguous.
            foreach (string term in searchTerms)
            {
                List<string> tokens = SignificantTokens(term);
                if (tokens.Count < 2)
                {
                    // A single leftover token ("patcher" from "4GB Patcher") matches any
                    // unrelated installer. UniqueLongToken owns the one-long-word case.
                    continue;
                }

                var subset = logical
                    .Where(g => TokensSatisfied(g, tokens))
                    .ToList();

                ArchiveResolution subsetResult = SingleOrAmbiguous(
                    subset, targetGame, ArchiveResolutionTier.UniqueTokenSubset,
                    $"Archive name contains every word of '{term}'.", searchTerms);

                if (subsetResult != null)
                {
                    return subsetResult;
                }
            }

            return null;
        }

        /// <summary>Roman-numeral sequel markers. Digits are excluded: they are usually versions.</summary>
        private static readonly HashSet<string> s_sequelNumerals =
            new HashSet<string>(StringComparer.Ordinal) { "ii", "iii", "iv", "v", "vi" };

        /// <summary>
        /// Drops candidates that lack the sequel the component names. Returns the input untouched
        /// when the component has no sequel marker, so this only ever narrows a real distinction.
        /// An empty result is deliberate: reporting the component beats installing its prequel.
        /// </summary>
        [NotNull]
        private static List<IGrouping<string, FileInfo>> DiscardSequelMismatch(
            [NotNull] List<IGrouping<string, FileInfo>> viable,
            [CanBeNull] string componentName)
        {
            string[] words = Regex.Split(
                    componentName ?? string.Empty,
                    @"[^A-Za-z0-9]+",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(5))
                .Where(w => w.Length > 0)
                .Select(w => w.ToLowerInvariant())
                .ToArray();

            if (words.Length < 2)
            {
                return viable;
            }

            string numeral = words[words.Length - 1];
            if (!s_sequelNumerals.Contains(numeral))
            {
                return viable;
            }

            string subject = words[words.Length - 2];
            string joined = subject + numeral;

            return viable
                .Where(g => g.Key.IndexOf(joined, StringComparison.Ordinal) >= 0
                    || RawTokens(g.First().Name).Contains(numeral))
                .ToList();
        }

        /// <summary>
        /// Exactly one archive that explicitly announces the build's own game and carries every
        /// significant word of the component name. Runs ahead of the word-reordering tier, which
        /// otherwise hands a K1 build a K2 mod whose words happen to be in the other order.
        /// <para>
        /// Only archives marked for the TARGET game are considered, so this can never introduce the
        /// other game's archive - it can only prefer one that says it belongs here. Word endings are
        /// matched leniently ("Rescaled" against "Rescale") because the marker already carries the
        /// safety, and a one-letter inflection is exactly what made the correct archive lose.
        /// </para>
        /// </summary>
        [CanBeNull]
        private static ArchiveResolution ResolveByTargetGameMarker(
            [NotNull] IReadOnlyList<string> searchTerms,
            [NotNull] List<IGrouping<string, FileInfo>> logical,
            GameMarker targetGame)
        {
            if (targetGame == GameMarker.None)
            {
                return null;
            }

            // A marker is not a licence to grab a neighbouring product. "HD Darth Malak" must not
            // land on "Darth_Malaks_Lightsaber_K1.zip" just because that file says K1.
            string componentName = searchTerms.Count > 0 ? searchTerms[0] : string.Empty;
            List<IGrouping<string, FileInfo>> marked = DiscardExtraProduct(logical, componentName ?? string.Empty)
                .Where(g => MarkerOf(g.First().Name) == targetGame)
                .ToList();

            if (marked.Count == 0)
            {
                return null;
            }

            foreach (string term in searchTerms)
            {
                List<string> tokens = SignificantTokens(term);
                if (tokens.Count < 2)
                {
                    continue;
                }

                List<IGrouping<string, FileInfo>> hits = marked
                    .Where(g => tokens.All(t => TokenAppearsInflected(g.Key, t)))
                    .ToList();

                if (hits.Count == 1)
                {
                    return new ArchiveResolution
                    {
                        Archive = PreferArchiveFile(hits[0]),
                        Tier = ArchiveResolutionTier.UniqueTokenSubset,
                        Reason =
                            $"Archive is marked for this build's game and contains every word of '{term}'.",
                    };
                }
            }

            return null;
        }

        /// <summary>Word endings that separate a mod title from its filename ("Rescaled"/"Rescale").</summary>
        private static readonly string[] s_inflectionSuffixes = { "ed", "es", "s", "d" };

        /// <summary>
        /// <see cref="TokenAppearsIn"/> plus tolerance for a trailing inflection. Used only where an
        /// explicit game marker already restricts the candidate set.
        /// </summary>
        private static bool TokenAppearsInflected([NotNull] string haystack, [NotNull] string token)
        {
            if (TokenAppearsIn(haystack, token))
            {
                return true;
            }

            foreach (string suffix in s_inflectionSuffixes)
            {
                if (token.Length - suffix.Length < 4
                    || !token.EndsWith(suffix, StringComparison.Ordinal))
                {
                    continue;
                }

                string stem = token.Substring(0, token.Length - suffix.Length);
                if (haystack.IndexOf(stem, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The words of a name that carry identity. Very short fragments are dropped because they
        /// match almost any filename, and the game markers are dropped from the TOKEN SET only - the
        /// wrong-game discard still runs over the full name, so dropping them here loosens matching
        /// without ever letting the other game's archive through.
        /// </summary>
        [NotNull]
        private static List<string> SignificantTokens([CanBeNull] string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new List<string>();
            }

            // Twi'lek / Malak's must stay one word. Splitting on apostrophe dropped
            // "twi"+"lek" (both < 4) and left Better Twi'lek Heads with no product token.
            string joined = value
                .Replace("'", string.Empty, StringComparison.Ordinal)
                .Replace("\u2019", string.Empty, StringComparison.Ordinal);

            return Regex.Split(joined, @"[^A-Za-z0-9]+", RegexOptions.None, TimeSpan.FromSeconds(5))
                .Select(t => t.ToLowerInvariant())
                .Where(t => t.Length >= 4)
                .Where(t => !t.Equals("kotor", StringComparison.Ordinal)
                    && !t.Equals("tslrcm", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Token presence in a normalized stem. "Renovation" and "Revamp" are the same
        /// word in this library (Kebla Yurt Renovation ships as folder Kebla Yurt Revamp).
        /// </summary>
        private static bool TokenAppearsIn([NotNull] string haystack, [NotNull] string token)
        {
            if (haystack.IndexOf(token, StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            if (token.Equals("armor", StringComparison.Ordinal))
            {
                return haystack.IndexOf("armour", StringComparison.Ordinal) >= 0;
            }

            if (token.Equals("armour", StringComparison.Ordinal))
            {
                return haystack.IndexOf("armor", StringComparison.Ordinal) >= 0;
            }

            if (token.Equals("renovation", StringComparison.Ordinal))
            {
                return haystack.IndexOf("revamp", StringComparison.Ordinal) >= 0;
            }

            if (token.Equals("revamp", StringComparison.Ordinal))
            {
                return haystack.IndexOf("renovation", StringComparison.Ordinal) >= 0;
            }

            // KOTOR resref stems: C_DrdProt, DrdProtHD. Unique-or-nothing still applies.
            if (token.Equals("protocol", StringComparison.Ordinal))
            {
                return haystack.IndexOf("prot", StringComparison.Ordinal) >= 0;
            }

            if (token.Equals("droid", StringComparison.Ordinal) || token.Equals("droids", StringComparison.Ordinal))
            {
                return haystack.IndexOf("drd", StringComparison.Ordinal) >= 0;
            }

            if (token.Equals("travel", StringComparison.Ordinal) || token.Equals("transit", StringComparison.Ordinal))
            {
                return haystack.IndexOf("taxi", StringComparison.Ordinal) >= 0;
            }

            if (token.Equals("nameless", StringComparison.Ordinal))
            {
                return haystack.IndexOf("unknown", StringComparison.Ordinal) >= 0;
            }

            return false;
        }

        /// <summary>
        /// Every significant token is present, or a consecutive pair is represented by its
        /// initials as a delimited filename token (SL for Shaleena/Lashowe, EH for Ebon Hawk)
        /// and the remaining tokens are present. Initials are checked against the original
        /// name so "sl" cannot match inside "soldier".
        /// </summary>
        private static bool TokensSatisfied(
            [NotNull] IGrouping<string, FileInfo> group,
            [NotNull] IReadOnlyList<string> tokens)
        {
            if (tokens.All(t => TokenAppearsIn(group.Key, t)))
            {
                return true;
            }

            if (tokens.Count < 3)
            {
                return false;
            }

            HashSet<string> raw = RawTokens(group.First().Name);
            for (int i = 0; i < tokens.Count - 1; i++)
            {
                if (tokens[i].Length < 4 || tokens[i + 1].Length < 4)
                {
                    continue;
                }

                string initials = string.Concat(tokens[i][0], tokens[i + 1][0]);
                if (!raw.Contains(initials))
                {
                    continue;
                }

                var remaining = tokens.Where((_, idx) => idx != i && idx != i + 1).ToList();
                if (remaining.Count > 0 && remaining.All(t => TokenAppearsIn(group.Key, t)))
                {
                    return true;
                }
            }

            return false;
        }

        [NotNull]
        private static HashSet<string> RawTokens([CanBeNull] string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            return new HashSet<string>(
                Regex.Split(
                        Path.GetFileNameWithoutExtension(name),
                        @"[^A-Za-z0-9]+",
                        RegexOptions.None,
                        TimeSpan.FromSeconds(5))
                    .Select(t => t.ToLowerInvariant())
                    .Where(t => t.Length > 0),
                StringComparer.Ordinal);
        }

        /// <summary>
        /// Reduces a tier's candidate set to a single archive, or reports it as ambiguous.
        /// <para>
        /// Ambiguity is not the end of the line: the tiers are signals that COMPOSE. Before giving up,
        /// the remaining candidates are intersected with the other signals available for this
        /// component - the words of its name, then the words of its guide slug. A candidate set of six
        /// that only one member of satisfies is not ambiguous, it is resolved; this is the same
        /// composition the Nexus mod id tier uses, where id 1282 yields six archives and the component
        /// name leaves exactly one.
        /// </para>
        /// <para>
        /// Wrong-game candidates are discarded FIRST, so narrowing can never promote the other game's
        /// archive. If more than one candidate still survives every signal, the component is reported
        /// with its candidate list and left unresolved.
        /// </para>
        /// </summary>
        [CanBeNull]
        private static ArchiveResolution SingleOrAmbiguous(
            [NotNull] List<IGrouping<string, FileInfo>> groups,
            GameMarker targetGame,
            ArchiveResolutionTier tier,
            [NotNull] string reason,
            [NotNull] IReadOnlyList<string> narrowingSignals)
        {
            if (groups.Count == 0)
            {
                return null;
            }

            List<IGrouping<string, FileInfo>> viable = groups
                .Where(g => !IsWrongGame(g.First().Name, targetGame))
                .ToList();

            string componentName = narrowingSignals.Count > 0 ? narrowingSignals[0] : string.Empty;

            // "Skyboxes II" and "Skyboxes" are different products, exactly as K1 and K2 are.
            // SignificantTokens drops "II" for being under four characters, so the sequel marker was
            // invisible and "High Quality Skyboxes II" matched the seven-file patch
            // "High quality skyboxes model fixes.rar" on high+quality+skyboxes alone.
            viable = DiscardSequelMismatch(viable, componentName);
            viable = PreferRealArchivesOverHeadingFolders(viable);

            if (viable.Count == 1)
            {
                // A lone compatibility-patch hit is not a unique match for a non-patch
                // component (K1 Community Patch vs the UCO "KOTOR 1 Community Patch -
                // Compatibility Patch"). Fall through so a later tier can find the real archive.
                if (NameLooksLikeCompatibilityPatch(viable[0].Key)
                    && !ComponentLooksLikeAPatch(componentName))
                {
                    return null;
                }

                return new ArchiveResolution
                {
                    Archive = PreferArchiveFile(viable[0]),
                    Tier = tier,
                    Reason = reason,
                };
            }

            if (viable.Count == 0)
            {
                return null;
            }

            foreach (string signal in narrowingSignals)
            {
                List<string> tokens = SignificantTokens(signal);
                if (tokens.Count == 0)
                {
                    continue;
                }

                List<IGrouping<string, FileInfo>> narrowed = viable
                    .Where(g => tokens.All(t => TokenAppearsIn(g.Key, t)))
                    .ToList();

                if (narrowed.Count == 1)
                {
                    return new ArchiveResolution
                    {
                        Archive = PreferArchiveFile(narrowed[0]),
                        Tier = tier,
                        Reason =
                            $"{reason} {viable.Count} archives matched; '{signal}' narrowed them to one.",
                    };
                }
            }

            // A guide heading that is not itself a patch must not settle on a
            // "Compatibility Patch" sitting beside the main archive (JC's Minor Fixes,
            // JC's Mandalorian Armor, Diversified Jedi Captives). This only fires when
            // exactly one non-patch survivor remains — two mains stay ambiguous.
            List<IGrouping<string, FileInfo>> withoutPatches = PreferNonCompatibilityPatch(viable, componentName);
            if (withoutPatches.Count == 1)
            {
                return new ArchiveResolution
                {
                    Archive = PreferArchiveFile(withoutPatches[0]),
                    Tier = tier,
                    Reason =
                        $"{reason} {viable.Count} archives matched; compatibility-patch files were discarded "
                        + "because the component is not itself a patch.",
                };
            }

            List<IGrouping<string, FileInfo>> k1Marked = PreferExplicitK1Marker(viable, componentName, targetGame);
            if (k1Marked.Count == 1)
            {
                return new ArchiveResolution
                {
                    Archive = PreferArchiveFile(k1Marked[0]),
                    Tier = tier,
                    Reason =
                        $"{reason} {viable.Count} archives matched; the component names K1 and exactly one "
                        + "archive carries an explicit K1 marker.",
                };
            }

            List<IGrouping<string, FileInfo>> withoutPatchSuffix = PreferNonPatchSuffix(viable, componentName);
            if (withoutPatchSuffix.Count == 1)
            {
                return new ArchiveResolution
                {
                    Archive = PreferArchiveFile(withoutPatchSuffix[0]),
                    Tier = tier,
                    Reason =
                        $"{reason} {viable.Count} archives matched; a trailing 'Patch' sibling was discarded.",
                };
            }

            List<IGrouping<string, FileInfo>> guideVariant = PreferExplicitGuideVariant(
                withoutPatchSuffix,
                narrowingSignals);
            if (guideVariant.Count == 1)
            {
                return new ArchiveResolution
                {
                    Archive = PreferArchiveFile(guideVariant[0]),
                    Tier = tier,
                    Reason =
                        $"{reason} {viable.Count} archives matched; an explicit guide recommendation "
                        + "narrowed the archive variant to one.",
                };
            }

            List<IGrouping<string, FileInfo>> quality = PreferRecommendedTextureOption(
                guideVariant,
                narrowingSignals);
            if (quality.Count == 1)
            {
                return new ArchiveResolution
                {
                    Archive = PreferArchiveFile(quality[0]),
                    Tier = tier,
                    Reason =
                        $"{reason} {viable.Count} archives matched; the guide's 2x/TPC recommendation "
                        + "narrowed them to one.",
                };
            }

            List<IGrouping<string, FileInfo>> hdExact = PreferHdHqStrippedExact(viable, componentName);
            if (hdExact.Count == 1)
            {
                return new ArchiveResolution
                {
                    Archive = PreferArchiveFile(hdExact[0]),
                    Tier = tier,
                    Reason =
                        $"{reason} {viable.Count} archives matched; the HD/HQ-stripped name equals exactly one.",
                };
            }

            List<IGrouping<string, FileInfo>> withoutExtra = DiscardExtraProduct(viable, componentName);
            if (withoutExtra.Count == 1)
            {
                return new ArchiveResolution
                {
                    Archive = PreferArchiveFile(withoutExtra[0]),
                    Tier = tier,
                    Reason =
                        $"{reason} {viable.Count} archives matched; extra-product names (Vurt, lightsaber, "
                        + "romance, new clothes) that the component does not mention were discarded.",
                };
            }

            if (withoutExtra.Count == 0)
            {
                // This term's survivors were all a different product. Let a later tier try.
                return null;
            }

            List<IGrouping<string, FileInfo>> newest = PreferNewestSameTitle(withoutExtra);
            if (newest.Count == 1)
            {
                return new ArchiveResolution
                {
                    Archive = PreferArchiveFile(newest[0]),
                    Tier = tier,
                    Reason =
                        $"{reason} {viable.Count} archives matched; they are the same title at different "
                        + "versions, so the newest was taken.",
                };
            }

            List<IGrouping<string, FileInfo>> medium = PreferMedium(withoutExtra, narrowingSignals);
            if (medium.Count == 1)
            {
                return new ArchiveResolution
                {
                    Archive = PreferArchiveFile(medium[0]),
                    Tier = tier,
                    Reason =
                        $"{reason} {viable.Count} archives matched; the guide recommends Medium.",
                };
            }

            List<IGrouping<string, FileInfo>> withoutAnd = PreferNoExtraAndProduct(withoutExtra, componentName);
            if (withoutAnd.Count == 1)
            {
                return new ArchiveResolution
                {
                    Archive = PreferArchiveFile(withoutAnd[0]),
                    Tier = tier,
                    Reason =
                        $"{reason} {viable.Count} archives matched; an 'and <extra product>' sibling "
                        + "the component does not mention was discarded.",
                };
            }

            if (RequestsApplyAllFiles(narrowingSignals) && withoutExtra.Count >= 2)
            {
                List<FileInfo> allFiles = withoutExtra.Select(PreferArchiveFile).ToList();
                return new ArchiveResolution
                {
                    Archive = allFiles[0],
                    AdditionalArchives = allFiles.Skip(1).ToList(),
                    Tier = tier,
                    Reason =
                        $"{reason} {allFiles.Count} archives matched; the guide says to apply all of them.",
                    Candidates = allFiles.Select(a => a.Name).ToList(),
                };
            }

            return new ArchiveResolution
            {
                Tier = ArchiveResolutionTier.Unresolved,
                Reason =
                    $"{reason} but {viable.Count} distinct archives matched equally well and no further signal "
                    + "narrowed them to one. Ambiguous, so not guessed - picking one risks installing the "
                    + "wrong mod.",
                Candidates = viable.Select(g => g.First().Name).ToList(),
            };
        }

        [NotNull]
        private static List<FileInfo> PreferNonCompatibilityPatch(
            [NotNull] IReadOnlyList<FileInfo> archives,
            [NotNull] string componentName)
        {
            if (ComponentLooksLikeAPatch(componentName))
            {
                return archives.ToList();
            }

            List<FileInfo> withoutPatches = archives
                .Where(a => !NameLooksLikeCompatibilityPatch(a.Name))
                .ToList();
            return withoutPatches.Count == 1 ? withoutPatches : archives.ToList();
        }

        [NotNull]
        private static List<IGrouping<string, FileInfo>> PreferNonCompatibilityPatch(
            [NotNull] IReadOnlyList<IGrouping<string, FileInfo>> groups,
            [NotNull] string componentName)
        {
            if (ComponentLooksLikeAPatch(componentName))
            {
                return groups.ToList();
            }

            List<IGrouping<string, FileInfo>> withoutPatches = groups
                .Where(g => !NameLooksLikeCompatibilityPatch(g.Key) && !NameLooksLikeCompatibilityPatch(g.First().Name))
                .ToList();
            return withoutPatches.Count == 1 ? withoutPatches : groups.ToList();
        }

        [NotNull]
        private static List<IGrouping<string, FileInfo>> PreferExplicitK1Marker(
            [NotNull] IReadOnlyList<IGrouping<string, FileInfo>> groups,
            [NotNull] string componentName,
            GameMarker targetGame)
        {
            if (targetGame != GameMarker.Kotor1 || !ComponentMentionsK1(componentName))
            {
                return groups.ToList();
            }

            List<IGrouping<string, FileInfo>> marked = groups
                .Where(g => MarkerOf(g.First().Name) == GameMarker.Kotor1)
                .ToList();
            return marked.Count == 1 ? marked : groups.ToList();
        }

        private static bool ComponentMentionsK1([CanBeNull] string componentName)
        {
            string padded = " " + Regex.Replace(
                componentName ?? string.Empty,
                @"[^A-Za-z0-9]+",
                " ",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)) + " ";
            return padded.ToLowerInvariant().Contains(" k1 ");
        }

        /// <summary>
        /// True when the component IS itself a compatibility patch, so a "Compatibility Patch"
        /// archive is a legitimate answer for it.
        /// <para>
        /// A bare <c>EndsWith("patch")</c> used to satisfy this, which quietly disabled the guard
        /// for every base mod whose name ends in the word: "KOTOR Community Patch" was therefore
        /// allowed to resolve to "KOTOR 1 Community Patch - Compatibility Patch-1282-....rar"
        /// instead of "K1_Community_Patch_v1.10.0.zip". Being named "... Patch" is not the same as
        /// being a compatibility patch for something else.
        /// </para>
        /// </summary>
        private static bool ComponentLooksLikeAPatch([CanBeNull] string componentName)
        {
            string normalized = Normalize(componentName);
            GuideInterpretationPolicy.FilterRules filters = GuideInterpretationPolicyStore.Current.Filters;
            if (ContainsAnyToken(normalized, filters.CompatArchiveTokens)
                || ContainsAnyToken(normalized, filters.CompatArchiveContains))
            {
                return true;
            }

            IReadOnlyList<string> suffixes = filters.CompatArchiveSuffixes;
            if (suffixes != null)
            {
                for (int i = 0; i < suffixes.Count; i++)
                {
                    if (!string.IsNullOrEmpty(suffixes[i])
                        && normalized.EndsWith(suffixes[i], StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool ContainsAnyToken([NotNull] string normalized, [CanBeNull] IReadOnlyList<string> tokens)
        {
            if (tokens == null || tokens.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < tokens.Count; i++)
            {
                if (!string.IsNullOrEmpty(tokens[i])
                    && normalized.Contains(tokens[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ComponentLooksLikeAPatchSet([CanBeNull] string componentName)
        {
            return Normalize(componentName).EndsWith("patches", StringComparison.Ordinal);
        }

        [NotNull]
        private static List<IGrouping<string, FileInfo>> PreferNonPatchSuffix(
            [NotNull] IReadOnlyList<IGrouping<string, FileInfo>> groups,
            [NotNull] string componentName)
        {
            if (ComponentLooksLikeAPatch(componentName))
            {
                return groups.ToList();
            }

            List<IGrouping<string, FileInfo>> without = groups
                .Where(g => !g.Key.EndsWith("patch", StringComparison.Ordinal)
                    && !NameLooksLikeCompatibilityPatch(g.Key))
                .ToList();
            return without.Count == 1 ? without : groups.ToList();
        }

        [NotNull]
        private static List<IGrouping<string, FileInfo>> PreferHdHqStrippedExact(
            [NotNull] IReadOnlyList<IGrouping<string, FileInfo>> groups,
            [NotNull] string componentName)
        {
            string withoutQuality = Regex.Replace(
                componentName ?? string.Empty,
                @"^(HD|HQ|Hi-?Res)\s+",
                string.Empty,
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5));
            withoutQuality = Regex.Replace(
                withoutQuality,
                @"\s+(HD|HQ|Hi-?Res)$",
                string.Empty,
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5));
            string stripped = Normalize(withoutQuality);
            if (stripped.Length < 6)
            {
                return groups.ToList();
            }

            List<IGrouping<string, FileInfo>> exact = groups
                .Where(g => Normalize(TitleWithoutParens(g.First().Name)) == stripped)
                .ToList();
            return exact.Count == 1 ? exact : groups.ToList();
        }

        [NotNull]
        private static string TitleWithoutParens([CanBeNull] string fileName)
        {
            string baseName = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
            return Regex.Replace(baseName, @"\s*\([^)]*\)", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(5))
                .Trim();
        }

        [NotNull]
        private static List<IGrouping<string, FileInfo>> DiscardExtraProduct(
            [NotNull] IReadOnlyList<IGrouping<string, FileInfo>> groups,
            [NotNull] string componentName)
        {
            string componentNorm = Normalize(componentName);
            string[] extras = { "vurt", "visualresurgence", "lightsaber", "romance", "newclothes", "ultimate", "duncan" };
            List<IGrouping<string, FileInfo>> kept = groups
                .Where(g =>
                {
                    foreach (string extra in extras)
                    {
                        if (g.Key.IndexOf(extra, StringComparison.Ordinal) >= 0
                            && componentNorm.IndexOf(extra, StringComparison.Ordinal) < 0)
                        {
                            return false;
                        }
                    }

                    return true;
                })
                .ToList();
            return kept;
        }

        [NotNull]
        private static List<IGrouping<string, FileInfo>> PreferNewestSameTitle(
            [NotNull] IReadOnlyList<IGrouping<string, FileInfo>> groups)
        {
            if (groups.Count < 2)
            {
                return groups.ToList();
            }

            var parsed = groups
                .Select(g => (Group: g, Title: VersionlessTitle(g.First().Name), Version: ParseTrailingVersion(g.First().Name) ?? new Version(0, 0, 0)))
                .ToList();
            if (parsed.Select(p => p.Title).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                return groups.ToList();
            }

            IGrouping<string, FileInfo> newest = parsed
                .OrderByDescending(p => p.Version)
                .First()
                .Group;
            return new List<IGrouping<string, FileInfo>> { newest };
        }

        [CanBeNull]
        private static Version ParseTrailingVersion([CanBeNull] string fileName)
        {
            string baseName = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
            Match m = Regex.Match(
                baseName,
                @"v(\d+)(?:[._](\d+))?(?:[._](\d+))?$",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5));
            if (!m.Success)
            {
                return null;
            }

            int major = int.Parse(m.Groups[1].Value);
            int minor = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            int build = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            return new Version(major, minor, build);
        }

        [NotNull]
        private static string VersionlessTitle([CanBeNull] string fileName)
        {
            string baseName = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
            return Normalize(Regex.Replace(
                baseName,
                @"v\d+(?:[._]\d+)*$",
                string.Empty,
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5)));
        }

        [NotNull]
        private static List<IGrouping<string, FileInfo>> PreferMedium(
            [NotNull] IReadOnlyList<IGrouping<string, FileInfo>> groups,
            [NotNull] IReadOnlyList<string> signals)
        {
            bool recommendsMedium = signals.Any(s =>
                Normalize(s).IndexOf("medium", StringComparison.Ordinal) >= 0);
            if (!recommendsMedium)
            {
                return groups.ToList();
            }

            List<IGrouping<string, FileInfo>> medium = groups
                .Where(g =>
                {
                    string padded = " " + Regex.Replace(
                        Path.GetFileNameWithoutExtension(g.First().Name),
                        @"[^A-Za-z0-9]+",
                        " ",
                        RegexOptions.None,
                        TimeSpan.FromSeconds(5)) + " ";
                    padded = padded.ToLowerInvariant();
                    return padded.Contains(" m ") || padded.Contains(" medium ");
                })
                .ToList();
            return medium.Count == 1 ? medium : groups.ToList();
        }

        /// <summary>
        /// Guide download notes such as "Strongly recommend the 2x .tpc version" are a
        /// first-class signal, not a score. When they name a texture size/format and
        /// exactly one candidate carries those tokens, that candidate wins — including
        /// over a same-title TSL folder that merely equals the heading.
        /// </summary>
        [NotNull]
        private static List<IGrouping<string, FileInfo>> PreferRecommendedTextureOption(
            [NotNull] IReadOnlyList<IGrouping<string, FileInfo>> groups,
            [NotNull] IReadOnlyList<string> signals)
        {
            string joined = string.Join(" ", signals.Select(Normalize));
            bool wants2x = joined.IndexOf("2x", StringComparison.Ordinal) >= 0;
            bool wantsTpc = joined.IndexOf("tpc", StringComparison.Ordinal) >= 0;
            if (!wants2x && !wantsTpc)
            {
                return groups.ToList();
            }

            List<IGrouping<string, FileInfo>> matched = groups
                .Where(g =>
                {
                    string name = Normalize(g.First().Name);
                    if (wants2x && name.IndexOf("2x", StringComparison.Ordinal) < 0)
                    {
                        return false;
                    }

                    if (wantsTpc && name.IndexOf("tpc", StringComparison.Ordinal) < 0)
                    {
                        return false;
                    }

                    return true;
                })
                .ToList();
            return matched.Count == 1 ? matched : groups.ToList();
        }

        /// <summary>
        /// Narrows variants only from directive clauses in guide prose (for example, "download only
        /// the Damaged version" or "I recommend the reskin-friendly version"). Merely mentioning a
        /// variant elsewhere is not enough. A "main file, not compatches" directive first removes
        /// compatibility products; when the guide lists an unsuffixed main and an explicitly reduced
        /// resolution alternative, the unsuffixed main is the deterministic default.
        /// </summary>
        [NotNull]
        private static List<IGrouping<string, FileInfo>> PreferExplicitGuideVariant(
            [NotNull] IReadOnlyList<IGrouping<string, FileInfo>> groups,
            [NotNull] IReadOnlyList<string> signals)
        {
            if (groups.Count < 2)
            {
                return groups.ToList();
            }

            string prose = string.Join(" ", signals);
            string[] directiveClauses = Regex.Split(
                    prose,
                    @"(?<=[.!?;])\s+",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(5))
                .Where(clause => Regex.IsMatch(
                    clause,
                    @"\b(?:recommend|download\s+(?:only|just|the)|use\s+the)\b",
                    RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(5))
                    && !IsNegativeGuideClause(clause))
                .ToArray();

            foreach (string clause in directiveClauses)
            {
                string normalizedClause = Normalize(clause);
                var scored = new List<(IGrouping<string, FileInfo> Group, int Score)>();
                foreach (IGrouping<string, FileInfo> group in groups)
                {
                    List<string> candidateTokens = RawTokens(group.First().Name)
                        .Where(token => token.Length >= 4 || Regex.IsMatch(
                            token,
                            @"^\d+k$",
                            RegexOptions.IgnoreCase,
                            TimeSpan.FromSeconds(5)))
                        .Where(token => !groups.All(other => RawTokens(other.First().Name).Contains(token)))
                        .ToList();
                    int score = candidateTokens.Count(token =>
                        normalizedClause.Contains(token, StringComparison.Ordinal));
                    scored.Add((group, score));
                }

                int bestScore = scored.Max(candidate => candidate.Score);
                List<IGrouping<string, FileInfo>> hits = scored
                    .Where(candidate => candidate.Score == bestScore && bestScore > 0)
                    .Select(candidate => candidate.Group)
                    .ToList();
                if (hits.Count == 1)
                {
                    return hits;
                }
            }

            bool requestsMainWithoutCompatches = Regex.IsMatch(
                prose,
                @"\bmain\s+file\b[\s\S]*?\bnot\s+(?:any\s+)?(?:of\s+the\s+)?compatches\b",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5));
            if (requestsMainWithoutCompatches)
            {
                List<IGrouping<string, FileInfo>> main = groups
                    .Where(group => !Regex.IsMatch(
                        group.First().Name,
                        @"(?:compatch|compatib|m4[-_ ]?78|jedi\s*temple)",
                        RegexOptions.IgnoreCase,
                        TimeSpan.FromSeconds(5)))
                    .ToList();
                List<IGrouping<string, FileInfo>> unsuffixed = main
                    .Where(group => !Regex.IsMatch(
                        group.First().Name,
                        @"(?:^|[^0-9])\d+k(?:[^0-9]|$)",
                        RegexOptions.IgnoreCase,
                        TimeSpan.FromSeconds(5)))
                    .ToList();
                if (unsuffixed.Count == 1)
                {
                    return unsuffixed;
                }
            }

            return groups.ToList();
        }

        /// <summary>
        /// "Ignore the txi.rar file" / "Do not download the .tga file" name a file so the
        /// reader will skip it. Those clauses must not select that file.
        /// </summary>
        [CanBeNull]
        private static ArchiveResolution ResolveByDownloadOnlyVariant(
            [CanBeNull] IReadOnlyList<string> extraSignals,
            [NotNull] IReadOnlyList<FileInfo> candidates,
            GameMarker targetGame)
        {
            if (extraSignals == null || extraSignals.Count == 0)
            {
                return null;
            }

            string prose = string.Join(" ", extraSignals);
            if (!Regex.IsMatch(
                    prose,
                    @"\bdownload\s+(?:only|just)\b",
                    RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(5)))
            {
                return null;
            }

            List<IGrouping<string, FileInfo>> logical = candidates
                .Where(a => !IsWrongGame(a.Name, targetGame))
                .GroupBy(a => LogicalArchiveStem(a.Name), StringComparer.Ordinal)
                .ToList();
            if (logical.Count < 2)
            {
                return null;
            }

            List<IGrouping<string, FileInfo>> narrowed = PreferExplicitGuideVariant(logical, extraSignals);
            if (narrowed.Count != 1)
            {
                return null;
            }

            return new ArchiveResolution
            {
                Archive = PreferArchiveFile(narrowed[0]),
                Tier = ArchiveResolutionTier.GuideDirective,
                Reason = "Guide download note named a unique archive variant.",
            };
        }

        private static bool RequestsApplyAllFiles([CanBeNull] IReadOnlyList<string> signals)
        {
            if (signals == null || signals.Count == 0)
            {
                return false;
            }

            string prose = string.Join(" ", signals);
            return Regex.IsMatch(
                prose,
                @"\b(?:download\s+and\s+apply|apply)\s+all\s+files\b",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5));
        }

        private static bool IsNegativeGuideClause([CanBeNull] string clause)
        {
            if (string.IsNullOrWhiteSpace(clause))
            {
                return false;
            }

            return Regex.IsMatch(
                    clause,
                    @"\brecommend(?:s|ed)?\s+(?:against|not)\b",
                    RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(5))
                || Regex.IsMatch(
                    clause,
                    @"\b(?:do\s+not|don'?t|does\s+not|ignore|never|skip(?:ping)?)\b",
                    RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(5));
        }

        private static bool ProseRejectsNamedFile([CanBeNull] string prose, [CanBeNull] string fileName)
        {
            if (string.IsNullOrWhiteSpace(prose) || string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            foreach (string clause in Regex.Split(
                         prose,
                         @"(?<=[.!?;])\s+",
                         RegexOptions.None,
                         TimeSpan.FromSeconds(5)))
            {
                if (clause.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (IsNegativeGuideClause(clause))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// "4GB Patcher" has no four-letter identity token besides "patcher". The library
        /// has exactly one <c>4gb…</c> archive (<c>4GB_Patch.zip</c>).
        /// </summary>
        [CanBeNull]
        private static ArchiveResolution ResolveByCompactProductToken(
            [NotNull] string componentName,
            [NotNull] List<IGrouping<string, FileInfo>> logical,
            GameMarker targetGame)
        {
            Match compact = Regex.Match(
                Normalize(componentName),
                @"^\d+[a-z]{2}",
                RegexOptions.None,
                TimeSpan.FromSeconds(5));
            if (!compact.Success)
            {
                return null;
            }

            string token = compact.Value;
            List<IGrouping<string, FileInfo>> hits = logical
                .Where(g => !IsWrongGame(g.First().Name, targetGame)
                    && g.Key.IndexOf(token, StringComparison.Ordinal) >= 0)
                .ToList();
            return hits.Count == 1
                ? new ArchiveResolution
                {
                    Archive = PreferArchiveFile(hits[0]),
                    Tier = ArchiveResolutionTier.UniqueLongToken,
                    Reason = $"Exactly one archive name contains the compact product token '{token}'.",
                }
                : null;
        }

        /// <summary>
        /// "CK-Classic Class Attack Bonus and Weaker Consulars" is a different product sitting
        /// beside the main archive. Discard the conjunct only when the component does not
        /// mention those extra words and exactly one archive remains.
        /// </summary>
        [NotNull]
        private static List<IGrouping<string, FileInfo>> PreferNoExtraAndProduct(
            [NotNull] IReadOnlyList<IGrouping<string, FileInfo>> groups,
            [NotNull] string componentName)
        {
            if (groups.Count < 2 || Normalize(componentName).Contains("and", StringComparison.Ordinal))
            {
                return groups.ToList();
            }

            List<IGrouping<string, FileInfo>> withoutAnd = groups
                .Where(g => !Regex.IsMatch(
                    g.First().Name,
                    @"\band\b",
                    RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(5)))
                .ToList();
            return withoutAnd.Count == 1 ? withoutAnd : groups.ToList();
        }

        [CanBeNull]
        private static ArchiveResolution ResolveByAcronym(
            [NotNull] string componentName,
            [NotNull] List<IGrouping<string, FileInfo>> logical,
            GameMarker targetGame)
        {
            List<string> tokens = AcronymTokens(componentName);
            if (tokens.Count < 3)
            {
                return null;
            }

            string acronym = BuildIdentityAcronym(tokens);
            if (acronym.Length < 3)
            {
                return null;
            }

            var hits = logical
                .Where(g => !IsWrongGame(g.First().Name, targetGame) && g.Key == acronym)
                .ToList();
            return hits.Count == 1
                ? new ArchiveResolution
                {
                    Archive = PreferArchiveFile(hits[0]),
                    Tier = ArchiveResolutionTier.AcronymExact,
                    Reason = $"Archive basename equals the initials of '{componentName}' ({acronym.ToUpperInvariant()}).",
                }
                : null;
        }

        /// <summary>
        /// The archive basename contains the initials rather than equalling them
        /// (<c>di_kaw2.7z</c> for Korriban Academy Workbench, <c>SMRE Version 3.0.zip</c>
        /// for Sunry Murder Recording Enhancement, <c>K1 PAVOR v1.3.2.7z</c> for Ported Alien VO
        /// Replacements). Still unique-or-nothing.
        /// </summary>
        [CanBeNull]
        private static ArchiveResolution ResolveByAcronymContained(
            [NotNull] string componentName,
            [NotNull] List<IGrouping<string, FileInfo>> logical,
            GameMarker targetGame)
        {
            List<string> tokens = AcronymTokens(componentName);
            if (tokens.Count < 3)
            {
                return null;
            }

            string acronym = BuildIdentityAcronym(tokens);
            if (acronym.Length < 3)
            {
                return null;
            }

            var hits = logical
                .Where(g => !IsWrongGame(g.First().Name, targetGame)
                    && g.Key.IndexOf(acronym, StringComparison.Ordinal) >= 0)
                .ToList();
            return hits.Count == 1
                ? new ArchiveResolution
                {
                    Archive = PreferArchiveFile(hits[0]),
                    Tier = ArchiveResolutionTier.AcronymExact,
                    Reason =
                        $"Archive name contains the initials of '{componentName}' ({acronym.ToUpperInvariant()}).",
                }
                : null;
        }

        /// <summary>
        /// Two-letter tokens (VO, HD) contribute both letters so "Ported Alien VO Replacements"
        /// becomes PAVOR rather than PAVR.
        /// </summary>
        [NotNull]
        private static string BuildIdentityAcronym([NotNull] IReadOnlyList<string> tokens)
        {
            var sb = new StringBuilder(tokens.Count + 4);
            foreach (string token in tokens)
            {
                if (token.Length == 2)
                {
                    _ = sb.Append(token);
                }
                else
                {
                    _ = sb.Append(token[0]);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// The guide description names an expanded title whose initials are the archive
        /// ("Senni Vek Restoration" → SVR1.2). Only phrases that start with the component's
        /// own distinctive tokens are considered, and the acronym must uniquely match.
        /// </summary>
        [CanBeNull]
        private static ArchiveResolution ResolveByExpandedAcronym(
            [NotNull] string componentName,
            [CanBeNull] IReadOnlyList<string> extraSignals,
            [NotNull] List<IGrouping<string, FileInfo>> logical,
            GameMarker targetGame)
        {
            if (extraSignals == null || extraSignals.Count == 0)
            {
                return null;
            }

            List<string> nameTokens = AcronymTokens(componentName);
            if (nameTokens.Count < 2)
            {
                return null;
            }

            var hits = new List<IGrouping<string, FileInfo>>();
            foreach (string signal in extraSignals)
            {
                List<string> words = AcronymTokens(signal);
                for (int i = 0; i + nameTokens.Count < words.Count; i++)
                {
                    if (!nameTokens.SequenceEqual(words.Skip(i).Take(nameTokens.Count)))
                    {
                        continue;
                    }

                    List<string> phrase = words.Skip(i).Take(nameTokens.Count + 1).ToList();
                    if (phrase.Count < 3)
                    {
                        continue;
                    }

                    string acronym = string.Concat(phrase.Select(t => t[0]));
                    if (acronym.Length < 3)
                    {
                        continue;
                    }

                    IEnumerable<IGrouping<string, FileInfo>> matched = logical
                        .Where(g => !IsWrongGame(g.First().Name, targetGame)
                            && StemStartsWithAcronymThenVersion(g.Key, acronym));
                    hits.AddRange(matched);
                }
            }

            List<IGrouping<string, FileInfo>> unique = hits
                .GroupBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
            return unique.Count == 1
                ? new ArchiveResolution
                {
                    Archive = PreferArchiveFile(unique[0]),
                    Tier = ArchiveResolutionTier.AcronymExact,
                    Reason =
                        $"Archive basename is the initials of a guide phrase that expands '{componentName}'.",
                }
                : null;
        }

        [NotNull]
        private static List<string> AcronymTokens([CanBeNull] string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new List<string>();
            }

            // Length >= 2 keeps "VO" in "K1 Ported Alien VO Replacements" → PAVOR.
            // Game markers and function words stay out so they never inflate the initials.
            string[] stop =
            {
                "mod", "pack", "the", "for", "and", "with", "from", "this", "that", "your",
                "on", "of", "to", "in", "or", "an", "a", "k1", "k2", "kotor", "tsl",
            };
            return Regex.Split(value, @"[^A-Za-z0-9]+", RegexOptions.None, TimeSpan.FromSeconds(5))
                .Select(t => t.ToLowerInvariant())
                .Where(t => t.Length >= 2)
                .Where(t => !stop.Contains(t))
                .ToList();
        }

        private static bool StemStartsWithAcronymThenVersion([NotNull] string stem, [NotNull] string acronym)
        {
            if (!stem.StartsWith(acronym, StringComparison.Ordinal))
            {
                return false;
            }

            string rest = stem.Substring(acronym.Length);
            return rest.Length == 0 || rest.All(char.IsDigit);
        }

        /// <summary>
        /// Author initials or short handle as a filename prefix (LDR → ldr_repshipunknownworld),
        /// unique among archives that also carry a subject token from the component name.
        /// </summary>
        [CanBeNull]
        private static ArchiveResolution ResolveByAuthorPrefix(
            [CanBeNull] string author,
            [NotNull] string componentName,
            [NotNull] List<IGrouping<string, FileInfo>> logical,
            GameMarker targetGame)
        {
            string authorKey = Normalize(author);
            if (authorKey.Length < 3)
            {
                return null;
            }

            List<string> subject = SignificantTokens(componentName);
            var prefixed = logical
                .Where(g => !IsWrongGame(g.First().Name, targetGame)
                    && g.Key.StartsWith(authorKey, StringComparison.Ordinal))
                .ToList();
            if (prefixed.Count == 0)
            {
                return null;
            }

            if (prefixed.Count == 1 && subject.Any(t => TokenAppearsIn(prefixed[0].Key, t)))
            {
                return new ArchiveResolution
                {
                    Archive = PreferArchiveFile(prefixed[0]),
                    Tier = ArchiveResolutionTier.SlugAuthorSubject,
                    Reason = $"Author prefix '{author}' plus a subject token uniquely matched.",
                };
            }

            if (subject.Count == 0)
            {
                return null;
            }

            var withSubject = prefixed
                .Where(g => subject.Any(t => TokenAppearsIn(g.Key, t)))
                .ToList();
            return withSubject.Count == 1
                ? new ArchiveResolution
                {
                    Archive = PreferArchiveFile(withSubject[0]),
                    Tier = ArchiveResolutionTier.SlugAuthorSubject,
                    Reason = $"Author prefix '{author}' plus a subject token uniquely matched.",
                }
                : null;
        }

        [CanBeNull]
        private static ArchiveResolution ResolveBySlugAuthorSubject(
            [NotNull] IReadOnlyList<string> componentUrls,
            [NotNull] List<IGrouping<string, FileInfo>> logical,
            GameMarker targetGame)
        {
            foreach (string url in componentUrls)
            {
                string slug = UrlSlug(url);
                if (string.IsNullOrEmpty(slug))
                {
                    continue;
                }

                List<string> tokens = SignificantTokens(slug);
                if (tokens.Count < 2)
                {
                    continue;
                }

                string author = tokens[0];
                List<string> subject = tokens.Skip(1).ToList();
                var hits = logical
                    .Where(g => !IsWrongGame(g.First().Name, targetGame))
                    .Where(g =>
                    {
                        bool authorHit = g.Key.StartsWith(author, StringComparison.Ordinal)
                            || (author.EndsWith("s", StringComparison.Ordinal)
                                && g.Key.StartsWith(author.TrimEnd('s'), StringComparison.Ordinal));
                        return authorHit && subject.Any(t => g.Key.IndexOf(t, StringComparison.Ordinal) >= 0
                            || (t.EndsWith("s", StringComparison.Ordinal)
                                && g.Key.IndexOf(t.TrimEnd('s'), StringComparison.Ordinal) >= 0));
                    })
                    .ToList();

                if (hits.Count == 1)
                {
                    return new ArchiveResolution
                    {
                        Archive = PreferArchiveFile(hits[0]),
                        Tier = ArchiveResolutionTier.SlugAuthorSubject,
                        Reason = $"Slug author '{author}' plus a subject token uniquely matched.",
                    };
                }
            }

            return null;
        }

        [CanBeNull]
        private static ArchiveResolution ResolveByUniqueLongToken(
            [NotNull] IReadOnlyList<string> searchTerms,
            [NotNull] List<IGrouping<string, FileInfo>> logical,
            GameMarker targetGame)
        {
            var tokens = searchTerms
                .SelectMany(SignificantTokens)
                .Where(t => t.Length >= 6)
                .Where(t => !t.Equals("patcher", StringComparison.Ordinal)
                    && !t.Equals("installer", StringComparison.Ordinal)
                    && !t.Equals("download", StringComparison.Ordinal)
                    && !t.Equals("override", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            string componentName = searchTerms.Count > 0 ? searchTerms[0] : string.Empty;

            // Every long token that lands on exactly one archive, longest first.
            var uniqueHits = new List<KeyValuePair<string, IGrouping<string, FileInfo>>>();
            foreach (string token in tokens.OrderByDescending(t => t.Length))
            {
                var hits = logical
                    .Where(g => !IsWrongGame(g.First().Name, targetGame)
                        && g.Key.IndexOf(token, StringComparison.Ordinal) >= 0)
                    .ToList();
                if (hits.Count == 1)
                {
                    uniqueHits.Add(new KeyValuePair<string, IGrouping<string, FileInfo>>(token, hits[0]));
                }
            }

            if (uniqueHits.Count == 0)
            {
                return null;
            }

            // Taking the LONGEST such token was arbitrary: for "Sherruk Attacks with Lightsabers"
            // it picked "lightsabers", landing on the unrelated "Assassins with Lightsabers" while
            // "sherruk" pointed straight at the right archive. Rank by how much of the component
            // name the archive actually accounts for instead.
            List<string> coverage = CoverageTokens(componentName);
            List<KeyValuePair<string, IGrouping<string, FileInfo>>> best = uniqueHits;
            if (coverage.Count > 0)
            {
                int max = uniqueHits.Max(u => CountCovered(u.Value, coverage));
                best = uniqueHits.Where(u => CountCovered(u.Value, coverage) == max).ToList();

                if (best.Count > 1)
                {
                    // Same coverage: prefer the archive that is about nothing else.
                    List<KeyValuePair<string, IGrouping<string, FileInfo>>> tight = best
                        .Where(u => IsTightFit(u.Value, coverage))
                        .ToList();
                    if (tight.Count > 0)
                    {
                        best = tight;
                    }
                }
            }

            KeyValuePair<string, IGrouping<string, FileInfo>> chosen = best[0];
            return ReconcileLongTokenHit(
                chosen.Value, logical, componentName ?? string.Empty, targetGame, chosen.Key);
        }

        /// <summary>
        /// True when every significant word of the archive's own title is accounted for by the
        /// component name, i.e. the archive introduces no other subject.
        /// </summary>
        private static bool IsTightFit(
            [NotNull] IGrouping<string, FileInfo> group,
            [NotNull] IReadOnlyList<string> nameTokens)
        {
            return OwnTokens(group).All(t => nameTokens.Any(n =>
                n.IndexOf(t, StringComparison.Ordinal) >= 0 || t.IndexOf(n, StringComparison.Ordinal) >= 0));
        }

        /// <summary>
        /// A single long token is a weak identifier: it says one word of the component's name occurs
        /// in exactly one filename, and nothing about the other words. Measured on a real K1 build,
        /// that handed three components an unrelated mod:
        /// <list type="bullet">
        /// <item>"Quanon's Canderous Ordo" -> Quanons_HK47_Reskin.rar (matched "quanons" only)</item>
        /// <item>"Ajunta Pall's Swords Revamped" -> Revamped FX.rar (matched "revamped" only)</item>
        /// <item>"Kill the Czerka Jerk on Kashyyyk" -> a Kashyyyk forcefield mod (matched "kashyyyk")</item>
        /// </list>
        /// In every case another archive in the same library carried MORE of the component's words.
        /// So before accepting a one-word hit, look for a candidate that covers more of the name;
        /// if one clearly does, it wins, and if the field stays tied the component is reported
        /// rather than guessed at.
        /// </summary>
        [CanBeNull]
        private static ArchiveResolution ReconcileLongTokenHit(
            [NotNull] IGrouping<string, FileInfo> hit,
            [NotNull] List<IGrouping<string, FileInfo>> logical,
            [NotNull] string componentName,
            GameMarker targetGame,
            [NotNull] string token)
        {
            ArchiveResolution accepted = new ArchiveResolution
            {
                Archive = PreferArchiveFile(hit),
                Tier = ArchiveResolutionTier.UniqueLongToken,
                Reason = $"Exactly one archive name contains the long token '{token}'.",
            };

            List<string> nameTokens = CoverageTokens(componentName);
            if (nameTokens.Count < 2)
            {
                // Nothing distinctive left to cross-check; this tier owns that case.
                return accepted;
            }

            int hitCoverage = CountCovered(hit, nameTokens);

            // One-word UniqueLongToken hits are weak: "replacements" alone handed
            // "K1 Ported Alien VO Replacements" to Quarterstaff Replacements.rar while
            // "K1 PAVOR v1.3.2.7z" (the component's own initials) sat unused. Prefer a unique
            // acronym archive over a single-token coincidence.
            if (hitCoverage <= 1 && nameTokens.Count >= 2)
            {
                ArchiveResolution byAcronym = PreferAcronymOverWeakLongToken(
                    componentName, logical, targetGame, token);
                if (byAcronym != null)
                {
                    return byAcronym;
                }
            }

            List<IGrouping<string, FileInfo>> better = logical
                .Where(g => !IsWrongGame(g.First().Name, targetGame))
                .Where(g => CountCovered(g, nameTokens) > hitCoverage)
                .ToList();
            better = PreferNonCompatibilityPatch(better, componentName);

            if (better.Count == 0)
            {
                return accepted;
            }

            int best = better.Max(g => CountCovered(g, nameTokens));
            List<IGrouping<string, FileInfo>> top = better
                .Where(g => CountCovered(g, nameTokens) == best)
                .ToList();

            if (top.Count == 1)
            {
                return BetterCoverage(top[0], token, best, nameTokens.Count);
            }

            // Tie-break 1: keep the distinctive word that made this tier look here at all.
            List<IGrouping<string, FileInfo>> keepsToken = top
                .Where(g => TokenAppearsInflected(g.Key, token))
                .ToList();
            if (keepsToken.Count == 1)
            {
                return BetterCoverage(keepsToken[0], token, best, nameTokens.Count);
            }

            // Tie-break 2: prefer the archive that introduces no subject the component never named.
            // "Ajunta's Swords" is entirely about Ajunta's swords; "Ajunta Pall Unique Appearance"
            // and "Legends Ajunta Pall's Blade" each drag in a different product.
            List<IGrouping<string, FileInfo>> tightFit = top
                .Where(g => IsTightFit(g, nameTokens))
                .ToList();
            if (tightFit.Count == 1)
            {
                return BetterCoverage(tightFit[0], token, best, nameTokens.Count);
            }

            // Several archives cover the name equally well. Reporting beats guessing.
            return null;
        }

        /// <summary>
        /// When UniqueLongToken latched onto a single shared noun ("replacements"), prefer the
        /// archive whose basename contains the component's initials (PAVOR) if that hit is unique.
        /// </summary>
        [CanBeNull]
        private static ArchiveResolution PreferAcronymOverWeakLongToken(
            [NotNull] string componentName,
            [NotNull] List<IGrouping<string, FileInfo>> logical,
            GameMarker targetGame,
            [NotNull] string weakToken)
        {
            List<string> acronymTokens = AcronymTokens(componentName);
            if (acronymTokens.Count < 3)
            {
                return null;
            }

            string acronym = BuildIdentityAcronym(acronymTokens);
            if (acronym.Length < 4)
            {
                return null;
            }

            var hits = logical
                .Where(g => !IsWrongGame(g.First().Name, targetGame)
                    && g.Key.IndexOf(acronym, StringComparison.Ordinal) >= 0)
                .ToList();
            if (hits.Count != 1)
            {
                return null;
            }

            return new ArchiveResolution
            {
                Archive = PreferArchiveFile(hits[0]),
                Tier = ArchiveResolutionTier.AcronymExact,
                Reason =
                    $"Archive name contains the initials of '{componentName}' ({acronym.ToUpperInvariant()}); "
                    + $"the lone '{weakToken}' match was a weaker one-word coincidence.",
            };
        }

        [NotNull]
        private static ArchiveResolution BetterCoverage(
            [NotNull] IGrouping<string, FileInfo> group,
            [NotNull] string token,
            int covered,
            int total)
        {
            return new ArchiveResolution
            {
                Archive = PreferArchiveFile(group),
                Tier = ArchiveResolutionTier.UniqueLongToken,
                Reason =
                    $"Covers {covered} of {total} words of the component name; the lone '{token}' match covered fewer.",
            };
        }

        private static int CountCovered(
            [NotNull] IGrouping<string, FileInfo> group,
            [NotNull] IReadOnlyList<string> nameTokens)
        {
            return nameTokens.Count(t => TokenAppearsIn(group.Key, t));
        }

        /// <summary>
        /// Words that are four characters or longer but carry no identity, so they must not be
        /// COUNTED as coverage even though they are fine to require as presence.
        /// <para>
        /// Measured regressions from counting them: "Sherruk Attacks with Lightsabers" scored
        /// "Assassins with Lightsabers" above the correct "sherruksabers.7z" on the strength of
        /// "with", and "High Quality Starfields and Nebulas" scored "High Quality Blasters" and
        /// "High quality skyboxes model fixes" above the correct "K1_HDStarsAndNebulas_1_3.zip"
        /// because "high" and "quality" describe half the library.
        /// </para>
        /// </summary>
        private static readonly HashSet<string> s_nonIdentityWords =
            new HashSet<string>(StringComparer.Ordinal)
            {
                // Function words.
                "with", "from", "your", "that", "this", "into", "then", "than", "they", "them",
                "some", "more", "most", "only", "also", "when", "what", "which", "will", "have",
                "does", "over", "under", "after", "before", "other", "their", "there", "these",
                "those", "both", "each", "such", "very", "just", "like", "onto", "upon", "while",
                "without", "using", "used", "same", "must", "need",
                // Generic quality descriptors shared by dozens of unrelated mods.
                "high", "quality", "better", "improved", "enhanced", "remaster", "remastered",
                "definitive", "edition", "version", "final", "full", "hires", "highres",
                "texture", "textures", "reskin", "retexture", "pack", "mods", "files",
            };

        /// <summary>
        /// The words of a component name that actually identify it, for coverage COUNTING.
        /// </summary>
        [NotNull]
        private static List<string> CoverageTokens([CanBeNull] string componentName)
        {
            return SignificantTokens(componentName)
                .Where(t => !s_nonIdentityWords.Contains(t))
                .ToList();
        }

        /// <summary>The significant words of an archive's own title.</summary>
        [NotNull]
        private static List<string> OwnTokens([NotNull] IGrouping<string, FileInfo> group)
        {
            string stem = ArchiveStemWithoutExtension(group.First().Name);
            stem = Regex.Replace(stem, @"\s*\([^)]*\)", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(5));
            return SignificantTokens(stem)
                .Where(t => !t.Equals("reskin", StringComparison.Ordinal)
                    && !t.Equals("retexture", StringComparison.Ordinal)
                    && !t.Equals("version", StringComparison.Ordinal)
                    && !t.Equals("final", StringComparison.Ordinal))
                .ToList();
        }

        [CanBeNull]
        private static ArchiveResolution ResolveByLastTokenExact(
            [NotNull] string componentName,
            [NotNull] List<IGrouping<string, FileInfo>> logical,
            GameMarker targetGame)
        {
            List<string> tokens = SignificantTokens(componentName);
            if (tokens.Count == 0)
            {
                return null;
            }

            string last = tokens[tokens.Count - 1];
            if (last.Length < 5)
            {
                return null;
            }

            var hits = logical
                .Where(g => !IsWrongGame(g.First().Name, targetGame) && g.Key == last)
                .Where(g => tokens.All(t =>
                    t.Length < 6
                    || t.Equals(last, StringComparison.Ordinal)
                    || TokenAppearsIn(g.Key, t)))
                .ToList();
            return hits.Count == 1
                ? new ArchiveResolution
                {
                    Archive = PreferArchiveFile(hits[0]),
                    Tier = ArchiveResolutionTier.UniqueLongToken,
                    Reason = $"Archive basename equals the last name token '{last}'.",
                }
                : null;
        }

        [CanBeNull]
        private static ArchiveResolution ResolveByDroppedTokenSubset(
            [NotNull] string componentName,
            [NotNull] List<IGrouping<string, FileInfo>> logical,
            GameMarker targetGame,
            [NotNull] IReadOnlyList<string> narrowingSignals)
        {
            List<string> tokens = SignificantTokens(componentName);
            if (tokens.Count < 3)
            {
                return null;
            }

            var variants = new List<List<string>>
            {
                tokens.Skip(1).ToList(),
                tokens.Take(tokens.Count - 1).ToList(),
            };

            foreach (List<string> subset in variants)
            {
                if (subset.Count < 2)
                {
                    continue;
                }

                var hits = logical
                    .Where(g => !IsWrongGame(g.First().Name, targetGame)
                        && subset.All(t => TokenAppearsIn(g.Key, t)))
                    .ToList();
                ArchiveResolution result = SingleOrAmbiguous(
                    hits,
                    targetGame,
                    ArchiveResolutionTier.UniqueTokenSubset,
                    $"Archive name contains every remaining word of '{componentName}' after dropping a qualifier.",
                    narrowingSignals);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private static bool NameLooksLikeCompatibilityPatch([CanBeNull] string name)
        {
            string normalized = Normalize(name);
            IReadOnlyList<string> tokens = GuideInterpretationPolicyStore.Current.Filters.CompatArchiveTokens;
            return ContainsAnyToken(normalized, tokens)
                || normalized.Contains("compatibilitypatch", StringComparison.Ordinal);
        }

        private static readonly string[] RealArchiveExtensions = { ".zip", ".rar", ".7z" };

        private static bool IsRealArchiveName([CanBeNull] string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            string ext = Path.GetExtension(name);
            return RealArchiveExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool IsLooseGameFileName([CanBeNull] string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            string ext = Path.GetExtension(name);
            return ext.Equals(".tga", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".tpc", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".dds", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Identity key for an archive or its extracted folder. Strips only a real archive
        /// extension and a trailing <c>-extracted</c>, never a version <c>.1</c>.
        /// </summary>
        [NotNull]
        private static string LogicalArchiveStem([CanBeNull] string name)
        {
            return Normalize(ArchiveStemWithoutExtension(name));
        }

        [NotNull]
        private static string ArchiveStemWithoutExtension([CanBeNull] string name)
        {
            string fileName = Path.GetFileName(name ?? string.Empty);
            if (IsRealArchiveName(fileName))
            {
                fileName = Path.GetFileNameWithoutExtension(fileName);
            }

            if (fileName.EndsWith("-extracted", StringComparison.OrdinalIgnoreCase))
            {
                fileName = fileName.Substring(0, fileName.Length - "-extracted".Length);
            }

            return fileName;
        }

        /// <summary>
        /// Leading <c>[K1]_</c> / <c>[KotOR]</c> / <c>[KOTOR]</c> on the archive, optional
        /// following underscore or space. Folders are usually extracted without that prefix
        /// (<c>[K1]_Taris_Dueling_Arena_Adjustment_v1.4.7z</c> vs
        /// <c>Taris_Dueling_Arena_Adjustment_v1.4</c>).
        /// </summary>
        [NotNull]
        private static string StemWithoutLeadingGamePrefix([CanBeNull] string name)
        {
            string stem = ArchiveStemWithoutExtension(name);
            stem = Regex.Replace(
                stem,
                @"^\s*\[(?:K1|KOTOR|KotOR|Kotor)\][_\s\-]*",
                string.Empty,
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5));
            return Normalize(stem);
        }

        /// <summary>
        /// Drops extensionless directory clones and <c>*-extracted</c> siblings when the real
        /// archive is also in the library. A folder with no archive twin (Kebla Yurt Revamp)
        /// is kept — it is the only payload on disk.
        /// <para>
        /// Also drops a folder whose name equals an archive stem after stripping a leading
        /// <c>[K1]_</c> / <c>[KotOR]</c> prefix (run13: Swoop Bike Upgrades, Taris Dueling Arena).
        /// K1 and TSL archives stay distinct because this does not rewrite the grouping key.
        /// </para>
        /// </summary>
        [NotNull]
        private static List<FileInfo> DiscardExtractedFolderTwins([NotNull] IReadOnlyList<FileInfo> archives)
        {
            List<FileInfo> realArchives = archives.Where(a => IsRealArchiveName(a.Name)).ToList();
            var archiveStems = new HashSet<string>(
                realArchives.Select(a => LogicalArchiveStem(a.Name)),
                StringComparer.Ordinal);
            var archiveStemsStripped = new HashSet<string>(
                realArchives.Select(a => StemWithoutLeadingGamePrefix(a.Name)),
                StringComparer.Ordinal);
            var archiveVersionless = new HashSet<string>(
                realArchives.Select(a => VersionlessTitle(a.Name)).Where(t => t.Length >= 6),
                StringComparer.Ordinal);

            return archives
                .Where(a =>
                {
                    if (IsRealArchiveName(a.Name))
                    {
                        return true;
                    }

                    string stem = LogicalArchiveStem(a.Name);
                    string stripped = StemWithoutLeadingGamePrefix(a.Name);
                    string versionless = VersionlessTitle(a.Name);
                    return !archiveStems.Contains(stem)
                        && !archiveStemsStripped.Contains(stripped)
                        && !archiveVersionless.Contains(stem)
                        && !archiveVersionless.Contains(versionless);
                })
                .ToList();
        }

        /// <summary>
        /// Drops library folders that are a FINISHED install rather than a mod source.
        /// <para>
        /// TSLPatcher writes <c>installlog.txt</c> and <c>backup/</c> + <c>uninstall/</c> into the
        /// folder it was run from. Measured: "HQ Skyboxes II" in the reference library is such a
        /// folder -- 290 files including the patcher's own backups -- and the resolver preferred it
        /// over the clean 239-file "HQSkyboxesII_K1.7z". Installing from a spent folder replays one
        /// install's leftovers, backups included.
        /// </para>
        /// <para>
        /// This reads the directory, not the name, so it is evidence rather than a guess.
        /// </para>
        /// </summary>
        [NotNull]
        private static List<FileInfo> DiscardSpentInstallFolders([NotNull] IReadOnlyList<FileInfo> entries)
        {
            return entries.Where(e => !IsSpentInstallFolder(e)).ToList();
        }

        /// <summary>
        /// Drops empty directories and directories whose only real content is a nested archive
        /// (plus docs). ExactName on "Hires Beam Effects" otherwise latched onto an empty folder
        /// and emitted zero instructions; UniqueContainment on Character Textures preferred a
        /// folder that only wrapped the KotOR 2x .tpc .7z already present at the library root.
        /// </summary>
        [NotNull]
        private static List<FileInfo> DiscardHollowFolders([NotNull] IReadOnlyList<FileInfo> entries)
        {
            return entries.Where(e => !IsHollowFolder(e)).ToList();
        }

        /// <summary>
        /// Guide download notes naming a texture size/format ("Strongly recommend the 2x .tpc
        /// version") resolve to the unique real archive carrying those tokens and the component's
        /// subject words, before heading-name containment can pick a same-titled folder.
        /// </summary>
        [CanBeNull]
        private static ArchiveResolution ResolveByRecommendedTextureVariant(
            [NotNull] string componentName,
            [CanBeNull] IReadOnlyList<string> extraSignals,
            [NotNull] IReadOnlyList<FileInfo> candidates,
            GameMarker targetGame)
        {
            if (extraSignals == null || extraSignals.Count == 0)
            {
                return null;
            }

            string joined = string.Join(" ", extraSignals.Select(Normalize));
            bool wants2x = joined.IndexOf("2x", StringComparison.Ordinal) >= 0;
            bool wantsTpc = joined.IndexOf("tpc", StringComparison.Ordinal) >= 0;
            if (!wants2x && !wantsTpc)
            {
                return null;
            }

            List<string> subject = CoverageTokens(componentName);
            if (subject.Count == 0)
            {
                return null;
            }

            var matches = candidates
                .Where(a => IsRealArchiveName(a.Name))
                .Where(a => !IsWrongGame(a.Name, targetGame))
                .Where(a =>
                {
                    string name = Normalize(a.Name);
                    if (wants2x && name.IndexOf("2x", StringComparison.Ordinal) < 0)
                    {
                        return false;
                    }

                    if (wantsTpc && name.IndexOf("tpc", StringComparison.Ordinal) < 0)
                    {
                        return false;
                    }

                    // Require enough identity words that "fixes" alone cannot bind Character
                    // Textures' 2x .tpc archive to an unrelated *Fixes* heading.
                    int hits = subject.Count(t => name.IndexOf(t, StringComparison.Ordinal) >= 0);
                    return subject.Count >= 2 ? hits >= 2 : hits >= 1;
                })
                .GroupBy(a => LogicalArchiveStem(a.Name), StringComparer.Ordinal)
                .ToList();

            if (matches.Count != 1)
            {
                return null;
            }

            return new ArchiveResolution
            {
                Archive = PreferArchiveFile(matches[0]),
                Tier = ArchiveResolutionTier.UniqueContainment,
                Reason =
                    $"Guide recommends a {(wants2x ? "2x " : string.Empty)}{(wantsTpc ? ".tpc " : string.Empty)}"
                    + $"variant and exactly one archive matches that recommendation for '{componentName}'.",
            };
        }

        /// <summary>
        /// Verdict cache. Resolve() runs once per component over the whole library, so without this
        /// the same few hundred folders are stat-ed ~186 times -- minutes of IO on an external disk.
        /// </summary>
        [NotNull]
        private static readonly Dictionary<string, bool> s_spentFolderCache =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        private static readonly object s_spentFolderCacheLock = new object();

        [NotNull]
        private static readonly Dictionary<string, bool> s_hollowFolderCache =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        private static readonly object s_hollowFolderCacheLock = new object();

        private static bool IsSpentInstallFolder([CanBeNull] FileInfo entry)
        {
            if (entry is null || IsRealArchiveName(entry.Name))
            {
                return false;
            }

            string key = entry.FullName;
            lock (s_spentFolderCacheLock)
            {
                if (s_spentFolderCache.TryGetValue(key, out bool cached))
                {
                    return cached;
                }
            }

            bool verdict = InspectSpentInstallFolder(entry);
            lock (s_spentFolderCacheLock)
            {
                s_spentFolderCache[key] = verdict;
            }

            return verdict;
        }

        private static bool IsHollowFolder([CanBeNull] FileInfo entry)
        {
            if (entry is null || IsRealArchiveName(entry.Name))
            {
                return false;
            }

            string key = entry.FullName;
            lock (s_hollowFolderCacheLock)
            {
                if (s_hollowFolderCache.TryGetValue(key, out bool cached))
                {
                    return cached;
                }
            }

            bool verdict = InspectHollowFolder(entry);
            lock (s_hollowFolderCacheLock)
            {
                s_hollowFolderCache[key] = verdict;
            }

            return verdict;
        }

        private static bool InspectSpentInstallFolder([NotNull] FileInfo entry)
        {
            try
            {
                var directory = new DirectoryInfo(entry.FullName);
                if (!directory.Exists)
                {
                    return false;
                }

                if (directory.GetFiles("installlog.txt", SearchOption.TopDirectoryOnly).Length > 0)
                {
                    return true;
                }

                bool backup = directory.GetDirectories("backup", SearchOption.TopDirectoryOnly).Length > 0;
                bool uninstall = directory.GetDirectories("uninstall", SearchOption.TopDirectoryOnly).Length > 0;
                return backup && uninstall;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // An unreadable folder is not evidence of anything; leave it in play.
                Logger.LogVerbose($"[ArchiveResolver] Could not inspect '{entry.Name}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// True when the directory has no game payload files — empty, docs-only, or wrapping
        /// nested archives with no loose game content beside them.
        /// </summary>
        private static bool InspectHollowFolder([NotNull] FileInfo entry)
        {
            try
            {
                var directory = new DirectoryInfo(entry.FullName);
                if (!directory.Exists)
                {
                    return false;
                }

                FileInfo[] files;
                try
                {
                    files = directory.GetFiles("*", SearchOption.AllDirectories);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Logger.LogVerbose($"[ArchiveResolver] Could not enumerate '{entry.Name}': {ex.Message}");
                    return false;
                }

                if (files.Length == 0)
                {
                    return true;
                }

                foreach (FileInfo file in files)
                {
                    if (IsRealArchiveName(file.Name))
                    {
                        continue;
                    }

                    if (NonGameContentFilter.IsNonGameContent(file.FullName))
                    {
                        continue;
                    }

                    string ext = Path.GetExtension(file.Name);
                    if (NonGameContentFilter.ProtectedGameExtensions.Contains(ext))
                    {
                        return false;
                    }
                }

                // No game payload: empty of useful content, or only nested archive(s) + docs.
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.LogVerbose($"[ArchiveResolver] Could not inspect hollow '{entry.Name}': {ex.Message}");
                return false;
            }
        }

        [NotNull]
        private static FileInfo PreferArchiveFile([NotNull] IGrouping<string, FileInfo> group)
        {
            return group.FirstOrDefault(a => IsRealArchiveName(a.Name)) ?? group.First();
        }

        /// <summary>
        /// True when the hit is an extensionless directory, not a .zip/.rar/.7z or loose game file.
        /// </summary>
        private static bool IsFolderOnlyHit([CanBeNull] ArchiveResolution result)
        {
            return result?.Archive != null
                && !IsRealArchiveName(result.Archive.Name)
                && !IsLooseGameFileName(result.Archive.Name);
        }

        /// <summary>
        /// Returns true when <paramref name="candidate"/> is a real archive (or loose game file).
        /// Folder-only hits are stashed in <paramref name="folderFallback"/> and skipped so a
        /// later tier can still pick the archive sitting beside the leftover extract.
        /// </summary>
        private static bool TryTakeArchiveHit(
            [CanBeNull] ArchiveResolution candidate,
            [CanBeNull] ref ArchiveResolution folderFallback,
            [CanBeNull] out ArchiveResolution archiveHit)
        {
            archiveHit = null;
            if (candidate == null)
            {
                return false;
            }

            if (!IsFolderOnlyHit(candidate))
            {
                archiveHit = candidate;
                return true;
            }

            if (folderFallback == null)
            {
                folderFallback = candidate;
            }

            return false;
        }

        /// <summary>
        /// A leftover extract folder named after the guide heading is not a second product
        /// when a real archive already matched the same tier.
        /// </summary>
        [NotNull]
        private static List<IGrouping<string, FileInfo>> PreferRealArchivesOverHeadingFolders(
            [NotNull] List<IGrouping<string, FileInfo>> viable)
        {
            if (!viable.Any(g => g.Any(f => IsRealArchiveName(f.Name))))
            {
                return viable;
            }

            List<IGrouping<string, FileInfo>> archives = viable
                .Where(g => g.Any(f => IsRealArchiveName(f.Name) || IsLooseGameFileName(f.Name)))
                .ToList();
            return archives.Count > 0 ? archives : viable;
        }

        [NotNull]
        private static string TitleBeforeNexusSuffix([CanBeNull] string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return string.Empty;
            }

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            Match suffix = Regex.Match(
                baseName,
                @"^(.+)-\d+-\d+(-\d+)*-\d{9,}$",
                RegexOptions.None,
                TimeSpan.FromSeconds(5));
            return suffix.Success ? suffix.Groups[1].Value : baseName;
        }

        // ---------- game markers ----------

        // NOTE: TSLPatcher's `LookupGameNumber` in changes.ini looked like an authoritative
        // statement of which game a mod patches (1 = KOTOR, 2 = TSL), and an earlier revision of
        // this file vetoed name matches that contradicted the build's game on that basis.
        //
        // Measured against the reference library, it is not usable: of 26 archives whose filename
        // unambiguously marks them KOTOR 1 and which declare the field, 11 (42%) say `2` --
        // including "JC's Mandalorian Armor for K1", "[K1] Repair Affects Stun Droid" and
        // "KOTOR1-Thematic-Companions". Authors copy a TSL template or leave the default. Acting on
        // it rejected 20 correct archives in a single K1 build. Filename game markers (MarkerOf)
        // are the signal that actually holds; see ResolveByTargetGameMarker.

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

            string firstToken = padded.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;

            bool k2 = padded.Contains(" k2 ")
                || padded.Contains(" kotor2 ")
                || padded.Contains(" kotor 2 ")
                || padded.Contains(" tsl ")
                || padded.Contains(" tslrcm ")
                || firstToken.StartsWith("tsl", StringComparison.Ordinal) && firstToken.Length > 3
                || firstToken.Equals("k2", StringComparison.Ordinal)
                || firstToken.StartsWith("k2", StringComparison.Ordinal) && firstToken.Length > 2
                    && !char.IsDigit(firstToken[2]);

            bool k1 = padded.Contains(" k1 ")
                || padded.Contains(" kotor1 ")
                || padded.Contains(" kotor 1 ")
                || firstToken.Equals("k1", StringComparison.Ordinal)
                || firstToken.StartsWith("k1", StringComparison.Ordinal) && firstToken.Length > 2
                    && !char.IsDigit(firstToken[2]);

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

            value = value
                .Replace("&#39;", "'", StringComparison.OrdinalIgnoreCase)
                .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
                .Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase);

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
                if (Regex.IsMatch(last, @"^\d+$", RegexOptions.None, TimeSpan.FromSeconds(5)))
                {
                    return string.Empty;
                }

                // DeadlyStream prefixes its slug with the file id ("1378-jcs-fashion-line-i-cloaked-
                // jedi-robes-for-k1"). The id belongs to the site, not the mod, and leaving it on
                // makes the slug match nothing: normalized it becomes "1378jcsfashionline..." while
                // the archive is "jcsfashionlineicloakedjedirobesfork1v14". Stripped, the slug is a
                // prefix of exactly one archive - an effectively exact identifier match, which is why
                // this is safe where token-overlap scoring is not.
                Match withId = Regex.Match(last, @"^\d+-(.+)$", RegexOptions.None, TimeSpan.FromSeconds(5));
                return withId.Success ? withId.Groups[1].Value : last;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
