// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

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
                if (component.Instructions.Count > 0 || string.IsNullOrWhiteSpace(component.Directions))
                {
                    continue;
                }

                ObservableCollection<Instruction> parsed;
                IReadOnlyList<string> unparsedGaps;
                IReadOnlyList<string> conditionalDrafts;
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
                    // Graceful degradation: unparseable prose keeps today's behavior (no instructions drafted).
                    verbose($"[DraftInstructions] Failed to parse prose for '{component.Name}': {ex.Message}");
                    continue;
                }

                int added = 0;
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

                results.Add(new DraftInstructionResult(component, added, unparsedGaps, conditionalDrafts));
            }

            return results;
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
                .Where(IsSandboxedPath)
                .ToList();

            string destination = NormalizePlaceholders(instruction.Destination);
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
            return action == Instruction.ActionType.Move
                || action == Instruction.ActionType.Copy;
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
