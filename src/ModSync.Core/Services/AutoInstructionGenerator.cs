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
                    result.SkipReason = "Component already has instructions";
                    return result;
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

                var allArchives = ArchiveHelper.DefaultArchiveSearchPatterns
                    .SelectMany(ext => MainConfig.SourcePath.GetFiles(ext, SearchOption.TopDirectoryOnly))
                    .Where(f => f.Exists)
                    .ToList();

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

                int instructionCountBefore = component.Instructions.Count;
                bool generated = GenerateFromArchiveOrExtractedFolder(component, resolution.Archive);

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

            ArchiveResolver.GameMarker targetGame =
                string.Equals(MainConfig.TargetGame, "K1", StringComparison.OrdinalIgnoreCase)
                    ? ArchiveResolver.GameMarker.Kotor1
                    : string.Equals(MainConfig.TargetGame, "TSL", StringComparison.OrdinalIgnoreCase)
                        ? ArchiveResolver.GameMarker.Kotor2
                        : ArchiveResolver.GameMarker.None;

            return ArchiveResolver.Resolve(component.Name, componentUrls, allArchives, targetGame);
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
            if (component is null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            try
            {
                if (component.Instructions.Count > 0)
                {
                    return false;
                }

                if (component.ResourceRegistry.Count == 0)
                {
                    return false;
                }

                if (MainConfig.SourcePath is null || !MainConfig.SourcePath.Exists)
                {
                    return false;
                }

                var allArchives = ArchiveHelper.DefaultArchiveSearchPatterns
                    .SelectMany(ext => MainConfig.SourcePath.GetFiles(ext, SearchOption.TopDirectoryOnly))
                    .Where(f => f.Exists)
                    .ToList();

                if (allArchives.Count == 0)
                {
                    Logger.LogVerbose(
                        $"[TryGenerateInstructions] Component '{component.Name}': No archives found in directory");
                    return false;
                }

                ArchiveResolution resolution = ResolveArchiveFor(component, allArchives);

                if (!resolution.IsResolved)
                {
                    LogUnresolved(component.Name, resolution);
                    return false;
                }

                LogResolved(component.Name, resolution);

                bool generated = GenerateFromArchiveOrExtractedFolder(component, resolution.Archive);
                if (generated)
                {
                    component.IsDownloaded = true;
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
                if (archive is null || stream is null)
                {
                    return new List<string>();
                }

                using (stream)
                using (archive)
                {
                    return SafeListArchiveEntries(archive, archivePath).ToList();
                }
            }
            catch (Exception ex)
            {
                Logger.LogVerbose($"[AutoInstructionGenerator] Could not list '{archivePath}': {ex.Message}");
                return new List<string>();
            }
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
        private static bool GenerateFromArchiveOrExtractedFolder(
            [NotNull] ModComponent component,
            [NotNull] FileInfo matchingArchive)
        {
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
            var delDuplicateInstruction = new Instruction
            {
                Action = Instruction.ActionType.DelDuplicate,
                Source = new List<string>(),
                Arguments = ".tpc",
                Overwrite = true,
            };
            delDuplicateInstruction.SetParentComponent(component);

            if (!InstructionAlreadyExists(component, delDuplicateInstruction))
            {
                component.Instructions.Add(delDuplicateInstruction);
                Logger.LogVerbose("[AutoInstructionGenerator] Added DelDuplicate instruction for Remove Duplicate TGA/TPC mod");
                return true;
            }

            Logger.LogVerbose("[AutoInstructionGenerator] DelDuplicate instruction already exists for Remove Duplicate TGA/TPC mod");
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
                    return false;
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
                    Logger.LogWarning("[AutoInstructionGenerator] Leaving it untouched; instructions for this component must be resolved another way.");
                }
                else if (isExeFile)
                {
                    Logger.LogVerbose($"[AutoInstructionGenerator] Failed to extract EXE file '{Path.GetFileName(archivePath)}', creating Execute instruction instead");
                    return GenerateExecuteInstruction(component, archivePath);
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
                        analysis.HasNamespacesIni = true;
                        analysis.TslPatcherPath = GetTslPatcherPath(normalizedPath);
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
                        if (!analysis.FoldersWithFiles.Contains(topLevelFolder, StringComparer.Ordinal))
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
            bool directoryMode = extractedPathOverride != null;
            string archiveFileName = directoryMode
                ? extractArchiveFileName
                : Path.GetFileName(archivePath);

            bool hasArchiveToExtract = !string.IsNullOrEmpty(archiveFileName);
            string extractedPath = extractedPathOverride
                ?? archiveFileName.Replace(Path.GetExtension(archiveFileName), "");

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
                var overrideFolders = analysis.FoldersWithFiles
                    .Where(f => !IsTslPatcherFolder(f, analysis))
                    .ToList();

                if (overrideFolders.Count > 1)
                {
                    AddMultiFolderChooseInstructions(component, fileList, extractedPath, overrideFolders);
                }
                else if (overrideFolders.Count == 1)
                {
                    AddSimpleMoveInstruction(component, fileList, extractedPath, overrideFolders[0]);
                }
                else if (analysis.HasFlatFiles)
                {
                    AddSimpleMoveInstruction(component, fileList, extractedPath, folderName: null);
                }
            }

            if (analysis.HasTslPatchData && analysis.HasSimpleOverrideFiles)
            {
                component.InstallationMethod = "Hybrid (TSLPatcher + Loose Files)";
            }
            else if (analysis.HasTslPatchData)
            {
                component.InstallationMethod = "TSLPatcher";
            }
            else if (analysis.HasSimpleOverrideFiles)
            {
                component.InstallationMethod = "Loose-File Mod";
            }

            int consolidatedCount = ConsolidateDuplicateOptions(component);
            if (consolidatedCount > 0)
            {
                Logger.LogVerbose($"[AutoInstructionGenerator] Consolidated and removed {consolidatedCount} duplicate option(s)");
            }

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
                Action = isArchive ? Instruction.ActionType.Extract : Instruction.ActionType.Move,
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

        private static void AddSimpleMoveInstruction(
            ModComponent component,
            IReadOnlyList<string> fileList,
            string extractedPath,
            string folderName
        )
        {
            string folderPathInArchive = string.IsNullOrEmpty(folderName) ? null : folderName;

            if (!FolderContainsGameFiles(fileList, folderPathInArchive))
            {
                string location = string.IsNullOrEmpty(folderName) ? "root" : $"folder '{folderName}'";
                Logger.LogVerbose($"[AutoInstructionGenerator] Skipping Move instruction for {location} - no game files found");
                return;
            }

            string sourcePath = string.IsNullOrEmpty(folderName)
                ? $@"<<modDirectory>>\{extractedPath}\*"
                : $@"<<modDirectory>>\{extractedPath}\{folderName}\*";

            if (IsFolderAlreadyCoveredByInstructions(component, sourcePath))
            {
                string location = string.IsNullOrEmpty(folderName) ? "root" : $"folder '{folderName}'";
                Logger.LogVerbose($"[AutoInstructionGenerator] Skipping Move instruction for {location} - already covered by existing instructions");
                return;
            }

            var moveInstruction = new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string> { sourcePath },
                Destination = @"<<gameDirectory>>\Override",
                Overwrite = true,
            };
            moveInstruction.SetParentComponent(component);

            if (!InstructionAlreadyExists(component, moveInstruction))
            {
                component.Instructions.Add(moveInstruction);
                Logger.LogVerbose($"[AutoInstructionGenerator] Added Move instruction for '{sourcePath}'");
            }
            else
            {
                Logger.LogVerbose($"[AutoInstructionGenerator] Move instruction for '{sourcePath}' already exists, skipping");
            }
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
                            analysis.HasNamespacesIni = true;
                            analysis.TslPatcherPath = GetTslPatcherPath(path);
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
                            if (!analysis.FoldersWithFiles.Contains(topLevelFolder, StringComparer.Ordinal))
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
    }
}
