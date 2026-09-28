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
using ModSync.Core.Services.Interpretation;

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

        public GuideIngestResult IngestFromText(string content, string formatHint = null, bool parseDirections = false)
        {
            if (content is null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            string format = string.IsNullOrWhiteSpace(formatHint) ? null : formatHint.Trim().ToLowerInvariant();
            bool isMarkdown = format != null && ModComponentSerializationService.MarkdownFormatAliases.Contains(format);

            if (isMarkdown)
            {
                return IngestMarkdown(content, parseDirections);
            }

            bool parsedAsTomlWithoutHint = false;
            if (format is null)
            {
                // Preserve DetectFormatFromContent's TOML-before-markdown cascade order, but without its
                // full re-parse-from-scratch cost for the markdown case: try TOML first (cheap, fails fast
                // on non-TOML content), then attempt markdown directly and keep the parsed result instead
                // of detecting "markdown" and parsing the same content a second time.
                parsedAsTomlWithoutHint = TryParsesAsToml(content);
                if (!parsedAsTomlWithoutHint)
                {
                    GuideIngestResult markdownAttempt = IngestMarkdown(content, parseDirections);
                    if (markdownAttempt.Components.Count > 0)
                    {
                        return markdownAttempt;
                    }
                }
            }

            IReadOnlyList<ModComponent> components =
                ModComponentSerializationService.DeserializeModComponentFromString(content, format);
            string detectedFormat = format ?? (parsedAsTomlWithoutHint
                ? "toml"
                : ModComponentSerializationService.DetectFormatFromContent(content));
            StampSourceFormat(components, detectedFormat);

            IReadOnlyList<DraftInstructionResult> drafts = Array.Empty<DraftInstructionResult>();
            if (parseDirections && components != null && components.Count > 0)
            {
                drafts = DraftInstructionService.GenerateDraftInstructions(components);
            }

            return new GuideIngestResult(components ?? Array.Empty<ModComponent>(), drafts, detectedFormat);
        }

        /// <summary>
        /// Cheap TOML membership check (parses and discards the result) used only to preserve
        /// <see cref="ModComponentSerializationService.DetectFormatFromContent"/>'s TOML-before-markdown
        /// precedence when no format hint is given, without paying for a second full markdown parse.
        /// </summary>
        private static bool TryParsesAsToml([NotNull] string content)
        {
            try
            {
                IReadOnlyList<ModComponent> parsed = ModComponentSerializationService.DeserializeModComponentFromTomlString(content);
                return parsed != null && parsed.Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        [NotNull]
        private static GuideIngestResult IngestMarkdown(string content, bool parseDirections)
        {
            var profile = MarkdownImportProfile.CreateDefault();
            var parser = new MarkdownParser(profile);
            MarkdownParserResult parsed = parser.Parse(content);

            IReadOnlyList<ModComponent> components = (parsed.Components ?? new List<ModComponent>()).ToList();
            StampSourceFormat(components, "markdown");

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

        private static void StampSourceFormat(
            [CanBeNull][ItemNotNull] IEnumerable<ModComponent> components,
            [CanBeNull] string format)
        {
            foreach (ModComponent component in components ?? Array.Empty<ModComponent>())
            {
                component.SourceFormat = format ?? string.Empty;
            }
        }
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
