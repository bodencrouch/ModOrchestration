// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using JetBrains.Annotations;

using ModSync.Core.FileSystemUtils;
using ModSync.Core.Utility;

namespace ModSync.Core.Services
{
    public static class FileLoadingService
    {
        [NotNull]
        [ItemNotNull]
        public static IReadOnlyList<ModComponent> LoadFromFile([NotNull] string filePath)
        {
            if (filePath is null)
            {
                throw new ArgumentNullException(nameof(filePath));
            }

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {filePath}");
            }

            if (MainConfig.CaseInsensitivePathing)
            {
                filePath = PathHelper.GetCaseSensitivePath(filePath, isFile: true).Item1;
            }

            string content = ReadFileWithEncodingFallback(filePath);
            string format = GetFormatHintFromExtension(filePath);

            if (string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase))
            {
                return Ports.Guides.GuideIngestService.Instance.IngestFromText(
                    content,
                    formatHint: "markdown",
                    parseDirections: true).Components;
            }

            return ModComponentSerializationService.DeserializeModComponentFromString(content, format);
        }

        [NotNull]
        [ItemNotNull]
        public static async Task<List<ModComponent>> LoadFromFileAsync([NotNull] string filePath)
        {
            (string content, string format) = await ReadFileContentAndFormatHintAsync(filePath).ConfigureAwait(false);

            // Prose Markdown guides must run NaturalLanguageInstructionParser so Directions
            // become executable instructions (GUI paste/file-open and CLI install share this).
            if (string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase))
            {
                Ports.Guides.GuideIngestResult ingested = await Task.Run(() =>
                    Ports.Guides.GuideIngestService.Instance.IngestFromText(
                        content,
                        formatHint: "markdown",
                        parseDirections: true)).ConfigureAwait(false);
                return ingested.Components.ToList();
            }

            return (List<ModComponent>)await ModComponentSerializationService.DeserializeModComponentFromStringAsync(content, format).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads a file's content (with the same UTF-8-with-fallback and case-insensitive-path handling as
        /// <see cref="LoadFromFileAsync"/>) and resolves its format hint from the file extension, without
        /// deserializing. Callers that need the raw content - e.g. to route through
        /// <see cref="Ports.Guides.IGuideIngestService"/> - use this instead of duplicating the file-reading
        /// logic <see cref="LoadFromFileAsync"/> already gets right.
        /// </summary>
        [ItemNotNull]
        public static async Task<(string Content, string FormatHint)> ReadFileContentAndFormatHintAsync([NotNull] string filePath)
        {
            if (filePath is null)
            {
                throw new ArgumentNullException(nameof(filePath));
            }

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {filePath}");
            }

            if (MainConfig.CaseInsensitivePathing)
            {
                filePath = PathHelper.GetCaseSensitivePath(filePath, isFile: true).Item1;
            }

            string content = await Task.Run(() => ReadFileWithEncodingFallback(filePath)).ConfigureAwait(false);
            string format = GetFormatHintFromExtension(filePath);

            return (content, format);
        }

        [CanBeNull]
        private static string GetFormatHintFromExtension([NotNull] string filePath)
        {
            string extension = Path.GetExtension(filePath)?.TrimStart(new[] { '.' }).ToLowerInvariant();
            if (string.IsNullOrEmpty(extension))
            {
                return null;
            }

            switch (extension)
            {
                case "md":
                case "markdown":
                case "mdown":
                case "mkdn":
                case "mkd":
                case "mdtxt":
                case "mdtext":
                case "text":
                    return "markdown";
                case "toml":
                case "tml":
                    return "toml";
                case "yaml":
                case "yml":
                    return "yaml";
                case "json":
                    return "json";
                case "xml":
                    return "xml";
                default:
                    return null;
            }
        }

        public static void SaveToFile([NotNull] List<ModComponent> components, [NotNull] string filePath)
        {
            if (components is null)
            {
                throw new ArgumentNullException(nameof(components));
            }

            if (filePath is null)
            {
                throw new ArgumentNullException(nameof(filePath));
            }

            if (MainConfig.CaseInsensitivePathing)
            {
                filePath = PathHelper.GetCaseSensitivePath(filePath, isFile: true).Item1;
            }

            string extension = Path.GetExtension(filePath)?.TrimStart('.').ToLowerInvariant() ?? "toml";
            string content = ModComponentSerializationService.SerializeModComponentAsString(components, extension);

            string outputDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            File.WriteAllText(filePath, content, NetFrameworkCompatibility.Utf8WithoutBom);
        }

        public static async Task SaveToFileAsync([NotNull] List<ModComponent> components, [NotNull] string filePath)
        {
            if (components is null)
            {
                throw new ArgumentNullException(nameof(components));
            }

            if (filePath is null)
            {
                throw new ArgumentNullException(nameof(filePath));
            }

            if (MainConfig.CaseInsensitivePathing)
            {
                filePath = PathHelper.GetCaseSensitivePath(filePath, isFile: true).Item1;
            }

            string extension = Path.GetExtension(filePath)?.TrimStart('.').ToLowerInvariant() ?? "toml";

            string content = await ModComponentSerializationService.SerializeModComponentAsStringAsync(components, extension).ConfigureAwait(false);

            string outputDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            await Task.Run(() => File.WriteAllText(filePath, content, NetFrameworkCompatibility.Utf8WithoutBom)).ConfigureAwait(false);
        }

        private static string ReadFileWithEncodingFallback(string filePath)
        {
            try
            {
                var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
                byte[] bytes = File.ReadAllBytes(filePath);
                string content = encoding.GetString(bytes);

                content = content.Replace('\uFFFD', '_');

                return content;
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, $"Failed to read '{filePath}' with UTF-8 fallback. Falling back to default encoding.");
                return File.ReadAllText(filePath);
            }
        }
    }
}
