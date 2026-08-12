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

            string markdown = await File.ReadAllTextAsync(guidePath).ConfigureAwait(false);
            GuideIngestResult result = GuideIngestService.Instance.IngestFromText(
                markdown,
                formatHint: "markdown",
                parseDirections: true);
            return result.Components.ToList();
        }

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

                if (component.WidescreenOnly)
                {
                    component.IsSelected = false;
                    skippedWidescreen++;
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

        private static bool HasExecutableActions([NotNull] ModComponent component) =>
            component.Instructions.Count > 0 || component.Options.Any(HasExecutableActions);

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
