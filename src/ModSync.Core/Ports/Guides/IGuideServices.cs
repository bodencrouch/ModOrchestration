// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using JetBrains.Annotations;

using ModSync.Core.Parsing;

namespace ModSync.Core.Ports.Guides
{
    /// <summary>Result of ingesting guide or instruction text into components.</summary>
    public sealed class GuideIngestResult
    {
        public GuideIngestResult(
            [NotNull][ItemNotNull] IReadOnlyList<ModComponent> components,
            [CanBeNull][ItemNotNull] IReadOnlyList<DraftInstructionResult> draftResults = null,
            [CanBeNull] string detectedFormat = null,
            [CanBeNull] string preambleContent = null,
            [CanBeNull] string epilogueContent = null,
            [CanBeNull] string widescreenWarningContent = null,
            [CanBeNull] string aspyrExclusiveWarningContent = null,
            [CanBeNull] string installationWarningContent = null,
            [CanBeNull] ParsingTraceInfo trace = null)
        {
            Components = components ?? throw new ArgumentNullException(nameof(components));
            DraftResults = draftResults ?? Array.Empty<DraftInstructionResult>();
            DetectedFormat = detectedFormat;
            PreambleContent = preambleContent;
            EpilogueContent = epilogueContent;
            WidescreenWarningContent = widescreenWarningContent;
            AspyrExclusiveWarningContent = aspyrExclusiveWarningContent;
            InstallationWarningContent = installationWarningContent;
            Trace = trace;
        }

        [NotNull]
        [ItemNotNull]
        public IReadOnlyList<ModComponent> Components { get; }

        [NotNull]
        [ItemNotNull]
        public IReadOnlyList<DraftInstructionResult> DraftResults { get; }

        [CanBeNull]
        public string DetectedFormat { get; }

        /// <summary>
        /// Guide content preceding the "## Mod List" heading. Only populated for markdown-format input -
        /// null for TOML/YAML/JSON/XML ingest, which has no equivalent section.
        /// </summary>
        [CanBeNull]
        public string PreambleContent { get; }

        /// <summary>Guide content following the last component. Markdown-only; see <see cref="PreambleContent"/>.</summary>
        [CanBeNull]
        public string EpilogueContent { get; }

        /// <summary>Widescreen-fix warning section content. Markdown-only; see <see cref="PreambleContent"/>.</summary>
        [CanBeNull]
        public string WidescreenWarningContent { get; }

        /// <summary>Aspyr-exclusive-content warning section. Markdown-only; see <see cref="PreambleContent"/>.</summary>
        [CanBeNull]
        public string AspyrExclusiveWarningContent { get; }

        /// <summary>Guide-level installation warning section. Markdown-only; see <see cref="PreambleContent"/>.</summary>
        [CanBeNull]
        public string InstallationWarningContent { get; }

        /// <summary>
        /// What <see cref="MarkdownParser"/> matched, where, and with which patterns. Markdown-only; null
        /// for other formats, which do not go through <see cref="MarkdownParser"/>.
        /// </summary>
        [CanBeNull]
        public ParsingTraceInfo Trace { get; }
    }

    /// <summary>
    /// Guide / instruction ingest port (paste, file, CLI convert).
    /// Parses text into components and optionally drafts instructions from prose.
    /// </summary>
    public interface IGuideIngestService
    {
        [NotNull]
        GuideIngestResult IngestFromText(
            [NotNull] string content,
            [CanBeNull] string formatHint = null,
            bool parseDirections = false);
    }

    /// <summary>Guide emission port: components → human-readable markdown.</summary>
    public interface IGuideEmitService
    {
        [NotNull]
        string EmitMarkdown(
            [NotNull][ItemNotNull] IReadOnlyList<ModComponent> components,
            [CanBeNull] string preambleContent = null,
            [CanBeNull] string epilogueContent = null,
            [CanBeNull] string widescreenWarningContent = null,
            [CanBeNull] string aspyrExclusiveWarningContent = null);

        [ItemNotNull]
        Task<string> EmitMarkdownAsync(
            [NotNull][ItemNotNull] IReadOnlyList<ModComponent> components,
            [CanBeNull] string preambleContent = null,
            [CanBeNull] string epilogueContent = null,
            [CanBeNull] string widescreenWarningContent = null,
            [CanBeNull] string aspyrExclusiveWarningContent = null,
            CancellationToken cancellationToken = default);
    }
}
