// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using JetBrains.Annotations;

using ModSync.Core.Parsing;
using ModSync.Core.Services;

namespace ModSync.Core.Ports.Guides
{
    /// <summary>
    /// Default guide ingest: markdown-format content is parsed directly via <see cref="MarkdownParser"/> so
    /// the full <see cref="MarkdownParserResult"/> (preamble/epilogue/widescreen/Aspyr content, parse trace)
    /// survives onto <see cref="GuideIngestResult"/> instead of being dropped at the generic deserializer
    /// boundary. Other formats continue through <see cref="ModComponentSerializationService"/> unchanged -
    /// they have no equivalent content sections to carry. Optionally drafts instructions from prose via
    /// <see cref="DraftInstructionService"/>.
    /// </summary>
    public sealed class GuideIngestService : IGuideIngestService
    {
        public static GuideIngestService Instance { get; } = new GuideIngestService();

        [NotNull]
        private static readonly string[] s_markdownFormatAliases =
        {
            "md", "markdown", "mdown", "mkdn", "mkd", "mdtxt", "mdtext", "text",
        };

        public GuideIngestResult IngestFromText(string content, string formatHint = null, bool parseDirections = false)
        {
            if (content is null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            string format = string.IsNullOrWhiteSpace(formatHint) ? null : formatHint.Trim().ToLowerInvariant();
            bool isMarkdown = format != null && s_markdownFormatAliases.Contains(format);

            if (format is null)
            {
                string detected = ModComponentSerializationService.DetectFormatFromContent(content);
                if (string.Equals(detected, "markdown", StringComparison.OrdinalIgnoreCase))
                {
                    format = detected;
                    isMarkdown = true;
                }
            }

            if (isMarkdown)
            {
                return IngestMarkdown(content, parseDirections);
            }

            IReadOnlyList<ModComponent> components =
                ModComponentSerializationService.DeserializeModComponentFromString(content, format);

            IReadOnlyList<DraftInstructionResult> drafts = Array.Empty<DraftInstructionResult>();
            if (parseDirections && components != null && components.Count > 0)
            {
                drafts = DraftInstructionService.GenerateDraftInstructions(components);
            }

            return new GuideIngestResult(components ?? Array.Empty<ModComponent>(), drafts, format);
        }

        [NotNull]
        private static GuideIngestResult IngestMarkdown(string content, bool parseDirections)
        {
            var profile = MarkdownImportProfile.CreateDefault();
            var parser = new MarkdownParser(profile);
            MarkdownParserResult parsed = parser.Parse(content);

            IReadOnlyList<ModComponent> components = (parsed.Components ?? new List<ModComponent>()).ToList();

            IReadOnlyList<DraftInstructionResult> drafts = Array.Empty<DraftInstructionResult>();
            if (parseDirections && components.Count > 0)
            {
                drafts = DraftInstructionService.GenerateDraftInstructions(components);
            }

            return new GuideIngestResult(
                components,
                drafts,
                detectedFormat: "markdown",
                preambleContent: NullIfEmpty(parsed.PreambleContent),
                epilogueContent: NullIfEmpty(parsed.EpilogueContent),
                widescreenWarningContent: NullIfEmpty(parsed.WidescreenWarningContent),
                aspyrExclusiveWarningContent: NullIfEmpty(parsed.AspyrExclusiveWarningContent),
                installationWarningContent: NullIfEmpty(parsed.InstallationWarningContent),
                trace: parsed.Trace,
                warnings: (parsed.Warnings ?? new List<string>()).ToList());
        }

        [CanBeNull]
        private static string NullIfEmpty([CanBeNull] string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Default guide emit over <see cref="ModComponentSerializationService.GenerateModDocumentation"/>.</summary>
    public sealed class GuideEmitService : IGuideEmitService
    {
        public static GuideEmitService Instance { get; } = new GuideEmitService();

        public string EmitMarkdown(
            IReadOnlyList<ModComponent> components,
            string preambleContent = null,
            string epilogueContent = null,
            string widescreenWarningContent = null,
            string aspyrExclusiveWarningContent = null)
        {
            if (components is null)
            {
                throw new ArgumentNullException(nameof(components));
            }

            return ModComponentSerializationService.GenerateModDocumentation(
                components,
                preambleContent,
                epilogueContent,
                widescreenWarningContent,
                aspyrExclusiveWarningContent);
        }

        public Task<string> EmitMarkdownAsync(
            IReadOnlyList<ModComponent> components,
            string preambleContent = null,
            string epilogueContent = null,
            string widescreenWarningContent = null,
            string aspyrExclusiveWarningContent = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(EmitMarkdown(components, preambleContent, epilogueContent, widescreenWarningContent, aspyrExclusiveWarningContent));
        }
    }
}
