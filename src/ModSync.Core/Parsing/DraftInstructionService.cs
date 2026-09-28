// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using JetBrains.Annotations;

namespace ModSync.Core.Parsing
{
    /// <summary>
    /// Result of drafting instructions for a single component from its natural-language prose.
    /// </summary>
    public sealed class DraftInstructionResult
    {
        [NotNull]
        public ModComponent Component { get; }

        public int DraftInstructionCount { get; }

        /// <summary>
        /// Directions prose that contained an action verb but matched no known instruction pattern.
        /// Never populated with commentary/informational prose - only genuine unparsed gaps. Callers
        /// should render these (e.g. "N of M directions produced no draft") rather than drop them silently.
        /// </summary>
        [NotNull]
        [ItemNotNull]
        public IReadOnlyList<string> UnparsedGaps { get; }

        public bool HasUnparsedGaps => UnparsedGaps.Count > 0;

        /// <summary>
        /// Human-readable notes for instructions drafted from a nested conditional clause (the
        /// K2CP+HD-Visas pattern: "delete these files; if also using X, additionally delete these").
        /// Each note names the mod the draft is conditional on - the drafted instruction is never
        /// auto-applied unconditionally, but this distinguishes it from an ordinary unconditional draft.
        /// </summary>
        [NotNull]
        [ItemNotNull]
        public IReadOnlyList<string> ConditionalDrafts { get; }

        public bool HasConditionalDrafts => ConditionalDrafts.Count > 0;

        public DraftInstructionResult(
            [NotNull] ModComponent component,
            int draftInstructionCount,
            [CanBeNull][ItemNotNull] IReadOnlyList<string> unparsedGaps = null,
            [CanBeNull][ItemNotNull] IReadOnlyList<string> conditionalDrafts = null)
        {
            Component = component ?? throw new ArgumentNullException(nameof(component));
            DraftInstructionCount = draftInstructionCount;
            UnparsedGaps = unparsedGaps ?? Array.Empty<string>();
            ConditionalDrafts = conditionalDrafts ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Wires <see cref="NaturalLanguageInstructionParser"/> into guide ingestion: converts a component's
    /// natural-language <see cref="ModComponent.Directions"/> prose into draft <see cref="Instruction"/> objects.
    /// Drafts are sandboxed (all paths placeholder-prefixed) and must always be flagged for user review by
    /// callers - they are never auto-trusted. Unparseable prose degrades gracefully to no drafts.
    /// </summary>
    public static class DraftInstructionService
    {
        /// <summary>
        /// Review-flag message callers should surface (e.g. as a serialized validation issue or a log warning)
        /// for every component that received draft instructions.
        /// </summary>
        [NotNull]
        public const string ReviewFlagMessage =
            "DRAFT INSTRUCTIONS: parsed from guide prose by the natural-language importer. Review before installing - never auto-trusted.";

        [NotNull] internal const string ModDirectoryPlaceholder = "<<modDirectory>>";
        [NotNull] private const string KotorDirectoryPlaceholder = "<<kotorDirectory>>";
        [NotNull] private const string LegacyGameDirectoryPlaceholder = "<<gameDirectory>>";

        /// <summary>
        /// Matches KOTOR loose-file names mentioned in guide prose (e.g. 153sion.dlg).
        /// </summary>
        [NotNull]
        private static readonly Regex s_looseFileNamePattern = new Regex(
            @"\b([\w\.\-]+\.(?:dlg|2da|tga|tpc|utc|uti|utm|utd|ute|uts|utw|ssf|bwm|mdl|mdx|txi|lip|lyt|vis|pth|ncs|gui))\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Generates draft instructions for every component that has natural-language Directions prose
        /// but no authored instructions. Components that already have instructions are never touched.
        /// </summary>
        /// <returns>
        /// One result per component that has Directions prose to draft from - including components where
        /// zero instructions were successfully drafted, so callers can render unparsed gaps for review.
        /// </returns>
        [NotNull]
        [ItemNotNull]
        public static IReadOnlyList<DraftInstructionResult> GenerateDraftInstructions(
            [NotNull][ItemNotNull] IEnumerable<ModComponent> components,
            [CanBeNull] Action<string> logInfo = null,
            [CanBeNull] Action<string> logVerbose = null)
        {
            if (components is null)
            {
                throw new ArgumentNullException(nameof(components));
            }

            Action<string> info = logInfo ?? (_ => { });
            Action<string> verbose = logVerbose ?? (_ => { });
            var parser = new NaturalLanguageInstructionParser(info, verbose);
            var results = new List<DraftInstructionResult>();

            foreach (ModComponent component in components)
            {
                if (component.Instructions.Count > 0)
                {
                    continue;
                }

                int added = 0;
                bool hasDirections = !string.IsNullOrWhiteSpace(component.Directions);
                IReadOnlyList<string> unparsedGaps = Array.Empty<string>();
                IReadOnlyList<string> conditionalDrafts = Array.Empty<string>();

                if (hasDirections)
                {
                    ObservableCollection<Instruction> parsed;
                    try
                    {
                        parsed = parser.ParseInstructions(
                            component.Directions,
                            string.IsNullOrWhiteSpace(component.DownloadInstructions) ? null : component.DownloadInstructions,
                            component,
                            out unparsedGaps,
                            out conditionalDrafts);
                    }
                    catch (Exception ex)
                    {
                        // Graceful degradation: unparseable prose may still get an InstallationMethod fallback.
                        verbose($"[DraftInstructions] Failed to parse prose for '{component.Name}': {ex.Message}");
                        parsed = new ObservableCollection<Instruction>();
                        unparsedGaps = Array.Empty<string>();
                        conditionalDrafts = Array.Empty<string>();
                    }

                    foreach (Instruction instruction in parsed)
                    {
                        if (!TrySanitizeInstruction(instruction))
                        {
                            verbose($"[DraftInstructions] Dropped non-sandboxed draft ({instruction.Action}) for '{component.Name}'");
                            continue;
                        }

                        instruction.SetParentComponent(component);
                        component.Instructions.Add(instruction);
                        added++;
                    }
                }

                // K2 Full and similar guides often describe TSLPatcher/HoloPatcher installs via
                // Installation Method alone, or preference prose ("recommend the X option") that yields
                // no regex hits. Still draft a sandboxed Patcher (or loose-file Move) for review.
                if (added == 0)
                {
                    Instruction fallback = TryCreateMethodFallbackInstruction(component);
                    if (fallback != null && TrySanitizeInstruction(fallback))
                    {
                        fallback.SetParentComponent(component);
                        component.Instructions.Add(fallback);
                        added++;
                        verbose($"[DraftInstructions] Applied InstallationMethod fallback ({fallback.Action}) for '{component.Name}'");
                    }
                }

                if (added > 0)
                {
                    ApplyReviewFlag(component);
                    info($"[DraftInstructions] Drafted {added} instruction(s) from prose for '{component.Name}' - flagged for review.");
                }

                if (unparsedGaps.Count > 0)
                {
                    info($"[DraftInstructions] {unparsedGaps.Count} direction(s) for '{component.Name}' produced no draft - review needed.");
                }

                if (conditionalDrafts.Count > 0)
                {
                    ApplyConditionalDraftNote(component, conditionalDrafts);
                    info($"[DraftInstructions] {conditionalDrafts.Count} draft(s) for '{component.Name}' are conditional on another mod - review needed.");
                }

                // One result per component with Directions prose (so callers can render unparsed gaps),
                // plus any component that only received an InstallationMethod fallback draft.
                if (hasDirections || added > 0)
                {
                    results.Add(new DraftInstructionResult(component, added, unparsedGaps, conditionalDrafts));
                }
            }

            return results;
        }

        /// <summary>
        /// When prose does not parse into instructions, invent a minimal sandboxed draft from
        /// <see cref="ModComponent.InstallationMethod"/> and/or patcher keywords in Directions.
        /// </summary>
        [CanBeNull]
        private static Instruction TryCreateMethodFallbackInstruction([NotNull] ModComponent component)
        {
            string method = component.InstallationMethod ?? string.Empty;
            string directions = component.Directions ?? string.Empty;
            string combined = method + " " + directions;

            if (LooksLikePatcherInstall(combined))
            {
                return new Instruction
                {
                    Action = Instruction.ActionType.Patcher,
                    Source = new List<string> { ModDirectoryPlaceholder },
                    Destination = KotorDirectoryPlaceholder,
                    Overwrite = true,
                };
            }

            if (LooksLikeLooseFileInstall(method)
                && !LooksLikePatcherInstall(directions)
                && method.IndexOf("executable", StringComparison.OrdinalIgnoreCase) < 0)
            {
                string destination = directions.IndexOf("movies", StringComparison.OrdinalIgnoreCase) >= 0
                    ? KotorDirectoryPlaceholder + @"\Movies"
                    : KotorDirectoryPlaceholder + @"\Override";

                Match fileMatch = s_looseFileNamePattern.Match(directions);
                List<string> sources = fileMatch.Success
                    ? BuildLooseFileMoveSources(fileMatch.Groups[1].Value)
                    : new List<string> { ModDirectoryPlaceholder + @"\*" };

                return new Instruction
                {
                    Action = Instruction.ActionType.Move,
                    Source = sources,
                    Destination = destination,
                    Overwrite = directions.IndexOf("do not overwrite", StringComparison.OrdinalIgnoreCase) < 0
                        && directions.IndexOf("don't overwrite", StringComparison.OrdinalIgnoreCase) < 0,
                };
            }

            if (method.IndexOf("executable", StringComparison.OrdinalIgnoreCase) >= 0
                || directions.IndexOf("executable", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new Instruction
                {
                    Action = Instruction.ActionType.Execute,
                    Source = new List<string> { ModDirectoryPlaceholder + @"\*" },
                    Destination = string.Empty,
                    Overwrite = true,
                };
            }

            return null;
        }

        private static bool LooksLikePatcherInstall([NotNull] string text)
        {
            string lower = text.ToLowerInvariant();
            return lower.IndexOf("tslpatcher", StringComparison.Ordinal) >= 0
                || lower.IndexOf("holopatcher", StringComparison.Ordinal) >= 0
                || lower.IndexOf("multi-run", StringComparison.Ordinal) >= 0;
        }

        private static bool LooksLikeLooseFileInstall([NotNull] string text)
        {
            string lower = text.ToLowerInvariant();
            return lower.IndexOf("loose-file", StringComparison.Ordinal) >= 0
                || lower.IndexOf("loose file", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Builds sandboxed Move sources for a named loose file, including nested paths after Extract.
        /// </summary>
        [NotNull]
        internal static List<string> BuildLooseFileMoveSources([NotNull] string fileName, int maxNestedDepth = 3)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("File name is required.", nameof(fileName));
            }

            string trimmed = fileName.Trim().Trim('"', '\'');
            var sources = new List<string> { $"{ModDirectoryPlaceholder}\\{trimmed}" };

            for (int depth = 1; depth <= maxNestedDepth; depth++)
            {
                sources.Add($"{ModDirectoryPlaceholder}\\{string.Join("\\", Enumerable.Repeat("*", depth))}\\{trimmed}");
            }

            return sources;
        }

        /// <summary>
        /// Expands existing Move sources with nested mod-directory search paths for a named file.
        /// </summary>
        [NotNull]
        internal static List<string> ExpandLooseFileMoveSources(
            [CanBeNull] IEnumerable<string> existingSources,
            [NotNull] string fileName,
            int maxNestedDepth = 3)
        {
            var merged = new List<string>();
            if (existingSources != null)
            {
                merged.AddRange(existingSources.Where(source => !string.IsNullOrWhiteSpace(source)));
            }

            foreach (string source in BuildLooseFileMoveSources(fileName, maxNestedDepth))
            {
                if (!merged.Any(existing => string.Equals(existing, source, StringComparison.OrdinalIgnoreCase)))
                {
                    merged.Add(source);
                }
            }

            return merged;
        }

        /// <summary>
        /// Builds sandboxed Move sources for a named folder (optionally path-like prose), including nested
        /// paths after Extract and slug/fuzzy variants when archive folders differ from guide wording.
        /// </summary>
        [NotNull]
        internal static List<string> BuildFolderMoveSources([NotNull] string folderPhrase, int maxNestedDepth = 3)
        {
            if (string.IsNullOrWhiteSpace(folderPhrase))
            {
                throw new ArgumentException("Folder phrase is required.", nameof(folderPhrase));
            }

            string trimmed = folderPhrase.Trim().Trim('"', '\'', '.', ' ', ',', ';');
            trimmed = Regex.Replace(trimmed, @"^\s*the\s+", string.Empty, RegexOptions.IgnoreCase);

            var folderCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                trimmed.Replace('/', '\\'),
            };

            foreach (string slug in GenerateFolderSlugCandidates(trimmed))
            {
                folderCandidates.Add(slug);
            }

            var sources = new List<string>();
            foreach (string candidate in folderCandidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                sources.Add($"{ModDirectoryPlaceholder}\\{candidate}\\*");

                for (int depth = 1; depth <= maxNestedDepth; depth++)
                {
                    sources.Add($"{ModDirectoryPlaceholder}\\{string.Join("\\", Enumerable.Repeat("*", depth))}\\{candidate}\\*");
                }

                string fuzzySegment = BuildFuzzyFolderSegmentPattern(candidate);
                if (!string.IsNullOrWhiteSpace(fuzzySegment))
                {
                    for (int depth = 1; depth <= maxNestedDepth; depth++)
                    {
                        sources.Add($"{ModDirectoryPlaceholder}\\{string.Join("\\", Enumerable.Repeat("*", depth))}\\{fuzzySegment}\\*");
                    }
                }
            }

            return sources;
        }

        /// <summary>
        /// Expands existing Move sources with nested folder search paths for guide prose folder names.
        /// </summary>
        [NotNull]
        internal static List<string> ExpandFolderMoveSources(
            [CanBeNull] IEnumerable<string> existingSources,
            [NotNull] string folderPhrase,
            int maxNestedDepth = 3)
        {
            var merged = new List<string>();
            if (existingSources != null)
            {
                merged.AddRange(existingSources.Where(source => !string.IsNullOrWhiteSpace(source)));
            }

            foreach (string source in BuildFolderMoveSources(folderPhrase, maxNestedDepth))
            {
                if (!merged.Any(existing => string.Equals(existing, source, StringComparison.OrdinalIgnoreCase)))
                {
                    merged.Add(source);
                }
            }

            return merged;
        }

        [NotNull]
        private static IEnumerable<string> GenerateFolderSlugCandidates([NotNull] string phrase)
        {
            string[] segments = phrase
                .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => segment.Trim())
                .Where(segment => segment.Length > 0)
                .ToArray();

            if (segments.Length == 0)
            {
                yield break;
            }

            yield return string.Join("\\", segments);

            if (segments.Length >= 2)
            {
                string head = segments[0].Replace(" ", string.Empty).ToLowerInvariant();
                string tail = string.Join(" ", segments.Skip(1)).ToLowerInvariant();
                tail = Regex.Replace(tail, @"\bsith\s+lord\b", "sithlord", RegexOptions.IgnoreCase);
                yield return $"{head}_{tail}";
            }
        }

        [CanBeNull]
        private static string BuildFuzzyFolderSegmentPattern([NotNull] string folderCandidate)
        {
            string segment = folderCandidate;
            int lastSeparator = Math.Max(folderCandidate.LastIndexOf('\\'), folderCandidate.LastIndexOf('/'));
            if (lastSeparator >= 0 && lastSeparator < folderCandidate.Length - 1)
            {
                segment = folderCandidate.Substring(lastSeparator + 1);
            }

            string merged = Regex.Replace(segment.ToLowerInvariant(), @"\bsith\s+lord\b", "sithlord");
            string[] tokens = merged
                .Split(new[] { ' ', '_', '-', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.Length > 3 || string.Equals(token, "fixes", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (tokens.Length == 0)
            {
                return null;
            }

            return "*" + string.Join("*", tokens) + "*";
        }

        /// <summary>
        /// Normalizes placeholders and enforces the path-sandboxing rules on a draft instruction:
        /// every Source entry and any non-empty Destination must start with
        /// <c>&lt;&lt;modDirectory&gt;&gt;</c> or <c>&lt;&lt;kotorDirectory&gt;&gt;</c>,
        /// followed by a separator when a relative path is present, and must not contain
        /// <c>..</c> segments, rooted segments, or drive letters after the placeholder.
        /// </summary>
        /// <returns>false when the instruction cannot be made sandbox-safe and must be dropped.</returns>
        public static bool TrySanitizeInstruction([NotNull] Instruction instruction)
        {
            if (instruction is null)
            {
                throw new ArgumentNullException(nameof(instruction));
            }

            List<string> sanitizedSources = instruction.Source
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Select(NormalizePlaceholders)
                .Select(SandboxBareFilenameSource)
                .Where(IsSandboxedPath)
                .ToList();

            string destination = NormalizePlaceholders(instruction.Destination);

            // Copy-as / rename targets are bare filenames in guide prose ("rename it PMBJ01.tga").
            // Sandbox rules require a placeholder root; park the new name under modDirectory so the
            // draft survives review and merge with Extract (rename runs in the extract folder before Move).
            if (!string.IsNullOrEmpty(destination)
                && !IsSandboxedPath(destination)
                && (instruction.Action == Instruction.ActionType.Rename
                    || instruction.Action == Instruction.ActionType.Copy)
                && IsBareFilenameDestination(destination))
            {
                destination = ModDirectoryPlaceholder + @"\" + destination.Trim().Trim('"', '\'');
            }

            if (!string.IsNullOrEmpty(destination) && !IsSandboxedPath(destination))
            {
                return false;
            }

            bool sourceRequired =
                instruction.Action == Instruction.ActionType.Move ||
                instruction.Action == Instruction.ActionType.Copy ||
                instruction.Action == Instruction.ActionType.Delete ||
                instruction.Action == Instruction.ActionType.Rename ||
                instruction.Action == Instruction.ActionType.Extract ||
                instruction.Action == Instruction.ActionType.Execute ||
                instruction.Action == Instruction.ActionType.Patcher;

            if (sourceRequired && sanitizedSources.Count == 0)
            {
                return false;
            }

            if (RequiresDestination(instruction.Action) && string.IsNullOrWhiteSpace(destination))
            {
                return false;
            }

            instruction.Source = sanitizedSources;
            instruction.Destination = destination;
            return true;
        }

        /// <summary>
        /// Attaches <see cref="ReviewFlagMessage"/> to a component that received draft instructions so
        /// install-review surfaces (GUI warnings and CLI validation-issue serialization) can see it.
        /// Does not overwrite an existing identical flag.
        /// </summary>
        public static void ApplyReviewFlag([NotNull] ModComponent component)
        {
            if (component is null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            AppendWarningIfMissing(component, ReviewFlagMessage, prepend: true);
        }

        /// <summary>
        /// Appends conditional-draft notes (see <see cref="DraftInstructionResult.ConditionalDrafts"/>) to a
        /// component's <see cref="ModComponent.InstallationWarning"/> so a reviewer sees, alongside the
        /// general draft review flag, exactly which drafted instructions are conditional on another mod.
        /// Does not duplicate notes already present.
        /// </summary>
        private static void ApplyConditionalDraftNote(
            [NotNull] ModComponent component,
            [NotNull][ItemNotNull] IReadOnlyList<string> conditionalDrafts)
        {
            foreach (string note in conditionalDrafts)
            {
                AppendWarningIfMissing(component, note, prepend: false);
            }
        }

        /// <summary>
        /// Adds <paramref name="text"/> to a component's <see cref="ModComponent.InstallationWarning"/>
        /// unless it's already present. <paramref name="prepend"/> controls whether new text goes before
        /// or after any existing warning content.
        /// </summary>
        private static void AppendWarningIfMissing([NotNull] ModComponent component, [NotNull] string text, bool prepend)
        {
            if (string.IsNullOrWhiteSpace(component.InstallationWarning))
            {
                component.InstallationWarning = text;
                return;
            }

            if (component.InstallationWarning.IndexOf(text, StringComparison.Ordinal) < 0)
            {
                component.InstallationWarning = prepend
                    ? text + Environment.NewLine + component.InstallationWarning
                    : component.InstallationWarning + Environment.NewLine + text;
            }
        }

        private static bool RequiresDestination(Instruction.ActionType action)
        {
            // Move always needs a destination folder. Copy-as halves from guide prose
            // ("copy the file 'X' and make a duplicate") legitimately have no Destination
            // until a following "rename this duplicate to Y" clause is coalesced.
            return action == Instruction.ActionType.Move;
        }

        private static bool IsBareFilenameDestination([NotNull] string destination)
        {
            string dest = destination.Trim().Trim('"', '\'');
            return dest.IndexOf("<<", StringComparison.Ordinal) < 0
                && Path.HasExtension(dest)
                && dest.IndexOf('\\') < 0
                && dest.IndexOf('/') < 0
                && dest.IndexOf("..", StringComparison.Ordinal) < 0;
        }

        /// <summary>
        /// Guide copy-as sources are often bare stems ("LDA_EHawk01") without a placeholder.
        /// Prefix them so sandbox filtering does not drop the draft before coalesce.
        /// </summary>
        [NotNull]
        private static string SandboxBareFilenameSource([NotNull] string source)
        {
            if (IsSandboxedPath(source))
            {
                return source;
            }

            string leaf = source.Trim().Trim('"', '\'');
            if (leaf.IndexOf("<<", StringComparison.Ordinal) >= 0
                || leaf.IndexOf('\\') >= 0
                || leaf.IndexOf('/') >= 0
                || leaf.IndexOf("..", StringComparison.Ordinal) >= 0
                || leaf.IndexOf(' ') >= 0)
            {
                return source;
            }

            // Accept stems with or without an extension (Ebon Hawk quotes LDA_EHawk01 without .tga).
            if (leaf.Length >= 3 && leaf.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.'))
            {
                return ModDirectoryPlaceholder + @"\" + leaf;
            }

            return source;
        }

        [NotNull]
        private static string NormalizePlaceholders([CanBeNull] string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            return path.Replace(LegacyGameDirectoryPlaceholder, KotorDirectoryPlaceholder);
        }

        /// <summary>
        /// Returns true when <paramref name="path"/> is confined to a placeholder root:
        /// starts with <c>&lt;&lt;modDirectory&gt;&gt;</c> or <c>&lt;&lt;kotorDirectory&gt;&gt;</c>,
        /// optionally followed by <c>/</c> or <c>\</c> and relative segments that do not escape
        /// (mirrors <c>FomodToComponentMapper.NormalizeRelativePath</c> rejection rules).
        /// </summary>
        internal static bool IsSandboxedPath([NotNull] string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string placeholder;
            if (path.StartsWith(ModDirectoryPlaceholder, StringComparison.Ordinal))
            {
                placeholder = ModDirectoryPlaceholder;
            }
            else if (path.StartsWith(KotorDirectoryPlaceholder, StringComparison.Ordinal))
            {
                placeholder = KotorDirectoryPlaceholder;
            }
            else
            {
                return false;
            }

            if (path.Length == placeholder.Length)
            {
                return true;
            }

            char separator = path[placeholder.Length];
            if (separator != '/' && separator != '\\')
            {
                // Require an explicit separator after the placeholder (reject "<<modDirectory>>../x").
                return false;
            }

            string remainder = path.Substring(placeholder.Length + 1).Replace('\\', '/');
            foreach (string segment in remainder.Split('/'))
            {
                if (segment.Length == 0 || string.Equals(segment, ".", StringComparison.Ordinal))
                {
                    continue;
                }

                // Reject traversal, drive letters (e.g. "C:"), and rooted segments after the placeholder.
                if (string.Equals(segment, "..", StringComparison.Ordinal)
                    || segment.IndexOf(':') >= 0
                    || Path.IsPathRooted(segment))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
