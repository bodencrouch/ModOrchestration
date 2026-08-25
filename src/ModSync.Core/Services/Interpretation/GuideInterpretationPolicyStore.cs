// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.IO;

using JetBrains.Annotations;

using ModSync.Core.Services.Settings;

namespace ModSync.Core.Services.Interpretation
{
    /// <summary>
    /// Process-wide interpretation policy. Tests and Core start on embedded defaults only.
    /// CLI/GUI call <see cref="EnsureUserOverlays"/>; document ingest calls <see cref="BeginDocument"/>.
    /// </summary>
    public static class GuideInterpretationPolicyStore
    {
        private static readonly object Sync = new object();
        private static GuideInterpretationPolicy s_current = GuideInterpretationPolicyLoader.LoadEmbeddedDefaults();
        private static string s_cliOverlayPath;
        private static bool s_userOverlaysEnabled;
        private static int s_version;

        [NotNull]
        public static GuideInterpretationPolicy Current
        {
            get
            {
                lock (Sync)
                {
                    return s_current;
                }
            }
        }

        [CanBeNull]
        public static string CliOverlayPath
        {
            get
            {
                lock (Sync)
                {
                    return s_cliOverlayPath;
                }
            }
        }

        public static void ResetToDefaults()
        {
            lock (Sync)
            {
                s_cliOverlayPath = null;
                s_userOverlaysEnabled = false;
                s_current = GuideInterpretationPolicyLoader.LoadEmbeddedDefaults();
                Bump(s_current);
            }
        }

        public static void SetCliOverlayPath([CanBeNull] string path)
        {
            lock (Sync)
            {
                s_cliOverlayPath = string.IsNullOrWhiteSpace(path) ? null : path;
            }

            if (s_userOverlaysEnabled)
            {
                EnsureUserOverlays();
            }
        }

        public static void EnsureUserOverlays()
        {
            lock (Sync)
            {
                s_userOverlaysEnabled = true;
                s_current = BuildStack(documentToml: null, siblingPath: null);
            }
        }

        public static void BeginDocument([CanBeNull] string content, [CanBeNull] string instructionFilePath)
        {
            string overlay = GuideInterpretationPolicyLoader.TryExtractDocumentOverlay(content);
            string sibling = FindSiblingOverlay(instructionFilePath);
            lock (Sync)
            {
                s_current = BuildStack(overlay, sibling);
            }
        }

        public static void EndDocument()
        {
            lock (Sync)
            {
                s_current = BuildStack(documentToml: null, siblingPath: null);
            }
        }

        public static void MergeOverlayToml([NotNull] string toml, bool replaceInstructionTables = false)
        {
            if (string.IsNullOrWhiteSpace(toml))
            {
                return;
            }

            lock (Sync)
            {
                GuideInterpretationPolicyLoader.MergeToml(s_current, toml, replaceInstructionTables);
                Bump(s_current);
            }
        }

        [NotNull]
        public static string StripDocumentOverlay([NotNull] string content)
        {
            return GuideInterpretationPolicyLoader.StripDocumentOverlay(content);
        }

        [CanBeNull]
        public static string FindSiblingOverlay([CanBeNull] string instructionFilePath)
        {
            if (string.IsNullOrWhiteSpace(instructionFilePath))
            {
                return null;
            }

            try
            {
                string beside = instructionFilePath + ".interpretation.toml";
                if (File.Exists(beside))
                {
                    return File.ReadAllText(beside);
                }

                string directory = Path.GetDirectoryName(instructionFilePath);
                if (string.IsNullOrEmpty(directory))
                {
                    return null;
                }

                string named = Path.Combine(directory, GuideInterpretationPolicyLoader.UserFileName);
                return File.Exists(named) ? File.ReadAllText(named) : null;
            }
            catch (IOException ex)
            {
                Logger.LogVerbose($"[GuideInterpretation] Sibling overlay unread: {ex.Message}");
                return null;
            }
        }

        [NotNull]
        private static GuideInterpretationPolicy BuildStack(
            [CanBeNull] string documentToml,
            [CanBeNull] string siblingPath)
        {
            GuideInterpretationPolicy policy = GuideInterpretationPolicyLoader.LoadEmbeddedDefaults();

            if (s_userOverlaysEnabled)
            {
                TryMergeFile(policy, ResolveUserOverlayPath(), replaceInstructionTables: false);
                TryMergeFile(policy, s_cliOverlayPath, replaceInstructionTables: false);
            }

            if (!string.IsNullOrWhiteSpace(siblingPath))
            {
                GuideInterpretationPolicyLoader.MergeToml(policy, siblingPath, replaceInstructionTables: false);
            }

            if (!string.IsNullOrWhiteSpace(documentToml))
            {
                GuideInterpretationPolicyLoader.MergeToml(policy, documentToml, replaceInstructionTables: false);
            }

            Bump(policy);
            return policy;
        }

        [CanBeNull]
        internal static string ResolveUserOverlayPath()
        {
            try
            {
                ModSyncSettings settings = ModSyncSettings.Load();
                if (!string.IsNullOrWhiteSpace(settings.GuideInterpretationPath)
                    && File.Exists(settings.GuideInterpretationPath))
                {
                    return settings.GuideInterpretationPath;
                }

                string besideSettings = Path.Combine(
                    ModSyncSettings.GetSettingsDirectory(),
                    GuideInterpretationPolicyLoader.UserFileName);
                return File.Exists(besideSettings) ? besideSettings : null;
            }
            catch (Exception ex)
            {
                Logger.LogVerbose($"[GuideInterpretation] User overlay path unresolved: {ex.Message}");
                return null;
            }
        }

        private static void TryMergeFile(
            [NotNull] GuideInterpretationPolicy policy,
            [CanBeNull] string path,
            bool replaceInstructionTables)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            try
            {
                GuideInterpretationPolicyLoader.MergeToml(
                    policy,
                    File.ReadAllText(path),
                    replaceInstructionTables);
            }
            catch (IOException ex)
            {
                Logger.LogWarning($"[GuideInterpretation] Could not read '{path}': {ex.Message}");
            }
        }

        private static void Bump([NotNull] GuideInterpretationPolicy policy)
        {
            s_version++;
            policy.Version = s_version;
        }
    }
}
