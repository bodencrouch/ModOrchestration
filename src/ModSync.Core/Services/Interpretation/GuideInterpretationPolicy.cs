// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using JetBrains.Annotations;

namespace ModSync.Core.Services.Interpretation
{
    /// <summary>
    /// App- and instruction-level interpretation rules. Generic norms live here as defaults
    /// that are on; named-mod exceptions belong in <see cref="Exceptions"/>, never in C#.
    /// </summary>
    public sealed class GuideInterpretationPolicy
    {
        public GuideInterpretationPolicy()
        {
            Matching = new MatchingRules();
            Tokens = new TokenRules();
            Patterns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PatternOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            NlpInstructions = new List<NlpInstructionSpec>();
            Recommendations = new List<NlpPatternSpec>();
            Entities = new Dictionary<string, NlpPatternSpec>(StringComparer.OrdinalIgnoreCase);
            Destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Filters = new FilterRules();
            UnixNss = new UnixNssRules();
            Exceptions = new List<InterpretationException>();
        }

        public int Version { get; set; }

        [NotNull]
        public MatchingRules Matching { get; set; }

        [NotNull]
        public TokenRules Tokens { get; set; }

        [NotNull]
        public Dictionary<string, string> Patterns { get; set; }

        [NotNull]
        public Dictionary<string, string> PatternOptions { get; set; }

        [NotNull]
        public List<NlpInstructionSpec> NlpInstructions { get; set; }

        [NotNull]
        public List<NlpPatternSpec> Recommendations { get; set; }

        [NotNull]
        public Dictionary<string, NlpPatternSpec> Entities { get; set; }

        [NotNull]
        public Dictionary<string, string> Destinations { get; set; }

        [NotNull]
        public FilterRules Filters { get; set; }

        [NotNull]
        public UnixNssRules UnixNss { get; set; }

        [NotNull]
        public List<InterpretationException> Exceptions { get; set; }

        [CanBeNull]
        public string TryGetPattern([NotNull] string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            return Patterns.TryGetValue(name, out string value) ? value : null;
        }

        [CanBeNull]
        public Regex TryCompile([NotNull] string name, RegexOptions extra = RegexOptions.None)
        {
            string pattern = TryGetPattern(name);
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return null;
            }

            RegexOptions options = GuideInterpretationPolicyLoader.ParseRegexOptions(
                PatternOptions.TryGetValue(name, out string listed) ? listed : null);
            options |= extra | RegexOptions.Compiled | RegexOptions.CultureInvariant;
            try
            {
                return new Regex(pattern, options, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException)
            {
                Logger.LogWarning($"[GuideInterpretation] Invalid regex for '{name}'");
                return null;
            }
        }

        [NotNull]
        public Regex CompileOrFallback(
            [NotNull] string name,
            [NotNull] string fallbackPattern,
            RegexOptions extra = RegexOptions.None)
        {
            Regex compiled = TryCompile(name, extra);
            if (compiled != null)
            {
                return compiled;
            }

            return new Regex(
                fallbackPattern,
                extra | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                TimeSpan.FromSeconds(2));
        }

        public bool ContainsAny([CanBeNull] string haystack, [CanBeNull] IReadOnlyList<string> needles)
        {
            if (string.IsNullOrEmpty(haystack) || needles == null || needles.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < needles.Count; i++)
            {
                string needle = needles[i];
                if (!string.IsNullOrEmpty(needle)
                    && haystack.IndexOf(needle, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        public sealed class MatchingRules
        {
            public bool OptionNameContainsPhrase { get; set; } = true;
            public int MinPhraseLength { get; set; } = 4;
            public int MinOptionTokenLength { get; set; } = 4;
            public int MinSingleHitTokenLength { get; set; } = 6;
            public int MinNamedHitTokenLength { get; set; } = 4;
            public int MinForeignOptionNameLength { get; set; } = 8;
            public int MinSelfNameLengthForForeignMatch { get; set; } = 10;
            public int MinPhraseContainsOptionNameLength { get; set; } = 6;
            public int MinComponentNameLength { get; set; } = 4;
            public bool PersonallyRecommendNamedInstall { get; set; } = true;
            public bool UnresolvedIfYouUseDefaultsOff { get; set; } = true;
            public bool ExcludeOptionalFromMutex { get; set; } = true;
            public bool PreferPrimaryNamespace { get; set; } = true;
            public double ExactPhraseScoreBase { get; set; } = 1000d;
            public double UnmatchedTokenPenalty { get; set; } = 0.5d;
        }

        public sealed class TokenRules
        {
            [NotNull]
            public List<string> PrimaryNamespace { get; set; } = new List<string>();

            [NotNull]
            public List<string> PrimaryDescriptionPhrases { get; set; } = new List<string>();

            [NotNull]
            public List<string> OptionalNamespace { get; set; } = new List<string>();

            [NotNull]
            public List<string> ExcludeFromPrimaryWhenAlso { get; set; } = new List<string>();

            [NotNull]
            public List<string> GuideRequestsPrimary { get; set; } = new List<string>();

            [NotNull]
            public List<string> GuideRequestsOptional { get; set; } = new List<string>();

            [NotNull]
            public List<string> SignificantTokenStopwords { get; set; } = new List<string>();
        }

        public sealed class FilterRules
        {
            [NotNull]
            public List<string> NonGameExtensions { get; set; } = new List<string>();

            [NotNull]
            public List<string> NonGameFileNames { get; set; } = new List<string>();

            [NotNull]
            public List<string> ProtectedGameExtensions { get; set; } = new List<string>();

            [NotNull]
            public string AppleDoublePrefix { get; set; } = "._";

            [NotNull]
            public string OfficeLockPrefix { get; set; } = ".~lock";

            [NotNull]
            public List<string> CompatArchiveTokens { get; set; } = new List<string>();

            [NotNull]
            public List<string> CompatArchiveContains { get; set; } = new List<string>();

            [NotNull]
            public List<string> CompatArchiveSuffixes { get; set; } = new List<string>();
        }

        public sealed class UnixNssRules
        {
            [NotNull]
            public List<string> CrashMarkers { get; set; } = new List<string>();

            public bool SaveProcessedScripts { get; set; } = true;
        }

        public sealed class NlpInstructionSpec
        {
            [CanBeNull]
            public string Id { get; set; }

            [NotNull]
            public string Pattern { get; set; } = string.Empty;

            public Instruction.ActionType Action { get; set; }

            [CanBeNull]
            public string Options { get; set; }
        }

        public sealed class NlpPatternSpec
        {
            [CanBeNull]
            public string Id { get; set; }

            [NotNull]
            public string Pattern { get; set; } = string.Empty;

            [CanBeNull]
            public string Options { get; set; }
        }

        public sealed class InterpretationException
        {
            [CanBeNull]
            public string Id { get; set; }

            [CanBeNull]
            public string WhenComponentContains { get; set; }

            [CanBeNull]
            public string WhenOptionContains { get; set; }

            public bool? Select { get; set; }
        }
    }
}
