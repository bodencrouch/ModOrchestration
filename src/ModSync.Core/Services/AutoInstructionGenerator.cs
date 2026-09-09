// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using JetBrains.Annotations;

using ModSync.Core.FileSystemUtils;
using ModSync.Core.Services.Interpretation;
using ModSync.Core.TSLPatcher;
using ModSync.Core.Utility;

using SharpCompress.Archives;

namespace ModSync.Core.Services
{

    public static class AutoInstructionGenerator
    {
        /// <summary>
        /// Attempts to generate instructions from archive with detailed result information
        /// </summary>
        public static GenerationResult TryGenerateInstructionsFromArchiveDetailed([NotNull] ModComponent component)
        {
            if (component is null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            var result = new GenerationResult
            {
                ComponentGuid = component.Guid,
                ComponentName = component.Name,
                Success = false,
                InstructionsGenerated = 0,
                SkipReason = string.Empty,
            };

            try
            {
                if (component.Instructions.Count > 0)
                {
                    if (!DiscardUngroundedProseInstructions(component))
                    {
                        result.SkipReason = "Component already has instructions";
                        return result;
                    }
                }
                if (component.ResourceRegistry.Count == 0)
                {
                    result.SkipReason = "No mod links available";
                    return result;
                }
                if (MainConfig.SourcePath is null || !MainConfig.SourcePath.Exists)
                {
                    result.SkipReason = "Source path not configured or doesn't exist";
                    return result;
                }

                List<FileInfo> allArchives = ListLibraryEntries();

                if (allArchives.Count == 0)
                {
                    result.SkipReason = "No archives found in source directory";
                    return result;
                }

                ArchiveResolution resolution = ResolveArchiveFor(component, allArchives);

                if (!resolution.IsResolved)
                {
                    result.SkipReason = DescribeUnresolved(resolution);
                    LogUnresolved(component.Name, resolution);
                    return result;
                }

                LogResolved(component.Name, resolution);
                result.ResolvedArchivePath = resolution.Archive.FullName;
                result.ResolutionTier = resolution.Tier.ToString();
                result.ResolutionReason = resolution.Reason;

                int instructionCountBefore = component.Instructions.Count;
                bool generated = GenerateFromResolution(component, resolution);

                if (generated)
                {
                    component.IsDownloaded = true;
                    result.Success = true;
                    result.InstructionsGenerated = component.Instructions.Count - instructionCountBefore;
                    result.SkipReason = string.Empty;
                }
                else
                {
                    result.SkipReason = "Failed to generate instructions from archive";
                }

                return result;
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, $"Failed to auto-generate instructions for component '{component.Name}'");
                result.SkipReason = $"Error: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Resolves the component's archive through the tier chain in <see cref="ArchiveResolver"/>.
        /// <para>
        /// This replaced a scored best-guess (exact 100 / contains 50 / reverse-contains 25, then take
        /// the most recently modified). Reverse containment matched a component against any archive
        /// whose name was a substring of the component's, which is how "Gammorean Reskin Pack" was
        /// mapped to "Quanons_HK47_Reskin.rar". Scoring cannot express "I do not know", and a wrong
        /// archive installs the wrong mod while every step reports success, so the chain narrows and
        /// reports ambiguity instead of ranking.
        /// </para>
        /// </summary>
        [NotNull]
        private static ArchiveResolution ResolveArchiveFor(
            [NotNull] ModComponent component,
            [NotNull] IReadOnlyList<FileInfo> allArchives)
        {
            var componentUrls = component.ResourceRegistry.Keys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToList();

            var extraSignals = new List<string>();
            if (!string.IsNullOrWhiteSpace(component.DownloadInstructions))
            {
                extraSignals.Add(component.DownloadInstructions);
            }

            if (!string.IsNullOrWhiteSpace(component.Directions))
            {
                extraSignals.Add(component.Directions);
            }

            if (!string.IsNullOrWhiteSpace(component.Description))
            {
                extraSignals.Add(component.Description);
            }

            return ArchiveResolver.Resolve(
                component.Name,
                componentUrls,
                allArchives,
                DetectTargetGame(),
                extraSignals,
                component.Author);
        }

        [NotNull]
        private static ArchiveResolution ResolveArchiveFor(
            [NotNull] ModComponent component,
            [NotNull] ArchiveLibrarySnapshot library)
        {
            var componentUrls = component.ResourceRegistry.Keys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToList();
            var extraSignals = new List<string>();
            if (!string.IsNullOrWhiteSpace(component.DownloadInstructions))
            {
                extraSignals.Add(component.DownloadInstructions);
            }
            if (!string.IsNullOrWhiteSpace(component.Directions))
            {
                extraSignals.Add(component.Directions);
            }
            if (!string.IsNullOrWhiteSpace(component.Description))
            {
                extraSignals.Add(component.Description);
            }

            return ArchiveResolver.ResolvePrepared(
                component.Name,
                componentUrls,
                library,
                DetectTargetGame(),
                extraSignals,
                component.Author);
        }

        [NotNull]
        private static List<FileInfo> ListLibraryEntries()
        {
            if (MainConfig.SourcePath is null || !MainConfig.SourcePath.Exists)
            {
                return new List<FileInfo>();
            }

            IEnumerable<FileInfo> files = ArchiveHelper.DefaultArchiveSearchPatterns
                .SelectMany(ext => MainConfig.SourcePath.GetFiles(ext, SearchOption.TopDirectoryOnly))
                .Concat(MainConfig.SourcePath.GetFiles("*.tga", SearchOption.TopDirectoryOnly))
                .Concat(MainConfig.SourcePath.GetFiles("*.tpc", SearchOption.TopDirectoryOnly))
                .Where(f => f.Exists);

            IEnumerable<FileInfo> folders = MainConfig.SourcePath.GetDirectories()
                .Where(d => !d.Name.StartsWith(".", StringComparison.Ordinal))
                .Select(d => new FileInfo(d.FullName));

            return files.Concat(folders).ToList();
        }

        /// <summary>
        /// Which game this install targets, used to discard archives belonging to the other one.
        /// <para>
        /// `MainConfig.TargetGame` is only populated when a serialized instruction file carries a
        /// `game` field, so under `--direct-markdown` it is empty and the wrong-game guard silently
        /// does nothing — measured in run 9, where `[K1] Repair Affects Stun Droid.zip` and
        /// `[TSL] Repair Affects Stun Droid.zip` were reported as an unresolvable tie that the marker
        /// should have broken. The destination install itself is the reliable signal, so fall back to
        /// inspecting it.
        /// </para>
        /// </summary>
        private static ArchiveResolver.GameMarker DetectTargetGame()
        {
            if (string.Equals(MainConfig.TargetGame, "K1", StringComparison.OrdinalIgnoreCase))
            {
                return ArchiveResolver.GameMarker.Kotor1;
            }

            if (string.Equals(MainConfig.TargetGame, "TSL", StringComparison.OrdinalIgnoreCase))
            {
                return ArchiveResolver.GameMarker.Kotor2;
            }

            switch (PathUtilities.DetectGame(MainConfig.DestinationPath?.FullName))
            {
                case PathUtilities.DetectedGame.Kotor1:
                    return ArchiveResolver.GameMarker.Kotor1;
                case PathUtilities.DetectedGame.Kotor2Legacy:
                case PathUtilities.DetectedGame.Kotor2Aspyr:
                    return ArchiveResolver.GameMarker.Kotor2;
                default:
                    return ArchiveResolver.GameMarker.None;
            }
        }

        [NotNull]
        private static string DescribeUnresolved([NotNull] ArchiveResolution resolution)
        {
            return resolution.Candidates.Count > 0
                ? $"{resolution.Reason} Candidates: {string.Join(", ", resolution.Candidates)}"
                : resolution.Reason;
        }

        private static void LogUnresolved([NotNull] string componentName, [NotNull] ArchiveResolution resolution)
        {
            Logger.LogWarning(
                $"[TryGenerateInstructions] Component '{componentName}': UNRESOLVED. {DescribeUnresolved(resolution)}");
        }

        private static void LogResolved([NotNull] string componentName, [NotNull] ArchiveResolution resolution)
        {
            Logger.LogVerbose(
                $"[TryGenerateInstructions] Component '{componentName}': resolved to "
                + $"'{resolution.Archive.Name}' via tier {resolution.Tier}. {resolution.Reason}");
        }

        public static bool TryGenerateInstructionsFromArchive([NotNull] ModComponent component)
        {
            return TryGenerateInstructionsFromArchive(component, ListLibraryEntries());
        }

        /// <summary>
        /// Generates instructions using a caller-supplied immutable snapshot of the archive library.
        /// Batch callers must use this overload so a cold/removable archive store is enumerated once
        /// per build rather than once per component.
        /// </summary>
        internal static bool TryGenerateInstructionsFromArchive(
            [NotNull] ModComponent component,
            [NotNull] IReadOnlyList<FileInfo> allArchives)
        {
            if (component is null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            if (allArchives is null)
            {
                throw new ArgumentNullException(nameof(allArchives));
            }
            return TryGenerateInstructionsFromArchive(
                component,
                ArchiveResolver.CreateLibrarySnapshot(allArchives));
        }

        internal static bool TryGenerateInstructionsFromArchive(
            [NotNull] ModComponent component,
            [NotNull] ArchiveLibrarySnapshot library)
        {
            if (component is null)
            {
                throw new ArgumentNullException(nameof(component));
            }
            if (library is null)
            {
                throw new ArgumentNullException(nameof(library));
            }

            try
            {
                if (component.Instructions.Count > 0 || (component.Options?.Count ?? 0) > 0)
                {
                    _ = DiscardUngroundedProseInstructions(component);
                }
                // The dedupe step is implemented natively by ModSync and intentionally has no archive
                // payload requirement. Resolve it before the resource/library gate so a missing or
                // platform-specific .bat/.sh download cannot turn this crash-prevention barrier into a no-op.
                if (IsRemoveDuplicateTgaTpcMod(component))
                {
                    for (int index = component.Instructions.Count - 1; index >= 0; index--)
                    {
                        if (component.Instructions[index].Action != Instruction.ActionType.DelDuplicate)
                        {
                            component.Instructions.RemoveAt(index);
                        }
                    }
                    component.Options.Clear();
                    return GenerateDelDuplicateInstruction(component);
                }
                if (HasPayloadInstruction(component))
                {
                    return false;
                }
                if (component.ResourceRegistry.Count == 0 || MainConfig.SourcePath is null
                    || !MainConfig.SourcePath.Exists || library.Count == 0)
                {
                    return false;
                }

                ArchiveResolution resolution = ResolveArchiveFor(component, library);
                if (!resolution.IsResolved)
                {
                    LogUnresolved(component.Name, resolution);
                    return false;
                }

                LogResolved(component.Name, resolution);
                bool generated = GenerateFromResolution(component, resolution);
                if (generated)
                {
                    component.IsDownloaded = true;
                    OrderMergedGuideAndArchiveInstructions(component);
                }
                return generated;
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, $"Failed to auto-generate instructions for component '{component.Name}'");
                return false;
            }
        }

        /// <summary>
        /// Captures the current top-level archive/folder library once for batch generation.
        /// </summary>
        [NotNull]
        internal static ArchiveLibrarySnapshot SnapshotLibraryEntries() =>
            ArchiveResolver.CreateLibrarySnapshot(ListLibraryEntries());

        /// <summary>
        /// NLP drafts from English sometimes capture destination phrases or article fragments as
        /// Source paths: <c>&lt;&lt;modDirectory&gt;&gt;\to override</c>, <c>\installer</c>,
        /// <c>\f\*</c>. Those block archive generation (which refuses to overwrite existing
        /// instructions) and then fail validation because the files do not exist. A human reading
        /// the same sentence looks at the archive; this method drops the ungrounded draft so that
        /// path can run.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> if generation should proceed (no Extract/Move/Patcher payload
        /// remains); <see langword="false"/> if a real install payload is already present.
        /// </returns>
        internal static bool DiscardUngroundedProseInstructions([NotNull] ModComponent component)
        {
            if (component.Instructions.Count == 0 && (component.Options == null || component.Options.Count == 0))
            {
                return true;
            }

            int removed = RemoveUngroundedInstructions(component.Instructions);
            if (component.Options != null)
            {
                foreach (Option option in component.Options)
                {
                    removed += RemoveUngroundedInstructions(option.Instructions);
                }
            }

            if (removed > 0)
            {
                Logger.LogWarning(
                    $"[TryGenerateInstructions] Component '{component.Name}': discarding "
                    + $"{removed} NLP-drafted instruction(s) whose Source paths "
                    + "are prose fragments (e.g. 'to override', 'installer', 'f*', 'patcher', 'Option 5'), not files. "
                    + "Grounded paths are kept. Regenerating from the matched archive if no payload remains.");
            }

            // Grounded exclusions (deletes, renames, cleanlist) stay. Generation runs when nothing
            // executable is left to install from the archive.
            return !HasPayloadInstruction(component);
        }

        private static int RemoveUngroundedInstructions([CanBeNull] IList<Instruction> instructions)
        {
            if (instructions == null || instructions.Count == 0)
            {
                return 0;
            }

            int removed = 0;
            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                Instruction instruction = instructions[i];
                if (instruction.Action == Instruction.ActionType.DelDuplicate
                    || instruction.Action == Instruction.ActionType.CleanList
                    || instruction.Action == Instruction.ActionType.Delete
                    || instruction.Action == Instruction.ActionType.Rename
                    || instruction.Action == Instruction.ActionType.Copy)
                {
                    // Guide-grounded secondary actions: filenames often exist only after Extract
                    // (LSI_win01.tpc) or live outside the archive store (cleanlist_k1.txt).
                    // Never drop them as "missing from library".
                    //
                    // Copy-as / rename-with-unknown-source may carry only a Destination filename
                    // (Detran: "rename it PMBJ01.tga") or a wildcard Source — those are still
                    // grounded in the guide and must survive for merge with Extract/Move.
                    bool hasBareRenameTarget = !string.IsNullOrWhiteSpace(instruction.Destination)
                        && IsBareFilenameCopyAs(instruction);

                    bool sourcesAllUngrounded = instruction.Source == null
                        || instruction.Source.Count == 0
                        || instruction.Source.Any(SourceIsUngroundedProseFragment);

                    if (sourcesAllUngrounded && !hasBareRenameTarget)
                    {
                        instructions.RemoveAt(i);
                        removed++;
                    }

                    continue;
                }

                if (instruction.Source == null
                    || instruction.Source.Count == 0
                    || instruction.Source.Any(s =>
                        SourceIsUngroundedProseFragment(s) || SourceIsMissingFromLibrary(s)))
                {
                    instructions.RemoveAt(i);
                    removed++;
                }
            }

            return removed;
        }

        private static bool HasPayloadInstruction([NotNull] ModComponent component)
        {
            if (component.Instructions.Any(IsPayloadInstruction))
            {
                return true;
            }

            return component.Options != null && component.Options.Any(option =>
                option.Instructions != null && option.Instructions.Any(IsPayloadInstruction));
        }

        private static bool IsPayloadInstruction([NotNull] Instruction instruction)
        {
            // Copy-as / duplicate-rename (bare filename Destination) is a guide secondary action,
            // not an install payload — archive generation must still emit Extract/Move beside it.
            if (instruction.Action == Instruction.ActionType.Copy && IsBareFilenameCopyAs(instruction))
            {
                return false;
            }

            return instruction.Action == Instruction.ActionType.Extract
                || instruction.Action == Instruction.ActionType.Move
                || instruction.Action == Instruction.ActionType.Copy
                || instruction.Action == Instruction.ActionType.Patcher
                || instruction.Action == Instruction.ActionType.Execute
                || instruction.Action == Instruction.ActionType.Run;
        }

        private static bool IsBareFilenameCopyAs([NotNull] Instruction instruction)
        {
            if (string.IsNullOrWhiteSpace(instruction.Destination))
            {
                return false;
            }

            string dest = instruction.Destination.Trim();
            // After DraftInstructionService sanitizes, copy-as targets look like
            // <<modDirectory>>\PMBJ01.tga — still a filename rename, not a folder copy.
            string leaf = dest
                .Replace("<<modDirectory>>", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("<<kotorDirectory>>", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("<<gameDirectory>>", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim('\\', '/', ' ');
            return Path.HasExtension(leaf)
                && leaf.IndexOf('\\') < 0
                && leaf.IndexOf('/') < 0
                && leaf.IndexOf("..", StringComparison.Ordinal) < 0;
        }

        /// <summary>
        /// True when <paramref name="source"/> is an English fragment the NLP parser captured as a
        /// path, rather than a placeholder-rooted file that could exist on disk.
        /// Choose-option GUIDs are never fragments.
        /// </summary>
        internal static bool SourceIsUngroundedProseFragment([CanBeNull] string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return true;
            }

            string trimmed = source.Trim();
            if (Guid.TryParse(trimmed, out _))
            {
                return false;
            }

            bool isGameRooted = trimmed.IndexOf("<<kotorDirectory>>", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("<<gameDirectory>>", StringComparison.OrdinalIgnoreCase) >= 0;

            string rest = trimmed
                .Replace("<<modDirectory>>", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("<<kotorDirectory>>", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("<<gameDirectory>>", string.Empty, StringComparison.OrdinalIgnoreCase)
                .TrimStart('\\', '/');

            if (string.IsNullOrWhiteSpace(rest))
            {
                return true;
            }

            // `<<kotorDirectory>>\Override` is the game folder, not the NLP leftover
            // "copy to override". Destination-rooted paths stay grounded.
            if (isGameRooted)
            {
                return false;
            }

            string lower = rest.ToLowerInvariant();
            if (string.Equals(lower, "to override", StringComparison.Ordinal)
                || string.Equals(lower, "to your override", StringComparison.Ordinal)
                || string.Equals(lower, "installer", StringComparison.Ordinal)
                || string.Equals(lower, "patcher", StringComparison.Ordinal)
                || string.Equals(lower, "the", StringComparison.Ordinal)
                || string.Equals(lower, "override", StringComparison.Ordinal))
            {
                return true;
            }

            if (rest.IndexOf('"') >= 0)
            {
                return true;
            }

            // "files from your override" is prose only when no real filename survived.
            if (lower.Contains("files from", StringComparison.Ordinal)
                && !Path.HasExtension(rest.TrimEnd('*')))
            {
                return true;
            }

            // "the\*", "them\*", "override\*", "patcher\*", "installer\*" — article/pronoun leftovers
            // (K2 Terminal Texture drafted Rename them* / them*/them* from "copy them").
            if (Regex.IsMatch(
                    rest,
                    @"^(the|them|override|patcher|installer)(?:[\\/]\*)?$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1)))
            {
                return true;
            }

            if (rest.StartsWith("them*", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // "Option 5\*" resolved against the library root in run 10; it is a folder inside
            // an extracted archive, not a path that exists before Extract.
            if (Regex.IsMatch(
                    rest,
                    @"^option\s*\d+(?:[\\/].*)?$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1)))
            {
                return true;
            }

            // Single-letter glob captured from "move all f* files" / "files in f\".
            if (Regex.IsMatch(rest, @"^[A-Za-z]\*?(?:[\\/]\*)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                return true;
            }

            // Sentence fragments the delete/cleanlist NLP latched onto ("files that need to be
            // removed regardless of what mods you're using…") — real paths are short tokens or
            // have an extension / path separator.
            if (rest.IndexOf(' ') >= 0
                && rest.IndexOf('\\') < 0
                && rest.IndexOf('/') < 0
                && !Path.HasExtension(rest.TrimEnd('*')))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// True when a <c>&lt;&lt;modDirectory&gt;&gt;</c> source's first segment is not a file or
        /// folder in the archive library. NLP often captures a filename or inner folder
        /// (<c>P_CandH01.tga</c>, <c>pillar facing fix\*</c>) that only exists after Extract.
        /// Destination-rooted paths are never judged this way.
        /// </summary>
        internal static bool SourceIsMissingFromLibrary([CanBeNull] string source)
        {
            if (string.IsNullOrWhiteSpace(source) || MainConfig.SourcePath == null || !MainConfig.SourcePath.Exists)
            {
                return false;
            }

            string trimmed = source.Trim();
            if (Guid.TryParse(trimmed, out _))
            {
                return false;
            }

            if (trimmed.IndexOf("<<kotorDirectory>>", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("<<gameDirectory>>", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            if (trimmed.IndexOf("<<modDirectory>>", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            string rest = trimmed
                .Replace("<<modDirectory>>", string.Empty, StringComparison.OrdinalIgnoreCase)
                .TrimStart('\\', '/');

            if (string.IsNullOrWhiteSpace(rest))
            {
                return true;
            }

            string first = rest.Split(new[] { '\\', '/' }, 2)[0].TrimEnd('*').Trim();
            if (string.IsNullOrEmpty(first) || first.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }

            string candidate = Path.Combine(MainConfig.SourcePath.FullName, first);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return false;
            }

            try
            {
                if (MainConfig.SourcePath.GetFiles(first).Length > 0
                    || MainConfig.SourcePath.GetFiles(first + ".*").Length > 0)
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Decides whether an extracted folder may be used as the description of a mod's layout,
        /// INSTEAD of the archive beside it.
        /// <para>
        /// The archive wins whenever it can be listed, because the archive is what actually gets
        /// extracted at install time and is therefore the only thing instruction paths can be
        /// validated against. An extracted folder can contain files the archive does not - install
        /// residue, or a stray second executable. `Darth_Malaks_Lightsaber_K1` holds both
        /// "Darth Malak's Lightsaber.exe" (from the archive) and a "TSLPatcher.exe" that exists only
        /// on disk; naming the latter produced a Source path present in no archive and failed
        /// validation.
        /// </para>
        /// <para>
        /// The folder is used only when the archive cannot be listed at all, and even then only if it
        /// actually contains files. Directory existence is NOT proof of content: the library holds
        /// folders left by an earlier failed extraction with the directory tree present and zero files
        /// inside (`Ultimate Kashyyyk ...` = 0 files, 3 directories; `PMHA05 HD` likewise). Generating
        /// from one of those emits instructions that copy nothing while every step reports success.
        /// A folder holding only empty subdirectories is empty, so the check recurses.
        /// </para>
        /// </summary>
        /// <param name="archiveEntries">
        /// The archive's entry paths when the listing succeeded, so the caller can generate from them
        /// without opening the archive a second time. Null whenever the listing was not performed or
        /// could not be read - "unknown", never "the archive is empty". Most components in a mod
        /// library have a sibling extracted folder, so without this the archive would be opened and
        /// enumerated twice for nearly every component.
        /// </param>
        private static bool IsExtractedFolderTrustworthy(
            [NotNull] string folderPath,
            [NotNull] string archivePath,
            [NotNull] string componentName,
            [CanBeNull] out IReadOnlyList<string> archiveEntries)
        {
            archiveEntries = null;

            if (!Directory.Exists(folderPath))
            {
                return false;
            }

            List<string> entries = ListArchiveEntryPaths(archivePath);
            if (entries.Count > 0)
            {
                // Archive readable - it is authoritative.
                archiveEntries = entries;
                return false;
            }

            bool hasFiles;
            try
            {
                hasFiles = Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories).Any();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[AutoInstructionGenerator] Could not inspect '{folderPath}': {ex.Message}");
                return false;
            }

            if (!hasFiles)
            {
                Logger.LogWarning(
                    $"[AutoInstructionGenerator] Component '{componentName}': archive could not be listed and "
                    + $"extracted folder '{Path.GetFileName(folderPath)}' contains no files (failed earlier "
                    + "extraction); no usable source for this component.");
                return false;
            }

            Logger.LogWarning(
                $"[AutoInstructionGenerator] Component '{componentName}': archive '{Path.GetFileName(archivePath)}' "
                + "could not be listed; falling back to the populated extracted folder.");
            return true;
        }

        /// <summary>
        /// Lists an archive's file entries, returning an empty list when the archive cannot be opened
        /// or listed. Callers treat "empty" as "unknown", never as "the archive is empty".
        /// </summary>
        [NotNull]
        [ItemNotNull]
        private static List<string> ListArchiveEntryPaths([NotNull] string archivePath)
        {
            try
            {
                (IArchive archive, FileStream stream) = ArchiveHelper.OpenArchive(archivePath);
                if (archive != null && stream != null)
                {
                    using (stream)
                    using (archive)
                    {
                        List<string> managed = SafeListArchiveEntries(archive, archivePath).ToList();
                        if (managed.Count > 0)
                        {
                            return managed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogVerbose($"[AutoInstructionGenerator] Could not list '{archivePath}': {ex.Message}");
            }

            return TryListArchiveViaCli(archivePath);
        }

        /// <summary>
        /// Lists archive entries through 7z, then unrar when 7z cannot open the file
        /// (RAR5 on this machine). Empty means unknown, never "the archive is empty".
        /// </summary>
        [NotNull]
        [ItemNotNull]
        private static List<string> TryListArchiveViaCli([NotNull] string archivePath)
        {
            try
            {
                Task<List<string>> task = ArchiveHelper.TryListArchiveWithSevenZipCliAsync(archivePath);
                task.Wait();
                return task.Result?
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Replace('\\', '/'))
                    .ToList()
                    ?? new List<string>();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[AutoInstructionGenerator] CLI listing failed for '{archivePath}': {ex.Message}");
                return new List<string>();
            }
        }

        private static bool TryGenerateFromCliListing(
            [NotNull] ModComponent component,
            [NotNull] string archivePath)
        {
            List<string> cliList = TryListArchiveViaCli(archivePath);
            if (cliList.Count == 0)
            {
                return false;
            }

            Logger.LogVerbose(
                $"[AutoInstructionGenerator] Generating from {cliList.Count}-entry CLI listing of "
                + $"'{Path.GetFileName(archivePath)}'");
            ArchiveAnalysis analysis = AnalyzeArchiveFromFileList(cliList);
            return GenerateAllInstructions(component, archivePath, cliList, analysis);
        }

        /// <summary>
        /// Generates a component's instructions from its archive, falling back to the archive's
        /// already-extracted folder only when the archive itself cannot be listed.
        /// <para>
        /// Only a folder whose name equals the archive's base name is considered. Folder names in the
        /// mod library do not reliably correspond to their archives ("C_DrdWar.rar" sits beside both
        /// "C_DrdWar" and "War Droid Mk 1 HD"), and analyzing a folder that the archive does not
        /// expand into would produce paths that never materialize.
        /// </para>
        /// </summary>
        private static bool GenerateFromResolution(
            [NotNull] ModComponent component,
            [NotNull] ArchiveResolution resolution)
        {
            if (resolution.Archive is null)
            {
                return false;
            }

            bool generated = GenerateFromArchiveOrExtractedFolder(component, resolution.Archive);
            bool patcherOnly = GuideIsPatcherOnly(
                component,
                ComponentGuideProse(component),
                ComponentEmitsAction(component, Instruction.ActionType.Patcher)
                    || ComponentEmitsAction(component, Instruction.ActionType.Choose));
            foreach (FileInfo extra in resolution.AdditionalArchives)
            {
                if (patcherOnly)
                {
                    Logger.LogVerbose(
                        $"[AutoInstructionGenerator] Skipping additional archive '{extra.Name}' "
                        + $"for '{component.Name}' (guide is installer-only)");
                    continue;
                }

                generated = GenerateFromArchiveOrExtractedFolder(component, extra) || generated;
            }

            return generated;
        }

        /// <summary>
        /// Dialogue Fixes: "move your chosen dialog.tlk to the main game directory — NOT the override."
        /// The guide recommends PC Response Moderation. Manual step 001 copied that file to the
        /// game root (size 5390721) and left Override empty.
        /// </summary>
        private static bool TryBindGameRootDialogTlk(
            [NotNull] ModComponent component,
            [NotNull] IReadOnlyList<string> fileList,
            [NotNull] string extractedPath)
        {
            if (!DirectionsWantGameRootNotOverride(component))
            {
                return false;
            }

            List<string> dialogs = fileList
                .Where(path => !string.IsNullOrWhiteSpace(path)
                    && string.Equals(Path.GetFileName(path), "dialog.tlk", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (dialogs.Count == 0)
            {
                return false;
            }

            string chosen = ChooseRecommendedDialogTlk(component, dialogs);
            var move = new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string>
                {
                    $@"<<modDirectory>>\{extractedPath}\{chosen.Replace('/', '\\')}",
                },
                Destination = @"<<kotorDirectory>>",
                Overwrite = true,
            };
            move.SetParentComponent(component);
            if (!InstructionAlreadyExists(component, move))
            {
                component.Instructions.Add(move);
            }

            Logger.LogVerbose(
                $"[AutoInstructionGenerator] Bound game-root dialog.tlk '{chosen}' for '{component.Name}'");
            return true;
        }

        private static bool DirectionsWantGameRootNotOverride([NotNull] ModComponent component)
        {
            string prose = ((component.Directions ?? string.Empty) + " " + (component.DownloadInstructions ?? string.Empty))
                .ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(prose))
            {
                return false;
            }

            return prose.Contains("not the override", StringComparison.Ordinal)
                || prose.Contains("not override", StringComparison.Ordinal)
                || (prose.Contains("main game directory", StringComparison.Ordinal)
                    && prose.Contains("not", StringComparison.Ordinal)
                    && prose.Contains("override", StringComparison.Ordinal));
        }

        [NotNull]
        private static string ChooseRecommendedDialogTlk(
            [NotNull] ModComponent component,
            [NotNull] IReadOnlyList<string> dialogs)
        {
            if (dialogs.Count == 1)
            {
                return dialogs[0];
            }

            string prose = (component.Directions ?? string.Empty) + " " + (component.DownloadInstructions ?? string.Empty);
            Match recommend = Regex.Match(
                prose,
                @"\bi recommend\s+([^,.;]+)",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(2));
            if (recommend.Success)
            {
                string[] tokens = Regex.Split(
                        recommend.Groups[1].Value.ToLowerInvariant(),
                        @"[^a-z0-9]+",
                        RegexOptions.None,
                        TimeSpan.FromSeconds(1))
                    .Where(t => t.Length >= 3)
                    .ToArray();
                if (tokens.Length > 0)
                {
                    var best = dialogs
                        .Select(path => new
                        {
                            path,
                            hits = tokens.Count(token =>
                                path.Replace('\\', '/').IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0),
                        })
                        .OrderByDescending(x => x.hits)
                        .First();
                    if (best.hits > 0)
                    {
                        return best.path;
                    }
                }
            }

            string moderation = dialogs.FirstOrDefault(path =>
                path.IndexOf("moderation", StringComparison.OrdinalIgnoreCase) >= 0);
            return moderation ?? dialogs[0];
        }

        private static bool GenerateLooseFileMove([NotNull] ModComponent component, [NotNull] string fileName)
        {
            var copy = new Instruction
            {
                Action = Instruction.ActionType.Copy,
                Source = new List<string> { $@"<<modDirectory>>\{fileName}" },
                Destination = @"<<kotorDirectory>>\Override",
                Overwrite = true,
            };
            copy.SetParentComponent(component);
            if (!InstructionAlreadyExists(component, copy))
            {
                component.Instructions.Add(copy);
                Logger.LogVerbose($"[AutoInstructionGenerator] Added loose-file Copy for '{fileName}'");
            }

            return true;
        }

        private static bool GenerateFromArchiveOrExtractedFolder(
            [NotNull] ModComponent component,
            [NotNull] FileInfo matchingArchive)
        {
            if (Directory.Exists(matchingArchive.FullName) && !File.Exists(matchingArchive.FullName))
            {
                // Folder-only payload: no archive to extract. Passing the folder name here
                // used to emit Extract against a directory, which validation then rejected.
                return GenerateInstructionsFromDirectory(component, matchingArchive.FullName);
            }

            if (File.Exists(matchingArchive.FullName) && ArchiveResolver.IsLooseGameFileName(matchingArchive.Name))
            {
                return GenerateLooseFileMove(component, matchingArchive.Name);
            }

            string baseName = Path.GetFileNameWithoutExtension(matchingArchive.Name);
            string siblingFolder = Path.Combine(matchingArchive.DirectoryName ?? string.Empty, baseName);

            if (IsExtractedFolderTrustworthy(
                siblingFolder,
                matchingArchive.FullName,
                component.Name,
                out IReadOnlyList<string> archiveEntries))
            {
                Logger.LogVerbose(
                    $"[TryGenerateInstructions] Component '{component.Name}': using extracted folder "
                    + $"'{baseName}' as the source of truth for '{matchingArchive.Name}'");

                if (GenerateInstructionsFromDirectory(component, siblingFolder, matchingArchive.Name))
                {
                    return true;
                }

                Logger.LogVerbose(
                    $"[TryGenerateInstructions] Component '{component.Name}': extracted folder yielded no "
                    + "instructions, falling back to reading the archive");
            }

            return GenerateInstructions(component, matchingArchive.FullName, archiveEntries);
        }

        private static bool IsRemoveDuplicateTgaTpcMod([NotNull] ModComponent component)
        {
            if (component.Name.Equals("Remove Duplicate TGA/TPC", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(component.Author))
            {
                string authorLower = component.Author.ToLowerInvariant();
                if (authorLower.Contains("flachzangen") && authorLower.Contains("th3w1zard1"))
                {
                    return true;
                }
            }

            if (component.ResourceRegistry.Count > 0)
            {
                foreach (string link in component.ResourceRegistry.Keys)
                {
                    if (string.IsNullOrEmpty(link))
                    {
                        continue;
                    }

                    string linkLower = link.ToLowerInvariant();
                    if (linkLower.Contains("nexusmods.com/kotor/mods/1384") ||
                         linkLower.Contains("pastebin.com/6wcn122s"))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool GenerateDelDuplicateInstruction([NotNull] ModComponent component)
        {
            Instruction existing = component.Instructions.FirstOrDefault(
                i => i.Action == Instruction.ActionType.DelDuplicate);
            if (existing != null)
            {
                if (string.IsNullOrWhiteSpace(existing.Destination))
                {
                    existing.Destination = @"<<kotorDirectory>>\Override";
                }

                if (string.IsNullOrWhiteSpace(existing.Arguments))
                {
                    existing.Arguments = ".tpc";
                }

                Logger.LogVerbose("[AutoInstructionGenerator] DelDuplicate instruction already exists for Remove Duplicate TGA/TPC mod");
                return true;
            }

            var delDuplicateInstruction = new Instruction
            {
                Action = Instruction.ActionType.DelDuplicate,
                Source = new List<string>(),
                Destination = @"<<kotorDirectory>>\Override",
                Arguments = ".tpc",
                Overwrite = true,
            };
            delDuplicateInstruction.SetParentComponent(component);
            component.Instructions.Add(delDuplicateInstruction);
            Logger.LogVerbose("[AutoInstructionGenerator] Added DelDuplicate instruction for Remove Duplicate TGA/TPC mod");
            return true;
        }

        private static bool GenerateExecuteInstruction(
            [NotNull] ModComponent component,
            [NotNull] string exePath
        )
        {
            string fileName = Path.GetFileName(exePath);

            var executeInstruction = new Instruction
            {
                Action = Instruction.ActionType.Execute,
                Source = new List<string> { $@"<<modDirectory>>\{fileName}" },
                Overwrite = true,
            };
            executeInstruction.SetParentComponent(component);

            if (!InstructionAlreadyExists(component, executeInstruction))
            {
                component.Instructions.Add(executeInstruction);
                Logger.LogVerbose($"[AutoInstructionGenerator] Added Execute instruction for '{fileName}'");
                component.InstallationMethod = "Executable Installer";
                return true;
            }

            Logger.LogVerbose($"[AutoInstructionGenerator] Execute instruction for '{fileName}' already exists, skipping");
            return true;
        }

        private static bool AreInstructionsEquivalent(
            [NotNull] Instruction existing,
            [NotNull] Instruction potential
        )
        {
            if (existing.Action != potential.Action)
            {
                return false;
            }

            if (!AreSourcesEquivalent(existing.Source, potential.Source))
            {
                return false;
            }

            if (existing.ShouldSerializeDestination() && !AreDestinationsEquivalent(existing.Destination, potential.Destination))
            {
                return false;
            }

            if (existing.ShouldSerializeArguments() && !string.Equals(existing.Arguments, potential.Arguments, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (existing.ShouldSerializeOverwrite() && existing.Overwrite != potential.Overwrite)
            {
                return false;
            }

            return true;
        }

        private static bool AreSourcesEquivalent([NotNull] IReadOnlyList<string> existingSources, [NotNull] IReadOnlyList<string> potentialSources)
        {
            if (existingSources.Count == 0 && potentialSources.Count == 0)
            {
                return true;
            }

            if (existingSources.Count == 0 || potentialSources.Count == 0)
            {
                return false;
            }

            foreach (string potentialSource in potentialSources)
            {
                bool foundMatch = false;
                for (int i = 0; i < existingSources.Count; i++)
                {
                    string existingSource = existingSources[i];
                    if (DoSourcesMatch(existingSource, potentialSource))
                    {
                        foundMatch = true;
                        break;
                    }
                }
                if (!foundMatch)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool DoSourcesMatch(
            [NotNull] string existing,
            [NotNull] string potential
        )
        {
            string existingNormalized = NormalizePathForComparison(existing);
            string potentialNormalized = NormalizePathForComparison(potential);

            if (string.Equals(existingNormalized, potentialNormalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            bool existingHasWildcards = ContainsWildcards(existingNormalized);
            bool potentialHasWildcards = ContainsWildcards(potentialNormalized);

            if (existingHasWildcards && potentialHasWildcards)
            {
                if (DoWildcardPatternsOverlap(existingNormalized, potentialNormalized))
                {
                    return true;
                }
            }
            else if (existingHasWildcards)
            {
                try
                {
                    if (PathHelper.WildcardPathMatch(potentialNormalized, existingNormalized))
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogException(ex);
                }
            }
            else if (potentialHasWildcards)
            {
                try
                {
                    if (PathHelper.WildcardPathMatch(existingNormalized, potentialNormalized))
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogException(ex);
                }
            }

            string existingFilename = Path.GetFileName(existingNormalized);
            string potentialFilename = Path.GetFileName(potentialNormalized);

            bool existingFilenameHasWildcards = ContainsWildcards(existingFilename);
            bool potentialFilenameHasWildcards = ContainsWildcards(potentialFilename);

            if (existingFilenameHasWildcards && potentialFilenameHasWildcards)
            {
                return false;
            }

            if (!existingFilenameHasWildcards && !potentialFilenameHasWildcards &&
                string.Equals(existingFilename, potentialFilename, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (existingFilenameHasWildcards)
            {
                try
                {
                    if (PathHelper.WildcardPathMatch(potentialFilename, existingFilename))
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogException(ex);
                }
            }

            if (potentialFilenameHasWildcards)
            {
                try
                {
                    if (PathHelper.WildcardPathMatch(existingFilename, potentialFilename))
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogException(ex);
                }
            }

            return false;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0051:Method is too long", Justification = "<Pending>")]
        private static bool DoWildcardPatternsOverlap([NotNull] string pattern1, [NotNull] string pattern2)
        {
            string[] parts1 = pattern1.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            string[] parts2 = pattern2.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

            int minParts = Math.Min(parts1.Length, parts2.Length);

            for (int i = 0; i < minParts - 1; i++)
            {
                string part1 = parts1[i];
                string part2 = parts2[i];

                if (string.Equals(part1, part2, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ContainsWildcards(part1) && !ContainsWildcards(part2))
                {
                    try
                    {
                        if (!PathHelper.WildcardPathMatch(part2, part1))
                        {
                            return false;
                        }
                    }
                    catch
                    {
                        return false;
                    }
                }
                else if (ContainsWildcards(part2) && !ContainsWildcards(part1))
                {
                    try
                    {
                        if (!PathHelper.WildcardPathMatch(part1, part2))
                        {
                            return false;
                        }
                    }
                    catch
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }

            string filename1 = parts1[parts1.Length - 1];
            string filename2 = parts2[parts2.Length - 1];

            if (string.Equals(filename1, filename2, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(filename1, "*", StringComparison.Ordinal) || string.Equals(filename2, "*", StringComparison.Ordinal))
            {
                return true;
            }

            if (ContainsWildcards(filename1) && !ContainsWildcards(filename2))
            {
                try
                {
                    return PathHelper.WildcardPathMatch(filename2, filename1);
                }
                catch
                {
                    return false;
                }
            }

            if (ContainsWildcards(filename2) && !ContainsWildcards(filename1))
            {
                try
                {
                    return PathHelper.WildcardPathMatch(filename1, filename2);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static bool AreDestinationsEquivalent([CanBeNull] string existing, [CanBeNull] string potential)
        {
            if (string.IsNullOrEmpty(existing) && string.IsNullOrEmpty(potential))
            {
                return true;
            }

            if (string.IsNullOrEmpty(existing) || string.IsNullOrEmpty(potential))
            {
                return false;
            }

            string existingNormalized = NormalizePathForComparison(existing);
            string potentialNormalized = NormalizePathForComparison(potential);

            if (string.Equals(existingNormalized, potentialNormalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ContainsWildcards(existingNormalized))
            {
                try
                {
                    if (PathHelper.WildcardPathMatch(potentialNormalized, existingNormalized))
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogException(ex);
                }
            }

            if (ContainsWildcards(potentialNormalized))
            {
                try
                {
                    if (PathHelper.WildcardPathMatch(existingNormalized, potentialNormalized))
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogException(ex);
                }
            }

            return false;
        }

        private static string NormalizePathForComparison([NotNull] string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            string normalized = path
                .Replace('/', '\\')
                .TrimEnd('\\');

            return normalized;
        }

        private static bool ContainsWildcards([NotNull] string path)
        {
            return !string.IsNullOrEmpty(path) && (path.Contains('*') || path.Contains('?'));
        }

        private static bool InstructionAlreadyExists([NotNull] ModComponent component, [NotNull] Instruction potentialInstruction)
        {
            return component.Instructions.Any(existing => AreInstructionsEquivalent(existing, potentialInstruction));
        }

        private static Option FindEquivalentOption([NotNull] ModComponent component, [NotNull] Option potentialOption)
        {
            foreach (Option existingOption in component.Options.Where(existingOption => AreOptionsEquivalentByInstructions(existingOption, potentialOption)))
            {
                if (AreOptionsEquivalentByInstructions(existingOption, potentialOption))
                {
                    return existingOption;
                }
            }

            return null;
        }

        private static int ConsolidateDuplicateOptions([NotNull] ModComponent component)
        {
            int removedCount = 0;
            var processedOptions = new HashSet<Guid>();

            var allOptions = component.Options.ToList();

            for (int i = 0; i < allOptions.Count; i++)
            {
                Option primaryOption = allOptions[i];

                if (processedOptions.Contains(primaryOption.Guid))
                {
                    continue;
                }

                var equivalentOptions = new List<Option>();

                for (int j = i + 1; j < allOptions.Count; j++)
                {
                    Option candidateOption = allOptions[j];

                    if (processedOptions.Contains(candidateOption.Guid))
                    {
                        continue;
                    }

                    int overlapScore = CalculateOptionInstructionOverlap(primaryOption, candidateOption);

                    if (overlapScore > 0)
                    {
                        equivalentOptions.Add(candidateOption);
                    }
                }

                if (equivalentOptions.Count > 0)
                {
                    Logger.LogVerbose($"[AutoInstructionGenerator] Found {equivalentOptions.Count} duplicate option(s) equivalent to '{primaryOption.Name}'");

                    foreach (Option duplicate in equivalentOptions)
                    {
                        int addedCount = AddMissingInstructionsToOption(primaryOption, duplicate);
                        if (addedCount > 0)
                        {
                            Logger.LogVerbose($"[AutoInstructionGenerator] Merged {addedCount} instruction(s) from duplicate option '{duplicate.Name}' into '{primaryOption.Name}'");
                        }

                        ReplaceOptionGuidInChooseInstructions(component, duplicate.Guid, primaryOption.Guid);

                        processedOptions.Add(duplicate.Guid);

                        component.Options.Remove(duplicate);
                        removedCount++;

                        Logger.LogVerbose($"[AutoInstructionGenerator] Removed duplicate option '{duplicate.Name}' (GUID: {duplicate.Guid})");
                    }

                    Logger.LogVerbose($"[AutoInstructionGenerator] Consolidated {equivalentOptions.Count} duplicate(s) into option '{primaryOption.Name}' (GUID: {primaryOption.Guid})");
                }

                processedOptions.Add(primaryOption.Guid);
            }

            return removedCount;
        }

        private static void ReplaceOptionGuidInChooseInstructions(
            [NotNull] ModComponent component,
            Guid oldGuid,
            Guid newGuid
        )
        {
            string oldGuidStr = oldGuid.ToString();
            string newGuidStr = newGuid.ToString();
            int replacementCount = 0;

            foreach (Instruction instruction in component.Instructions)
            {
                if (instruction.Action != Instruction.ActionType.Choose)
                {
                    continue;
                }

                bool found = false;
                int indexToReplace = -1;

                for (int i = 0; i < instruction.Source.Count; i++)
                {
                    if (string.Equals(instruction.Source[i], oldGuidStr, StringComparison.OrdinalIgnoreCase))
                    {
                        indexToReplace = i;
                        found = true;
                        break;
                    }
                }

                if (found)
                {
                    bool newGuidExists = instruction.Source.Any(guid =>
                        string.Equals(guid, newGuidStr, StringComparison.OrdinalIgnoreCase));

                    // Because instruction.Source is IReadOnlyList<string>, we must replace the whole list to update/remove elements.
                    // Convert to a list, modify, then assign back.

                    var updatedSource = instruction.Source.ToList();

                    if (newGuidExists)
                    {
                        updatedSource.RemoveAt(indexToReplace);
                        Logger.LogVerbose($"[AutoInstructionGenerator] Removed duplicate GUID {oldGuid} from Choose instruction (kept {newGuid})");
                    }
                    else
                    {
                        updatedSource[indexToReplace] = newGuidStr;
                        replacementCount++;
                        Logger.LogVerbose($"[AutoInstructionGenerator] Replaced GUID {oldGuid} with {newGuid} in Choose instruction");
                    }

                    instruction.Source = updatedSource;
                }
            }

            if (replacementCount > 0)
            {
                Logger.LogVerbose($"[AutoInstructionGenerator] Updated {replacementCount} Choose instruction(s) to reference consolidated option");
            }
        }

        private static int CalculateOptionInstructionOverlap([NotNull] Option existing, [NotNull] Option potential)
        {
            int matchCount = 0;

            foreach (Instruction potentialInstr in potential.Instructions)
            {
                foreach (Instruction existingInstr in existing.Instructions.Where(existingInstr => AreInstructionsEquivalent(existingInstr, potentialInstr)))
                {
                    if (AreInstructionsEquivalent(existingInstr, potentialInstr))
                    {
                        matchCount++;
                        break;
                    }
                }
            }

            return matchCount;
        }

        private static bool AreOptionsEquivalentByInstructions([NotNull] Option existing, [NotNull] Option potential)
        {
            if (existing.Instructions.Count != potential.Instructions.Count)
            {
                return false;
            }

            foreach (Instruction potentialInstr in potential.Instructions)
            {
                bool foundMatch = false;
                foreach (Instruction existingInstr in existing.Instructions.Where(existingInstr => AreInstructionsEquivalent(existingInstr, potentialInstr)))
                {
                    if (AreInstructionsEquivalent(existingInstr, potentialInstr))
                    {
                        foundMatch = true;
                        break;
                    }
                }
                if (!foundMatch)
                {
                    return false;
                }
            }

            foreach (Instruction existingInstr in existing.Instructions)
            {
                bool foundMatch = false;
                foreach (Instruction potentialInstr in potential.Instructions)
                {
                    if (AreInstructionsEquivalent(existingInstr, potentialInstr))
                    {
                        foundMatch = true;
                        break;
                    }
                }
                if (!foundMatch)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsFolderAlreadyCoveredByInstructions(
            [NotNull] ModComponent component,
            [NotNull] string folderSourcePath)
        {
            foreach (Instruction existingInstruction in component.Instructions)
            {
                // Only check Move and Copy instructions - other instruction types (Extract, Patcher, etc.)
                // don't move files to the game directory, so they shouldn't prevent us from adding Move instructions
                if (existingInstruction.Action != Instruction.ActionType.Move &&
                     existingInstruction.Action != Instruction.ActionType.Copy)
                {
                    continue;
                }

                foreach (string existingSource in existingInstruction.Source)
                {
                    if (DoSourcesMatch(existingSource, folderSourcePath))
                    {
                        Logger.LogVerbose($"[AutoInstructionGenerator] Folder path '{folderSourcePath}' is covered by existing {existingInstruction.Action} instruction source '{existingSource}'");
                        return true;
                    }

                    if (IsParentPathCovering(existingSource, folderSourcePath))
                    {
                        Logger.LogVerbose($"[AutoInstructionGenerator] Folder path '{folderSourcePath}' is covered by parent path '{existingSource}' ({existingInstruction.Action} instruction)");
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsParentPathCovering([NotNull] string parentPath, [NotNull] string childPath)
        {
            string parentNormalized = NormalizePathForComparison(parentPath);
            string childNormalized = NormalizePathForComparison(childPath);

            string parentWithoutWildcard = parentNormalized.TrimEnd('*', '\\');
            string childWithoutWildcard = childNormalized.TrimEnd('*', '\\');

            // Check if child path starts with parent path
            if (childWithoutWildcard.StartsWith(parentWithoutWildcard, StringComparison.OrdinalIgnoreCase) &&
                (parentNormalized.EndsWith("\\*", StringComparison.Ordinal) || parentNormalized.EndsWith("\\*\\*", StringComparison.Ordinal)))
            {
                // Additional validation: ensure the child is actually within the parent directory
                // by checking that the next character after the parent path is a path separator
                if (childWithoutWildcard.Length > parentWithoutWildcard.Length)
                {
                    char nextChar = childWithoutWildcard[parentWithoutWildcard.Length];
                    if (nextChar == '\\' || nextChar == '/')
                    {
                        return true;
                    }
                }
                // If the child path is exactly the same as the parent path (without wildcard), it's covered
                else if (childWithoutWildcard.Length == parentWithoutWildcard.Length)
                {
                    return true;
                }
            }

            return false;
        }

        private static int AddMissingInstructionsToOption([NotNull] Option existingOption, [NotNull] Option potentialOption)
        {
            int addedCount = 0;

            foreach (Instruction potentialInstr in potentialOption.Instructions)
            {
                bool alreadyExists = existingOption.Instructions.Any(existingInstr =>
                    AreInstructionsEquivalent(existingInstr, potentialInstr));

                if (!alreadyExists)
                {
                    var newInstruction = new Instruction
                    {
                        Action = potentialInstr.Action,
                        Source = new List<string>(potentialInstr.Source),
                        Destination = potentialInstr.Destination,
                        Arguments = potentialInstr.Arguments,
                        Overwrite = potentialInstr.Overwrite,
                        Dependencies = new List<Guid>(potentialInstr.Dependencies),
                        Restrictions = new List<Guid>(potentialInstr.Restrictions),
                    };
                    newInstruction.SetParentComponent(existingOption);
                    existingOption.Instructions.Add(newInstruction);
                    addedCount++;
                }
            }

            return addedCount;
        }

        private static Instruction FindCompatibleChooseInstruction([NotNull] ModComponent component)
        {
            return component.Instructions.FirstOrDefault(instr => instr.Action == Instruction.ActionType.Choose);
        }

        private static bool AddOptionToChooseInstruction([NotNull] Instruction chooseInstruction, [NotNull] string optionGuid)
        {
            if (chooseInstruction.Action != Instruction.ActionType.Choose)
            {
                Logger.LogWarning("[AutoInstructionGenerator] Attempted to add option GUID to non-Choose instruction");
                return false;
            }

            if (chooseInstruction.Source.Any(guid => string.Equals(guid, optionGuid, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var updatedSource = chooseInstruction.Source.ToList();
            updatedSource.Add(optionGuid);
            chooseInstruction.Source = updatedSource;
            return true;
        }

        public static bool GenerateInstructions([NotNull] ModComponent component, [NotNull] string archivePath)
        {
            return GenerateInstructions(component, archivePath, precomputedFileList: null);
        }

        /// <summary>
        /// Generates a component's instructions from an archive.
        /// </summary>
        /// <param name="precomputedFileList">
        /// The archive's entry paths when a caller has already listed them, letting the archive be
        /// opened and enumerated once per component instead of twice. Pass null when the listing is
        /// unknown, which sends this through the normal open-and-analyze path (including the 7zip CLI
        /// fallback and the .exe handling an unreadable archive needs). An empty list means the same
        /// as null - "unknown" - and never "the archive has no entries".
        /// </param>
        private static bool GenerateInstructions(
            [NotNull] ModComponent component,
            [NotNull] string archivePath,
            [CanBeNull] IReadOnlyList<string> precomputedFileList)
        {
            if (component is null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            if (string.IsNullOrWhiteSpace(archivePath))
            {
                throw new ArgumentException("Archive path cannot be null or empty", nameof(archivePath));
            }

            if (!File.Exists(archivePath))
            {
                return false;
            }

            if (IsRemoveDuplicateTgaTpcMod(component))
            {
                Logger.LogVerbose("[AutoInstructionGenerator] Detected Remove Duplicate TGA/TPC mod, generating DelDuplicate instruction only");
                return GenerateDelDuplicateInstruction(component);
            }

            if (precomputedFileList != null && precomputedFileList.Count > 0)
            {
                Logger.LogVerbose(
                    $"[AutoInstructionGenerator] Reusing the {precomputedFileList.Count}-entry listing already read "
                    + $"from '{Path.GetFileName(archivePath)}'");
                ArchiveAnalysis precomputedAnalysis = AnalyzeArchiveFromFileList(precomputedFileList);
                return GenerateAllInstructions(component, archivePath, precomputedFileList, precomputedAnalysis);
            }

            string fileExtension = Path.GetExtension(archivePath).ToLowerInvariant();
            bool isExeFile = string.Equals(fileExtension, ".exe", StringComparison.Ordinal);

            try
            {
                (IArchive archive, FileStream stream) = ArchiveHelper.OpenArchive(archivePath);
                if (archive is null || stream is null)
                {
                    if (isExeFile)
                    {
                        Logger.LogVerbose($"[AutoInstructionGenerator] EXE file '{Path.GetFileName(archivePath)}' is not an extractable archive, creating Execute instruction");
                        return GenerateExecuteInstruction(component, archivePath);
                    }

                    return TryGenerateFromCliListing(component, archivePath);
                }

                using (stream)
                using (archive)
                {
                    ArchiveAnalysis analysis = AnalyzeArchive(archive, archivePath, out IReadOnlyList<string> fileList);
                    return GenerateAllInstructions(component, archivePath, fileList, analysis);
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, $"Failed to generate instructions for {archivePath}");

                if (IsCorruptedArchiveException(ex))
                {
                    if (isExeFile)
                    {
                        Logger.LogVerbose($"[AutoInstructionGenerator] EXE file '{Path.GetFileName(archivePath)}' is not a valid archive, creating Execute instruction instead");
                        return GenerateExecuteInstruction(component, archivePath);
                    }

                    // NEVER delete the user's archive here. "Unreadable by our archive reader" is not the
                    // same as "corrupted": an unsupported format or compression method (RAR5 in
                    // particular), a permissions problem, or a transient IO error all land in this branch.
                    // The source directory is the user's own mod library -- often the only copy, and
                    // frequently not re-downloadable -- so this path must be strictly read-only. Report it
                    // and let the caller fall through to its placeholder handling.
                    Logger.LogWarning($"[AutoInstructionGenerator] Could not read archive (unsupported format or damaged): {archivePath}");
                    Logger.LogWarning("[AutoInstructionGenerator] Trying 7z/unrar CLI listing before giving up.");
                    if (TryGenerateFromCliListing(component, archivePath))
                    {
                        return true;
                    }
                }
                else if (isExeFile)
                {
                    Logger.LogVerbose($"[AutoInstructionGenerator] Failed to extract EXE file '{Path.GetFileName(archivePath)}', creating Execute instruction instead");
                    return GenerateExecuteInstruction(component, archivePath);
                }
                else if (TryGenerateFromCliListing(component, archivePath))
                {
                    return true;
                }

                return false;
            }
        }

        private static bool IsCorruptedArchiveException(Exception ex)
        {
            string exceptionType = ex.GetType().Name;
            string message = ex.Message.ToLowerInvariant();

            if (exceptionType.Contains("ArchiveException"))
            {
                return true;
            }

            if (string.Equals(exceptionType, "InvalidOperationException", StringComparison.Ordinal) &&
                (message.Contains("nextheaderoffset") ||
                 message.Contains("header offset") ||
                 message.Contains("invalid")))
            {
                return true;
            }

            if (message.Contains("failed to locate") ||
                 message.Contains("zip header") ||
                 message.Contains("corrupt") ||
                 message.Contains("invalid archive") ||
                 message.Contains("unexpected end") ||
                 message.Contains("damaged") ||
                 message.Contains("cannot read") ||
                 message.Contains("invalid header") ||
                 message.Contains("bad archive") ||
                 message.Contains("crc mismatch") ||
                 message.Contains("data error"))
            {
                return true;
            }

            return false;
        }
        private static ArchiveAnalysis AnalyzeArchiveFromFileList([NotNull] IReadOnlyList<string> fileList)
        {
            var analysis = new ArchiveAnalysis();

            foreach (string path in fileList)
            {
                string normalizedPath = path.Replace('\\', '/');
                if (IsInstallerRuntimeArtifact(normalizedPath))
                {
                    continue;
                }

                string[] pathParts = normalizedPath.Split('/');

                if (normalizedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    analysis.ExecutableCandidates.Add(normalizedPath);
                }

                if (pathParts.Any(p => p.Equals("tslpatchdata", StringComparison.OrdinalIgnoreCase)))
                {
                    analysis.HasTslPatchData = true;

                    string fileName = Path.GetFileName(normalizedPath);
                    if (fileName.Equals("namespaces.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        // An archive can contain more than one independent tslpatchdata tree (e.g. a
                        // "Main Install" mod plus an unrelated "Patch - <OtherMod>" compat-patch
                        // subtree, each with its own namespaces.ini). Keep the FIRST one found, not
                        // the last: ReadNamespacesIni()/IniHelper.TraverseDirectories() also stops at
                        // the first namespaces.ini it encounters (same entry-order traversal) to build
                        // the namespace option list in AddNamespacesChooseInstructions, so this field
                        // must agree with that same directory or the generated Patcher instruction
                        // points at a tslpatchdata tree whose namespaces.ini never defined the selected
                        // option. Gate on HasNamespacesIni (not string.IsNullOrEmpty(TslPatcherPath)):
                        // a changes.ini found earlier in a *different* tree must not block the first
                        // namespaces.ini from claiming this path - namespaces.ini always outranks a
                        // changes.ini-derived path, exactly once, on first sight.
                        if (!analysis.HasNamespacesIni)
                        {
                            analysis.TslPatcherPath = GetTslPatcherPath(normalizedPath);
                        }

                        analysis.HasNamespacesIni = true;
                    }
                    else if (fileName.Equals("changes.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        analysis.HasChangesIni = true;
                        if (string.IsNullOrEmpty(analysis.TslPatcherPath))
                        {
                            analysis.TslPatcherPath = GetTslPatcherPath(normalizedPath);
                        }
                    }
                }
                else
                {
                    string extension = Path.GetExtension(normalizedPath).ToLowerInvariant();
                    if (!IsGameFile(extension))
                    {
                        continue;
                    }

                    analysis.HasSimpleOverrideFiles = true;

                    if (pathParts.Length == 1)
                    {
                        analysis.HasFlatFiles = true;
                    }
                    else if (pathParts.Length >= 2)
                    {
                        string topLevelFolder = pathParts[0];
                        if (!IsNonGamePayloadFolder(topLevelFolder)
                            && !analysis.FoldersWithFiles.Contains(topLevelFolder, StringComparer.Ordinal))
                        {
                            analysis.FoldersWithFiles.Add(topLevelFolder);
                        }
                    }
                }
            }

            ResolvePatcherExecutable(analysis);

            return analysis;
        }

        /// <summary>
        /// Materializes the archive's entry paths, falling back to the 7zip CLI when the managed
        /// reader cannot enumerate them (RAR5 in particular). Returns an empty list rather than
        /// throwing: an empty list suppresses Move instructions, which is the safe direction.
        /// </summary>
        [NotNull]
        [ItemNotNull]
        private static IReadOnlyList<string> SafeListArchiveEntries(
            [NotNull] IArchive archive,
            [NotNull] string archivePath)
        {
            try
            {
                return archive.Entries
                    .Where(e => !e.IsDirectory && e.Key != null)
                    .Select(e => e.Key.Replace('\\', '/'))
                    .ToList();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[AutoInstructionGenerator] Could not enumerate entries of '{archivePath}': {ex.Message}");

                try
                {
                    Task<List<string>> task = ArchiveHelper.TryListArchiveWithSevenZipCliAsync(archivePath);
                    task.Wait();
                    return task.Result?.Select(p => p.Replace('\\', '/')).ToList()
                        ?? (IReadOnlyList<string>)new List<string>();
                }
                catch (Exception fallbackEx)
                {
                    Logger.LogWarning($"[AutoInstructionGenerator] 7zip CLI listing also failed: {fallbackEx.Message}");
                    return new List<string>();
                }
            }
        }

        /// <summary>
        /// Files that TSLPatcher WRITES when a mod is installed, rather than files the mod ships.
        /// An extracted folder that has been installed from before contains a "backup" tree holding
        /// the game files it replaced and an "uninstall" tree, both full of real game files. Treating
        /// those as mod content generates Move instructions that would copy a previous installation's
        /// displaced originals into Override - the exact opposite of what the mod intends. Archives
        /// never contain them, so this only ever filters install residue.
        /// </summary>
        private static bool IsNonGamePayloadFolder([CanBeNull] string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return false;
            }

            return folder.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)
                || folder.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith("._", StringComparison.Ordinal);
        }

        private static bool GuideTreatsFolderAsOptional(
            [NotNull] string folder,
            [CanBeNull] string prose)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(prose))
            {
                return false;
            }

            string template = Policy.TryGetPattern("optional_folder_near_token")
                ?? @"\b(?:if\s+you(?:'d|\s+would)\s+like|optionally)\b[\s\S]{0,120}\b{token}\b";
            string pattern = template.Replace("{token}", Regex.Escape(folder), StringComparison.Ordinal);
            return Regex.IsMatch(
                prose,
                pattern,
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Guide says skip / do not install this archive folder (or "skip it" after
        /// naming the folder). "Bugfix folder" matches archive folder "Bug Fixes".
        /// </summary>
        private static bool GuideTreatsFolderAsSkipped(
            [NotNull] string folder,
            [CanBeNull] string prose)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(prose))
            {
                return false;
            }

            string skipVerb = Policy.TryGetPattern("skip_folder_verb")
                ?? @"(?:you\s+can\s+also\s+skip|skip(?:\s+it)?|do\s+not\s+(?:install|move|use)|don't\s+(?:install|move|use)|not\s+including)";

            foreach (Match skip in Regex.Matches(
                prose,
                skipVerb,
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5)))
            {
                int start = Math.Max(0, skip.Index - 160);
                int end = Math.Min(prose.Length, skip.Index + skip.Length + 80);
                string window = prose.Substring(start, end - start);
                foreach (Match word in Regex.Matches(
                    window,
                    @"[A-Za-z][A-Za-z0-9'%-]{3,}",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(5)))
                {
                    if (FolderNameMatchesGuideToken(folder, word.Value))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool FolderNameMatchesGuideToken(
            [NotNull] string folder,
            [CanBeNull] string token)
        {
            string folderKey = AlnumKey(folder);
            string tokenKey = AlnumKey(token);
            if (folderKey.Length < 5 || tokenKey.Length < 5)
            {
                return false;
            }

            if (folderKey.Equals(tokenKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string shorter = folderKey.Length <= tokenKey.Length ? folderKey : tokenKey;
            string longer = folderKey.Length <= tokenKey.Length ? tokenKey : folderKey;
            if (longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase)
                && longer.Length - shorter.Length <= 2)
            {
                return true;
            }

            // "Transparent" names archive folder "Transparent Skins".
            if (folderKey.StartsWith(tokenKey, StringComparison.OrdinalIgnoreCase)
                && tokenKey.Length >= 8)
            {
                string rest = folderKey.Substring(tokenKey.Length);
                return s_guideFolderNameSuffixes.Any(suffix =>
                    suffix.Equals(rest, StringComparison.OrdinalIgnoreCase));
            }

            return false;
        }

        private static readonly string[] s_guideFolderNameSuffixes =
        {
            "skins", "textures", "folder", "files", "appearance", "heads", "pack", "mod",
        };

        [NotNull]
        private static string AlnumKey([CanBeNull] string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var chars = value.Where(char.IsLetterOrDigit).ToArray();
            return new string(chars).ToLowerInvariant();
        }

        /// <summary>
        /// Filenames listed after EXCEPT / excluding in guide prose.
        /// </summary>
        [NotNull]
        [ItemNotNull]
        private static List<string> GuideExceptedFilenames([CanBeNull] string prose)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(prose))
            {
                return names;
            }

            foreach (Match clause in Regex.Matches(
                prose,
                Policy.TryGetPattern("except_or_excluding_clause")
                    ?? @"\b(?:except|excluding|but\s+not|not\s+including)\b[\s\S]{0,500}",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5)))
            {
                foreach (Match file in Regex.Matches(
                    clause.Value,
                    Policy.TryGetPattern("guide_filename_token")
                        ?? @"\b([\w.-]+\.[A-Za-z0-9]{2,4})\b",
                    RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(5)))
                {
                    string name = Path.GetFileName(file.Groups[1].Value);
                    if (string.IsNullOrWhiteSpace(name) || !IsGameFile(Path.GetExtension(name)))
                    {
                        continue;
                    }

                    if (!names.Exists(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        names.Add(name);
                    }
                }
            }

            return names;
        }

        /// <summary>
        /// Filenames the guide says to remove from the payload before the Override copy.
        /// "Delete X before moving to override" is not an Override cleanup.
        /// </summary>
        [NotNull]
        [ItemNotNull]
        private static List<string> GuideDeletedBeforeMoveFilenames([CanBeNull] string prose)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(prose))
            {
                return names;
            }

            foreach (Match clause in Regex.Matches(
                prose,
                Policy.TryGetPattern("delete_before_move_clause")
                    ?? @"\bdelete\b[\s\S]{0,500}?\b(?:before\s+)?mov(?:e|ing)\b",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5)))
            {
                foreach (Match file in Regex.Matches(
                    clause.Value,
                    Policy.TryGetPattern("guide_filename_token")
                        ?? @"\b([\w.-]+\.[A-Za-z0-9]{2,4})\b",
                    RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(5)))
                {
                    string name = Path.GetFileName(file.Groups[1].Value);
                    if (string.IsNullOrWhiteSpace(name) || !IsGameFile(Path.GetExtension(name)))
                    {
                        continue;
                    }

                    if (!names.Exists(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        names.Add(name);
                    }
                }
            }

            return names;
        }

        /// <summary>
        /// Filenames the guide says to ignore / skip as payload ("you can ignore X unless").
        /// </summary>
        [NotNull]
        [ItemNotNull]
        private static List<string> GuideIgnoredFilenames([CanBeNull] string prose)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(prose))
            {
                return names;
            }

            foreach (Match file in Regex.Matches(
                prose,
                Policy.TryGetPattern("ignore_named_file_clause")
                    ?? @"(?:you\s+can\s+)?ignore\s+([\w.-]+\.[A-Za-z0-9]{2,4})",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5)))
            {
                string name = Path.GetFileName(file.Groups[1].Value);
                if (string.IsNullOrWhiteSpace(name) || !IsGameFile(Path.GetExtension(name)))
                {
                    continue;
                }

                if (!names.Exists(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    names.Add(name);
                }
            }

            return names;
        }

        /// <summary>
        /// Folders the guide names as the install payload ("ONLY look at X", "enter the X folder").
        /// Empty means the archive walk is unrestricted.
        /// </summary>
        [NotNull]
        [ItemNotNull]
        private static List<string> GuideNamedInstallFolders([CanBeNull] string prose)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(prose))
            {
                return names;
            }

            void AddPieces(string raw)
            {
                foreach (string piece in Regex.Split(
                    raw,
                    Policy.TryGetPattern("folder_name_split")
                        ?? @"\s*(?:/|\\|,|\band\b|\bor\b)\s*",
                    RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(2)))
                {
                    string cleaned = Regex.Replace(
                            piece.Trim().Trim('"', '\'', '`'),
                            Policy.TryGetPattern("strip_trailing_folders") ?? @"\s+folders?$",
                            string.Empty,
                            RegexOptions.IgnoreCase,
                            TimeSpan.FromSeconds(1))
                        .Trim();
                    if (cleaned.Length < 3
                        || names.Exists(n => n.Equals(cleaned, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    names.Add(cleaned);
                }
            }

            foreach (Match match in Regex.Matches(
                prose,
                Policy.TryGetPattern("only_look_at_folders")
                    ?? @"only\s+look\s+at\s+the\s+([^.;]+?)(?:\s+folders?|\s+folder)\b",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5)))
            {
                AddPieces(match.Groups[1].Value);
            }

            foreach (Match match in Regex.Matches(
                prose,
                Policy.TryGetPattern("enter_the_folder")
                    ?? @"enter\s+the\s+([^.;]+?)\s+folder\b",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5)))
            {
                AddPieces(match.Groups[1].Value);
            }

            foreach (Match match in Regex.Matches(
                prose,
                Policy.TryGetPattern("only_move_from_folder")
                    ?? @"only\s+move\s+(?:the\s+)?files\s+from\s+[""']?([^""'.]+)[""']?",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5)))
            {
                AddPieces(match.Groups[1].Value);
            }

            if (GuidePicksOneOfNamedFolders(prose))
            {
                foreach (Match match in Regex.Matches(
                    prose,
                    Policy.TryGetPattern("from_the_named_folders")
                        ?? @"from\s+the\s+([^.;]+?)\s+folders\b",
                    RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(5)))
                {
                    AddPieces(match.Groups[1].Value);
                }
            }

            return names;
        }

        private static bool GuidePicksOneOfNamedFolders([CanBeNull] string prose)
        {
            if (string.IsNullOrWhiteSpace(prose))
            {
                return false;
            }

            return Regex.IsMatch(
                prose,
                Policy.TryGetPattern("picks_one_of_folders")
                    ?? @"\b(?:from\s+one\s+of|your\s+preferred)\b",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(2));
        }

        private static bool GuideForbidsRootLooseFiles([CanBeNull] string prose)
        {
            if (string.IsNullOrWhiteSpace(prose))
            {
                return false;
            }

            return Regex.IsMatch(
                prose,
                Policy.TryGetPattern("forbids_root_loose_files")
                    ?? @"do\s+not\s+move\s+(?:any\s+of\s+the\s+)?files\s+in\s+the\s+main",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(2));
        }

        private static bool GuideRequestsLooseFileInstall([CanBeNull] string prose)
        {
            if (string.IsNullOrWhiteSpace(prose))
            {
                return false;
            }

            return Regex.IsMatch(
                prose,
                Policy.TryGetPattern("requests_loose_file_install")
                    ?? @"\b(?:enter\s+the|only\s+look\s+at|mov(?:e|ing)\b|copy\b[\s\S]{0,80}override|loose\s+files)",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(2));
        }

        private static bool GuideIsPatcherOnly(
            [NotNull] ModComponent component,
            [CanBeNull] string prose,
            bool hasTslPatchData)
        {
            if (string.IsNullOrWhiteSpace(prose) || GuideRequestsLooseFileInstall(prose))
            {
                return false;
            }

            string method = component.InstallationMethod ?? string.Empty;
            bool markedPatcher = method.IndexOf("holopatcher", StringComparison.OrdinalIgnoreCase) >= 0
                || method.IndexOf("tslpatcher", StringComparison.OrdinalIgnoreCase) >= 0;
            return hasTslPatchData || markedPatcher;
        }

        private static bool FolderMatchesGuideNames(
            [NotNull] string folder,
            [NotNull] IReadOnlyList<string> names)
        {
            if (names.Count == 0)
            {
                return true;
            }

            string leaf = folder.Replace('\\', '/').Split('/').LastOrDefault() ?? folder;
            return names.Any(name =>
                leaf.Equals(name, StringComparison.OrdinalIgnoreCase)
                || folder.Equals(name, StringComparison.OrdinalIgnoreCase)
                || FolderNameMatchesGuideToken(leaf, name));
        }

        /// <summary>
        /// Guide-named folders may sit under the archive's own wrapper
        /// (<c>ModName/NPC Replacement/…</c>). Top-level <see cref="ArchiveAnalysis.FoldersWithFiles"/>
        /// only sees the wrapper, so the allowlist must walk every path segment.
        /// </summary>
        [NotNull]
        [ItemNotNull]
        private static List<string> ResolveAllowlistedFolderPaths(
            [NotNull] IReadOnlyList<string> fileList,
            [NotNull] IReadOnlyList<string> allowlist)
        {
            var resolved = new List<string>();
            if (allowlist.Count == 0)
            {
                return resolved;
            }

            foreach (string entry in fileList)
            {
                string[] parts = entry.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (!FolderMatchesGuideNames(parts[i], allowlist))
                    {
                        continue;
                    }

                    string path = string.Join("/", parts.Take(i + 1));
                    if (!resolved.Exists(existing =>
                            existing.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    {
                        resolved.Add(path);
                    }
                }
            }

            return resolved;
        }

        private static bool ComponentEmitsAction(
            [NotNull] ModComponent component,
            Instruction.ActionType action)
        {
            if (component.Instructions.Any(instruction => instruction.Action == action))
            {
                return true;
            }

            return component.Options != null
                && component.Options.Any(option =>
                    option.Instructions.Any(instruction => instruction.Action == action));
        }

        private static bool IsPatcherInputFolder([CanBeNull] string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return false;
            }

            return folder.Equals("source", StringComparison.OrdinalIgnoreCase)
                || AlnumKey(folder).Equals("sourcescripts", StringComparison.OrdinalIgnoreCase);
        }

        [NotNull]
        private static string ComponentGuideProse([NotNull] ModComponent component)
        {
            return StripGuideMarkup(
                (component.Directions ?? string.Empty) + " "
                + (component.DownloadInstructions ?? string.Empty));
        }

        /// <summary>
        /// full.md wraps emphasis around guide verbs (`**before** moving`). Patterns
        /// match the spoken sentence, not the markdown.
        /// </summary>
        [NotNull]
        private static string StripGuideMarkup([CanBeNull] string prose)
        {
            if (string.IsNullOrEmpty(prose))
            {
                return string.Empty;
            }

            return prose.Replace("**", string.Empty, StringComparison.Ordinal)
                .Replace("__", string.Empty, StringComparison.Ordinal);
        }

        [NotNull]
        [ItemNotNull]
        private static List<string> MergeFilenameLists(
            [NotNull] IEnumerable<string> first,
            [NotNull] IEnumerable<string> second)
        {
            var names = new List<string>();
            foreach (string name in first.Concat(second))
            {
                if (string.IsNullOrWhiteSpace(name)
                    || names.Exists(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                names.Add(name);
            }

            return names;
        }

        private static bool IsInstallerRuntimeArtifact([NotNull] string relativePath)
        {
            string[] parts = relativePath.Split('/');

            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Equals("backup", StringComparison.OrdinalIgnoreCase)
                    || parts[i].Equals("uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            string fileName = parts[parts.Length - 1];
            return fileName.Equals("installlog.txt", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("installlog.rtf", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Generates instructions from an ALREADY-EXTRACTED mod folder sitting in the mod directory.
        /// <para>
        /// This is the most reliable source available: the folder is what the archive actually expands
        /// to, so filenames - above all the installer's real name - are read rather than inferred, and
        /// no archive reader is involved (several RAR5 files in the library cannot be opened at all).
        /// It is also the only way to see mods that exist solely as a folder with no archive beside
        /// them, which the archive-file enumeration cannot observe.
        /// </para>
        /// <para>
        /// Paths are made relative to the folder, so the layout matches a flat archive whose extraction
        /// folder is the folder's own name - the shape the rest of the generator already handles.
        /// </para>
        /// </summary>
        public static bool GenerateInstructionsFromDirectory(
            [NotNull] ModComponent component,
            [NotNull] string directoryPath,
            [CanBeNull] string extractArchiveFileName = null)
        {
            if (component is null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                throw new ArgumentException("Directory path cannot be null or empty", nameof(directoryPath));
            }

            if (!Directory.Exists(directoryPath))
            {
                return false;
            }

            if (IsRemoveDuplicateTgaTpcMod(component))
            {
                return GenerateDelDuplicateInstruction(component);
            }

            string folderName = new DirectoryInfo(directoryPath).Name;

            List<string> fileList;
            try
            {
                int prefixLength = directoryPath.TrimEnd(Path.DirectorySeparatorChar).Length + 1;
                fileList = Directory
                    .EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
                    .Select(f => f.Substring(prefixLength).Replace('\\', '/'))
                    .Where(f => !IsInstallerRuntimeArtifact(f))
                    .ToList();
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, $"Failed to enumerate extracted folder '{directoryPath}'");
                return false;
            }

            if (fileList.Count == 0)
            {
                return false;
            }

            ArchiveAnalysis analysis = AnalyzeArchiveFromFileList(fileList);

            Logger.LogVerbose(
                $"[AutoInstructionGenerator] Component '{component.Name}': analyzed extracted folder "
                + $"'{folderName}' ({fileList.Count} files, installer="
                + $"{(string.IsNullOrEmpty(analysis.PatcherExecutable) ? "<none>" : analysis.PatcherExecutable)})");

            return GenerateAllInstructions(
                component,
                directoryPath,
                fileList,
                analysis,
                extractedPathOverride: folderName,
                extractArchiveFileName: extractArchiveFileName);
        }

        private static bool GenerateAllInstructions(
            ModComponent component,
            string archivePath,
            IReadOnlyList<string> fileList,
            ArchiveAnalysis analysis,
            string extractedPathOverride = null,
            string extractArchiveFileName = null
        )
        {
            // In directory mode the layout is read from the extracted folder, but the archive beside it
            // (when there is one) must still be extracted so the install reproduces on a machine where
            // the folder does not yet exist. Folder-only mods have no archive and so emit no Extract.
            fileList = fileList
                .Where(entry => !IsInstallerRuntimeArtifact(entry.Replace('\\', '/')))
                .ToList();

            bool directoryMode = extractedPathOverride != null;
            string archiveFileName = directoryMode
                ? extractArchiveFileName
                : Path.GetFileName(archivePath);

            bool hasArchiveToExtract = !string.IsNullOrEmpty(archiveFileName);
            // Only strip a trailing real archive extension. Replace(GetExtension) would
            // also delete an inner ".zip" in Nexus names like NO_Fighters.zip-90-v1-0.zip.
            string extractedPath = extractedPathOverride
                ?? (archiveFileName is null
                    ? string.Empty
                    : Path.GetFileNameWithoutExtension(archiveFileName));

            if (hasArchiveToExtract && (analysis.HasTslPatchData || analysis.HasSimpleOverrideFiles))
            {
                var extractInstruction = new Instruction
                {
                    Action = Instruction.ActionType.Extract,
                    Source = new List<string> { $@"<<modDirectory>>\{archiveFileName}" },
                    Overwrite = true,
                };
                extractInstruction.SetParentComponent(component);

                if (!InstructionAlreadyExists(component, extractInstruction))
                {
                    component.Instructions.Add(extractInstruction);
                    Logger.LogVerbose($"[AutoInstructionGenerator] Added Extract instruction for '{archiveFileName}'");
                }
                else
                {
                    Logger.LogVerbose($"[AutoInstructionGenerator] Extract instruction for '{archiveFileName}' already exists, skipping");
                }
            }
            else if (!analysis.HasTslPatchData && !analysis.HasSimpleOverrideFiles)
            {
                return false;
            }

            if (analysis.HasTslPatchData)
            {
                if (analysis.HasNamespacesIni)
                {
                    AddNamespacesChooseInstructions(component, archivePath, analysis, extractedPath);
                }
                else if (analysis.HasChangesIni)
                {
                    AddSimplePatcherInstruction(component, analysis, extractedPath);
                }
            }

            if (analysis.HasSimpleOverrideFiles)
            {
                string folderProse = ComponentGuideProse(component);
                List<string> exceptedFiles = MergeFilenameLists(
                    MergeFilenameLists(
                        GuideExceptedFilenames(folderProse),
                        GuideDeletedBeforeMoveFilenames(folderProse)),
                    GuideIgnoredFilenames(folderProse));
                List<string> allowlist = GuideNamedInstallFolders(folderProse);
                if (GuidePicksOneOfNamedFolders(folderProse) && allowlist.Count >= 2)
                {
                    string firstExisting = allowlist.FirstOrDefault(name =>
                        ResolveAllowlistedFolderPaths(fileList, new[] { name }).Count > 0
                        || analysis.FoldersWithFiles.Any(folder =>
                            FolderMatchesGuideNames(folder, new[] { name })));
                    if (!string.IsNullOrEmpty(firstExisting))
                    {
                        allowlist = new List<string> { firstExisting };
                    }
                }

                List<string> excludedFolders = CollectGuideExcludedFolders(fileList, folderProse);
                if (analysis.HasTslPatchData)
                {
                    excludedFolders.Add("source");
                    excludedFolders.Add("Source Scripts");
                }

                if (TryBindGameRootDialogTlk(component, fileList, extractedPath))
                {
                    // Guide: chosen dialog.tlk goes in the game root, not Override.
                    // Do not also sweep the variant folders into Override.
                }
                else if (analysis.HasTslPatchData)
                {
                    if (GuideIsPatcherOnly(component, folderProse, analysis.HasTslPatchData))
                    {
                        Logger.LogVerbose(
                            $"[AutoInstructionGenerator] Skipping loose Moves for '{component.Name}' "
                            + "(guide is installer-only; leftover source/ files stay with the patcher)");
                    }
                    else
                    {
                        // A conventional archive root often contains both tslpatchdata and OPTIONAL/
                        // below the same outer folder. Filtering by top-level folder classified the
                        // entire outer folder as patcher data, then labelled the component Hybrid while
                        // emitting no Move. Walk the actual game-file parents and exclude only the exact
                        // tslpatchdata subtree.
                        AddSimpleMoveInstruction(
                            component,
                            fileList,
                            extractedPath,
                            folderName: null,
                            excludedSubtree: string.IsNullOrEmpty(analysis.TslPatcherPath)
                                ? "tslpatchdata"
                                : analysis.TslPatcherPath.TrimEnd('/', '\\') + "/tslpatchdata",
                            excludedFileNames: exceptedFiles,
                            excludedFolderNames: excludedFolders);
                    }
                }
                else
                {
                    // Root loose files and subfolders are independent payloads. HD Pazaak
                    // Cards ships lbl_*.tga at the zip root plus optional green/ and
                    // packaging debris in __MACOSX/. Walking only FoldersWithFiles dropped
                    // the real cards and installed AppleDouble stubs.
                    bool skipRoot = allowlist.Count > 0 || GuideForbidsRootLooseFiles(folderProse);
                    if (analysis.HasFlatFiles && !skipRoot)
                    {
                        AddSimpleMoveInstruction(
                            component,
                            fileList,
                            extractedPath,
                            folderName: null,
                            excludedFileNames: exceptedFiles,
                            excludedFolderNames: excludedFolders);
                    }
                    else if (analysis.HasFlatFiles && skipRoot)
                    {
                        Logger.LogVerbose(
                            $"[AutoInstructionGenerator] Skipping archive-root files "
                            + "(guide names specific folders / forbids the main folder)");
                    }

                    IReadOnlyList<string> foldersToWalk = allowlist.Count > 0
                        ? ResolveAllowlistedFolderPaths(fileList, allowlist)
                        : analysis.FoldersWithFiles;
                    if (allowlist.Count > 0 && foldersToWalk.Count == 0)
                    {
                        Logger.LogVerbose(
                            "[AutoInstructionGenerator] Guide named install folders "
                            + $"({string.Join(", ", allowlist)}) but none appear in the archive");
                    }

                    foreach (string folder in foldersToWalk)
                    {
                        if (IsNonGamePayloadFolder(folder))
                        {
                            Logger.LogVerbose(
                                $"[AutoInstructionGenerator] Skipping packaging folder '{folder}'");
                            continue;
                        }

                        if (IsPatcherInputFolder(Path.GetFileName(folder.Replace('/', Path.DirectorySeparatorChar))))
                        {
                            Logger.LogVerbose(
                                $"[AutoInstructionGenerator] Skipping patcher-input folder '{folder}'");
                            continue;
                        }

                        if (GuideTreatsFolderAsOptional(
                                Path.GetFileName(folder.Replace('/', Path.DirectorySeparatorChar)),
                                folderProse))
                        {
                            Logger.LogVerbose(
                                $"[AutoInstructionGenerator] Skipping optional folder '{folder}' "
                                + "(guide says if you'd like / optionally)");
                            continue;
                        }

                        if (GuideTreatsFolderAsSkipped(
                                Path.GetFileName(folder.Replace('/', Path.DirectorySeparatorChar)),
                                folderProse))
                        {
                            Logger.LogVerbose(
                                $"[AutoInstructionGenerator] Skipping folder '{folder}' "
                                + "(guide says skip / do not install)");
                            continue;
                        }

                        AddSimpleMoveInstruction(
                            component,
                            fileList,
                            extractedPath,
                            folder,
                            excludedFileNames: exceptedFiles,
                            excludedFolderNames: excludedFolders);
                    }
                }
            }

            // Method is a guide contract (preflight keys off "patcher" / "loose").
            // Leftover source/ files next to tslpatchdata do not make a patcher-only
            // guide into Hybrid — that forced Moves the guide never asked for.
            bool emittedPatcher = ComponentEmitsAction(component, Instruction.ActionType.Patcher)
                || ComponentEmitsAction(component, Instruction.ActionType.Choose);
            bool emittedLoose = ComponentEmitsAction(component, Instruction.ActionType.Move)
                || ComponentEmitsAction(component, Instruction.ActionType.Copy);
            string existingMethod = component.InstallationMethod ?? string.Empty;
            if (emittedPatcher && emittedLoose)
            {
                component.InstallationMethod = "Hybrid (TSLPatcher + Loose Files)";
            }
            else if (emittedPatcher)
            {
                if (existingMethod.IndexOf("patcher", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    component.InstallationMethod = "TSLPatcher";
                }
            }
            else if (emittedLoose)
            {
                if (existingMethod.IndexOf("loose", StringComparison.OrdinalIgnoreCase) < 0
                    && existingMethod.IndexOf("patcher", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    component.InstallationMethod = "Loose-File Mod";
                }
            }

            int consolidatedCount = ConsolidateDuplicateOptions(component);
            if (consolidatedCount > 0)
            {
                Logger.LogVerbose($"[AutoInstructionGenerator] Consolidated and removed {consolidatedCount} duplicate option(s)");
            }

            BindBareCopyAsInstructions(component, extractedPath, fileList);
            BindGuideSecondaryPaths(component, extractedPath, fileList);
            BindCleanListToPayloadFolder(component);

            return component.Instructions.Count > 0;
        }

        public static async Task<bool> GenerateInstructionsFromUrlsAsync(
            [NotNull] ModComponent component,
            [NotNull] DownloadCacheService downloadCache,
            CancellationToken cancellationToken = default)
        {
            if (component is null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            if (downloadCache is null)
            {
                throw new ArgumentNullException(nameof(downloadCache));
            }

            if (component.ResourceRegistry.Count == 0)
            {
                await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Component '{component.Name}' has no URLs to process").ConfigureAwait(false);
                return false;
            }

            if (IsRemoveDuplicateTgaTpcMod(component))
            {
                await Logger.LogVerboseAsync("[AutoInstructionGenerator] Detected Remove Duplicate TGA/TPC mod, generating DelDuplicate instruction only").ConfigureAwait(false);
                return GenerateDelDuplicateInstruction(component);
            }

            try
            {
                await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Pre-resolving URLs for component: {component.Name}").ConfigureAwait(false);

                IReadOnlyDictionary<string, List<string>> resolvedUrls = await downloadCache.PreResolveUrlsAsync(component, downloadManager: null, sequential: true, cancellationToken).ConfigureAwait(false);

                if (resolvedUrls.Count == 0)
                {
                    await Logger.LogVerboseAsync($"[AutoInstructionGenerator] No URLs resolved for component: {component.Name}").ConfigureAwait(false);
                    return false;
                }

                await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Resolved {resolvedUrls.Count} URLs").ConfigureAwait(false);

                if (MainConfig.SourcePath is null || !MainConfig.SourcePath.Exists)
                {
                    await Logger.LogVerboseAsync("[AutoInstructionGenerator] No source directory configured, creating placeholder instructions").ConfigureAwait(false);

                    foreach (KeyValuePair<string, List<string>> kvp in resolvedUrls)
                    {
                        List<string> filenames = kvp.Value;
                        if (filenames.Count == 0)
                        {
                            continue;
                        }

                        // Process ALL files from this URL, not just the first one
                        foreach (string fileName in filenames)
                        {
                            Instruction potentialInstruction = CreatePlaceholderInstructionObject(component, fileName);

                            if (potentialInstruction is null)
                            {
                                continue;
                            }

                            if (!InstructionAlreadyExists(component, potentialInstruction))
                            {
                                component.Instructions.Add(potentialInstruction);
                                await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Added placeholder instruction for '{fileName}'").ConfigureAwait(false);
                            }
                            else
                            {
                                await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Placeholder instruction for '{fileName}' already exists, skipping").ConfigureAwait(false);
                            }
                        }
                    }

                    return component.Instructions.Count > 0;
                }

                // Track files that are missing
                var missingFiles = new List<string>();

                foreach (KeyValuePair<string, List<string>> kvp in resolvedUrls)
                {
                    List<string> filenames = kvp.Value;
                    if (filenames.Count == 0)
                    {
                        continue;
                    }

                    // Process ALL files from this URL, not just the first one
                    foreach (string fileName in filenames)
                    {
                        // Skip empty or null filenames
                        if (string.IsNullOrWhiteSpace(fileName))
                        {
                            await Logger.LogWarningAsync($"[AutoInstructionGenerator] Skipping empty filename from URL: {kvp.Key}").ConfigureAwait(false);
                            continue;
                        }

                        string filePath = Path.Combine(MainConfig.SourcePath.FullName, fileName);

                        if (File.Exists(filePath))
                        {
                            await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Found '{fileName}' on disk, performing comprehensive analysis").ConfigureAwait(false);

                            bool isArchive = ArchiveHelper.IsArchive(fileName);
                            if (isArchive)
                            {
                                bool generated = GenerateInstructions(component, filePath);
                                if (!generated)
                                {
                                    bool fileStillExists = File.Exists(filePath);

                                    if (!fileStillExists)
                                    {
                                        await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Corrupted file '{fileName}' has been deleted, creating placeholder instruction").ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Comprehensive analysis failed for '{fileName}', creating placeholder Extract instruction").ConfigureAwait(false);
                                    }

                                    Instruction potentialInstruction = CreatePlaceholderInstructionObject(component, fileName);
                                    if (potentialInstruction != null)
                                    {
                                        if (!InstructionAlreadyExists(component, potentialInstruction))
                                        {
                                            component.Instructions.Add(potentialInstruction);
                                            await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Added placeholder Extract instruction for '{fileName}'").ConfigureAwait(false);
                                        }
                                        else
                                        {
                                            await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Placeholder instruction for '{fileName}' already exists, skipping").ConfigureAwait(false);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                await Logger.LogVerboseAsync($"[AutoInstructionGenerator] '{fileName}' is not an archive, checking if it's a game file").ConfigureAwait(false);

                                Instruction potentialInstruction = CreatePlaceholderInstructionObject(component, fileName);
                                if (potentialInstruction != null)
                                {
                                    if (!InstructionAlreadyExists(component, potentialInstruction))
                                    {
                                        component.Instructions.Add(potentialInstruction);
                                        await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Added Move instruction for '{fileName}'").ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Move instruction for '{fileName}' already exists, skipping").ConfigureAwait(false);
                                    }
                                }
                            }
                        }
                        else
                        {
                            await Logger.LogVerboseAsync($"[AutoInstructionGenerator] '{fileName}' not found on disk, creating placeholder instruction").ConfigureAwait(false);
                            missingFiles.Add(fileName);

                            Instruction potentialInstruction = CreatePlaceholderInstructionObject(component, fileName);
                            if (potentialInstruction != null)
                            {
                                if (!InstructionAlreadyExists(component, potentialInstruction))
                                {
                                    component.Instructions.Add(potentialInstruction);
                                    await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Added placeholder instruction for '{fileName}'").ConfigureAwait(false);
                                }
                                else
                                {
                                    await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Placeholder instruction for '{fileName}' already exists, skipping").ConfigureAwait(false);
                                }
                            }
                        }
                    }
                }

                // Warn if files are missing (CLI context - user should have used --download flag)
                if (missingFiles.Count > 0)
                {
                    await Logger.LogWarningAsync($"[AutoInstructionGenerator] Component '{component.Name}' has {missingFiles.Count} file(s) not found on disk:").ConfigureAwait(false);
                    foreach (string fileName in missingFiles.Take(5))
                    {
                        await Logger.LogWarningAsync($"  � {fileName}").ConfigureAwait(false);
                    }
                    if (missingFiles.Count > 5)
                    {
                        await Logger.LogWarningAsync($"  ... and {missingFiles.Count - 5} more").ConfigureAwait(false);
                    }
                    await Logger.LogWarningAsync("[AutoInstructionGenerator] To download files automatically, use the --download flag").ConfigureAwait(false);
                    await Logger.LogWarningAsync("[AutoInstructionGenerator] Example: dotnet run --project ModSync.Core -- convert --input file.toml --auto --download --source-path ./mods").ConfigureAwait(false);
                }

                int consolidatedCount = ConsolidateDuplicateOptions(component);
                if (consolidatedCount > 0)
                {
                    await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Consolidated and removed {consolidatedCount} duplicate option(s)").ConfigureAwait(false);
                }

                await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Generated {component.Instructions.Count} instructions for component: {component.Name}").ConfigureAwait(false);
                return component.Instructions.Count > 0;
            }
            catch (Exception ex)
            {
                await Logger.LogExceptionAsync(ex, $"Failed to generate instructions from URLs for component: {component.Name}").ConfigureAwait(false);
                return false;
            }
        }

        /// <summary>
        /// Result of analyzing component files for auto-generation
        /// </summary>
        public class FileAnalysisResult
        {
            public List<string> ExistingArchives { get; set; } = new List<string>();
            public List<string> ExistingNonArchiveFiles { get; set; } = new List<string>();
            public List<string> MissingUrls { get; set; } = new List<string>();
            public List<string> InvalidLinks { get; set; } = new List<string>();
            public IReadOnlyDictionary<string, List<string>> ResolvedUrls { get; set; } = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Analyzes a component's mod links to determine what files exist and what needs downloading.
        /// This is cache-first and doesn't download anything.
        /// </summary>
        public static async Task<FileAnalysisResult> AnalyzeComponentFilesAsync(
            [NotNull] ModComponent component,
            [NotNull] DownloadCacheService downloadCache,
            [NotNull] string modDirectory,
            CancellationToken cancellationToken = default)
        {
            if (component is null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            if (downloadCache is null)
            {
                throw new ArgumentNullException(nameof(downloadCache));
            }

            if (string.IsNullOrEmpty(modDirectory))
            {
                throw new ArgumentException("Mod directory cannot be null or empty", nameof(modDirectory));
            }

            var result = new FileAnalysisResult();

            await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Analyzing files for component: {component.Name}").ConfigureAwait(false);

            // Pre-resolve URLs to filenames (uses cache, doesn't download)
            result.ResolvedUrls = await downloadCache.PreResolveUrlsAsync(
                component,
                downloadCache.DownloadManager,
                sequential: false,
                cancellationToken).ConfigureAwait(false);

            await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Resolved {result.ResolvedUrls.Count} URL(s)").ConfigureAwait(false);

            // Check which files exist on disk
            var existingFiles = new List<string>();

            foreach (string modLink in component.ResourceRegistry.Keys)
            {
                if (string.IsNullOrWhiteSpace(modLink))
                {
                    continue;
                }

                if (IsValidUrl(modLink))
                {
                    // Always check resolved filenames first to get all files for the URL
                    if (result.ResolvedUrls.TryGetValue(modLink, out List<string> filenames) && filenames.Count > 0)
                    {
                        bool anyFileExists = false;
                        foreach (string filename in filenames)
                        {
                            string filePath = Path.Combine(modDirectory, filename);
                            if (File.Exists(filePath))
                            {
                                existingFiles.Add(filePath);
                                anyFileExists = true;
                                await Logger.LogVerboseAsync($"[AutoInstructionGenerator] File exists: {filename}").ConfigureAwait(false);
                            }
                            else
                            {
                                await Logger.LogVerboseAsync($"[AutoInstructionGenerator] File missing: {filename}").ConfigureAwait(false);
                            }
                        }

                        if (!anyFileExists)
                        {
                            result.MissingUrls.Add(modLink);
                            await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Files missing for URL: {modLink}").ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        // Fallback to cached filename if no resolved filenames
                        string cachedFilename = DownloadCacheService.GetFileName(modLink);
                        if (!string.IsNullOrEmpty(cachedFilename))
                        {
                            string filePath = Path.Combine(modDirectory, cachedFilename);
                            if (File.Exists(filePath))
                            {
                                existingFiles.Add(filePath);
                                await Logger.LogVerboseAsync($"[AutoInstructionGenerator] File exists on disk (cached): {cachedFilename}").ConfigureAwait(false);
                            }
                            else
                            {
                                result.MissingUrls.Add(modLink);
                                await Logger.LogVerboseAsync($"[AutoInstructionGenerator] File missing (cached): {cachedFilename}").ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            result.MissingUrls.Add(modLink);
                            await Logger.LogVerboseAsync($"[AutoInstructionGenerator] URL not resolved: {modLink}").ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    // This is a local file path, not a URL
                    string fullPath = Path.IsPathRooted(modLink) ? modLink : Path.Combine(modDirectory, modLink);

                    if (File.Exists(fullPath))
                    {
                        existingFiles.Add(fullPath);
                    }
                    else
                    {
                        result.InvalidLinks.Add(modLink);
                    }
                }
            }

            // Categorize existing files
            foreach (string filePath in existingFiles)
            {
                if (ArchiveHelper.IsArchive(filePath))
                {
                    result.ExistingArchives.Add(filePath);
                }
                else
                {
                    result.ExistingNonArchiveFiles.Add(filePath);
                }
            }

            await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Analysis complete: {result.ExistingArchives.Count} archives, {result.ExistingNonArchiveFiles.Count} non-archives, {result.MissingUrls.Count} missing URLs").ConfigureAwait(false);

            return result;
        }

        /// <summary>
        /// Generates instructions from analyzed files (archives and non-archive files).
        /// Call after AnalyzeComponentFilesAsync() to generate from existing files.
        /// </summary>
        public static async Task<int> GenerateInstructionsFromAnalyzedFilesAsync(
            [NotNull] ModComponent component,
            [NotNull] FileAnalysisResult analysis,
            [NotNull] string modDirectory)
        {
            if (component is null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            if (analysis is null)
            {
                throw new ArgumentNullException(nameof(analysis));
            }

            if (string.IsNullOrEmpty(modDirectory))
            {
                throw new ArgumentException("Mod directory cannot be null or empty", nameof(modDirectory));
            }

            int totalInstructionsGenerated = 0;

            // Generate instructions from archives
            foreach (string archivePath in analysis.ExistingArchives)
            {
                await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Generating instructions for archive: {archivePath}").ConfigureAwait(false);
                bool success = GenerateInstructions(component, archivePath);
                if (success)
                {
                    int newInstructions = component.Instructions.Count - totalInstructionsGenerated;
                    totalInstructionsGenerated = component.Instructions.Count;
                    await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Generated {newInstructions} instruction(s) for: {archivePath}").ConfigureAwait(false);
                }
            }

            // Generate instructions for non-archive files
            foreach (string filePath in analysis.ExistingNonArchiveFiles)
            {
                string fileName = Path.GetFileName(filePath);

                // Generated, so debris in the mod workspace must not be swept into Override.
                if (NonGameContentFilter.IsNonGameContent(fileName))
                {
                    await Logger.LogVerboseAsync(
                        $"[AutoInstructionGenerator] Skipping non-game file '{fileName}'").ConfigureAwait(false);
                    continue;
                }

                string relativePath = GetRelativePathToModDirectory(modDirectory, filePath);

                var moveInstruction = new Instruction
                {
                    Action = Instruction.ActionType.Move,
                    Source = new List<string> { $@"<<modDirectory>>\{relativePath}" },
                    Destination = @"<<gameDirectory>>\Override",
                    Overwrite = true,
                };
                moveInstruction.SetParentComponent(component);

                if (!InstructionAlreadyExists(component, moveInstruction))
                {
                    component.Instructions.Add(moveInstruction);
                    totalInstructionsGenerated++;
                    await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Added Move instruction for: {fileName}").ConfigureAwait(false);
                }
            }

            await Logger.LogVerboseAsync($"[AutoInstructionGenerator] Total instructions generated: {totalInstructionsGenerated}").ConfigureAwait(false);
            return totalInstructionsGenerated;
        }

        private static string GetRelativePathToModDirectory(string modDirectory, string targetPath)
        {
            if (string.IsNullOrEmpty(modDirectory) || string.IsNullOrEmpty(targetPath))
            {
                return Path.GetFileName(targetPath);
            }

            string modDirFull = Path.GetFullPath(modDirectory);
            string targetFull = Path.GetFullPath(targetPath);

            if (!targetFull.StartsWith(modDirFull, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileName(targetPath);
            }

            string relativePath = targetFull.Substring(modDirFull.Length);
            if (relativePath.StartsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                relativePath = relativePath.Substring(1);
            }

            return relativePath;
        }

        private static bool IsValidUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                return false;
            }

            return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
        }

        [CanBeNull]
        private static Instruction CreatePlaceholderInstructionObject(
            [NotNull] ModComponent component,
            [NotNull] string fileName
        )
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                Logger.LogWarning($"Cannot create placeholder instruction for empty filename in component '{component.Name}'");
                return null;
            }

            bool isArchive = ArchiveHelper.IsArchive(fileName);

            if (!isArchive)
            {
                string extension = Path.GetExtension(fileName);
                if (!IsGameFile(extension))
                {
                    Logger.LogVerbose($"[AutoInstructionGenerator] Skipping non-game file '{fileName}' (extension: {extension})");
                    return null;
                }
            }

            var instruction = new Instruction
            {
                // Loose files live on the shared archive store. Move would consume
                // them (CineMalak's N_DarthMalak01.tga vanished after v39). Copy
                // still installs to Override; the store copy stays for the next run.
                Action = isArchive ? Instruction.ActionType.Extract : Instruction.ActionType.Copy,
                Source = new List<string> { $@"<<modDirectory>>\{fileName}" },
                Destination = isArchive ? string.Empty : @"<<gameDirectory>>\Override",
                Overwrite = true,
            };
            instruction.SetParentComponent(component);

            return instruction;
        }

        private static readonly char[] s_pathSeparators = new[] { '/', '\\' };

        private static bool IsTslPatcherFolder(string folderName, ArchiveAnalysis analysis)
        {
            if (string.IsNullOrEmpty(folderName))
            {
                return false;
            }

            if (folderName.Equals("tslpatchdata", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.IsNullOrEmpty(analysis.TslPatcherPath))
            {
                return false;
            }

            string[] pathParts = analysis.TslPatcherPath.Split(s_pathSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (pathParts.Length > 0 && pathParts[0].Equals(folderName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static void AddNamespacesChooseInstructions(
            ModComponent component,
            string archivePath,
            ArchiveAnalysis analysis,
            string extractedPath
        )
        {
            Dictionary<string, Dictionary<string, string>> namespaces =
                ReadNamespacesIni(archivePath);

            if (namespaces is null ||
                 !namespaces.TryGetValue("Namespaces", out Dictionary<string, string> value))
            {
                return;
            }

            var optionGuidsToAdd = new List<string>();

            // Convert to list to preserve order and allow indexing
            var namespaceValues = value.Values.ToList();

            for (int index = 0; index < namespaceValues.Count; index++)
            {
                string ns = namespaceValues[index];
                if (!namespaces.TryGetValue(ns, out Dictionary<string, string> namespaceData))
                {
                    continue;
                }

                var potentialOption = new Option
                {
                    Guid = Guid.NewGuid(),
                    Name = namespaceData.TryGetValue("Name", out string value2) ? value2 : ns,
                    Description = namespaceData.TryGetValue("Description", out string value3) ? value3 : string.Empty,
                    IsSelected = false,
                };

                string patcherPath = CombinePatcherPath(extractedPath, analysis.TslPatcherPath);

                // A namespaced TSLPatcher mod ships ONE executable at the archive root and selects the
                // namespace by index at runtime (Arguments below) - the namespace is a tslpatchdata
                // subfolder, not a sibling of the executable and not an executable name. Deriving the
                // path from the namespace produced sources that exist in no archive, e.g.
                // "Sith Soldier Texture Restoration-v2.4\Main\Main.exe" for an archive whose only
                // executable is "Install.exe" at the root, failing the whole install at validation.
                // Resolve the executable exactly as the single-namespace path does; uniqueness across
                // namespaces comes from Arguments and the owning Option, not from a fabricated path.
                if (string.IsNullOrEmpty(analysis.PatcherExecutable))
                {
                    Logger.LogError(
                        $"[AutoInstructionGenerator] Component '{component.Name}': no installer executable found "
                        + $"beside tslpatchdata for namespace '{potentialOption.Name}'. Not generating a Patcher "
                        + "instruction - guessing the name would create a Source path that exists in no archive.");
                    continue;
                }

                string executableName = Path.GetFileName(analysis.PatcherExecutable);

                var patcherInstruction = new Instruction
                {

                    Action = Instruction.ActionType.Patcher,
                    Source = new List<string> { $@"<<modDirectory>>\{patcherPath}\{executableName}" },
                    Destination = "<<gameDirectory>>",
                    // Arguments should be the 0-based index of the namespace option in namespaces.ini
                    Arguments = index.ToString(),
                    Overwrite = true,
                };
                patcherInstruction.SetParentComponent(potentialOption);
                potentialOption.Instructions.Add(patcherInstruction);

                Option existingOption = FindEquivalentOption(component, potentialOption);

                if (existingOption != null)
                {
                    int addedCount = AddMissingInstructionsToOption(existingOption, potentialOption);
                    if (addedCount > 0)
                    {
                        Logger.LogVerbose($"[AutoInstructionGenerator] Added {addedCount} missing instruction(s) to existing option '{existingOption.Name}'");
                    }
                    else
                    {
                        Logger.LogVerbose($"[AutoInstructionGenerator] Option equivalent to '{potentialOption.Name}' already exists as '{existingOption.Name}' with all instructions present");
                    }

                    optionGuidsToAdd.Add(existingOption.Guid.ToString());
                }
                else
                {
                    component.Options.Add(potentialOption);
                    optionGuidsToAdd.Add(potentialOption.Guid.ToString());
                    Logger.LogVerbose($"[AutoInstructionGenerator] Added new option '{potentialOption.Name}' for namespace");
                }
            }

            if (optionGuidsToAdd.Count > 0)
            {
                Instruction existingChoose = FindCompatibleChooseInstruction(component);

                if (existingChoose != null)
                {
                    int addedGuidCount = 0;
                    foreach (string optionGuid in optionGuidsToAdd)
                    {
                        if (AddOptionToChooseInstruction(existingChoose, optionGuid))
                        {
                            addedGuidCount++;
                        }
                    }

                    if (addedGuidCount > 0)
                    {
                        Logger.LogVerbose($"[AutoInstructionGenerator] Added {addedGuidCount} option GUID(s) to existing Choose instruction");
                    }
                    else
                    {
                        Logger.LogVerbose("[AutoInstructionGenerator] All namespace option GUIDs already present in existing Choose instruction");
                    }
                }
                else
                {
                    var chooseInstruction = new Instruction
                    {

                        Action = Instruction.ActionType.Choose,
                        Source = optionGuidsToAdd,
                        Overwrite = true,
                    };
                    chooseInstruction.SetParentComponent(component);
                    component.Instructions.Add(chooseInstruction);
                    Logger.LogVerbose($"[AutoInstructionGenerator] Created new Choose instruction with {optionGuidsToAdd.Count} namespace option(s)");
                }
            }

            int consolidatedCount = ConsolidateDuplicateOptions(component);
            if (consolidatedCount > 0)
            {
                Logger.LogVerbose($"[AutoInstructionGenerator] Consolidated {consolidatedCount} duplicate namespace option(s)");
            }

            SelectNamespaceOptionsFromGuide(component);
        }

        /// <summary>
        /// Sets <see cref="Option.IsSelected"/> from guide Directions / download notes so a
        /// non-interactive install actually runs the namespaced Patcher instructions.
        /// <para>
        /// Always selects the primary/main/base/default namespace unless the guide excludes it.
        /// Optional namespaces are selected when the prose asks for them and any named condition
        /// is met: a condition mod is in the build, or a possessive "if you use Mod's Option"
        /// clause matches a selected option on that other mod. Compatibility patches are never
        /// mutually exclusive with the primary/main install.
        /// </para>
        /// </summary>
        internal static void SelectNamespaceOptionsFromGuide(
            [NotNull] ModComponent component,
            [CanBeNull] IReadOnlyList<ModComponent> buildComponents = null)
        {
            if (component.Options == null || component.Options.Count == 0)
            {
                return;
            }

            string prose = string.Join(
                "\n",
                new[]
                {
                    component.Directions,
                    component.DirectionsSpoilerFree,
                    component.DownloadInstructions,
                    component.DownloadInstructionsSpoilerFree,
                }.Where(s => !string.IsNullOrWhiteSpace(s)));

            IReadOnlyList<ModComponent> build = buildComponents
                ?? MainConfig.AllComponents
                ?? (IReadOnlyList<ModComponent>)Array.Empty<ModComponent>();

            foreach (Option option in component.Options)
            {
                if (option == null)
                {
                    continue;
                }

                if (IsPrimaryNamespaceOption(option))
                {
                    option.IsSelected = !GuideExcludesNamespace(prose, option, component.Name);
                }
            }

            if (!component.Options.Any(o => o != null && o.IsSelected))
            {
                Option fallback = component.Options.FirstOrDefault(o => o != null && IsPrimaryNamespaceOption(o))
                    ?? component.Options.FirstOrDefault(o => o != null);
                if (fallback != null)
                {
                    fallback.IsSelected = true;
                }
            }

            foreach (Option option in component.Options)
            {
                if (option == null || option.IsSelected)
                {
                    continue;
                }

                if (GuideExcludesNamespace(prose, option, component.Name))
                {
                    continue;
                }

                if (!GuideRequestsNamespace(prose, option))
                {
                    continue;
                }

                if (IsOptionalNamespaceOption(option)
                    && !OptionalNamespaceConditionMet(option, prose, build))
                {
                    continue;
                }

                option.IsSelected = true;
            }

            foreach (Option option in component.Options)
            {
                if (option != null && GuideExcludesNamespace(prose, option, component.Name))
                {
                    option.IsSelected = false;
                }
            }

            List<string> usingList = ExtractNamedOptionsWeWillBeUsing(prose.ToLowerInvariant());
            if (usingList.Count > 0)
            {
                foreach (Option option in component.Options)
                {
                    if (option != null
                        && option.IsSelected
                        && !OptionMatchesNamedUsingList(option, usingList))
                    {
                        option.IsSelected = false;
                    }
                }
            }

            ArbitrateMutuallyExclusiveNamespaces(component, prose);
            ApplyCompatPresenceConstraints(component, build);
            Option otherwiseDefault = FindOtherwiseSimplyInstallOption(
                prose,
                component.Options.Where(o => o != null).ToList());
            if (otherwiseDefault != null)
            {
                foreach (Option option in component.Options)
                {
                    if (option != null)
                    {
                        option.IsSelected = ReferenceEquals(option, otherwiseDefault);
                    }
                }
            }

            ApplyConfiguredExceptions(component);

            Logger.LogVerbose(
                $"[AutoInstructionGenerator] Namespace selection for '{component.Name}': "
                + string.Join(
                    ", ",
                    component.Options.Where(o => o != null).Select(o => $"{o.Name}={(o.IsSelected ? "on" : "off")}")));
        }

        [NotNull]
        private static GuideInterpretationPolicy Policy => GuideInterpretationPolicyStore.Current;

        private static bool IsPrimaryNamespaceOption([NotNull] Option option)
        {
            if (!Policy.Matching.PreferPrimaryNamespace)
            {
                return false;
            }

            string name = (option.Name ?? string.Empty).ToLowerInvariant();
            string desc = (option.Description ?? string.Empty).ToLowerInvariant();
            if (Policy.ContainsAny(name, Policy.Tokens.ExcludeFromPrimaryWhenAlso)
                || Policy.ContainsAny(desc, Policy.Tokens.ExcludeFromPrimaryWhenAlso))
            {
                return false;
            }

            return Policy.ContainsAny(name, Policy.Tokens.PrimaryNamespace)
                || Policy.ContainsAny(desc, Policy.Tokens.PrimaryDescriptionPhrases);
        }

        /// <summary>
        /// Drop namespaces whose own description says they apply only when a named
        /// companion is absent/present, using the rest of the build as the signal.
        /// </summary>
        private static void ApplyCompatPresenceConstraints(
            [NotNull] ModComponent component,
            [NotNull] IReadOnlyList<ModComponent> build)
        {
            if (component.Options == null || component.Options.Count == 0)
            {
                return;
            }

            Regex absent = Policy.CompileOrFallback(
                "compat_absent_option",
                @"\bonly\s+if\s+you\s+do\s+not\s+have\s+(?<compat>.+?)\s+installed\b",
                RegexOptions.IgnoreCase);

            foreach (Option option in component.Options)
            {
                if (option == null || !option.IsSelected)
                {
                    continue;
                }

                string blob = ((option.Name ?? string.Empty) + " " + (option.Description ?? string.Empty)).Trim();
                if (blob.Length == 0)
                {
                    continue;
                }

                Match noCompat = absent.Match(blob);
                if (noCompat.Success && BuildHasNamedCompat(build, noCompat.Groups["compat"].Value))
                {
                    option.IsSelected = false;
                }
            }

            if (component.Options.Any(o => o != null && o.IsSelected))
            {
                return;
            }

            foreach (Option option in component.Options)
            {
                if (option == null)
                {
                    continue;
                }

                string blob = ((option.Name ?? string.Empty) + " " + (option.Description ?? string.Empty)).Trim();
                Match noCompat = absent.Match(blob);
                if (noCompat.Success && BuildHasNamedCompat(build, noCompat.Groups["compat"].Value))
                {
                    continue;
                }

                option.IsSelected = true;
                return;
            }
        }

        private static bool BuildHasNamedCompat(
            [NotNull] IReadOnlyList<ModComponent> build,
            [CanBeNull] string compatPhrase)
        {
            if (build == null || string.IsNullOrWhiteSpace(compatPhrase))
            {
                return false;
            }

            string canonCompat = CanonicalizePhrase(compatPhrase);
            if (canonCompat.Length < 3)
            {
                return false;
            }

            foreach (ModComponent other in build)
            {
                if (other == null || string.IsNullOrWhiteSpace(other.Name))
                {
                    continue;
                }

                string canonName = CanonicalizePhrase(other.Name);
                if (canonName.Length == 0)
                {
                    continue;
                }

                if (canonName.Contains(canonCompat, StringComparison.Ordinal)
                    || (canonCompat.Contains(canonName, StringComparison.Ordinal)
                        && canonName.Length >= Policy.Matching.MinComponentNameLength))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Alternative namespaces are mutually exclusive: a mod offering "100% Brown",
        /// "Brown-Red-Blue" and "Brown-Red-Blue Alternative" expects exactly ONE to be installed.
        /// Token-overlap matching selects all of them ("brown" hits inside every sibling), which
        /// would run every conflicting variant in sequence. When the guide names one specific
        /// variant, keep the best match and drop its siblings.
        /// <para>
        /// Additive namespaces (compatibility patches, "also install X") are left untouched — those
        /// are legitimately installed alongside the primary one.
        /// </para>
        /// </summary>
        private static void ArbitrateMutuallyExclusiveNamespaces(
            [NotNull] ModComponent component,
            [CanBeNull] string prose)
        {
            if (string.IsNullOrWhiteSpace(prose) || component.Options == null)
            {
                return;
            }

            // "…and install it as well", "also install X" — the guide is stacking namespaces on top
            // of the primary, so they are additive and must not be arbitrated down to one.
            Regex additive = Policy.CompileOrFallback(
                "additive_namespace",
                @"\b(as\s+well|also\s+install|in\s+addition|additionally|and\s+install\s+it)\b",
                RegexOptions.IgnoreCase);
            if (additive.IsMatch(prose))
            {
                return;
            }

            // "The installer will need to be run 6 times, once to install each of the options
            // we'll be using: A, B, C." Those named options are sequential runs, not a pick-one
            // variant. Mutex scoring otherwise keeps the single highest hit (measured: TSLRCM
            // Tweak Pack kept only Saedhe's Head).
            Regex runEach = Policy.CompileOrFallback(
                "run_each_named_namespace",
                @"\b(?:run\s+\d+\s+times|once\s+to\s+install\s+each|once\s+for\s+each\s+of)\b",
                RegexOptions.IgnoreCase);
            if (runEach.IsMatch(prose))
            {
                Logger.LogVerbose(
                    $"[AutoInstructionGenerator] '{component.Name}': guide runs the installer "
                    + "once per named option — not mutually exclusive.");
                return;
            }

            // Compatibility / optional namespaces stack on the primary ("install main, then
            // re-run the 100% Brown compatibility patch"). They must not compete with Basic.
            List<Option> alternatives = component.Options
                .Where(o => o != null && o.IsSelected
                    && (!Policy.Matching.ExcludeOptionalFromMutex || !IsOptionalNamespaceOption(o)))
                .ToList();
            if (alternatives.Count < 2)
            {
                return;
            }

            // An explicit ordinal ("I personally recommend option 2") is 1-based in guide prose.
            int ordinal = ExplicitOptionOrdinal(prose);
            if (ordinal >= 0 && ordinal < component.Options.Count)
            {
                Option picked = component.Options[ordinal];
                if (picked != null && alternatives.Contains(picked))
                {
                    foreach (Option other in alternatives.Where(o => !ReferenceEquals(o, picked)))
                    {
                        other.IsSelected = false;
                    }

                    Logger.LogVerbose(
                        $"[AutoInstructionGenerator] '{component.Name}': guide names option {ordinal + 1}; "
                        + $"keeping '{picked.Name}' and dropping {alternatives.Count - 1} sibling namespace(s).");
                    return;
                }
            }

            // "I personally recommend the \"Senni Vek's Ambush\" install" names a namespace,
            // not an ordinal. Length-scoring otherwise picks "Senni Vek Restoration" because
            // that longer phrase also appears in the description.
            Option recommended = FindPersonallyRecommendedOption(prose, alternatives);
            if (recommended != null)
            {
                foreach (Option other in alternatives.Where(o => !ReferenceEquals(o, recommended)))
                {
                    other.IsSelected = false;
                }

                Logger.LogVerbose(
                    $"[AutoInstructionGenerator] '{component.Name}': guide personally recommends "
                    + $"'{recommended.Name}'; dropping {alternatives.Count - 1} sibling namespace(s).");
            }

            // Optional namespaces are excluded from the primary mutex so a named
            // main install does not drop "then apply the X option". Arbitrate
            // those separately when the guide names one.
            List<Option> optionalAlts = component.Options
                .Where(o => o != null && o.IsSelected && IsOptionalNamespaceOption(o))
                .ToList();
            if (optionalAlts.Count >= 2)
            {
                Option applied = FindPersonallyRecommendedOption(prose, optionalAlts);
                if (applied != null)
                {
                    foreach (Option other in optionalAlts.Where(o => !ReferenceEquals(o, applied)))
                    {
                        other.IsSelected = false;
                    }

                    Logger.LogVerbose(
                        $"[AutoInstructionGenerator] '{component.Name}': guide names optional "
                        + $"'{applied.Name}'; dropping {optionalAlts.Count - 1} sibling option namespace(s).");
                }
            }

            if (recommended != null)
            {
                return;
            }

            // "If you would like X, install X. Otherwise, simply install Standard."
            // The otherwise-clause is the default. Scoring otherwise picks the longer
            // optional name because it also appears in the "if you would like" sentence
            // (measured: Thematic KOTOR 2 Companions → Standard + Sith Assassin Visas).
            Option otherwiseDefault = FindOtherwiseSimplyInstallOption(prose, alternatives);
            if (otherwiseDefault != null)
            {
                foreach (Option other in alternatives.Where(o => !ReferenceEquals(o, otherwiseDefault)))
                {
                    other.IsSelected = false;
                }

                Logger.LogVerbose(
                    $"[AutoInstructionGenerator] '{component.Name}': guide says otherwise simply "
                    + $"install '{otherwiseDefault.Name}'.");
                return;
            }

            var scored = alternatives
                .Select(o => new { Option = o, Score = NamespaceNameSpecificity(prose, o) })
                .OrderByDescending(x => x.Score)
                .ToList();

            // Only arbitrate when the guide actually discriminates between the siblings.
            if (scored[0].Score <= 0d || Math.Abs(scored[0].Score - scored[1].Score) < 0.0001d)
            {
                return;
            }

            foreach (var loser in scored.Skip(1))
            {
                loser.Option.IsSelected = false;
            }

            Logger.LogVerbose(
                $"[AutoInstructionGenerator] '{component.Name}': mutually-exclusive namespaces - kept "
                + $"'{scored[0].Option.Name}' (score {scored[0].Score:0.00}), dropped "
                + string.Join(", ", scored.Skip(1).Select(x => $"'{x.Option.Name}'")));
        }

        /// <summary>
        /// "If you would like X, install X. Otherwise, simply install Standard."
        /// </summary>
        [CanBeNull]
        internal static Option FindOtherwiseSimplyInstallOption(
            [CanBeNull] string prose,
            [NotNull] IList<Option> alternatives)
        {
            if (string.IsNullOrWhiteSpace(prose) || alternatives == null || alternatives.Count == 0)
            {
                return null;
            }

            Match named = Policy.CompileOrFallback(
                "otherwise_simply_install",
                @"\botherwise,?\s+simply\s+install\s+[""“']?(?<name>[^""”'\n.]{3,60}?)[""”']?(?=\s*[.]|$)",
                RegexOptions.IgnoreCase).Match(prose);
            if (!named.Success)
            {
                return null;
            }

            string phrase = named.Groups["name"].Value.Trim().Trim('"', '“', '”', '\'');
            foreach (Option option in alternatives)
            {
                if (option != null
                    && !string.IsNullOrWhiteSpace(option.Name)
                    && string.Equals(option.Name.Trim(), phrase, StringComparison.OrdinalIgnoreCase))
                {
                    return option;
                }
            }

            return null;
        }

        /// <summary>
        /// "I personally recommend the \"Senni Vek's Ambush\" install" / "I personally recommend X".
        /// Picks the alternative whose name is inside the recommended phrase.
        /// </summary>
        [CanBeNull]
        internal static Option FindPersonallyRecommendedOption(
            [CanBeNull] string prose,
            [NotNull] IList<Option> alternatives)
        {
            if (string.IsNullOrWhiteSpace(prose) || alternatives == null || alternatives.Count == 0)
            {
                return null;
            }

            // Require "install" after the name so "…Ambush install, … choose Restoration instead"
            // cannot swallow both names and then pick the longer one.
            if (!Policy.Matching.PersonallyRecommendNamedInstall)
            {
                return null;
            }

            var patterns = new[]
            {
                ("personally_recommend_named_install",
                    @"\b(?:I\s+)?personally\s+recommend(?:\s+the)?\s+[""“]?(?<name>[^""”\n,.]{3,60}?)[""”]?\s+install\b"),
                ("recommend_named_option",
                    @"\b(?:I\s+)?(?:strongly\s+)?recommend\s+(?:the\s+)?[""“]?(?<name>[^""”\n,.]{3,80}?)[""”]?\s+option\b"),
                ("recommend_using_named",
                    @"\b(?:I\s+)?(?:strongly\s+)?recommend\s+using\s+(?:the\s+)?[""“]?(?<name>[^""”\n,.]{3,80}?)[""”]?(?=\s+for\b|\s+option\b|[.,;]|$)"),
                ("default_install_named",
                    @"\bthe\s+default\s+install\s*,\s*[""“]?(?<name>[^""”\n,.]{3,60}?)[""”]?(?=\s|,|\.|$)"),
                ("install_the_named_main_option",
                    @"\binstall\s+the\s+[""“]?(?<name>[^""”\n,.]{2,40}?)[""”]?\s+main\s+install\s+option\b"),
                ("apply_the_named_option",
                    @"\bapply\s+the\s+[""“]?(?<name>[^""”\n,.]{3,80}?)[""”]?\s+option\b"),
            };

            foreach ((string key, string fallback) in patterns)
            {
                Match named = Policy.CompileOrFallback(key, fallback, RegexOptions.IgnoreCase).Match(prose);
                if (!named.Success)
                {
                    continue;
                }

                string phrase = named.Groups["name"].Value.Trim().Trim('"', '“', '”');
                Option best = FindOptionMatchingPhrase(alternatives, phrase);
                if (best != null)
                {
                    return best;
                }
            }

            return null;
        }

        [CanBeNull]
        private static Option FindOptionMatchingPhrase(
            [NotNull] IList<Option> alternatives,
            [NotNull] string phrase)
        {
            Option best = null;
            int bestLen = 0;
            foreach (Option option in alternatives)
            {
                if (option == null || string.IsNullOrWhiteSpace(option.Name))
                {
                    continue;
                }

                if (OptionNameContainsPhrase(option, phrase) || PhraseContainsOptionName(phrase, option))
                {
                    int len = option.Name.Length;
                    if (len > bestLen)
                    {
                        best = option;
                        bestLen = len;
                    }
                }
            }

            return best;
        }

        private static bool PhraseContainsOptionName([NotNull] string phrase, [NotNull] Option option)
        {
            if (!Policy.Matching.OptionNameContainsPhrase)
            {
                return string.Equals(phrase?.Trim(), option.Name?.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            string canonPhrase = CanonicalizePhrase(phrase);
            string canonName = CanonicalizePhrase(option.Name);
            return canonName.Length >= Policy.Matching.MinPhraseContainsOptionNameLength
                && canonPhrase.IndexOf(canonName, StringComparison.Ordinal) >= 0;
        }

        [NotNull]
        private static string CanonicalizePhrase([CanBeNull] string value)
        {
            Regex splitter = Policy.CompileOrFallback(
                "canonicalize_non_alnum",
                @"[^a-z0-9]+",
                RegexOptions.None);
            return splitter.Replace(
                (value ?? string.Empty).ToLowerInvariant()
                    .Replace("&", " and ", StringComparison.Ordinal)
                    .Replace("retexture", "reskin", StringComparison.Ordinal),
                " ").Trim();
        }

        /// <summary>
        /// "I personally recommend option 2" / "use option 3" -> zero-based namespace index.
        /// Guide prose numbers options from 1. Returns -1 when no ordinal is named.
        /// </summary>
        internal static int ExplicitOptionOrdinal([CanBeNull] string prose)
        {
            if (string.IsNullOrWhiteSpace(prose))
            {
                return -1;
            }

            Match m = Policy.CompileOrFallback(
                "explicit_option_ordinal",
                @"\boption\s+(\d{1,2})\b",
                RegexOptions.IgnoreCase).Match(prose);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out int n) || n <= 0)
            {
                return -1;
            }

            return n - 1;
        }

        /// <summary>
        /// How specifically the prose names this namespace. A full-name phrase match beats a
        /// partial one, and distinctive tokens the prose never mentions (e.g. "gloves" in
        /// "Brown-Red-Blue (No Gloves)") count against it, so the plainly-named variant wins.
        /// </summary>
        private static double NamespaceNameSpecificity([NotNull] string prose, [NotNull] Option option)
        {
            string lower = prose.ToLowerInvariant();
            string name = (option.Name ?? string.Empty).ToLowerInvariant().Trim();
            if (name.Length == 0)
            {
                return 0d;
            }

            string canonProse = CanonicalizePhrase(lower);
            string canonName = CanonicalizePhrase(name);
            if (canonName.Length > 0 && canonProse.Contains(canonName, StringComparison.Ordinal))
            {
                // Longer exact phrases are more specific than shorter ones they contain.
                return Policy.Matching.ExactPhraseScoreBase + canonName.Length;
            }

            List<string> tokens = SignificantOptionTokens(option);
            if (tokens.Count == 0)
            {
                return 0d;
            }

            int matched = tokens.Count(t => canonProse.Contains(t, StringComparison.Ordinal));
            int unmatched = tokens.Count - matched;
            return matched - (Policy.Matching.UnmatchedTokenPenalty * unmatched);
        }

        private static bool IsOptionalNamespaceOption([NotNull] Option option)
        {
            string blob = ((option.Name ?? string.Empty) + " " + (option.Description ?? string.Empty))
                .ToLowerInvariant();
            return Policy.ContainsAny(blob, Policy.Tokens.OptionalNamespace);
        }

        private static bool GuideExcludesNamespace(
            [CanBeNull] string prose,
            [NotNull] Option option,
            [CanBeNull] string componentName = null)
        {
            if (string.IsNullOrWhiteSpace(prose))
            {
                return false;
            }

            string lower = prose.ToLowerInvariant();
            string template = Policy.TryGetPattern("guide_excludes_namespace")
                ?? @"\b(?:skip|ignore|do\s+not\s+install|don't\s+install|recommend\s+against|recommend\s+not)\b[^\n.]{0,80}\b{token}\b";
            string incompatible = Policy.TryGetPattern("guide_excludes_incompatible_namespace")
                ?? @"\b(?:exception\s+is|incompatible)\b[^\n.]{0,80}\b{token}\b|\b{token}\b[^\n.]{0,80}\b(?:incompatible|exception)\b";

            // Tokens that also appear in the parent component's own name aren't
            // discriminating: sibling namespace options are almost always variants of
            // that same base name, so a generic word shared with the component name
            // ("Alignment Affects Force Powers") will match an unrelated sentence that
            // happens to mention a different mod sharing those same generic words
            // ("...unless using K2 Force Powers for K1..."), false-positive excluding
            // every namespace option and zeroing out the whole Choose.
            HashSet<string> nameTokens = ComponentNameTokenSet(componentName);
            foreach (string token in SignificantOptionTokens(option))
            {
                if (nameTokens.Contains(token))
                {
                    continue;
                }

                string escaped = Regex.Escape(token);
                string pattern = template.Replace("{token}", escaped, StringComparison.Ordinal);
                string incompatiblePattern = incompatible.Replace("{token}", escaped, StringComparison.Ordinal);
                if (Regex.IsMatch(
                        lower,
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromSeconds(1))
                    || Regex.IsMatch(
                        lower,
                        incompatiblePattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromSeconds(1)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// "…once to install each of the options we'll be using: A, B, and C."
        /// When that allow-list is present, a shared word (Mandalore) must not pull in
        /// an unlisted sibling (Ravager Mandalore Changes).
        /// </summary>
        [NotNull]
        [ItemNotNull]
        private static List<string> ExtractNamedOptionsWeWillBeUsing([CanBeNull] string lowerProse)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(lowerProse))
            {
                return names;
            }

            Match list = Regex.Match(
                lowerProse,
                @"options\s+we'll\s+be\s+using:\s*(.+?)(?:\.|most of the other)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
            if (!list.Success)
            {
                return names;
            }

            foreach (string part in Regex.Split(list.Groups[1].Value, @"\s*(?:,|,\s*and\s+|\s+and\s+)\s*"))
            {
                string trimmed = part.Trim().TrimEnd('.');
                if (trimmed.Length >= 4)
                {
                    names.Add(trimmed);
                }
            }

            return names;
        }

        private static bool OptionMatchesNamedUsingList(
            [NotNull] Option option,
            [NotNull] IReadOnlyList<string> usingList)
        {
            string subject = FirstSubjectToken(option.Name);
            if (string.IsNullOrEmpty(subject))
            {
                return false;
            }

            foreach (string item in usingList)
            {
                if (item.IndexOf(subject, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        [CanBeNull]
        private static string FirstSubjectToken([CanBeNull] string optionName)
        {
            if (string.IsNullOrWhiteSpace(optionName))
            {
                return null;
            }

            string stripped = Regex.Replace(
                optionName,
                @"^(?:\d+\s*-\s*|extras\s*-\s*\d+\s*-\s*)",
                string.Empty,
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));
            foreach (string word in Regex.Split(stripped, @"[^A-Za-z0-9']+"))
            {
                if (word.Length >= 4)
                {
                    return word.ToLowerInvariant();
                }
            }

            return null;
        }

        private static bool GuideRequestsNamespace([CanBeNull] string prose, [NotNull] Option option)
        {
            if (string.IsNullOrWhiteSpace(prose))
            {
                return false;
            }

            string lower = prose.ToLowerInvariant();
            List<string> usingList = ExtractNamedOptionsWeWillBeUsing(lower);
            if (usingList.Count > 0)
            {
                return OptionMatchesNamedUsingList(option, usingList);
            }

            List<string> tokens = SignificantOptionTokens(option);
            if (tokens.Count == 0 && !IsOptionalNamespaceOption(option))
            {
                return false;
            }

            // "install the main", "base install", "re-run ... optional", "also install X"
            if (IsPrimaryNamespaceOption(option)
                && Policy.ContainsAny(lower, Policy.Tokens.GuideRequestsPrimary))
            {
                return true;
            }

            // "re-run ... for each of the optional installs" — every optional namespace is in play
            // when its condition mod is present (checked by the caller). Bare "re-run" alone must
            // NOT select every optional (PAVOR only names the K1CP compat option).
            if (IsOptionalNamespaceOption(option)
                && Policy.ContainsAny(lower, Policy.Tokens.GuideRequestsOptional))
            {
                return true;
            }

            int hits = tokens.Count(t => lower.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
            if (hits == 0)
            {
                return false;
            }

            // A single distinctive token is enough when it meets the configured length.
            if (hits >= 1 && tokens.Any(t =>
                    t.Length >= Policy.Matching.MinSingleHitTokenLength
                    && lower.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            if (hits >= 1 && tokens.Any(t => t.Length >= Policy.Matching.MinNamedHitTokenLength
                && lower.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0
                && (lower.Contains("select the " + t, StringComparison.OrdinalIgnoreCase)
                    || lower.Contains(t + " compatibility", StringComparison.OrdinalIgnoreCase)
                    || lower.Contains(t + " compat", StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }

            return hits >= Math.Min(2, tokens.Count);
        }

        private static bool OptionalNamespaceConditionMet(
            [NotNull] Option option,
            [CanBeNull] string prose,
            [NotNull] IReadOnlyList<ModComponent> build)
        {
            // A compatibility patch named after another mod ("… for Cloaked Jedi Robes")
            // must not count as satisfied just because that mod is in the build when the
            // guide restricts it to one of that mod's options ("if you use X's Y option").
            // "Senni Vek's Ambush Compatibility" is for that specific other-mod option,
            // not merely "Senni Vek Mod is in the build".
            if (TryRequireMatchingForeignOption(option, build, out bool foreignOptionSelected))
            {
                return foreignOptionSelected;
            }

            bool proseNamesSpecificForeignOption = Policy.CompileOrFallback(
                "prose_names_specific_foreign_option",
                @"['\u2019]s\s+.+\s+option\b",
                RegexOptions.IgnoreCase).IsMatch(prose ?? string.Empty);
            if (!proseNamesSpecificForeignOption)
            {
                foreach (string token in SignificantOptionTokens(option))
                {
                    if (BuildHasComponentMatching(build, token))
                    {
                        return true;
                    }
                }
            }

            // "if using X" / "if you utilize X" — require X among build components.
            string blob = ((option.Name ?? string.Empty) + "\n" + (option.Description ?? string.Empty) + "\n" + (prose ?? string.Empty));
            MatchCollection conditions = Policy.CompileOrFallback(
                "if_using_condition",
                @"\bif\s+(?:using|you\s+utilize|you\s+use|installing)\s+(?:the\s+)?(?<mod>[A-Za-z0-9][A-Za-z0-9\s''\-%&.]{2,80}?)(?:\s*,|\s*\.|$|\s+and\b|\s+then\b|\s+re-)",
                RegexOptions.IgnoreCase).Matches(blob);

            foreach (Match match in conditions)
            {
                string modPhrase = match.Groups["mod"].Value.Trim();
                if (TryResolvePossessiveOptionCondition(modPhrase, build, out bool namedOptionSelected))
                {
                    if (namedOptionSelected)
                    {
                        return true;
                    }

                    continue;
                }

                // "Loadscreens in Color/HQ Blasters" → try each slash-separated part
                foreach (string part in modPhrase.Split(new[] { '/', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.Trim().Length >= 3 && BuildHasComponentMatching(build, part.Trim()))
                    {
                        return true;
                    }
                }
            }

            // Unresolved "if you use …" must stay off (the % in "100% Brown" used to
            // abort the capture, then this fallback installed the compatibility patch).
            if (conditions.Count == 0
                && Policy.Matching.UnresolvedIfYouUseDefaultsOff
                && Policy.CompileOrFallback(
                    "if_you_use_unresolved",
                    @"\bif\s+(?:using|you\s+use|you\s+utilize|installing)\b",
                    RegexOptions.IgnoreCase).IsMatch(prose ?? string.Empty))
            {
                return false;
            }

            // Guide asked for the optional with no resolvable condition — allow when the
            // option is not clearly tied to another product name.
            return conditions.Count == 0;
        }

        /// <summary>
        /// "Cloaked Jedi Robes's 100% Brown option" is a condition on that other mod's selected
        /// namespace, not merely on whether Cloaked Jedi Robes is in the build.
        /// </summary>
        private static bool TryResolvePossessiveOptionCondition(
            [NotNull] string phrase,
            [NotNull] IReadOnlyList<ModComponent> build,
            out bool namedOptionSelected)
        {
            namedOptionSelected = false;
            Match possessive = Policy.CompileOrFallback(
                "possessive_option_condition",
                @"^(?<mod>.+?)['\u2019]s\s+(?<opt>.+?)(?:\s+option)?\s*$",
                RegexOptions.IgnoreCase).Match(phrase);
            if (!possessive.Success)
            {
                return false;
            }

            string modName = possessive.Groups["mod"].Value.Trim();
            string optionPhrase = Policy.CompileOrFallback(
                "strip_trailing_option",
                @"\s+option$",
                RegexOptions.IgnoreCase).Replace(possessive.Groups["opt"].Value.Trim(), string.Empty).Trim();
            if (modName.Length < 3 || optionPhrase.Length < 3)
            {
                return false;
            }

            ModComponent other = build.FirstOrDefault(component =>
                component != null && ComponentNameMatchesPhrase(component, modName));
            if (other?.Options == null)
            {
                return true;
            }

            namedOptionSelected = other.Options.Any(option =>
                option != null
                && option.IsSelected
                && OptionNameContainsPhrase(option, optionPhrase));
            return true;
        }

        /// <summary>
        /// A compat patch named after another mod's namespace
        /// ("Senni Vek's Ambush Compatibility") is only in play when that option is selected.
        /// </summary>
        private static bool TryRequireMatchingForeignOption(
            [NotNull] Option option,
            [NotNull] IReadOnlyList<ModComponent> build,
            out bool foreignOptionSelected)
        {
            foreignOptionSelected = false;
            string selfName = option.Name ?? string.Empty;
            if (selfName.Length < Policy.Matching.MinSelfNameLengthForForeignMatch)
            {
                return false;
            }

            Option bestForeign = null;
            int bestLen = 0;
            foreach (ModComponent component in build)
            {
                if (component?.Options == null)
                {
                    continue;
                }

                foreach (Option foreign in component.Options)
                {
                    if (foreign == null
                        || ReferenceEquals(foreign, option)
                        || string.IsNullOrWhiteSpace(foreign.Name)
                        || foreign.Name.Length < Policy.Matching.MinForeignOptionNameLength
                        || foreign.Name.Length <= bestLen)
                    {
                        continue;
                    }

                    if (PhraseContainsOptionName(selfName, foreign))
                    {
                        bestForeign = foreign;
                        bestLen = foreign.Name.Length;
                    }
                }
            }

            if (bestForeign == null)
            {
                return false;
            }

            foreignOptionSelected = bestForeign.IsSelected;
            return true;
        }

        private static bool ComponentNameMatchesPhrase([NotNull] ModComponent component, [NotNull] string phrase)
        {
            return BuildHasComponentMatching(new[] { component }, phrase);
        }

        private static bool OptionNameContainsPhrase([NotNull] Option option, [NotNull] string phrase)
        {
            if (!Policy.Matching.OptionNameContainsPhrase)
            {
                return string.Equals(option.Name?.Trim(), phrase?.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            string optionCanon = CanonicalizePhrase(option.Name);
            string phraseCanon = CanonicalizePhrase(phrase);
            if (optionCanon.Length == 0 || phraseCanon.Length == 0)
            {
                return false;
            }

            return optionCanon.Contains(phraseCanon, StringComparison.Ordinal)
                || phraseCanon.Contains(optionCanon, StringComparison.Ordinal);
        }

        private static bool BuildHasComponentMatching(
            [NotNull] IReadOnlyList<ModComponent> build,
            [NotNull] string phrase)
        {
            string needle = ArchiveResolver.Normalize(phrase);
            if (needle.Length < Policy.Matching.MinComponentNameLength)
            {
                return false;
            }

            foreach (ModComponent component in build)
            {
                if (component == null)
                {
                    continue;
                }

                string name = ArchiveResolver.Normalize(component.Name);
                string heading = ArchiveResolver.Normalize(component.Heading);
                int min = Policy.Matching.MinComponentNameLength;
                if (name.Length >= min
                    && (name.IndexOf(needle, StringComparison.Ordinal) >= 0
                        || needle.IndexOf(name, StringComparison.Ordinal) >= 0))
                {
                    return true;
                }

                if (heading.Length >= min
                    && (heading.IndexOf(needle, StringComparison.Ordinal) >= 0
                        || needle.IndexOf(heading, StringComparison.Ordinal) >= 0))
                {
                    return true;
                }

                // Token overlap: "hq blasters" vs "High Quality Blasters"
                string[] phraseTokens = Regex.Split(phrase.ToLowerInvariant(), @"[^a-z0-9]+")
                    .Where(t => t.Length >= 4)
                    .ToArray();
                if (phraseTokens.Length == 0)
                {
                    continue;
                }

                string hay = (component.Name + " " + (component.Heading ?? string.Empty)).ToLowerInvariant();
                if (phraseTokens.Count(t => hay.IndexOf(t, StringComparison.Ordinal) >= 0) >= Math.Min(2, phraseTokens.Length))
                {
                    return true;
                }
            }

            return false;
        }

        [NotNull]
        private static List<string> SignificantOptionTokens([NotNull] Option option)
        {
            string blob = (option.Name ?? string.Empty) + " " + (option.Description ?? string.Empty);
            HashSet<string> stop = new HashSet<string>(
                Policy.Tokens.SignificantTokenStopwords,
                StringComparer.Ordinal);
            Regex splitter = Policy.CompileOrFallback(
                "canonicalize_non_alnum",
                @"[^a-z0-9]+",
                RegexOptions.None);
            return splitter.Split(blob.ToLowerInvariant())
                .Where(t => t.Length >= Policy.Matching.MinOptionTokenLength)
                .Where(t => !stop.Contains(t))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Tokens of the parent component's own name, canonicalized the same way as
        /// <see cref="SignificantOptionTokens"/>. Used to strip generic words a namespace
        /// option's name inherits from its component ("Alignment Affects Force Powers")
        /// before using those words to detect guide exclusions, since such words are
        /// shared by every sibling option and can't discriminate one from another.
        /// </summary>
        [NotNull]
        private static HashSet<string> ComponentNameTokenSet([CanBeNull] string componentName)
        {
            if (string.IsNullOrWhiteSpace(componentName))
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            Regex splitter = Policy.CompileOrFallback(
                "canonicalize_non_alnum",
                @"[^a-z0-9]+",
                RegexOptions.None);
            return new HashSet<string>(
                splitter.Split(componentName.ToLowerInvariant()).Where(t => t.Length > 0),
                StringComparer.Ordinal);
        }

        private static void ApplyConfiguredExceptions([NotNull] ModComponent component)
        {
            IReadOnlyList<GuideInterpretationPolicy.InterpretationException> exceptions = Policy.Exceptions;
            if (exceptions == null || exceptions.Count == 0 || component?.Options == null)
            {
                return;
            }

            string componentBlob = ((component.Name ?? string.Empty) + " " + (component.Heading ?? string.Empty))
                .ToLowerInvariant();
            foreach (GuideInterpretationPolicy.InterpretationException exception in exceptions)
            {
                if (exception == null || !exception.Select.HasValue)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(exception.WhenComponentContains)
                    && componentBlob.IndexOf(
                        exception.WhenComponentContains.ToLowerInvariant(),
                        StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                foreach (Option option in component.Options)
                {
                    if (option == null)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(exception.WhenOptionContains)
                        && (option.Name ?? string.Empty).IndexOf(
                            exception.WhenOptionContains,
                            StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    option.IsSelected = exception.Select.Value;
                    Logger.LogVerbose(
                        $"[AutoInstructionGenerator] Interpretation exception '{exception.Id ?? "(unnamed)"}' "
                        + $"set '{option.Name}' = {(option.IsSelected ? "on" : "off")}.");
                }
            }
        }

        /// <summary>
        /// After archive generation merges with kept NLP deletes/renames/cleanlist, put Extract
        /// first and Move last so "delete X before moving" and "copy then rename then move" hold.
        /// </summary>
        private static void OrderMergedGuideAndArchiveInstructions([NotNull] ModComponent component)
        {
            if (component.Instructions == null || component.Instructions.Count < 2)
            {
                return;
            }

            List<Instruction> ordered = component.Instructions
                .Select((instruction, index) => new { instruction, index })
                .OrderBy(t => InstructionMergePriority(t.instruction))
                .ThenBy(t => t.index)
                .Select(t => t.instruction)
                .ToList();

            component.Instructions.Clear();
            foreach (Instruction instruction in ordered)
            {
                component.Instructions.Add(instruction);
            }
        }

        /// <summary>
        /// Guide copy-as ("make a copy … rename it PMBJ01.tga") arrives as Rename of
        /// <c>&lt;&lt;modDirectory&gt;&gt;\*</c> onto a bare filename under the archive store.
        /// Copy and Move treat Destination as a folder and append the source leaf, so binding
        /// the dest to <c>Override\PMBJ01.tga</c> made validation create that path as a
        /// directory. Copy the extracted file into Override, then Rename to the guide name.
        /// </summary>
        private static void BindBareCopyAsInstructions(
            [NotNull] ModComponent component,
            [NotNull] string extractedPath,
            [NotNull] IReadOnlyList<string> fileList)
        {
            if (string.IsNullOrWhiteSpace(extractedPath) || fileList == null || fileList.Count == 0)
            {
                return;
            }

            var pendingRenames = new List<(Instruction Copy, Instruction Rename)>();
            foreach (Instruction instruction in component.Instructions)
            {
                if (instruction == null)
                {
                    continue;
                }

                if (instruction.Action != Instruction.ActionType.Rename
                    && instruction.Action != Instruction.ActionType.Copy)
                {
                    continue;
                }

                if (!IsBareFilenameCopyAs(instruction))
                {
                    continue;
                }

                string destLeaf = instruction.Destination
                    .Replace("<<modDirectory>>", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("<<kotorDirectory>>", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("<<gameDirectory>>", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Trim('\\', '/', ' ');
                string hinted = HintedCopyAsSourceLeaf(instruction)
                    ?? HintedCopyAsSourceFromProse(component);
                string chosen = ChooseCopyAsSource(fileList, hinted, destLeaf);
                if (string.IsNullOrEmpty(chosen))
                {
                    Logger.LogWarning(
                        $"[AutoInstructionGenerator] Could not rebind bare copy-as/rename instruction for component "
                        + $"'{component.Name}': no matching source found for '{destLeaf}'. Leaving Destination "
                        + $"unresolved: '{instruction.Destination}'.");
                    continue;
                }

                string sourceLeaf = Path.GetFileName(chosen.Replace('/', Path.DirectorySeparatorChar));
                instruction.Action = Instruction.ActionType.Copy;
                instruction.Overwrite = true;
                instruction.Source = new List<string>
                {
                    $@"<<modDirectory>>\{extractedPath}\{chosen.Replace('/', '\\')}",
                };
                instruction.Destination = @"<<kotorDirectory>>\Override";
                Logger.LogVerbose(
                    $"[AutoInstructionGenerator] Bound copy-as '{destLeaf}' to '{chosen}' under '{extractedPath}'");

                if (!string.IsNullOrEmpty(sourceLeaf)
                    && !string.Equals(sourceLeaf, destLeaf, StringComparison.OrdinalIgnoreCase))
                {
                    var rename = new Instruction
                    {
                        Action = Instruction.ActionType.Rename,
                        Overwrite = true,
                        Source = new List<string> { $@"<<kotorDirectory>>\Override\{sourceLeaf}" },
                        Destination = destLeaf,
                    };
                    rename.SetParentComponent(component);
                    pendingRenames.Add((instruction, rename));
                    Logger.LogVerbose(
                        $"[AutoInstructionGenerator] Copy-as will rename Override '{sourceLeaf}' to '{destLeaf}'");
                }
            }

            foreach ((Instruction copy, Instruction rename) in pendingRenames)
            {
                int index = component.Instructions.IndexOf(copy);
                if (index >= 0)
                {
                    component.Instructions.Insert(index + 1, rename);
                }
                else
                {
                    component.Instructions.Add(rename);
                }
            }
        }

        [CanBeNull]
        private static string HintedCopyAsSourceLeaf([NotNull] Instruction instruction)
        {
            if (instruction.Source == null)
            {
                return null;
            }

            foreach (string source in instruction.Source)
            {
                if (string.IsNullOrWhiteSpace(source) || SourceIsUngroundedProseFragment(source))
                {
                    continue;
                }

                string leaf = source
                    .Replace("<<modDirectory>>", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Trim('\\', '/', ' ');
                leaf = leaf.TrimEnd('*');
                if (string.IsNullOrEmpty(leaf) || leaf == "*")
                {
                    continue;
                }

                if (leaf.IndexOf('\\') >= 0)
                {
                    leaf = leaf.Split('\\').Last();
                }

                return leaf;
            }

            return null;
        }

        [CanBeNull]
        private static string HintedCopyAsSourceFromProse([NotNull] ModComponent component)
        {
            string prose = ((component.Directions ?? string.Empty) + " " + (component.DownloadInstructions ?? string.Empty));
            if (string.IsNullOrWhiteSpace(prose))
            {
                return null;
            }

            Match match = Regex.Match(
                prose,
                @"\b(?:copy|duplicate)\b[\s\S]{0,80}?\b(?:of|file)\b[\s\S]{0,40}?'([A-Za-z0-9_]+)'",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(2));
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            match = Regex.Match(
                prose,
                @"\b(?:copy|duplicate)\s+(?:of\s+)?([A-Za-z][A-Za-z0-9_]{3,})",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(2));
            return match.Success ? match.Groups[1].Value : null;
        }

        [CanBeNull]
        private static string ChooseCopyAsSource(
            [NotNull] IReadOnlyList<string> fileList,
            [CanBeNull] string hinted,
            [NotNull] string destLeaf)
        {
            List<string> files = fileList
                .Where(path => !string.IsNullOrWhiteSpace(path)
                    && !path.EndsWith("/", StringComparison.Ordinal)
                    && !path.EndsWith("\\", StringComparison.Ordinal)
                    && !NonGameContentFilter.IsNonGameContent(path))
                .ToList();
            if (files.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(hinted))
            {
                string hintStem = Path.GetFileNameWithoutExtension(hinted);
                List<string> hintedHits = files
                    .Where(path =>
                        string.Equals(
                            Path.GetFileNameWithoutExtension(path),
                            hintStem,
                            StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(path).StartsWith(hintStem, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (hintedHits.Count == 1)
                {
                    return hintedHits[0];
                }

                // Same stem can hit more than one archive entry (e.g. a texture plus its sidecar
                // "N_Duros02.tga" + "N_Duros02.txi"). The rename target's own extension picks the
                // right one when the stem match alone is ambiguous.
                if (hintedHits.Count > 1)
                {
                    string destExtension = Path.GetExtension(destLeaf);
                    if (!string.IsNullOrEmpty(destExtension))
                    {
                        List<string> extensionHits = hintedHits
                            .Where(path => string.Equals(
                                Path.GetExtension(path),
                                destExtension,
                                StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        if (extensionHits.Count == 1)
                        {
                            return extensionHits[0];
                        }
                    }
                }
            }

            List<string> game = files
                .Where(path => NonGameContentFilter.ProtectedGameExtensions.Contains(
                    Path.GetExtension(path)))
                .ToList();
            return game.Count == 1 ? game[0] : null;
        }

        /// <summary>
        /// Redrob's cleanlist deletes from the extracted payload folder before Move, not from
        /// Override (the batch file runs inside "Copy contents to KotOR's Override folder").
        /// </summary>
        private static void BindCleanListToPayloadFolder([NotNull] ModComponent component)
        {
            foreach (Instruction instruction in component.Instructions)
            {
                if (instruction?.Action != Instruction.ActionType.CleanList)
                {
                    continue;
                }

                Instruction move = component.Instructions.FirstOrDefault(
                    candidate => candidate?.Action == Instruction.ActionType.Move
                        && candidate.Source != null
                        && candidate.Source.Count > 0);
                if (move == null)
                {
                    continue;
                }

                string payload = move.Source[0]
                    .TrimEnd('*', '\\', '/');
                if (string.IsNullOrWhiteSpace(payload))
                {
                    continue;
                }

                instruction.Destination = payload;
                Logger.LogVerbose(
                    $"[AutoInstructionGenerator] CleanList destination bound to payload folder '{payload}'");
            }
        }

        private static int InstructionMergePriority([NotNull] Instruction instruction)
        {
            switch (instruction.Action)
            {
                case Instruction.ActionType.Extract:
                    return 0;
                case Instruction.ActionType.Choose:
                    return 1;
                case Instruction.ActionType.Delete:
                    // Guide-directed deletes from the extracted tree (e.g. keblastore.utm in
                    // tslpatchdata) must run before the patcher. Override cleanups stay after.
                    return DeleteTargetsExtractedModTree(instruction) ? 2 : 6;
                case Instruction.ActionType.Patcher:
                    return 3;
                case Instruction.ActionType.Execute:
                case Instruction.ActionType.Run:
                    return 4;
                case Instruction.ActionType.Rename:
                case Instruction.ActionType.Copy:
                    return 5;
                case Instruction.ActionType.CleanList:
                    return 7;
                case Instruction.ActionType.Move:
                    return 8;
                case Instruction.ActionType.DelDuplicate:
                    return 9;
                default:
                    return 10;
            }
        }

        private static bool DeleteTargetsExtractedModTree([NotNull] Instruction instruction)
        {
            if (instruction.Source == null || instruction.Source.Count == 0)
            {
                return false;
            }

            return instruction.Source.Any(source =>
                !string.IsNullOrWhiteSpace(source)
                && source.IndexOf("<<kotorDirectory>>", StringComparison.OrdinalIgnoreCase) < 0
                && source.IndexOf("<<gameDirectory>>", StringComparison.OrdinalIgnoreCase) < 0
                && source.IndexOf("<<modDirectory>>", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Bind guide-prose Delete/Rename leftovers onto the extracted tree or Override.
        /// A bare <c>&lt;&lt;modDirectory&gt;&gt;\keblastore.utm</c> is the TSLPatchdata file
        /// the guide said to remove before the patcher, not a missing file at the extract root.
        /// </summary>
        private static void BindGuideSecondaryPaths(
            [NotNull] ModComponent component,
            [NotNull] string extractedPath,
            [CanBeNull] IReadOnlyList<string> fileList)
        {
            if (string.IsNullOrWhiteSpace(extractedPath))
            {
                return;
            }

            string directions = ComponentGuideProse(component);
            IReadOnlyList<string> listing = fileList ?? Array.Empty<string>();

            foreach (Instruction instruction in component.Instructions)
            {
                if (instruction?.Source == null || instruction.Source.Count == 0)
                {
                    continue;
                }

                if (instruction.Action == Instruction.ActionType.Delete)
                {
                    instruction.Source = instruction.Source
                        .Select(source => BindGuideDeleteSource(source, extractedPath, listing, directions))
                        .ToList();
                }
                else if (instruction.Action == Instruction.ActionType.Rename)
                {
                    instruction.Source = instruction.Source
                        .Select(BindGuideRenameSource)
                        .ToList();
                }
            }

            SynthesizeMissingGuideSecondaries(component, extractedPath, listing, directions);
        }

        [NotNull]
        private static string BindGuideDeleteSource(
            [NotNull] string source,
            [NotNull] string extractedPath,
            [NotNull] IReadOnlyList<string> fileList,
            [NotNull] string directions)
        {
            if (source.IndexOf("<<kotorDirectory>>", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("tslpatchdata", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return source;
            }

            string leaf = GuidePathLeaf(source);
            if (string.IsNullOrWhiteSpace(leaf) || leaf.IndexOf('*') >= 0 || leaf.IndexOf("<<", StringComparison.Ordinal) >= 0)
            {
                return source;
            }

            if (GuideDeleteIsBeforeMove(directions, leaf))
            {
                string listed = fileList.FirstOrDefault(entry =>
                    Path.GetFileName(entry.Replace('/', Path.DirectorySeparatorChar))
                        .Equals(leaf, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(listed))
                {
                    string bound = $@"<<modDirectory>>\{extractedPath}\{listed.Replace('/', '\\')}";
                    Logger.LogVerbose($"[AutoInstructionGenerator] Bound delete-before-move '{leaf}' to '{bound}'");
                    return bound;
                }
            }

            int nameAt = directions.IndexOf(leaf, StringComparison.OrdinalIgnoreCase);
            int tslAt = directions.IndexOf("tslpatchdata", StringComparison.OrdinalIgnoreCase);
            int overrideAt = directions.IndexOf("override", StringComparison.OrdinalIgnoreCase);
            bool preferTsl = tslAt >= 0 && (nameAt < 0 || Math.Abs(nameAt - tslAt) <= Math.Abs(nameAt - overrideAt) || overrideAt < 0);

            if (preferTsl)
            {
                string listed = fileList.FirstOrDefault(entry =>
                    Path.GetFileName(entry.Replace('/', Path.DirectorySeparatorChar))
                        .Equals(leaf, StringComparison.OrdinalIgnoreCase)
                    && entry.IndexOf("tslpatchdata", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrEmpty(listed))
                {
                    string bound = $@"<<modDirectory>>\{extractedPath}\{listed.Replace('/', '\\')}";
                    Logger.LogVerbose($"[AutoInstructionGenerator] Bound tslpatchdata delete '{leaf}' to '{bound}'");
                    return bound;
                }
            }

            if (overrideAt >= 0)
            {
                string bound = $@"<<kotorDirectory>>\Override\{leaf}";
                Logger.LogVerbose($"[AutoInstructionGenerator] Bound Override delete '{leaf}' to '{bound}'");
                return bound;
            }

            return source;
        }

        private static bool GuideDeleteIsBeforeMove([NotNull] string directions, [NotNull] string leaf)
        {
            return GuideDeletedBeforeMoveFilenames(directions)
                .Exists(name => name.Equals(leaf, StringComparison.OrdinalIgnoreCase));
        }

        [NotNull]
        private static string GuidePathLeaf([CanBeNull] string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return string.Empty;
            }

            string trimmed = source.Trim().Trim('\'', '"', ' ', '*');
            int slash = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
            return slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
        }

        [NotNull]
        private static string BindGuideRenameSource([NotNull] string source)
        {
            if (source.IndexOf("<<kotorDirectory>>", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return source;
            }

            string leaf = GuidePathLeaf(source);
            if (string.IsNullOrWhiteSpace(leaf) || !Path.HasExtension(leaf) || leaf.IndexOf("<<", StringComparison.Ordinal) >= 0)
            {
                return source;
            }

            return $@"<<kotorDirectory>>\Override\{leaf}";
        }

        private static void SynthesizeMissingGuideSecondaries(
            [NotNull] ModComponent component,
            [NotNull] string extractedPath,
            [NotNull] IReadOnlyList<string> fileList,
            [NotNull] string directions)
        {
            if (string.IsNullOrWhiteSpace(directions))
            {
                return;
            }

            Match tslDelete = Regex.Match(
                directions,
                @"tslpatchdata[^.]{0,80}delete the file ['""]?(?<file>[\w.-]+\.\w+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
            if (!tslDelete.Success)
            {
                tslDelete = Regex.Match(
                    directions,
                    @"delete the file ['""]?(?<file>[\w.-]+\.\w+)['""]?[^.]{0,40}tslpatchdata",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1));
            }

            if (tslDelete.Success)
            {
                string leaf = tslDelete.Groups["file"].Value;
                if (!component.Instructions.Any(i =>
                        i.Action == Instruction.ActionType.Delete
                        && i.Source != null
                        && i.Source.Any(s => Path.GetFileName(s.Replace('/', Path.DirectorySeparatorChar))
                            .Equals(leaf, StringComparison.OrdinalIgnoreCase))))
                {
                    string listed = fileList.FirstOrDefault(entry =>
                        Path.GetFileName(entry.Replace('/', Path.DirectorySeparatorChar))
                            .Equals(leaf, StringComparison.OrdinalIgnoreCase))
                        ?? $@"tslpatchdata\{leaf}";
                    var del = new Instruction
                    {
                        Action = Instruction.ActionType.Delete,
                        Overwrite = false,
                        Source = new List<string>
                        {
                            $@"<<modDirectory>>\{extractedPath}\{listed.Replace('/', '\\')}",
                        },
                    };
                    del.SetParentComponent(component);
                    component.Instructions.Add(del);
                    Logger.LogVerbose($"[AutoInstructionGenerator] Synthesized tslpatchdata delete for '{leaf}'");
                }
            }

            Match rename = Regex.Match(
                directions,
                @"rename the files ['""]?(?<a>[\w.-]+)['""]? and ['""]?(?<b>[\w.-]+)['""]? to ['""]?(?<c>[\w.-]+)['""]? and ['""]?(?<d>[\w.-]+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
            if (rename.Success)
            {
                AddOverrideRenameIfMissing(component, rename.Groups["a"].Value, rename.Groups["c"].Value);
                AddOverrideRenameIfMissing(component, rename.Groups["b"].Value, rename.Groups["d"].Value);
            }

            Match overrideList = Regex.Match(
                directions,
                @"delete the following files from your override directory:\s*(?<list>[^\n]+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
            if (overrideList.Success)
            {
                foreach (string raw in Regex.Split(overrideList.Groups["list"].Value, @"\s*(?:,| and )\s*", RegexOptions.IgnoreCase))
                {
                    string leaf = raw.Trim().TrimEnd('.').Trim('\'', '"', ' ');
                    if (string.IsNullOrWhiteSpace(leaf) || !Path.HasExtension(leaf))
                    {
                        continue;
                    }

                    if (component.Instructions.Any(i =>
                            i.Action == Instruction.ActionType.Delete
                            && i.Source != null
                            && i.Source.Any(s =>
                                s.IndexOf("<<kotorDirectory>>", StringComparison.OrdinalIgnoreCase) >= 0
                                && Path.GetFileName(s.Replace('/', Path.DirectorySeparatorChar))
                                    .Equals(leaf, StringComparison.OrdinalIgnoreCase))))
                    {
                        continue;
                    }

                    var del = new Instruction
                    {
                        Action = Instruction.ActionType.Delete,
                        Overwrite = false,
                        Source = new List<string> { $@"<<kotorDirectory>>\Override\{leaf}" },
                    };
                    del.SetParentComponent(component);
                    component.Instructions.Add(del);
                    Logger.LogVerbose($"[AutoInstructionGenerator] Synthesized Override delete for '{leaf}'");
                }
            }
        }

        private static void AddOverrideRenameIfMissing(
            [NotNull] ModComponent component,
            [NotNull] string fromLeaf,
            [NotNull] string toLeaf)
        {
            if (string.IsNullOrWhiteSpace(fromLeaf) || string.IsNullOrWhiteSpace(toLeaf))
            {
                return;
            }

            if (component.Instructions.Any(i =>
                    i.Action == Instruction.ActionType.Rename
                    && i.Source != null
                    && i.Source.Any(s => Path.GetFileName(s.Replace('/', Path.DirectorySeparatorChar))
                        .Equals(fromLeaf, StringComparison.OrdinalIgnoreCase))))
            {
                return;
            }

            var rename = new Instruction
            {
                Action = Instruction.ActionType.Rename,
                Overwrite = true,
                Source = new List<string> { $@"<<kotorDirectory>>\Override\{fromLeaf}" },
                Destination = toLeaf,
            };
            rename.SetParentComponent(component);
            component.Instructions.Add(rename);
            Logger.LogVerbose($"[AutoInstructionGenerator] Synthesized Override rename '{fromLeaf}' → '{toLeaf}'");
        }

        /// <summary>
        /// Builds the mod-directory-relative path of the folder holding the installer.
        /// <para>
        /// Extraction always creates a folder named after the archive and unpacks the archive's own
        /// structure beneath it, so an archive that carries its own top-level folder ends up nested:
        /// "Character Start Up Changes.zip" containing "Character Start Up Changes/TSLPatcher.exe"
        /// lands on disk at "Character Start Up Changes/Character Start Up Changes/TSLPatcher.exe".
        /// </para>
        /// <para>
        /// The inner path was previously used on its own, dropping the extraction folder and pointing
        /// at a file one level too shallow. Validation did not catch it because the archive-entry
        /// lookup accepts the entry either as stored or prefixed with the archive name, so the check
        /// passed against the archive while the runtime path did not exist. The two segments are
        /// therefore joined rather than substituted.
        /// </para>
        /// </summary>
        [NotNull]
        private static string CombinePatcherPath(
            [NotNull] string extractedPath,
            [CanBeNull] string tslPatcherPathInsideArchive)
        {
            if (string.IsNullOrEmpty(tslPatcherPathInsideArchive))
            {
                return extractedPath;
            }

            // Instruction paths are written with '\' separators (PathHelper normalizes them per
            // platform later); the inner path comes from the archive and uses '/', so convert it
            // rather than emitting a path with both separators in it.
            string inner = tslPatcherPathInsideArchive.Replace('/', '\\');

            if (string.IsNullOrEmpty(extractedPath))
            {
                return inner;
            }

            return extractedPath + "\\" + inner;
        }

        /// <summary>
        /// Reads namespaces.ini from either an archive or an already-extracted mod folder. Directory
        /// mode analyzes the folder rather than the archive, so the namespace list has to come from
        /// the same place - reading it from the archive would reopen a RAR5 file the managed reader
        /// cannot handle, which is one of the reasons the folder is preferred in the first place.
        /// </summary>
        [CanBeNull]
        private static Dictionary<string, Dictionary<string, string>> ReadNamespacesIni(
            [NotNull] string archiveOrDirectoryPath)
        {
            if (!Directory.Exists(archiveOrDirectoryPath))
            {
                return IniHelper.ReadNamespacesIniFromArchive(archiveOrDirectoryPath);
            }

            try
            {
                string iniPath = Directory
                    .EnumerateFiles(archiveOrDirectoryPath, "namespaces.ini", SearchOption.AllDirectories)
                    .FirstOrDefault(f => !IsInstallerRuntimeArtifact(f));

                if (string.IsNullOrEmpty(iniPath))
                {
                    return null;
                }

                using (var reader = new StreamReader(iniPath))
                {
                    return IniHelper.ParseNamespacesIni(reader);
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, $"Failed to read namespaces.ini under '{archiveOrDirectoryPath}'");
                return null;
            }
        }

        private static void AddSimplePatcherInstruction(
            ModComponent component,
            ArchiveAnalysis analysis,
            string extractedPath)
        {
            string patcherPath = CombinePatcherPath(extractedPath, analysis.TslPatcherPath);

            if (string.IsNullOrEmpty(analysis.PatcherExecutable))
            {
                Logger.LogError(
                    $"[AutoInstructionGenerator] Component '{component.Name}': no installer executable found beside "
                    + $"tslpatchdata in '{patcherPath}'. Not generating a Patcher instruction - guessing the name "
                    + "would create a Source path that exists in no archive.");
                return;
            }

            string executableName = Path.GetFileName(analysis.PatcherExecutable);

            var patcherInstruction = new Instruction
            {
                Action = Instruction.ActionType.Patcher,
                Source = new List<string> { $@"<<modDirectory>>\{patcherPath}\{executableName}" },
                Destination = "<<gameDirectory>>",
                Overwrite = true,
            };
            patcherInstruction.SetParentComponent(component);

            if (!InstructionAlreadyExists(component, patcherInstruction))
            {
                component.Instructions.Add(patcherInstruction);
                Logger.LogVerbose($"[AutoInstructionGenerator] Added Patcher instruction for '{patcherPath}'");
            }
            else
            {
                Logger.LogVerbose($"[AutoInstructionGenerator] Patcher instruction for '{patcherPath}' already exists, skipping");
            }
        }

        private static void AddMultiFolderChooseInstructions(
            ModComponent component,
            IReadOnlyList<string> fileList,
            string extractedPath,
            List<string> folders
        )
        {
            var optionGuidsToAdd = new List<string>();

            foreach (string folder in folders)
            {
                if (!FolderContainsGameFiles(fileList, folder))
                {
                    Logger.LogVerbose($"[AutoInstructionGenerator] Skipping folder '{folder}' - no game files found");
                    continue;
                }

                if (ArchiveResolver.IsWrongGame(folder, DetectTargetGame()))
                {
                    Logger.LogVerbose(
                        $"[AutoInstructionGenerator] Skipping folder '{folder}' - belongs to the other game");
                    continue;
                }

                string potentialSourcePath = $@"<<modDirectory>>\{extractedPath}\{folder}\*";
                if (IsFolderAlreadyCoveredByInstructions(component, potentialSourcePath))
                {
                    Logger.LogVerbose($"[AutoInstructionGenerator] Skipping folder '{folder}' - already covered by existing instructions");
                    continue;
                }

                var potentialOption = new Option
                {
                    Guid = Guid.NewGuid(),
                    Name = folder,
                    Description = $"Install files from {folder} folder",
                    IsSelected = false,
                };

                var moveInstruction = new Instruction
                {

                    Action = Instruction.ActionType.Move,
                    Source = new List<string> { potentialSourcePath },
                    Destination = @"<<gameDirectory>>\Override",
                    Overwrite = true,
                    ExcludeNonGameContent = true,
                };
                moveInstruction.SetParentComponent(potentialOption);
                potentialOption.Instructions.Add(moveInstruction);

                Option existingOption = FindEquivalentOption(component, potentialOption);

                if (existingOption != null)
                {
                    int addedCount = AddMissingInstructionsToOption(existingOption, potentialOption);
                    if (addedCount > 0)
                    {
                        Logger.LogVerbose($"[AutoInstructionGenerator] Added {addedCount} missing instruction(s) to existing option '{existingOption.Name}'");
                    }
                    else
                    {
                        Logger.LogVerbose($"[AutoInstructionGenerator] Option equivalent to '{potentialOption.Name}' already exists as '{existingOption.Name}' with all instructions present");
                    }

                    optionGuidsToAdd.Add(existingOption.Guid.ToString());
                }
                else
                {
                    component.Options.Add(potentialOption);
                    optionGuidsToAdd.Add(potentialOption.Guid.ToString());
                    Logger.LogVerbose($"[AutoInstructionGenerator] Added new option '{potentialOption.Name}' for folder");
                }
            }

            if (optionGuidsToAdd.Count > 0)
            {
                Instruction existingChoose = FindCompatibleChooseInstruction(component);

                if (existingChoose != null)
                {
                    int addedGuidCount = 0;
                    foreach (string optionGuid in optionGuidsToAdd)
                    {
                        if (AddOptionToChooseInstruction(existingChoose, optionGuid))
                        {
                            addedGuidCount++;
                        }
                    }

                    if (addedGuidCount > 0)
                    {
                        Logger.LogVerbose($"[AutoInstructionGenerator] Added {addedGuidCount} option GUID(s) to existing Choose instruction");
                    }
                    else
                    {
                        Logger.LogVerbose($"[AutoInstructionGenerator] All folder option GUIDs already present in existing Choose instruction");
                    }
                }
                else
                {
                    var chooseInstruction = new Instruction
                    {

                        Action = Instruction.ActionType.Choose,
                        Source = optionGuidsToAdd,
                        Overwrite = true,
                    };
                    chooseInstruction.SetParentComponent(component);
                    component.Instructions.Add(chooseInstruction);
                    Logger.LogVerbose($"[AutoInstructionGenerator] Created new Choose instruction with {optionGuidsToAdd.Count} folder option(s)");
                }
            }

            int consolidatedCount = ConsolidateDuplicateOptions(component);
            if (consolidatedCount > 0)
            {
                Logger.LogVerbose($"[AutoInstructionGenerator] Consolidated {consolidatedCount} duplicate folder option(s)");
            }
        }

        [NotNull]
        [ItemNotNull]
        private static List<string> CollectGuideExcludedFolders(
            [NotNull] IReadOnlyList<string> fileList,
            [CanBeNull] string prose)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(prose))
            {
                return names;
            }

            foreach (string path in fileList)
            {
                string[] parts = path.Replace('\\', '/').Split('/');
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    string part = parts[i];
                    if (string.IsNullOrWhiteSpace(part)
                        || names.Exists(n => n.Equals(part, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (GuideTreatsFolderAsSkipped(part, prose) || GuideTreatsFolderAsOptional(part, prose))
                    {
                        names.Add(part);
                    }
                }
            }

            return names;
        }

        private static bool ParentFolderIsExcluded(
            [CanBeNull] string parent,
            [CanBeNull] IReadOnlyList<string> excludedFolderNames)
        {
            if (string.IsNullOrWhiteSpace(parent)
                || excludedFolderNames == null
                || excludedFolderNames.Count == 0)
            {
                return false;
            }

            return parent.Replace('\\', '/').Split('/').Any(part =>
                excludedFolderNames.Any(name =>
                    part.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || FolderNameMatchesGuideToken(part, name)));
        }

        private static bool HasExcludedDescendantFolder(
            [NotNull] IReadOnlyList<string> fileList,
            [CanBeNull] string parent,
            [CanBeNull] IReadOnlyList<string> excludedFolderNames)
        {
            if (excludedFolderNames == null || excludedFolderNames.Count == 0)
            {
                return false;
            }

            string prefix = string.IsNullOrEmpty(parent)
                ? string.Empty
                : parent.Replace('\\', '/').TrimEnd('/') + "/";

            foreach (string path in fileList)
            {
                string entry = path.Replace('\\', '/');
                if (prefix.Length > 0 && !entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = prefix.Length == 0 ? entry : entry.Substring(prefix.Length);
                int slash = relative.IndexOf('/');
                if (slash <= 0)
                {
                    continue;
                }

                string childFolder = relative.Substring(0, slash);
                if (excludedFolderNames.Any(name =>
                        childFolder.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || FolderNameMatchesGuideToken(childFolder, name)))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddSimpleMoveInstruction(
            ModComponent component,
            IReadOnlyList<string> fileList,
            string extractedPath,
            string folderName,
            string excludedSubtree = null,
            IReadOnlyList<string> excludedFileNames = null,
            IReadOnlyList<string> excludedFolderNames = null
        )
        {
            string folderPathInArchive = string.IsNullOrEmpty(folderName) ? null : folderName;

            List<string> gameFileParents = GameFileParentDirectories(
                fileList,
                folderPathInArchive,
                includeDescendants: !string.IsNullOrEmpty(excludedSubtree));
            if (gameFileParents.Count == 0)
            {
                string location = string.IsNullOrEmpty(folderName) ? "root" : $"folder '{folderName}'";
                Logger.LogVerbose($"[AutoInstructionGenerator] Skipping Move instruction for {location} - no game files found");
                return;
            }

            // WildcardPathMatch requires the same number of path segments, so
            // `Korriban HR\*` does not match `Korriban HR/Override/file.tpc`. Emit one
            // Move per directory that actually holds game files (run 10: Ultimate HR
            // packs extracted 64-110 files, then Move found 0).
            foreach (string parent in gameFileParents)
            {
                string normalizedParent = (parent ?? string.Empty).Replace('\\', '/').Trim('/');
                string normalizedExcluded = (excludedSubtree ?? string.Empty).Replace('\\', '/').Trim('/');
                if (!string.IsNullOrEmpty(normalizedExcluded)
                    && (normalizedParent.Equals(normalizedExcluded, StringComparison.OrdinalIgnoreCase)
                        || normalizedParent.StartsWith(normalizedExcluded + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (ArchiveResolver.IsWrongGame(parent, DetectTargetGame())
                    || ArchiveResolver.IsWrongGame(folderName, DetectTargetGame()))
                {
                    Logger.LogVerbose(
                        $"[AutoInstructionGenerator] Skipping Move for other-game folder '{parent ?? folderName}'");
                    continue;
                }

                if (ParentFolderIsExcluded(normalizedParent, excludedFolderNames))
                {
                    Logger.LogVerbose(
                        $"[AutoInstructionGenerator] Skipping Move for '{parent}' - guide excluded this folder");
                    continue;
                }

                List<string> filesInParent = GameFilesInParent(fileList, parent);
                List<string> remaining = filesInParent;
                if (excludedFileNames != null && excludedFileNames.Count > 0)
                {
                    remaining = filesInParent
                        .Where(f => !excludedFileNames.Any(ex =>
                            Path.GetFileName(f).Equals(ex, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                }

                if (remaining.Count == 0)
                {
                    Logger.LogVerbose(
                        $"[AutoInstructionGenerator] Skipping Move for '{parent}' - all game files are EXCEPT-listed");
                    continue;
                }

                bool emitPerFile = remaining.Count < filesInParent.Count
                    || HasExcludedDescendantFolder(fileList, parent, excludedFolderNames);
                if (emitPerFile)
                {
                    foreach (string entry in remaining)
                    {
                        AddSingleFileMove(component, extractedPath, entry);
                    }

                    continue;
                }

                string sourcePath = string.IsNullOrEmpty(parent)
                    ? $@"<<modDirectory>>\{extractedPath}\*"
                    : $@"<<modDirectory>>\{extractedPath}\{parent.Replace('/', '\\')}\*";

                if (IsFolderAlreadyCoveredByInstructions(component, sourcePath))
                {
                    Logger.LogVerbose($"[AutoInstructionGenerator] Skipping Move instruction for '{sourcePath}' - already covered");
                    continue;
                }

                var moveInstruction = new Instruction
                {
                    Action = Instruction.ActionType.Move,
                    Source = new List<string> { sourcePath },
                    Destination = @"<<gameDirectory>>\Override",
                    Overwrite = true,
                    ExcludeNonGameContent = true,
                };
                moveInstruction.SetParentComponent(component);

                if (!InstructionAlreadyExists(component, moveInstruction))
                {
                    component.Instructions.Add(moveInstruction);
                    Logger.LogVerbose($"[AutoInstructionGenerator] Added Move instruction for '{sourcePath}'");
                }
            }
        }

        [NotNull]
        [ItemNotNull]
        private static List<string> GameFilesInParent(
            [NotNull] IReadOnlyList<string> fileList,
            [CanBeNull] string parent)
        {
            string normalizedParent = (parent ?? string.Empty).Replace('\\', '/').Trim('/');
            var files = new List<string>();
            foreach (string filePath in fileList)
            {
                string entryPath = filePath.Replace('\\', '/');
                if (entryPath.Split('/').Any(IsNonGamePayloadFolder) || !IsGameFile(Path.GetExtension(entryPath)))
                {
                    continue;
                }

                int lastSlash = entryPath.LastIndexOf('/');
                string entryParent = lastSlash < 0 ? string.Empty : entryPath.Substring(0, lastSlash);
                if (!entryParent.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                files.Add(entryPath);
            }

            return files;
        }

        private static void AddSingleFileMove(
            [NotNull] ModComponent component,
            [NotNull] string extractedPath,
            [NotNull] string entryPath)
        {
            string relative = entryPath.Replace('/', '\\');
            string sourcePath = $@"<<modDirectory>>\{extractedPath}\{relative}";
            if (IsFolderAlreadyCoveredByInstructions(component, sourcePath))
            {
                return;
            }

            var moveInstruction = new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string> { sourcePath },
                Destination = @"<<gameDirectory>>\Override",
                Overwrite = true,
                ExcludeNonGameContent = true,
            };
            moveInstruction.SetParentComponent(component);

            if (!InstructionAlreadyExists(component, moveInstruction))
            {
                component.Instructions.Add(moveInstruction);
                Logger.LogVerbose($"[AutoInstructionGenerator] Added Move instruction for '{sourcePath}'");
            }
        }

        /// <summary>
        /// Parent directories of game files under <paramref name="folderPath"/>, relative to the
        /// archive root. Flat files yield an empty string (the extraction folder itself).
        /// </summary>
        [NotNull]
        [ItemNotNull]
        private static List<string> GameFileParentDirectories(
            [NotNull] IReadOnlyList<string> fileList,
            [CanBeNull] string folderPath,
            bool includeDescendants = false)
        {
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string prefix = string.IsNullOrEmpty(folderPath)
                ? null
                : folderPath.Replace('\\', '/').TrimEnd('/') + "/";

            foreach (string filePath in fileList)
            {
                string entryPath = filePath.Replace('\\', '/');
                if (entryPath.Split('/').Any(IsNonGamePayloadFolder))
                {
                    continue;
                }

                if (!IsGameFile(Path.GetExtension(entryPath)))
                {
                    continue;
                }

                if (prefix == null && !includeDescendants)
                {
                    if (entryPath.IndexOf('/') >= 0)
                    {
                        continue;
                    }
                }
                else if (prefix != null && !entryPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int lastSlash = entryPath.LastIndexOf('/');
                parents.Add(lastSlash < 0 ? string.Empty : entryPath.Substring(0, lastSlash));
            }

            return parents.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <param name="fileList">
        /// The entry paths this analysis was built from. Handed back so callers that need both the
        /// analysis and the listing get them from a single enumeration - and, when the managed reader
        /// fails, from a single 7zip CLI invocation.
        /// </param>
        [NotNull]
        private static ArchiveAnalysis AnalyzeArchive(
            [NotNull] IArchive archive,
            [NotNull] string archivePath,
            [NotNull] out IReadOnlyList<string> fileList
        )
        {
            var analysis = new ArchiveAnalysis();
            var entryPaths = new List<string>();

            try
            {
                foreach (IArchiveEntry entry in archive.Entries)
                {
                    if (entry.IsDirectory)
                    {
                        continue;
                    }

                    string path = entry.Key.Replace('\\', '/');
                    if (IsInstallerRuntimeArtifact(path))
                    {
                        continue;
                    }

                    entryPaths.Add(path);
                    string[] pathParts = path.Split('/');

                    if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        analysis.ExecutableCandidates.Add(path);
                    }

                    if (pathParts.Any(p => p.Equals("tslpatchdata", StringComparison.OrdinalIgnoreCase)))
                    {
                        analysis.HasTslPatchData = true;

                        string fileName = Path.GetFileName(path);
                        if (fileName.Equals("namespaces.ini", StringComparison.OrdinalIgnoreCase))
                        {
                            // See the matching comment in AnalyzeArchiveFromFileList: keep the FIRST
                            // tslpatchdata tree's namespaces.ini, not the last, so this stays consistent
                            // with ReadNamespacesIni()/IniHelper.TraverseDirectories() (which also stops
                            // at the first namespaces.ini found) when an archive has multiple independent
                            // tslpatchdata trees. Gate on HasNamespacesIni (not
                            // string.IsNullOrEmpty(TslPatcherPath)): a changes.ini found earlier in a
                            // different tree must not block the first namespaces.ini from claiming this
                            // path - namespaces.ini always outranks a changes.ini-derived path, exactly
                            // once, on first sight.
                            if (!analysis.HasNamespacesIni)
                            {
                                analysis.TslPatcherPath = GetTslPatcherPath(path);
                            }

                            analysis.HasNamespacesIni = true;
                        }
                        else if (fileName.Equals("changes.ini", StringComparison.OrdinalIgnoreCase))
                        {
                            analysis.HasChangesIni = true;
                            if (string.IsNullOrEmpty(analysis.TslPatcherPath))
                            {
                                analysis.TslPatcherPath = GetTslPatcherPath(path);
                            }
                        }
                    }
                    else
                    {

                        string extension = Path.GetExtension(path).ToLowerInvariant();
                        if (!IsGameFile(extension))
                        {
                            continue;
                        }

                        analysis.HasSimpleOverrideFiles = true;

                        if (pathParts.Length == 1)
                        {

                            analysis.HasFlatFiles = true;
                        }
                        else if (pathParts.Length >= 2)
                        {

                            string topLevelFolder = pathParts[0];
                            if (!IsNonGamePayloadFolder(topLevelFolder)
                                && !analysis.FoldersWithFiles.Contains(topLevelFolder, StringComparer.Ordinal))
                            {
                                analysis.FoldersWithFiles.Add(topLevelFolder);
                            }
                        }
                    }
                }

                ResolvePatcherExecutable(analysis);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[AutoInstructionGenerator] SharpCompress failed to read archive entries: {ex.Message}");
                Logger.LogVerbose($"[AutoInstructionGenerator] Attempting to use 7zip CLI as fallback to list archive contents...");

                try
                {
                    Task<List<string>> fileListTask = ArchiveHelper.TryListArchiveWithSevenZipCliAsync(archivePath);
                    fileListTask.Wait();
                    List<string> cliFileList = fileListTask.Result?.Select(p => p.Replace('\\', '/')).ToList();

                    if (cliFileList != null && cliFileList.Count > 0)
                    {
                        Logger.LogVerbose($"[AutoInstructionGenerator] Successfully listed {cliFileList.Count} files using 7zip CLI");
                        analysis = AnalyzeArchiveFromFileList(cliFileList);

                        // The partial listing gathered before the managed reader gave up describes only
                        // part of the archive; the CLI listing is the complete one.
                        entryPaths = cliFileList;
                    }
                    else
                    {
                        Logger.LogError($"[AutoInstructionGenerator] 7zip CLI could not list archive contents (returned empty or null): {archivePath}");
                        Logger.LogError($"[AutoInstructionGenerator] Original SharpCompress error: {ex.Message}");
                        throw new InvalidOperationException(
                            $"Unable to read archive contents with either SharpCompress or 7zip CLI. " +
                            $"Archive may be corrupted or in an unsupported format: {archivePath}. " +
                            $"SharpCompress error: {ex?.Message}",
                            innerException: ex
                        );
                    }
                }
                catch (Exception fallbackEx)
                {
                    Logger.LogError($"[AutoInstructionGenerator] 7zip CLI threw exception while reading archive: {archivePath}");
                    Logger.LogError($"[AutoInstructionGenerator] SharpCompress error: {ex.Message}");
                    Logger.LogException(fallbackEx, "[AutoInstructionGenerator] 7zip CLI error");
                    throw new InvalidOperationException(
                        $"Unable to read archive contents with either SharpCompress or 7zip CLI. " +
                        $"Archive may be corrupted or in an unsupported format: {archivePath}. " +
                        $"SharpCompress error: {ex.Message}. " +
                        $"7zip CLI error: {fallbackEx.Message}",
                        innerException: fallbackEx
                    );
                }
            }

            fileList = entryPaths;
            return analysis;
        }

        /// <summary>
        /// Executables that ship inside a mod but are build tooling, never the installer the user is
        /// meant to run. nwnnsscomp.exe (the NWScript compiler) is bundled in many tslpatchdata folders
        /// and was previously selected as the patcher, producing sources such as
        /// "TSL/nwnnsscomp.exe" that exist in no archive.
        /// </summary>
        private static readonly HashSet<string> s_nonInstallerExecutables =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "nwnnsscomp.exe",
                "nwnnsscomp64.exe",
                "erf.exe",
                "rim.exe",
                "2dahelper.exe",
                "tlkhelper.exe",
                "unins000.exe",
                "uninstall.exe",
            };

        /// <summary>
        /// Returns the directory portion of an archive-relative path, or an empty string when the
        /// entry sits at the archive root. Uses '/' only; entries are normalized before this point.
        /// </summary>
        private static string ArchiveEntryDirectory([NotNull] string normalizedPath)
        {
            int lastSlash = normalizedPath.LastIndexOf('/');
            return lastSlash < 0 ? string.Empty : normalizedPath.Substring(0, lastSlash);
        }

        private static bool IsInsideTslPatchData([NotNull] string normalizedPath)
        {
            return normalizedPath.Split('/')
                .Any(p => p.Equals("tslpatchdata", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Selects the TSLPatcher installer from the executables seen during the scan.
        /// <para>
        /// The installer is the executable that sits BESIDE the tslpatchdata folder, not inside it.
        /// Every one of the 67 already-extracted TSLPatcher mods in the reference library follows this
        /// layout, and only a third of them are actually named "TSLPatcher.exe" - the rest use names
        /// like "INSTALL.exe", "installer.exe" or the mod's own title ("Juhani Appearance Overhaul.exe").
        /// The name therefore cannot be assumed and is always read from the archive. When no executable
        /// can be identified this returns without setting one: a fabricated name produces a Source path
        /// that exists in no archive and fails the whole install at validation, so an explicit
        /// unresolved component is strictly better than a guess.
        /// </para>
        /// </summary>
        private static void ResolvePatcherExecutable([NotNull] ArchiveAnalysis analysis)
        {
            if (!analysis.HasTslPatchData || analysis.ExecutableCandidates.Count == 0)
            {
                return;
            }

            List<string> eligible = analysis.ExecutableCandidates
                .Where(p => !IsInsideTslPatchData(p))
                .Where(p => !s_nonInstallerExecutables.Contains(Path.GetFileName(p)))
                .ToList();

            if (eligible.Count == 0)
            {
                return;
            }

            string patcherRoot = analysis.TslPatcherPath ?? string.Empty;

            string atRoot = eligible.FirstOrDefault(
                p => ArchiveEntryDirectory(p).Equals(patcherRoot, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(atRoot))
            {
                analysis.PatcherExecutable = atRoot;
            }
        }

        private static string GetTslPatcherPath(string iniPath)
        {

            string[] parts = iniPath.Split(s_pathSeparators, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Equals("tslpatchdata", StringComparison.OrdinalIgnoreCase))
                {

                    return string.Join("/", parts.Take(i));
                }
            }
            return string.Empty;
        }

        private static bool IsGameFile(string extension)
        {

            var gameExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".2da", ".are", ".bik",
                ".dds", ".dlg", ".erf",
                ".git", ".gui", ".ifo",
                ".mod", ".jrl", ".lip",
                ".lyt", ".mdl", ".mdx",
                ".ncs", ".pth", ".rim",
                ".ssf", ".tga", ".tlk",
                ".txi", ".tpc", ".utc",
                ".utd", ".ute", ".uti",
                ".utm", ".utp", ".uts",
                ".utw", ".vis", ".wav",
            };

            return gameExtensions.Contains(extension);
        }

        private static bool FolderContainsGameFiles(
            [NotNull] IReadOnlyList<string> fileList,
            [CanBeNull] string folderPath
        )
        {
            try
            {
                foreach (string filePath in fileList)
                {
                    string entryPath = filePath.Replace('\\', '/');
                    string extension = Path.GetExtension(entryPath);

                    if (!IsGameFile(extension))
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(folderPath))
                    {
                        if (!entryPath.Contains('/') && !entryPath.Contains('\\'))
                        {
                            return true;
                        }
                    }
                    else
                    {
                        string normalizedFolderPath = folderPath.Replace('\\', '/');
                        if (!normalizedFolderPath.EndsWith("/", StringComparison.Ordinal))
                        {
                            normalizedFolderPath += "/";
                        }

                        if (entryPath.StartsWith(normalizedFolderPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // The file list is already materialized by the caller, so this only guards against
                // malformed entry paths. Treating an unreadable list as "contains game files" would
                // fabricate a Move instruction, so report and fall through to false.
                Logger.LogWarning($"[AutoInstructionGenerator] Failed to inspect folder contents: {ex.Message}");
            }

            return false;
        }

        private sealed class ArchiveAnalysis
        {
            public bool HasTslPatchData { get; set; }
            public bool HasNamespacesIni { get; set; }
            public bool HasChangesIni { get; set; }
            public bool HasSimpleOverrideFiles { get; set; }
            public bool HasFlatFiles { get; set; }
            public List<string> FoldersWithFiles { get; set; } = new List<string>();
            public string TslPatcherPath { get; set; } = string.Empty;
            public string PatcherExecutable { get; set; } = string.Empty;

            /// <summary>
            /// Every '.exe' seen while scanning, as stored in the archive. The installer cannot be
            /// picked while scanning because it is identified by its position relative to the
            /// tslpatchdata folder, which may be discovered after the executable itself.
            /// </summary>
            public List<string> ExecutableCandidates { get; } = new List<string>();
        }
    }

    /// <summary>
    /// Represents the result of attempting to generate instructions for a component
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0048:File name must match type name", Justification = "<Pending>")]
    public class GenerationResult
    {
        public Guid ComponentGuid { get; set; }
        public string ComponentName { get; set; }
        public bool Success { get; set; }
        public int InstructionsGenerated { get; set; }
        public string SkipReason { get; set; }
        public string ResolvedArchivePath { get; set; }
        public string ResolutionTier { get; set; }
        public string ResolutionReason { get; set; }
    }
}
