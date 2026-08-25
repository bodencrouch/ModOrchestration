// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

using JetBrains.Annotations;

using Tomlyn;
using Tomlyn.Model;

namespace ModSync.Core.Services.Interpretation
{
    public static class GuideInterpretationPolicyLoader
    {
        public const string EmbeddedResourceName = "ModSync.Core.Config.guide-interpretation.defaults.toml";
        public const string DefaultFileName = "guide-interpretation.defaults.toml";
        public const string UserFileName = "guide-interpretation.toml";

        private static readonly Regex s_markdownFence = new Regex(
            @"```(?:toml\s+interpretation|modsync-interpretation)\s*\r?\n(?<body>[\s\S]*?)```",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            TimeSpan.FromSeconds(2));

        [NotNull]
        public static GuideInterpretationPolicy LoadEmbeddedDefaults()
        {
            string toml = ReadEmbeddedDefaults();
            if (string.IsNullOrWhiteSpace(toml))
            {
                Logger.LogWarning("[GuideInterpretation] Embedded defaults missing; using empty policy.");
                return new GuideInterpretationPolicy();
            }

            return ParseToml(toml);
        }

        [NotNull]
        public static string ReadEmbeddedDefaults()
        {
            Assembly assembly = typeof(GuideInterpretationPolicyLoader).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream(EmbeddedResourceName))
            {
                if (stream != null)
                {
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }

            string besideAssembly = Path.Combine(
                Path.GetDirectoryName(assembly.Location) ?? string.Empty,
                "Config",
                DefaultFileName);
            if (File.Exists(besideAssembly))
            {
                return File.ReadAllText(besideAssembly);
            }

            return string.Empty;
        }

        [NotNull]
        public static GuideInterpretationPolicy ParseToml([NotNull] string toml)
        {
            var policy = new GuideInterpretationPolicy();
            MergeToml(policy, toml, replaceInstructionTables: true);
            return policy;
        }

        public static void MergeToml(
            [NotNull] GuideInterpretationPolicy target,
            [CanBeNull] string toml,
            bool replaceInstructionTables)
        {
            if (target == null || string.IsNullOrWhiteSpace(toml))
            {
                return;
            }

            TomlTable root;
            try
            {
                root = Toml.ToModel(toml);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[GuideInterpretation] Failed to parse overlay: {ex.Message}");
                return;
            }

            if (root.TryGetValue("interpretation", out object nested) && nested is TomlTable nestedTable)
            {
                MergeTable(target, nestedTable, replaceInstructionTables);
                return;
            }

            MergeTable(target, root, replaceInstructionTables);
        }

        public static void MergeTable(
            [NotNull] GuideInterpretationPolicy target,
            [NotNull] TomlTable root,
            bool replaceInstructionTables)
        {
            if (root.TryGetValue("matching", out object matchingObj) && matchingObj is TomlTable matching)
            {
                MergeMatching(target.Matching, matching);
            }

            if (root.TryGetValue("tokens", out object tokensObj) && tokensObj is TomlTable tokens)
            {
                MergeTokens(target.Tokens, tokens);
            }

            if (root.TryGetValue("patterns", out object patternsObj) && patternsObj is TomlTable patterns)
            {
                foreach (KeyValuePair<string, object> pair in patterns)
                {
                    if (pair.Value != null)
                    {
                        target.Patterns[pair.Key] = Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                    }
                }
            }

            if (root.TryGetValue("pattern_options", out object patternOptionsObj) && patternOptionsObj is TomlTable patternOptions)
            {
                foreach (KeyValuePair<string, object> pair in patternOptions)
                {
                    if (pair.Value != null)
                    {
                        target.PatternOptions[pair.Key] = Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                    }
                }
            }

            if (root.TryGetValue("filters", out object filtersObj) && filtersObj is TomlTable filters)
            {
                MergeFilters(target.Filters, filters);
            }

            if (root.TryGetValue("unix_nss", out object nssObj) && nssObj is TomlTable nss)
            {
                MergeUnixNss(target.UnixNss, nss);
            }

            if (root.TryGetValue("destinations", out object destObj) && destObj is TomlTable dest)
            {
                foreach (KeyValuePair<string, object> pair in dest)
                {
                    if (pair.Value != null)
                    {
                        target.Destinations[pair.Key] = Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                    }
                }
            }

            if (root.TryGetValue("entities", out object entitiesObj) && entitiesObj is TomlTable entities)
            {
                foreach (KeyValuePair<string, object> pair in entities)
                {
                    if (pair.Value is TomlTable entityTable)
                    {
                        target.Entities[pair.Key] = ReadPatternSpec(pair.Key, entityTable);
                    }
                }
            }

            if (root.TryGetValue("instruction", out object instructionObj) && instructionObj is TomlTableArray instructions)
            {
                if (replaceInstructionTables)
                {
                    target.NlpInstructions.Clear();
                }

                foreach (TomlTable table in instructions)
                {
                    GuideInterpretationPolicy.NlpInstructionSpec spec = ReadInstructionSpec(table);
                    if (spec != null && !string.IsNullOrWhiteSpace(spec.Pattern))
                    {
                        target.NlpInstructions.Add(spec);
                    }
                }
            }

            if (root.TryGetValue("recommendation", out object recObj) && recObj is TomlTableArray recommendations)
            {
                if (replaceInstructionTables)
                {
                    target.Recommendations.Clear();
                }

                foreach (TomlTable table in recommendations)
                {
                    target.Recommendations.Add(ReadPatternSpec(null, table));
                }
            }

            if (root.TryGetValue("exceptions", out object exObj) && exObj is TomlTableArray exceptions)
            {
                foreach (TomlTable table in exceptions)
                {
                    target.Exceptions.Add(ReadException(table));
                }
            }
        }

        [CanBeNull]
        public static string TryExtractDocumentOverlay([CanBeNull] string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            Match fence = s_markdownFence.Match(content);
            if (fence.Success)
            {
                return fence.Groups["body"].Value.Trim();
            }

            if (LooksLikeToml(content) && content.IndexOf("[interpretation]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    TomlTable root = Toml.ToModel(content);
                    if (root.TryGetValue("interpretation", out object nested) && nested is TomlTable)
                    {
                        return ExtractInterpretationSection(content);
                    }
                }
                catch (Exception)
                {
                    return ExtractInterpretationSection(content);
                }
            }

            return null;
        }

        [NotNull]
        public static string StripDocumentOverlay([NotNull] string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return content ?? string.Empty;
            }

            string withoutFence = s_markdownFence.Replace(content, string.Empty);
            if (withoutFence.IndexOf("[interpretation]", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return withoutFence;
            }

            return s_interpretationBlock.Replace(withoutFence, string.Empty);
        }

        private static readonly Regex s_interpretationBlock = new Regex(
            @"(?m)^\[interpretation(?:\.[^\]]+)?\][\s\S]*?(?=^\[(?!interpretation)|\z)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            TimeSpan.FromSeconds(2));

        [CanBeNull]
        private static string ExtractInterpretationSection([NotNull] string content)
        {
            MatchCollection matches = s_interpretationBlock.Matches(content);
            if (matches.Count == 0)
            {
                return null;
            }

            var builder = new StringBuilder();
            foreach (Match match in matches)
            {
                builder.AppendLine(match.Value.Trim());
            }

            return builder.Length == 0 ? null : builder.ToString();
        }

        private static bool LooksLikeToml([NotNull] string content)
        {
            string trimmed = content.TrimStart();
            return trimmed.StartsWith("[", StringComparison.Ordinal)
                || trimmed.IndexOf("[[", StringComparison.Ordinal) >= 0;
        }

        private static void MergeMatching(
            [NotNull] GuideInterpretationPolicy.MatchingRules matching,
            [NotNull] TomlTable table)
        {
            matching.OptionNameContainsPhrase = ReadBool(table, "option_name_contains_phrase", matching.OptionNameContainsPhrase);
            matching.MinPhraseLength = ReadInt(table, "min_phrase_length", matching.MinPhraseLength);
            matching.MinOptionTokenLength = ReadInt(table, "min_option_token_length", matching.MinOptionTokenLength);
            matching.MinSingleHitTokenLength = ReadInt(table, "min_single_hit_token_length", matching.MinSingleHitTokenLength);
            matching.MinNamedHitTokenLength = ReadInt(table, "min_named_hit_token_length", matching.MinNamedHitTokenLength);
            matching.MinForeignOptionNameLength = ReadInt(table, "min_foreign_option_name_length", matching.MinForeignOptionNameLength);
            matching.MinSelfNameLengthForForeignMatch = ReadInt(table, "min_self_name_length_for_foreign_match", matching.MinSelfNameLengthForForeignMatch);
            matching.MinPhraseContainsOptionNameLength = ReadInt(table, "min_phrase_contains_option_name_length", matching.MinPhraseContainsOptionNameLength);
            matching.MinComponentNameLength = ReadInt(table, "min_component_name_length", matching.MinComponentNameLength);
            matching.PersonallyRecommendNamedInstall = ReadBool(table, "personally_recommend_named_install", matching.PersonallyRecommendNamedInstall);
            matching.UnresolvedIfYouUseDefaultsOff = ReadBool(table, "unresolved_if_you_use_defaults_off", matching.UnresolvedIfYouUseDefaultsOff);
            matching.ExcludeOptionalFromMutex = ReadBool(table, "exclude_optional_from_mutex", matching.ExcludeOptionalFromMutex);
            matching.PreferPrimaryNamespace = ReadBool(table, "prefer_primary_namespace", matching.PreferPrimaryNamespace);
            matching.ExactPhraseScoreBase = ReadDouble(table, "exact_phrase_score_base", matching.ExactPhraseScoreBase);
            matching.UnmatchedTokenPenalty = ReadDouble(table, "unmatched_token_penalty", matching.UnmatchedTokenPenalty);
        }

        private static void MergeTokens(
            [NotNull] GuideInterpretationPolicy.TokenRules tokens,
            [NotNull] TomlTable table)
        {
            ReplaceIfPresent(table, "primary_namespace", tokens.PrimaryNamespace);
            ReplaceIfPresent(table, "primary_description_phrases", tokens.PrimaryDescriptionPhrases);
            ReplaceIfPresent(table, "optional_namespace", tokens.OptionalNamespace);
            ReplaceIfPresent(table, "exclude_from_primary_when_also", tokens.ExcludeFromPrimaryWhenAlso);
            ReplaceIfPresent(table, "guide_requests_primary", tokens.GuideRequestsPrimary);
            ReplaceIfPresent(table, "guide_requests_optional", tokens.GuideRequestsOptional);
            ReplaceIfPresent(table, "significant_token_stopwords", tokens.SignificantTokenStopwords);
        }

        private static void MergeFilters(
            [NotNull] GuideInterpretationPolicy.FilterRules filters,
            [NotNull] TomlTable table)
        {
            ReplaceIfPresent(table, "non_game_extensions", filters.NonGameExtensions);
            ReplaceIfPresent(table, "non_game_filenames", filters.NonGameFileNames);
            ReplaceIfPresent(table, "protected_game_extensions", filters.ProtectedGameExtensions);
            filters.AppleDoublePrefix = ReadString(table, "apple_double_prefix", filters.AppleDoublePrefix);
            filters.OfficeLockPrefix = ReadString(table, "office_lock_prefix", filters.OfficeLockPrefix);
            ReplaceIfPresent(table, "compat_archive_tokens", filters.CompatArchiveTokens);
            ReplaceIfPresent(table, "compat_archive_contains", filters.CompatArchiveContains);
            ReplaceIfPresent(table, "compat_archive_suffixes", filters.CompatArchiveSuffixes);
        }

        private static void MergeUnixNss(
            [NotNull] GuideInterpretationPolicy.UnixNssRules nss,
            [NotNull] TomlTable table)
        {
            ReplaceIfPresent(table, "crash_markers", nss.CrashMarkers);
            nss.SaveProcessedScripts = ReadBool(table, "save_processed_scripts", nss.SaveProcessedScripts);
        }

        [NotNull]
        private static GuideInterpretationPolicy.NlpPatternSpec ReadPatternSpec(
            [CanBeNull] string id,
            [NotNull] TomlTable table)
        {
            return new GuideInterpretationPolicy.NlpPatternSpec
            {
                Id = ReadString(table, "id", id),
                Pattern = ReadString(table, "pattern", string.Empty).Trim(),
                Options = ReadString(table, "options", null),
            };
        }

        [CanBeNull]
        private static GuideInterpretationPolicy.NlpInstructionSpec ReadInstructionSpec([NotNull] TomlTable table)
        {
            string actionText = ReadString(table, "action", null);
            if (string.IsNullOrWhiteSpace(actionText)
                || !Enum.TryParse(actionText, ignoreCase: true, result: out Instruction.ActionType action))
            {
                return null;
            }

            return new GuideInterpretationPolicy.NlpInstructionSpec
            {
                Id = ReadString(table, "id", null),
                Pattern = ReadString(table, "pattern", string.Empty).Trim(),
                Action = action,
                Options = ReadString(table, "options", null),
            };
        }

        [NotNull]
        private static GuideInterpretationPolicy.InterpretationException ReadException([NotNull] TomlTable table)
        {
            bool? select = null;
            if (table.TryGetValue("select", out object selectObj) && selectObj != null)
            {
                select = Convert.ToBoolean(selectObj, CultureInfo.InvariantCulture);
            }

            return new GuideInterpretationPolicy.InterpretationException
            {
                Id = ReadString(table, "id", null),
                WhenComponentContains = ReadString(table, "when_component_contains", null),
                WhenOptionContains = ReadString(table, "when_option_contains", null),
                Select = select,
            };
        }

        private static void ReplaceIfPresent(
            [NotNull] TomlTable table,
            [NotNull] string key,
            [NotNull] List<string> target)
        {
            if (!table.TryGetValue(key, out object value) || !(value is TomlArray array))
            {
                return;
            }

            target.Clear();
            foreach (object item in array)
            {
                string text = Convert.ToString(item, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    target.Add(text);
                }
            }
        }

        private static bool ReadBool([NotNull] TomlTable table, [NotNull] string key, bool fallback)
        {
            if (!table.TryGetValue(key, out object value) || value == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static int ReadInt([NotNull] TomlTable table, [NotNull] string key, int fallback)
        {
            if (!table.TryGetValue(key, out object value) || value == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static double ReadDouble([NotNull] TomlTable table, [NotNull] string key, double fallback)
        {
            if (!table.TryGetValue(key, out object value) || value == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        [CanBeNull]
        private static string ReadString([NotNull] TomlTable table, [NotNull] string key, [CanBeNull] string fallback)
        {
            if (!table.TryGetValue(key, out object value) || value == null)
            {
                return fallback;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback;
        }

        public static RegexOptions ParseRegexOptions([CanBeNull] string listed)
        {
            RegexOptions options = RegexOptions.None;
            if (string.IsNullOrWhiteSpace(listed))
            {
                return RegexOptions.IgnoreCase;
            }

            string[] parts = listed.Split(new[] { ',', '|', '+' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                switch (parts[i].Trim())
                {
                    case "IgnoreCase":
                    case "ignorecase":
                        options |= RegexOptions.IgnoreCase;
                        break;
                    case "Singleline":
                    case "singleline":
                        options |= RegexOptions.Singleline;
                        break;
                    case "Multiline":
                    case "multiline":
                        options |= RegexOptions.Multiline;
                        break;
                    case "CultureInvariant":
                    case "cultureinvariant":
                        options |= RegexOptions.CultureInvariant;
                        break;
                    case "Compiled":
                    case "compiled":
                        options |= RegexOptions.Compiled;
                        break;
                    case "IgnorePatternWhitespace":
                        options |= RegexOptions.IgnorePatternWhitespace;
                        break;
                }
            }

            return options == RegexOptions.None ? RegexOptions.IgnoreCase : options;
        }
    }
}
