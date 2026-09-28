// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using JetBrains.Annotations;

using ModSync.Core.Parsing;
using ModSync.Core.Ports.Guides;

namespace ModSync.Core.Services
{
    /// <summary>
    /// Preflight for installs whose Markdown guide is the sole instruction authority.
    /// Prose guides are ingested with <see cref="DraftInstructionService"/> /
    /// <see cref="NaturalLanguageInstructionParser"/> so Directions become executable
    /// instructions (the product path for <c>content/k1/full.md</c> / <c>k2/full.md</c>).
    /// </summary>
    public static class DirectMarkdownInstallPreflight
    {
        /// <summary>
        /// Loads Markdown via guide ingest with natural-language direction parsing enabled.
        /// </summary>
        [NotNull]
        [ItemNotNull]
        public static async Task<List<ModComponent>> LoadGuideAsync([NotNull] string guidePath)
        {
            if (!string.Equals(Path.GetExtension(guidePath), ".md", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Direct Markdown mode requires a .md guide.", nameof(guidePath));
            }

            return await FileLoadingService.LoadFromFileAsync(guidePath).ConfigureAwait(false);
        }

        public static bool IsGuideDerived([NotNull][ItemNotNull] IEnumerable<ModComponent> components) =>
            components.Any(HasDraftFlag);

        [NotNull]
        public static DirectMarkdownInstallPreflightResult Apply(
            [NotNull][ItemNotNull] IReadOnlyCollection<ModComponent> components)
        {
            if (components is null)
            {
                throw new ArgumentNullException(nameof(components));
            }

            var unresolved = new List<string>();
            int skippedFourGb = 0;
            int skippedWidescreen = 0;
            int drafted = 0;

            foreach (ModComponent component in components)
            {
                if (!component.IsSelected)
                {
                    continue;
                }

                if (string.Equals(component.Name?.Trim(), "4GB Patcher", StringComparison.OrdinalIgnoreCase))
                {
                    component.IsSelected = false;
                    skippedFourGb++;
                    continue;
                }

                if (HasDraftFlag(component))
                {
                    drafted++;
                }

                // NLP drafts are the expected executable actions for prose guides.
                // Only fail-closed when selected components still have nothing to run.
                if (!HasExecutableActions(component))
                {
                    unresolved.Add(component.Name);
                }
            }

            return new DirectMarkdownInstallPreflightResult(
                unresolved,
                skippedFourGb,
                skippedWidescreen,
                drafted);
        }

        private static bool HasExecutableActions([NotNull] ModComponent component)
        {
            List<Instruction.ActionType> actions = component.Instructions
                .Select(instruction => instruction.Action)
                .Concat(component.Options.SelectMany(option => option.Instructions)
                    .Select(instruction => instruction.Action))
                .ToList();

            bool hasPatcher = actions.Contains(Instruction.ActionType.Patcher);
            bool hasLooseDeployment = actions.Contains(Instruction.ActionType.Move)
                || actions.Contains(Instruction.ActionType.Copy);
            bool hasExecutable = actions.Contains(Instruction.ActionType.Execute)
                || actions.Contains(Instruction.ActionType.Run);
            bool hasSpecialTerminalOperation = actions.Contains(Instruction.ActionType.CleanList)
                || actions.Contains(Instruction.ActionType.DelDuplicate);

            string method = component.InstallationMethod ?? string.Empty;
            bool requiresPatcher = method.IndexOf("patcher", StringComparison.OrdinalIgnoreCase) >= 0;
            bool requiresLooseDeployment = method.IndexOf("loose", StringComparison.OrdinalIgnoreCase) >= 0;
            bool requiresExecutable = method.IndexOf("executable", StringComparison.OrdinalIgnoreCase) >= 0;

            // ".bat Patcher / .sh script" is the Remove Duplicate TGA/TPC method, not TSLPatcher.
            // DelDuplicate / CleanList are the payload for those steps; requiring a Patcher
            // instruction here left the crash-prevention barrier unresolved after generation.
            if (hasSpecialTerminalOperation)
            {
                return true;
            }

            if (requiresPatcher && !hasPatcher)
            {
                return false;
            }
            if (requiresLooseDeployment && !hasLooseDeployment)
            {
                return false;
            }
            if (requiresExecutable && !hasExecutable)
            {
                return false;
            }

            // Extract/Delete/Rename by themselves are preparation or secondary operations. Treating
            // them as readiness let archive-only and post-processing-only components report success
            // without ever deploying a payload (the measured silent-no-op failure mode).
            return hasPatcher || hasLooseDeployment || hasExecutable || hasSpecialTerminalOperation;
        }

        private static bool HasDraftFlag([NotNull] ModComponent component) =>
            component.InstallationWarning?.IndexOf(
                DraftInstructionService.ReviewFlagMessage,
                StringComparison.Ordinal) >= 0
            || component.Options.Any(HasDraftFlag);
    }

    public sealed class DirectMarkdownInstallPreflightResult
    {
        public DirectMarkdownInstallPreflightResult(
            [NotNull][ItemNotNull] IReadOnlyList<string> unresolvedComponents,
            int skippedFourGb,
            int skippedWidescreen,
            int draftedFromProse = 0)
        {
            UnresolvedComponents = unresolvedComponents
                ?? throw new ArgumentNullException(nameof(unresolvedComponents));
            SkippedFourGb = skippedFourGb;
            SkippedWidescreen = skippedWidescreen;
            DraftedFromProse = draftedFromProse;
        }

        [NotNull]
        [ItemNotNull]
        public IReadOnlyList<string> UnresolvedComponents { get; }

        public int SkippedFourGb { get; }

        public int SkippedWidescreen { get; }

        /// <summary>Selected components that carry the NLP draft review flag.</summary>
        public int DraftedFromProse { get; }

        public bool IsReady => UnresolvedComponents.Count == 0;
    }
}
