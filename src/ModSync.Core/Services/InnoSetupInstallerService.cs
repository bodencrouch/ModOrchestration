// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using JetBrains.Annotations;

using ModSync.Core.Utility;

namespace ModSync.Core.Services
{
    /// <summary>What to do with an <c>Execute</c> action that targets a Windows <c>.exe</c>.</summary>
    public enum ExeExecutionPlan
    {
        /// <summary>Hand it to the operating system, as before.</summary>
        ExecuteDirectly,

        /// <summary>Unpack it with innoextract and copy the game content out.</summary>
        InnoExtract,

        /// <summary>An Inno Setup installer on a platform that cannot run it, with no innoextract.</summary>
        MissingInnoExtract,
    }

    /// <summary>
    /// Routes Windows Inno Setup installers around exec on non-Windows platforms.
    /// <para>
    /// "The Sith Lords Restored Content Mod" -- the mandatory foundation of the whole K2 build --
    /// is generated as an <c>Execute</c> against <c>tslrcm2022.exe</c>. On a native-Linux game tree
    /// that failed with <c>Win32Exception ... Permission denied</c>, which says nothing about the
    /// real problem. Such installers are Inno Setup archives: innoextract unpacks them into an
    /// <c>app/</c> tree whose game folders can simply be copied into the game directory.
    /// </para>
    /// </summary>
    public static class InnoSetupInstallerService
    {
        /// <summary>The tool this depends on, named in the error the user sees.</summary>
        public const string ToolName = "innoextract";

        /// <summary>
        /// Directories inside an unpacked installer that hold game content. Everything else --
        /// notably <c>launcher</c>, a Windows helper -- stays behind.
        /// </summary>
        internal static readonly HashSet<string> GameContentDirectories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "override",
                "modules",
                "lips",
                "movies",
                "streammusic",
                "streamvoice",
                "streamsounds",
                "streamwaves",
                "rims",
                "texturepacks",
                "data",
            };

        /// <summary>
        /// Loose files at the root of an unpacked installer that belong in the game directory.
        /// </summary>
        internal static readonly HashSet<string> GameContentRootFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "dialog.tlk",
            };

        /// <summary>
        /// The decision, with no IO of its own so it can be tested exhaustively.
        /// </summary>
        /// <param name="isWindows">True when the host can run the executable itself.</param>
        /// <param name="isInnoSetup">True when the file was recognised as an Inno Setup installer.</param>
        /// <param name="innoExtractAvailable">True when <c>innoextract</c> is on PATH.</param>
        public static ExeExecutionPlan PlanExeExecution(
            bool isWindows,
            bool isInnoSetup,
            bool innoExtractAvailable)
        {
            // Windows runs its own installers, and a non-Inno executable is not ours to reinterpret
            // -- 7-Zip SFX archives and small helper tools still go straight to exec.
            if (isWindows || !isInnoSetup)
            {
                return ExeExecutionPlan.ExecuteDirectly;
            }

            return innoExtractAvailable
                ? ExeExecutionPlan.InnoExtract
                : ExeExecutionPlan.MissingInnoExtract;
        }

        /// <summary>
        /// The message shown when an Inno Setup installer cannot be unpacked. Names the tool and how
        /// to get it, instead of the opaque Win32Exception the exec attempt produced.
        /// </summary>
        [NotNull]
        public static string MissingToolMessage([CanBeNull] string exePath)
        {
            return $"'{Path.GetFileName(exePath ?? string.Empty)}' is a Windows Inno Setup installer and cannot be "
                + $"run on this platform. Install {ToolName} and re-run "
                + $"(Fedora: 'sudo dnf install {ToolName}', Debian/Ubuntu: 'sudo apt install {ToolName}', "
                + $"macOS: 'brew install {ToolName}').";
        }

        /// <summary>Inno Setup stamps this string into every installer it builds.</summary>
        private static readonly byte[] s_innoMarker = Encoding.ASCII.GetBytes("Inno Setup Setup Data");

        /// <summary>Inno Setup's loader header, present in installers that split their data out.</summary>
        private static readonly byte[] s_innoLoaderMarker = Encoding.ASCII.GetBytes("rDlPtS02");

        /// <summary>
        /// True when <paramref name="buffer"/> carries an Inno Setup signature. Separated from file
        /// IO so the detection rule is directly testable.
        /// </summary>
        internal static bool BufferLooksLikeInnoSetup([CanBeNull] byte[] buffer, int length)
        {
            if (buffer is null || length <= 0)
            {
                return false;
            }

            return IndexOf(buffer, length, s_innoMarker) >= 0
                || IndexOf(buffer, length, s_innoLoaderMarker) >= 0;
        }

        /// <summary>
        /// True when the file on disk is an Inno Setup installer. Reads a bounded prefix rather than
        /// assuming every <c>.exe</c> is Inno -- 7-Zip SFX archives are common in this library and
        /// must keep their existing handling.
        /// </summary>
        public static bool IsInnoSetupInstaller([CanBeNull] string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return false;
            }

            try
            {
                const int MaxScanBytes = 4 * 1024 * 1024;
                using (FileStream stream = File.OpenRead(exePath))
                {
                    int toRead = (int)Math.Min(MaxScanBytes, stream.Length);
                    byte[] buffer = new byte[toRead];
                    int read = 0;
                    while (read < toRead)
                    {
                        int chunk = stream.Read(buffer, read, toRead - read);
                        if (chunk <= 0)
                        {
                            break;
                        }

                        read += chunk;
                    }

                    return BufferLooksLikeInnoSetup(buffer, read);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.LogVerbose($"[InnoSetup] Could not inspect '{exePath}': {ex.Message}");
                return false;
            }
        }

        /// <summary>True when <c>innoextract</c> can be found on PATH.</summary>
        public static bool IsInnoExtractAvailable()
        {
            return !string.IsNullOrEmpty(FindInnoExtract());
        }

        [CanBeNull]
        public static string FindInnoExtract()
        {
            string pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVariable))
            {
                return null;
            }

            foreach (string directory in pathVariable.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                try
                {
                    string candidate = Path.Combine(directory.Trim(), ToolName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is not a reason to stop looking.
                }
            }

            return null;
        }

        /// <summary>
        /// Unpacks an Inno Setup installer and copies its game content into
        /// <paramref name="gameDirectory"/>. Returns false with a logged reason on any failure.
        /// </summary>
        public static async Task<bool> ExtractAndInstallAsync(
            [NotNull] string exePath,
            [NotNull] DirectoryInfo gameDirectory)
        {
            if (string.IsNullOrWhiteSpace(exePath))
            {
                throw new ArgumentNullException(nameof(exePath));
            }

            if (gameDirectory is null)
            {
                throw new ArgumentNullException(nameof(gameDirectory));
            }

            string workDirectory = Path.Combine(
                Path.GetTempPath(),
                "ModSync_inno_" + Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(workDirectory);

            try
            {
                await Logger.LogAsync(
                    $"'{Path.GetFileName(exePath)}' is a Windows Inno Setup installer; unpacking with {ToolName} "
                    + "instead of executing it.").ConfigureAwait(false);

                (int exitCode, string output, string error) = await PlatformAgnosticMethods.ExecuteProcessAsync(
                    FindInnoExtract() ?? ToolName,
                    $"--extract --output-dir \"{workDirectory}\" --progress=0 \"{exePath}\"").ConfigureAwait(false);

                if (exitCode != 0)
                {
                    await Logger.LogErrorAsync(
                        $"{ToolName} failed on '{Path.GetFileName(exePath)}' (exit {exitCode}). {error ?? output}")
                        .ConfigureAwait(false);
                    return false;
                }

                DirectoryInfo payload = LocatePayloadRoot(workDirectory);
                if (payload is null)
                {
                    await Logger.LogErrorAsync(
                        $"{ToolName} produced no game content for '{Path.GetFileName(exePath)}'.").ConfigureAwait(false);
                    return false;
                }

                int copied = CopyGameContent(payload, gameDirectory);
                if (copied == 0)
                {
                    await Logger.LogErrorAsync(
                        $"'{Path.GetFileName(exePath)}' unpacked, but held no recognisable game content.")
                        .ConfigureAwait(false);
                    return false;
                }

                await Logger.LogAsync(
                    $"Installed {copied} file(s) from '{Path.GetFileName(exePath)}' into '{gameDirectory.FullName}'.")
                    .ConfigureAwait(false);
                return true;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(workDirectory))
                    {
                        Directory.Delete(workDirectory, recursive: true);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Logger.LogVerbose($"[InnoSetup] Could not clean '{workDirectory}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// innoextract writes an installer's files under <c>app/</c>. Some installers have no such
        /// folder, in which case the extraction root is the payload.
        /// </summary>
        [CanBeNull]
        internal static DirectoryInfo LocatePayloadRoot([NotNull] string extractionRoot)
        {
            var root = new DirectoryInfo(extractionRoot);
            if (!root.Exists)
            {
                return null;
            }

            DirectoryInfo app = root.GetDirectories()
                .FirstOrDefault(d => string.Equals(d.Name, "app", StringComparison.OrdinalIgnoreCase));

            return app ?? root;
        }

        /// <summary>
        /// Copies the recognised game folders and root files from <paramref name="payload"/> into
        /// the game directory, and returns how many files were written. Anything unrecognised --
        /// <c>launcher</c>, readmes, the installer's own scaffolding -- is left alone.
        /// </summary>
        internal static int CopyGameContent(
            [NotNull] DirectoryInfo payload,
            [NotNull] DirectoryInfo gameDirectory)
        {
            int copied = 0;

            foreach (DirectoryInfo directory in payload.GetDirectories())
            {
                if (!GameContentDirectories.Contains(directory.Name))
                {
                    Logger.LogVerbose($"[InnoSetup] Skipping non-content folder '{directory.Name}'");
                    continue;
                }

                copied += CopyDirectory(directory, new DirectoryInfo(Path.Combine(gameDirectory.FullName, directory.Name)));
            }

            foreach (FileInfo file in payload.GetFiles())
            {
                if (!GameContentRootFiles.Contains(file.Name))
                {
                    continue;
                }

                string destination = Path.Combine(gameDirectory.FullName, file.Name);
                _ = Directory.CreateDirectory(gameDirectory.FullName);
                file.CopyTo(destination, overwrite: true);
                copied++;
            }

            return copied;
        }

        private static int CopyDirectory([NotNull] DirectoryInfo source, [NotNull] DirectoryInfo destination)
        {
            _ = Directory.CreateDirectory(destination.FullName);
            int copied = 0;

            foreach (FileInfo file in source.GetFiles())
            {
                file.CopyTo(Path.Combine(destination.FullName, file.Name), overwrite: true);
                copied++;
            }

            foreach (DirectoryInfo child in source.GetDirectories())
            {
                copied += CopyDirectory(child, new DirectoryInfo(Path.Combine(destination.FullName, child.Name)));
            }

            return copied;
        }

        /// <summary>True when the current host can execute a Windows <c>.exe</c> itself.</summary>
        public static bool HostRunsWindowsExecutables()
        {
            return UtilityHelper.GetOperatingSystem() == OSPlatform.Windows;
        }

        private static int IndexOf([NotNull] byte[] haystack, int length, [NotNull] byte[] needle)
        {
            if (needle.Length == 0 || length < needle.Length)
            {
                return -1;
            }

            byte first = needle[0];
            int last = length - needle.Length;
            for (int i = 0; i <= last; i++)
            {
                if (haystack[i] != first)
                {
                    continue;
                }

                int j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j])
                {
                    j++;
                }

                if (j == needle.Length)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
