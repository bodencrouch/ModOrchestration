// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

using JetBrains.Annotations;

using ModSync.Core.Services.Interpretation;
using ModSync.Core.Utility;

namespace ModSync.Core.Services
{
    /// <summary>
    /// Recovers HoloPatcher 1.5.1's Linux NSS builtin crash
    /// (<c>'str' object has no attribute 'info'</c>) the same way the K1/K2 manual oracles did:
    /// compile with the mod's <c>nwnnsscomp.exe</c> under Wine, then install the <c>.ncs</c>.
    /// Token-free scripts are rewritten from <c>[CompileList]</c> to <c>[InstallList]</c> before
    /// the patcher runs. Tokenized scripts keep <c>SaveProcessedScripts=1</c> and are compiled
    /// from <c>temp_nss_working_dir</c> after a crash.
    /// </summary>
    internal static class UnixNssCompileRecovery
    {
        internal const string BuiltinCrashMarker = "'str' object has no attribute 'info'";

        private static readonly Regex CompileListEntry = new Regex(
            @"^(?<key>(?:Replace|File|!ReplaceFile)\d+)\s*=\s*(?<file>.+\.nss)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex InstallFolderEntry = new Regex(
            @"^install_folder(?<n>\d+)\s*=\s*(?<dir>.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static bool HostNeedsWineCompiler() => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        internal static bool IsBuiltinNssCrash([CanBeNull] string patcherText)
        {
            if (string.IsNullOrEmpty(patcherText))
            {
                return false;
            }

            IReadOnlyList<string> markers = GuideInterpretationPolicyStore.Current.UnixNss.CrashMarkers;
            if (markers != null && markers.Count > 0)
            {
                for (int i = 0; i < markers.Count; i++)
                {
                    if (!string.IsNullOrEmpty(markers[i])
                        && patcherText.IndexOf(markers[i], StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }
                }

                return false;
            }

            return patcherText.IndexOf(BuiltinCrashMarker, StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// After a successful wine recovery, Holo's leftover <c>nwscript.nss</c> copy
        /// failures and the "install completed with errors" summary are not real
        /// missing-game-file problems — the recovered <c>.ncs</c> is already in Override.
        /// </summary>
        internal static bool IsRecoveredNssSupportError([CanBeNull] string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return false;
            }

            if (IsBuiltinNssCrash(line))
            {
                return true;
            }

            if (line.IndexOf("The install completed with errors", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (line.IndexOf("nwscript.nss", StringComparison.OrdinalIgnoreCase) >= 0
                && (line.IndexOf("Could not locate", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Could not load", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("FileNotFoundError", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            // Filename-less partner of the nwscript copy error (timestamp may contain ':').
            const string loadCopy = "Could not load source file to copy";
            int loadIdx = line.IndexOf(loadCopy, StringComparison.OrdinalIgnoreCase);
            if (loadIdx >= 0)
            {
                string after = line.Substring(loadIdx + loadCopy.Length).Trim().Trim(':').Trim();
                return after.Length == 0;
            }

            return false;
        }

        internal static void EnsureNwscriptInPatcherTree([NotNull] DirectoryInfo tslPatcherDirectory)
        {
            if (tslPatcherDirectory is null || !tslPatcherDirectory.Exists)
            {
                return;
            }

            string dest = Path.Combine(tslPatcherDirectory.FullName, "nwscript.nss");
            if (File.Exists(dest))
            {
                return;
            }

            EnsureInclude(tslPatcherDirectory.FullName, tslPatcherDirectory.FullName, "nwscript.nss");
            if (!File.Exists(dest))
            {
                return;
            }

            try
            {
                foreach (string dir in Directory.EnumerateDirectories(
                    tslPatcherDirectory.FullName,
                    "*",
                    SearchOption.AllDirectories))
                {
                    string leaf = Path.GetFileName(dir);
                    if (leaf == null
                        || (!leaf.StartsWith("mod", StringComparison.OrdinalIgnoreCase)
                            && !leaf.Equals("tslpatchdata", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    string copy = Path.Combine(dir, "nwscript.nss");
                    if (!File.Exists(copy))
                    {
                        File.Copy(dest, copy);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogVerbose($"[NSS] Could not stage nwscript.nss into namespace folders: {ex.Message}");
            }
        }

        internal static void EnableSaveProcessedScripts([NotNull] DirectoryInfo tslPatcherDirectory)
        {
            if (tslPatcherDirectory is null || !tslPatcherDirectory.Exists)
            {
                return;
            }

            if (!GuideInterpretationPolicyStore.Current.UnixNss.SaveProcessedScripts)
            {
                return;
            }

            try
            {
                TSLPatcher.IniHelper.ReplaceIniPattern(
                    tslPatcherDirectory,
                    @"^\s*SaveProcessedScripts\s*=\s*0\s*$",
                    "SaveProcessedScripts=1");
            }
            catch (Exception ex)
            {
                Logger.LogVerbose($"[NSS] Could not set SaveProcessedScripts=1: {ex.Message}");
            }
        }

        /// <summary>
        /// Wine-compiles token-free <c>[CompileList]</c> scripts and moves them onto
        /// <c>[InstallList]</c> so Holo never hits the Unix builtin.
        /// </summary>
        internal static int TryRewriteTokenFreeCompileList(
            [NotNull] DirectoryInfo tslPatcherDirectory,
            [CanBeNull] string namespaceArgument)
        {
            if (tslPatcherDirectory is null || !tslPatcherDirectory.Exists)
            {
                return 0;
            }

            string changesPath = FindActiveChangesIni(tslPatcherDirectory, namespaceArgument);
            if (string.IsNullOrEmpty(changesPath) || !File.Exists(changesPath))
            {
                return 0;
            }

            string[] lines = File.ReadAllLines(changesPath);
            var compileEntries = new List<CompileEntry>();
            string section = string.Empty;
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    section = trimmed.Trim('[', ']');
                    continue;
                }

                if (!section.Equals("CompileList", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Match match = CompileListEntry.Match(trimmed);
                if (!match.Success)
                {
                    continue;
                }

                compileEntries.Add(new CompileEntry
                {
                    LineIndex = i,
                    Key = match.Groups["key"].Value,
                    FileName = Path.GetFileName(match.Groups["file"].Value.Trim().Trim('"')),
                });
            }

            if (compileEntries.Count == 0)
            {
                return 0;
            }

            string changesDir = Path.GetDirectoryName(changesPath) ?? tslPatcherDirectory.FullName;
            int rewritten = 0;
            var compiledLeaves = new List<string>();
            foreach (CompileEntry entry in compileEntries)
            {
                string sourcePath = FindScript(changesDir, tslPatcherDirectory.FullName, entry.FileName);
                if (string.IsNullOrEmpty(sourcePath) || HasPatcherTokens(File.ReadAllText(sourcePath)))
                {
                    continue;
                }

                string ncsPath = CompileWithWine(sourcePath, tslPatcherDirectory.FullName);
                if (string.IsNullOrEmpty(ncsPath))
                {
                    continue;
                }

                string destNcs = Path.Combine(changesDir, Path.GetFileName(ncsPath));
                if (!PathsEqual(ncsPath, destNcs))
                {
                    File.Copy(ncsPath, destNcs, overwrite: true);
                }

                lines[entry.LineIndex] = string.Empty;
                compiledLeaves.Add(Path.GetFileName(destNcs));
                rewritten++;
            }

            if (rewritten == 0)
            {
                return 0;
            }

            File.WriteAllLines(changesPath, AppendToOverrideInstallList(lines, compiledLeaves));
            Logger.Log($"[NSS] Rewrote {rewritten} token-free CompileList script(s) to InstallList NCS via wine nwnnsscomp.exe.");
            return rewritten;
        }

        /// <summary>
        /// After a Holo Unix builtin crash, compile remaining scripts (processed copies first)
        /// and write the <c>.ncs</c> into the game Override folder.
        /// </summary>
        internal static bool TryInstallCompiledScripts(
            [NotNull] DirectoryInfo tslPatcherDirectory,
            [CanBeNull] string gameDirectory,
            [CanBeNull] string namespaceArgument)
        {
            if (tslPatcherDirectory is null || !tslPatcherDirectory.Exists || string.IsNullOrWhiteSpace(gameDirectory))
            {
                return false;
            }

            string overrideDir = ResolveOverrideDirectory(gameDirectory);
            if (string.IsNullOrEmpty(overrideDir))
            {
                return false;
            }

            Directory.CreateDirectory(overrideDir);

            string changesPath = FindActiveChangesIni(tslPatcherDirectory, namespaceArgument);
            var scriptNames = new List<string>();
            if (!string.IsNullOrEmpty(changesPath) && File.Exists(changesPath))
            {
                scriptNames.AddRange(ParseCompileListScripts(File.ReadAllLines(changesPath)));
            }

            if (scriptNames.Count == 0)
            {
                foreach (string nss in Directory.EnumerateFiles(tslPatcherDirectory.FullName, "*.nss", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(nss);
                    if (name.Equals("nwscript.nss", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("k_inc_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    scriptNames.Add(name);
                }
            }

            int installed = 0;
            int needed = 0;
            foreach (string scriptName in scriptNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                needed++;
                string leafNcs = Path.ChangeExtension(scriptName, ".ncs");
                string dest = Path.Combine(overrideDir, leafNcs);
                if (File.Exists(dest) && new FileInfo(dest).Length > 0)
                {
                    installed++;
                    continue;
                }

                string source = FindProcessedOrRawScript(tslPatcherDirectory.FullName, scriptName);
                if (string.IsNullOrEmpty(source))
                {
                    continue;
                }

                string ncsPath = CompileWithWine(source, tslPatcherDirectory.FullName);
                if (string.IsNullOrEmpty(ncsPath))
                {
                    continue;
                }

                File.Copy(ncsPath, dest, overwrite: true);
                installed++;
                Logger.Log($"[NSS] Installed recovered '{leafNcs}' to '{overrideDir}'.");
            }

            return needed > 0 && installed == needed;
        }

        [CanBeNull]
        private static string FindActiveChangesIni([NotNull] DirectoryInfo root, [CanBeNull] string namespaceArgument)
        {
            if (int.TryParse(
                    (namespaceArgument ?? string.Empty).Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int namespaceId))
            {
                string namespacesIni = Directory.EnumerateFiles(root.FullName, "namespaces.ini", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(namespacesIni))
                {
                    string iniName = ReadNamespaceIniName(namespacesIni, namespaceId);
                    if (!string.IsNullOrEmpty(iniName))
                    {
                        string named = Directory.EnumerateFiles(root.FullName, iniName, SearchOption.AllDirectories)
                            .FirstOrDefault();
                        if (!string.IsNullOrEmpty(named))
                        {
                            return named;
                        }
                    }
                }
            }

            return Directory.EnumerateFiles(root.FullName, "changes.ini", SearchOption.AllDirectories)
                .OrderBy(path => path.IndexOf("tslpatchdata", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                .FirstOrDefault();
        }

        [CanBeNull]
        private static string ReadNamespaceIniName([NotNull] string namespacesIniPath, int namespaceId)
        {
            string wanted = "Namespace" + namespaceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string section = string.Empty;
            foreach (string raw in File.ReadLines(namespacesIniPath))
            {
                string line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    section = line.Trim('[', ']');
                    continue;
                }

                if (!section.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                    && !section.Equals(namespaceId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, eq).Trim();
                if (key.Equals("IniName", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(eq + 1).Trim();
                }
            }

            return null;
        }

        [ItemNotNull]
        private static IEnumerable<string> ParseCompileListScripts([NotNull] IEnumerable<string> lines)
        {
            string section = string.Empty;
            foreach (string raw in lines)
            {
                string trimmed = raw.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    section = trimmed.Trim('[', ']');
                    continue;
                }

                if (!section.Equals("CompileList", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Match match = CompileListEntry.Match(trimmed);
                if (match.Success)
                {
                    yield return Path.GetFileName(match.Groups["file"].Value.Trim().Trim('"'));
                }
            }
        }

        [NotNull]
        private static string[] AppendToOverrideInstallList([NotNull] string[] lines, [NotNull] IList<string> ncsLeaves)
        {
            var output = new List<string>(lines);
            int installListIndex = -1;
            string overrideSection = null;
            int highestReplace = -1;
            string section = string.Empty;
            for (int i = 0; i < output.Count; i++)
            {
                string trimmed = output[i].Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    section = trimmed.Trim('[', ']');
                    if (section.Equals("InstallList", StringComparison.OrdinalIgnoreCase))
                    {
                        installListIndex = i;
                    }

                    continue;
                }

                if (section.Equals("InstallList", StringComparison.OrdinalIgnoreCase))
                {
                    Match folder = InstallFolderEntry.Match(trimmed);
                    if (folder.Success
                        && folder.Groups["dir"].Value.Trim().Equals("Override", StringComparison.OrdinalIgnoreCase))
                    {
                        overrideSection = "install_folder" + folder.Groups["n"].Value;
                    }
                }

                if (overrideSection != null && section.Equals(overrideSection, StringComparison.OrdinalIgnoreCase))
                {
                    Match replace = Regex.Match(trimmed, @"^(?:Replace|File)(?<n>\d+)\s*=", RegexOptions.IgnoreCase);
                    if (replace.Success)
                    {
                        highestReplace = Math.Max(highestReplace, int.Parse(replace.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }

            if (overrideSection is null)
            {
                if (installListIndex < 0)
                {
                    output.Add(string.Empty);
                    output.Add("[InstallList]");
                    installListIndex = output.Count - 1;
                }

                output.Insert(installListIndex + 1, "install_folder0=Override");
                overrideSection = "install_folder0";
                output.Add(string.Empty);
                output.Add("[" + overrideSection + "]");
            }

            int sectionHeader = output.FindIndex(line =>
            {
                string trimmed = line.Trim();
                return trimmed.Equals("[" + overrideSection + "]", StringComparison.OrdinalIgnoreCase);
            });
            int insertAt = sectionHeader + 1;
            while (insertAt < output.Count && !output[insertAt].TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                insertAt++;
            }

            foreach (string leaf in ncsLeaves)
            {
                highestReplace++;
                output.Insert(insertAt, "Replace" + highestReplace.ToString(System.Globalization.CultureInfo.InvariantCulture) + "=" + leaf);
                insertAt++;
            }

            return output.ToArray();
        }

        [CanBeNull]
        private static string FindScript([NotNull] string changesDir, [NotNull] string root, [NotNull] string fileName)
        {
            string beside = Path.Combine(changesDir, fileName);
            if (File.Exists(beside))
            {
                return beside;
            }

            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }

        [CanBeNull]
        private static string FindProcessedOrRawScript([NotNull] string root, [NotNull] string fileName)
        {
            foreach (string dir in Directory.EnumerateDirectories(root, "temp_nss_working_dir", SearchOption.AllDirectories))
            {
                string processed = Path.Combine(dir, fileName);
                if (File.Exists(processed) && new FileInfo(processed).Length > 0)
                {
                    return processed;
                }
            }

            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                .FirstOrDefault(path => path.IndexOf("temp_nss_working_dir", StringComparison.OrdinalIgnoreCase) < 0);
        }

        [CanBeNull]
        private static string ResolveOverrideDirectory([NotNull] string gameDirectory)
        {
            string[] candidates =
            {
                Path.Combine(gameDirectory, "Override"),
                Path.Combine(gameDirectory, "override"),
                Path.Combine(gameDirectory, "steamassets", "override"),
            };
            foreach (string candidate in candidates)
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(gameDirectory, "Override");
        }

        private static bool HasPatcherTokens([NotNull] string nssText)
        {
            return nssText.IndexOf("#2DAMEMORY", StringComparison.OrdinalIgnoreCase) >= 0
                || nssText.IndexOf("#StrRef", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        [CanBeNull]
        private static string CompileWithWine([NotNull] string sourceNss, [NotNull] string searchRoot)
        {
            string compiler = FindNwnnsscomp(Path.GetDirectoryName(sourceNss) ?? searchRoot, searchRoot);
            if (string.IsNullOrEmpty(compiler))
            {
                Logger.LogWarning("[NSS] nwnnsscomp.exe not found; cannot recover Unix builtin crash.");
                return null;
            }

            string wine = FindWine();
            if (string.IsNullOrEmpty(wine))
            {
                Logger.LogWarning("[NSS] wine not found; cannot recover Unix builtin crash.");
                return null;
            }

            string workDir = Path.GetDirectoryName(compiler);
            if (string.IsNullOrEmpty(workDir))
            {
                return null;
            }

            string stagedNss = Path.Combine(workDir, Path.GetFileName(sourceNss));
            if (!PathsEqual(sourceNss, stagedNss))
            {
                File.Copy(sourceNss, stagedNss, overwrite: true);
            }

            EnsureInclude(workDir, searchRoot, "nwscript.nss");
            string parent = Path.GetDirectoryName(sourceNss);
            if (!string.IsNullOrEmpty(parent))
            {
                foreach (string include in Directory.EnumerateFiles(parent, "*.nss"))
                {
                    string dest = Path.Combine(workDir, Path.GetFileName(include));
                    if (!File.Exists(dest))
                    {
                        File.Copy(include, dest);
                    }
                }
            }

            string expectedNcs = Path.ChangeExtension(stagedNss, ".ncs");
            if (File.Exists(expectedNcs))
            {
                File.Delete(expectedNcs);
            }

            var start = new ProcessStartInfo
            {
                FileName = wine,
                Arguments = QuoteArg(compiler) + " -c " + QuoteArg(Path.GetFileName(stagedNss)),
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.EnvironmentVariables["WINEDEBUG"] = "-all";
            start.EnvironmentVariables["WINEDLLOVERRIDES"] = "mscoree,mshtml=";
            string prefix = ResolveWinePrefix();
            if (!string.IsNullOrEmpty(prefix))
            {
                start.EnvironmentVariables["WINEPREFIX"] = prefix;
                Directory.CreateDirectory(prefix);
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using (var process = new Process { StartInfo = start })
            {
                process.OutputDataReceived += (sender, args) =>
                {
                    if (args.Data != null)
                    {
                        stdout.AppendLine(args.Data);
                    }
                };
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (args.Data != null)
                    {
                        stderr.AppendLine(args.Data);
                    }
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(180000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    Logger.LogWarning("[NSS] wine nwnnsscomp.exe timed out.");
                    return null;
                }
            }

            string combined = stdout + Environment.NewLine + stderr;
            if (!File.Exists(expectedNcs) || new FileInfo(expectedNcs).Length <= 0)
            {
                Logger.LogWarning($"[NSS] wine nwnnsscomp.exe produced no NCS for '{Path.GetFileName(sourceNss)}'. {combined}");
                return null;
            }

            if (combined.IndexOf("Error:", StringComparison.OrdinalIgnoreCase) >= 0
                && combined.IndexOf("Compiling:", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Logger.LogWarning($"[NSS] wine nwnnsscomp.exe reported an error for '{Path.GetFileName(sourceNss)}'. {combined}");
                return null;
            }

            Logger.LogVerbose($"[NSS] Compiled '{Path.GetFileName(sourceNss)}' -> '{expectedNcs}' ({new FileInfo(expectedNcs).Length} bytes).");
            return expectedNcs;
        }

        [CanBeNull]
        private static string _cachedCompiler;

        internal static void ResetCompilerCacheForTests() => _cachedCompiler = null;

        [CanBeNull]
        internal static string FindNwnnsscomp([NotNull] string preferredDir, [NotNull] string searchRoot)
        {
            string local = Path.Combine(preferredDir, "nwnnsscomp.exe");
            if (File.Exists(local))
            {
                return local;
            }

            string inRoot = SafeFindFirst(searchRoot, "nwnnsscomp.exe");
            if (!string.IsNullOrEmpty(inRoot))
            {
                return inRoot;
            }

            string env = Environment.GetEnvironmentVariable("MODSYNC_NWNNSSCOMP");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            {
                return env;
            }

            if (!string.IsNullOrEmpty(_cachedCompiler) && File.Exists(_cachedCompiler))
            {
                return _cachedCompiler;
            }

            string parent = Directory.GetParent(searchRoot)?.FullName;
            if (!string.IsNullOrEmpty(parent))
            {
                string sibling = FindNwnnsscompUnderExtractParent(parent);
                if (!string.IsNullOrEmpty(sibling))
                {
                    _cachedCompiler = sibling;
                    Logger.LogVerbose($"[NSS] Using nwnnsscomp.exe from sibling extract '{sibling}'.");
                    return sibling;
                }
            }

            return null;
        }

        [CanBeNull]
        private static string FindNwnnsscompUnderExtractParent([NotNull] string parent)
        {
            try
            {
                foreach (string child in Directory.EnumerateDirectories(parent))
                {
                    string[] candidates =
                    {
                        Path.Combine(child, "tslpatchdata", "nwnnsscomp.exe"),
                        Path.Combine(child, Path.GetFileName(child), "tslpatchdata", "nwnnsscomp.exe"),
                    };
                    foreach (string candidate in candidates)
                    {
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }

                return SafeFindFirst(parent, "nwnnsscomp.exe");
            }
            catch (Exception ex)
            {
                Logger.LogVerbose($"[NSS] Sibling nwnnsscomp search failed: {ex.Message}");
                return null;
            }
        }

        [CanBeNull]
        private static string SafeFindFirst([NotNull] string root, [NotNull] string fileName)
        {
            if (!Directory.Exists(root))
            {
                return null;
            }

            try
            {
                return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
            }
            catch (Exception)
            {
                return null;
            }
        }

        [CanBeNull]
        private static string FindWine()
        {
            string env = Environment.GetEnvironmentVariable("WINE");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            {
                return env;
            }

            foreach (string candidate in new[] { "/usr/bin/wine", "/usr/local/bin/wine" })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        [CanBeNull]
        private static string ResolveWinePrefix()
        {
            string env = Environment.GetEnvironmentVariable("WINEPREFIX");
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env;
            }

            // Prefer an already-booted prefix. A fresh wineboot is ~1.6G and this machine
            // often has only a few gigabytes free on /home.
            string tmp = Environment.GetEnvironmentVariable("TMPDIR");
            if (!string.IsNullOrWhiteSpace(tmp))
            {
                string sibling = Path.GetFullPath(Path.Combine(tmp, "..", "wine-nss"));
                if (File.Exists(Path.Combine(sibling, "system.reg")))
                {
                    return sibling;
                }
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                string cached = Path.Combine(home, ".cache", "modsync", "wine-nss");
                if (File.Exists(Path.Combine(cached, "system.reg")))
                {
                    return cached;
                }

                string workSibling = Path.Combine(home, "modsync-hot", "work", "wine-nss");
                if (File.Exists(Path.Combine(workSibling, "system.reg")))
                {
                    return workSibling;
                }

                return cached;
            }

            return Path.Combine(Path.GetTempPath(), "modsync-wine-nss");
        }

        private static void EnsureInclude([NotNull] string workDir, [NotNull] string searchRoot, [NotNull] string fileName)
        {
            string dest = Path.Combine(workDir, fileName);
            if (File.Exists(dest))
            {
                return;
            }

            string found = SafeFindFirst(searchRoot, fileName);
            if (string.IsNullOrEmpty(found))
            {
                string parent = Directory.GetParent(searchRoot)?.FullName;
                if (!string.IsNullOrEmpty(parent))
                {
                    found = FindIncludeUnderExtractParent(parent, fileName);
                }
            }

            if (!string.IsNullOrEmpty(found))
            {
                File.Copy(found, dest);
            }
        }

        [CanBeNull]
        private static string FindIncludeUnderExtractParent([NotNull] string parent, [NotNull] string fileName)
        {
            try
            {
                foreach (string child in Directory.EnumerateDirectories(parent))
                {
                    string[] candidates =
                    {
                        Path.Combine(child, "tslpatchdata", fileName),
                        Path.Combine(child, Path.GetFileName(child), "tslpatchdata", fileName),
                    };
                    foreach (string candidate in candidates)
                    {
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }

                return SafeFindFirst(parent, fileName);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool PathsEqual([NotNull] string left, [NotNull] string right)
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }

        [NotNull]
        private static string QuoteArg([CanBeNull] string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                return value;
            }

            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private sealed class CompileEntry
        {
            public int LineIndex { get; set; }

            public string Key { get; set; }

            public string FileName { get; set; }
        }
    }
}
