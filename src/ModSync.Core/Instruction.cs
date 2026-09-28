// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using ModSync.Core.Data;
using ModSync.Core.FileSystemUtils;
using ModSync.Core.TSLPatcher;
using ModSync.Core.Utility;
using Newtonsoft.Json;
namespace ModSync.Core
{
    public sealed class Instruction : INotifyPropertyChanged
    {
        [CanBeNull]
        private Services.FileSystem.IFileSystemProvider _fileSystemProvider;
        internal void SetFileSystemProvider([NotNull] Services.FileSystem.IFileSystemProvider provider) => _fileSystemProvider = provider ?? throw new ArgumentNullException(nameof(provider));

        /// <summary>
        /// Set by <see cref="Services.AutoInstructionGenerator"/> on the blanket <c>folder\*</c>
        /// sweeps it synthesizes from archive listings. Those wildcards match every file the mod
        /// author packaged -- readmes, screenshots, macOS resource forks, installer executables --
        /// and copy all of it into the game's Override folder. When this is set, wildcard matches
        /// that are packaging debris are dropped before the Move runs.
        /// <para>
        /// Deliberately internal and not serialized: it describes how the path was produced, not
        /// what the author asked for. A hand-authored instruction naming a specific file is never
        /// filtered.
        /// </para>
        /// </summary>
        internal bool ExcludeNonGameContent { get; set; }
        public enum ActionExitCode
        {
            UnauthorizedAccessException = -1,
            Success,
            InvalidSelfExtractingExecutable,
            InvalidArchive,
            ArchiveParseError,
            FileNotFoundPre = 4,
            FileNotFoundPost = 4,
            IOException,
            RenameTargetAlreadyExists,
            PatcherError,
            ChildProcessError,
            UnknownError,
            UnknownInnerError,
            TSLPatcherError,
            UnknownInstruction,
            TSLPatcherLogNotFound,
            FallbackArchiveExtractionFailed,
            OptionalInstallFailed,
        }
        public enum ActionType
        {
            Unset,
            Extract,
            Execute,
            Patcher,
            Move,
            Copy,
            Rename,
            Delete,
            DelDuplicate,
            Choose,
            Run,
            CleanList,
        }
        private ActionType _action;
        [NotNull] private string _arguments = string.Empty;
        [NotNull] private List<Guid> _dependencies = new List<Guid>();
        [NotNull] private string _destination = string.Empty;
        private bool _overwrite = true;
        [NotNull] private List<Guid> _restrictions = new List<Guid>();
        [NotNull][ItemNotNull] private List<string> _source = new List<string>();
        public static IEnumerable<string> ActionTypes => Enum.GetValues(typeof(ActionType)).Cast<ActionType>()
            .Select(actionType => actionType.ToString());
        [JsonIgnore]
        public ActionType Action
        {
            get => _action;
            set
            {
                if (_action == value)
                {
                    return;
                }

                _action = value;
                OnPropertyChanged();
            }
        }
        [JsonProperty(nameof(Action))]
        public string ActionString
        {
            get => Action.ToString();
            set => Action = (ActionType)Enum.Parse(typeof(ActionType), value);
        }
        [NotNull]
        [ItemNotNull]
        public IReadOnlyList<string> Source
        {
            get => _source;
            set
            {
                // CRITICAL: Check for infinite recursion with empty GUID strings
                if (value != null && value.Count == 1 && string.Equals(value[0], "00000000-0000-0000-0000-000000000000", StringComparison.Ordinal))
                {
                    Logger.LogError($"[Instruction.set_Source] INFINITE RECURSION DETECTED! Attempting to set empty GUID string. Current source: [{string.Join(", ", _source ?? new List<string>())}]. Breaking the loop.");
                    return; // Break the infinite loop
                }

                // Break change-notify loops when the contents are identical
                if (ReferenceEquals(_source, value))
                {
                    return;
                }
                if (_source != null && value != null && _source.Count == value.Count)
                {
                    bool same = true;
                    for (int i = 0; i < _source.Count; i++)
                    {
                        if (!string.Equals(_source[i], value[i], StringComparison.Ordinal))
                        {
                            same = false;
                            break;
                        }
                    }
                    if (same)
                    {
                        return;
                    }
                }

                Logger.LogVerbose($"[Instruction.set_Source] Setting new source and calling OnPropertyChanged");
                _source = value?.ToList() ?? new List<string>();
                OnPropertyChanged();
                Logger.LogVerbose($"[Instruction.set_Source] OnPropertyChanged completed");
            }
        }
        [NotNull]
        public string Destination
        {
            get => _destination;
            set
            {
                if (string.Equals(_destination, value, StringComparison.Ordinal))
                {
                    return;
                }

                _destination = value;
                OnPropertyChanged();
            }
        }
        public bool Overwrite
        {
            get => _overwrite;
            set
            {
                if (_overwrite == value)
                {
                    return;
                }

                _overwrite = value;
                OnPropertyChanged();
            }
        }
        [NotNull]
        public string Arguments
        {
            get => _arguments;
            set
            {
                if (string.Equals(_arguments, value, StringComparison.Ordinal))
                {
                    return;
                }

                _arguments = value;
                OnPropertyChanged();
            }
        }
        [NotNull]
        public List<Guid> Dependencies
        {
            get => _dependencies;
            set
            {
                if (_dependencies == value)
                {
                    return;
                }

                _dependencies = value;
                OnPropertyChanged();
            }
        }
        [NotNull]
        public List<Guid> Restrictions
        {
            get => _restrictions;
            set
            {
                if (_restrictions == value)
                {
                    return;
                }

                _restrictions = value;
                OnPropertyChanged();
            }
        }

        public bool ShouldSerializeOverwrite()
        {
            return Action == ActionType.Move || Action == ActionType.Copy || Action == ActionType.Rename;
        }
        public bool ShouldSerializeDestination()
        {
            return Action == ActionType.Move || Action == ActionType.Copy || Action == ActionType.Rename
                || Action == ActionType.Patcher || Action == ActionType.Delete || Action == ActionType.CleanList;
        }
        public bool ShouldSerializeArguments()
        {
            return Action == ActionType.DelDuplicate || Action == ActionType.Execute || Action == ActionType.Patcher;
        }
        [NotNull][ItemNotNull] private List<string> RealSourcePaths { get; set; } = new List<string>();
        [CanBeNull] private DirectoryInfo RealDestinationPath { get; set; }
        private ModComponent _parentComponent { get; set; }
        public Dictionary<FileInfo, SHA1> ExpectedChecksums { get; set; }
        public Dictionary<FileInfo, SHA1> OriginalChecksums { get; internal set; }
        public ModComponent GetParentComponent() => _parentComponent;
        public void SetParentComponent(ModComponent thisComponent) => _parentComponent = thisComponent;

        internal void SetRealPaths(MainConfig _, bool skipExistenceCheck = false)
        {
            SetRealPaths(skipExistenceCheck: skipExistenceCheck);
        }

        internal void SetRealPaths(bool sourceIsNotFilePath = false, bool skipExistenceCheck = false)
        {
            if (_fileSystemProvider is null)
            {
                throw new InvalidOperationException("File system provider must be set before calling SetRealPaths. Call SetFileSystemProvider() first.");
            }

            if (Source is null)
            {
                throw new InvalidOperationException($"Source is null for instruction");
            }

            // Security: Validate that source paths use placeholders, not absolute paths outside sandbox
            string sourcePath = MainConfig.SourcePath?.FullName ?? string.Empty;
            string destPath = MainConfig.DestinationPath?.FullName ?? string.Empty;
            var sanitizedSources = new List<string>();
            bool hasSecurityViolation = false;
            string violationPath = string.Empty;
            string violationResolvedPath = string.Empty;

            foreach (string rawSource in Source)
            {
                if (string.IsNullOrWhiteSpace(rawSource))
                {
                    sanitizedSources.Add(rawSource);
                    continue;
                }

                // Check if path is absolute and outside sandbox
                if (Path.IsPathRooted(rawSource) && !rawSource.StartsWith("<<modDirectory>>", StringComparison.OrdinalIgnoreCase) && !rawSource.StartsWith("<<kotorDirectory>>", StringComparison.OrdinalIgnoreCase) && !rawSource.StartsWith("<<gameDirectory>>", StringComparison.OrdinalIgnoreCase))
                {
                    // This is an absolute path that doesn't use placeholders - check if it's in sandbox
                    string resolvedPath = UtilityHelper.ReplaceCustomVariables(rawSource);
                    bool isInSandbox = (!string.IsNullOrEmpty(sourcePath) && resolvedPath.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase)) ||
                                      (!string.IsNullOrEmpty(destPath) && resolvedPath.StartsWith(destPath, StringComparison.OrdinalIgnoreCase));

                    if (!isInSandbox)
                    {
                        // Security violation - sanitize path and mark for exception
                        hasSecurityViolation = true;
                        violationPath = rawSource;
                        violationResolvedPath = resolvedPath;
                        // Sanitize: replace with a safe placeholder path
                        sanitizedSources.Add(string.IsNullOrEmpty(sourcePath) ? "<<modDirectory>>" : Path.Combine(sourcePath, Path.GetFileName(rawSource)));
                    }
                    else
                    {
                        // Path is in sandbox, allow it
                        sanitizedSources.Add(rawSource);
                    }
                }
                else
                {
                    sanitizedSources.Add(rawSource);
                }
            }

            if (hasSecurityViolation)
            {
                // Update Source with sanitized paths before throwing
                Source = sanitizedSources;
                throw new FileNotFoundException(
                    $"Security violation: Absolute path outside sandbox detected. Path '{violationPath}' (resolved to '{violationResolvedPath}') is not within allowed directories. Paths must use <<modDirectory>> or <<gameDirectory>> placeholders."
                );
            }

            Logger.LogVerbose($"[Instruction.SetRealPaths] Action={Action}, Source count={Source.Count}, sourceIsNotFilePath={sourceIsNotFilePath}, skipExistenceCheck={skipExistenceCheck}");
            Logger.LogVerbose($"[Instruction.SetRealPaths] Raw Source paths: [{string.Join(", ", Source)}]");
            Logger.LogVerbose($"[Instruction.SetRealPaths] MainConfig.SourcePath: {MainConfig.SourcePath?.FullName ?? "NULL"}");
            Logger.LogVerbose($"[Instruction.SetRealPaths] MainConfig.DestinationPath: {MainConfig.DestinationPath?.FullName ?? "NULL"}");
            List<string> newSourcePaths;
            if (!sourceIsNotFilePath)
            {
                Logger.LogVerbose($"[Instruction.SetRealPaths] Calling ReplaceCustomVariables on source paths...");
                var processedSource = Source.Select(UtilityHelper.ReplaceCustomVariables)
                    .Select(RemapExtractedTreeToScratch)
                    .ToList();
                Logger.LogVerbose($"[Instruction.SetRealPaths] After ReplaceCustomVariables on source: [{string.Join(", ", processedSource)}]");
                Logger.LogVerbose($"[Instruction.SetRealPaths] Calling EnumerateFilesWithWildcards with processed paths...");
                newSourcePaths = PathHelper.EnumerateFilesWithWildcards(processedSource, _fileSystemProvider);
                newSourcePaths = DropNonGameContentFromWildcardMatches(processedSource, newSourcePaths);
                if (skipExistenceCheck)
                {
                    foreach (string processedPath in processedSource)
                    {
                        if (string.IsNullOrWhiteSpace(processedPath)
                            || NetFrameworkCompatibility.Contains(processedPath, '*', StringComparison.Ordinal)
                            || NetFrameworkCompatibility.Contains(processedPath, '?', StringComparison.Ordinal))
                        {
                            continue;
                        }

                        string literalPath = PathHelper.FixPathFormatting(processedPath);
                        try
                        {
                            literalPath = Path.GetFullPath(literalPath);
                        }
                        catch (IOException)
                        {
                            // Keep formatted path when full path cannot be resolved.
                        }

                        if (newSourcePaths is null)
                        {
                            newSourcePaths = new List<string>();
                        }

                        bool literalAlreadyListed = MainConfig.CaseInsensitivePathing
                            ? newSourcePaths.Exists(p => string.Equals(p, literalPath, StringComparison.OrdinalIgnoreCase))
                            : newSourcePaths.Contains(literalPath);
                        if (!literalAlreadyListed)
                        {
                            newSourcePaths.Add(literalPath);
                        }
                    }
                }

                Logger.LogVerbose($"[Instruction.SetRealPaths] After EnumerateFilesWithWildcards: Found {newSourcePaths?.Count ?? 0} files");
                if (!skipExistenceCheck)
                {
                    if (newSourcePaths.IsNullOrEmptyOrAllNull())
                    {
                        Logger.LogVerbose($"[Instruction.SetRealPaths] ERROR: newSourcePaths is null/empty after wildcard expansion");
                        bool containsWildcards = processedSource.Any(path => path != null && (path.Contains('*') || path.Contains('?')));
                        if (containsWildcards)
                        {
                            throw new Exceptions.WildcardPatternNotFoundException(
                                Source,
                                _parentComponent?.Name
                            );
                        }
                        else
                        {
                            throw new FileNotFoundException(
                                $"Could not find file(s) in the 'Source' path on disk! Got [{string.Join(separator: ", ", Source)}]"
                            );
                        }
                    }
                    var missingFiles = newSourcePaths.Where(f => !_fileSystemProvider.FileExists(f)).ToList();
                    if (missingFiles.Count > 0)
                    {
                        Logger.LogVerbose($"[Instruction.SetRealPaths] ERROR: {missingFiles.Count} files do not exist: [{string.Join(", ", missingFiles)}]");
                        throw new FileNotFoundException(
                            $"Could not find all files in the 'Source' path on disk! Got [{string.Join(separator: ", ", Source)}]"
                        );
                    }
                }
                RealSourcePaths = (
                    MainConfig.CaseInsensitivePathing
                        ? newSourcePaths.Distinct(StringComparer.OrdinalIgnoreCase)
                        : newSourcePaths.Distinct(StringComparer.Ordinal)
                ).ToList();
            }
            else
            {
                Logger.LogVerbose($"[Instruction.SetRealPaths] sourceIsNotFilePath=true, calling ReplaceCustomVariables on original paths...");
                newSourcePaths = Source.Select(UtilityHelper.ReplaceCustomVariables).ToList();
                Logger.LogVerbose($"[Instruction.SetRealPaths] After ReplaceCustomVariables: [{string.Join(", ", newSourcePaths)}]");
            }
            string destinationPath = UtilityHelper.ReplaceCustomVariables(Destination);
            Logger.LogVerbose($"[Instruction.SetRealPaths] Raw Destination: {Destination ?? "NULL"}");
            Logger.LogVerbose($"[Instruction.SetRealPaths] After ReplaceCustomVariables on Destination: {destinationPath ?? "NULL"}");

            DirectoryInfo thisDestination = PathHelper.TryGetValidDirectoryInfo(destinationPath);
            Logger.LogVerbose($"[Instruction.SetRealPaths] TryGetValidDirectoryInfo result: {thisDestination?.FullName ?? "NULL"}");
            if (sourceIsNotFilePath)
            {
                RealDestinationPath = thisDestination;
                return;
            }
            bool skipDestinationValidation = Action == ActionType.Copy
                                            || Action == ActionType.Move
                                            || Action == ActionType.Rename
                                            || Action == ActionType.Extract
                                            || Action == ActionType.DelDuplicate;
            if (skipDestinationValidation && thisDestination is null && !string.IsNullOrWhiteSpace(destinationPath))
            {
                thisDestination = new DirectoryInfo(destinationPath);
            }

            // Copy/Move/Extract used to skip existence checks so they could create a missing
            // destination. On a case-sensitive volume that created steamassets/Override beside
            // the real steamassets/override (or threw DirectoryNotFoundException writing into the
            // missing Override). Always remap to an existing case-insensitive sibling first.
            if (thisDestination != null
                && MainConfig.CaseInsensitivePathing
                && _fileSystemProvider != null
                && !_fileSystemProvider.DirectoryExists(thisDestination.FullName))
            {
                DirectoryInfo caseMatched = PathHelper.GetCaseSensitivePath(thisDestination);
                if (caseMatched != null
                    && _fileSystemProvider.DirectoryExists(caseMatched.FullName))
                {
                    thisDestination = caseMatched;
                }
                else if (
                    !skipExistenceCheck
                    && !skipDestinationValidation
                    && Action != ActionType.DelDuplicate)
                {
                    throw new DirectoryNotFoundException("Could not find the 'Destination' path on disk!");
                }
            }
            RealDestinationPath = thisDestination;
        }

        internal bool TryGetResolvedDestinationFullName([CanBeNull] out string fullName)
        {
            fullName = RealDestinationPath?.FullName;
            return !string.IsNullOrWhiteSpace(fullName);
        }

        /// <summary>
        /// Returns resolved source full paths when <see cref="SetRealPaths"/> has already run.
        /// </summary>
        internal bool TryGetResolvedSourcePaths([CanBeNull] out IReadOnlyList<string> paths)
        {
            paths = RealSourcePaths;
            return RealSourcePaths != null && RealSourcePaths.Count > 0;
        }

        internal void RedirectResolvedDestination([NotNull] string newFullPath)
        {
            if (string.IsNullOrWhiteSpace(newFullPath))
            {
                throw new ArgumentException("Destination path cannot be null or whitespace.", nameof(newFullPath));
            }

            RealDestinationPath = new DirectoryInfo(Path.GetFullPath(newFullPath));
        }

        /// <summary>
        /// Replaces already-resolved source file paths after <see cref="SetRealPaths"/>.
        /// Used by managed install so later Move/Copy/Rename steps follow files into the staging tree.
        /// </summary>
        internal void RedirectResolvedSources([NotNull][ItemNotNull] IReadOnlyList<string> newFullPaths)
        {
            if (newFullPaths is null)
            {
                throw new ArgumentNullException(nameof(newFullPaths));
            }

            RealSourcePaths = new List<string>(newFullPaths);
        }
        /// <summary>
        /// Removes packaging debris from the expansion of a GENERATED <c>folder\*</c> sweep.
        /// <para>
        /// Only wildcard matches are considered: a literal source path is something someone asked
        /// for by name. If every match is filtered out the original list is kept, because turning
        /// "this component installed a readme" into "this component failed" would be a worse bug
        /// than the one being fixed.
        /// </para>
        /// </summary>
        [CanBeNull]
        private List<string> DropNonGameContentFromWildcardMatches(
            [NotNull][ItemCanBeNull] IReadOnlyList<string> processedSource,
            [CanBeNull] List<string> resolvedPaths)
        {
            if (!ExcludeNonGameContent || resolvedPaths is null || resolvedPaths.Count == 0)
            {
                return resolvedPaths;
            }

            bool anyWildcard = processedSource.Any(p =>
                !string.IsNullOrEmpty(p)
                && (p.IndexOf('*') >= 0 || p.IndexOf('?') >= 0));
            if (!anyWildcard)
            {
                return resolvedPaths;
            }

            var kept = resolvedPaths
                .Where(p => !Services.NonGameContentFilter.IsNonGameContent(p))
                .ToList();

            if (kept.Count == 0)
            {
                return resolvedPaths;
            }

            if (kept.Count != resolvedPaths.Count)
            {
                Logger.LogVerbose(
                    $"[Instruction.SetRealPaths] Excluded {resolvedPaths.Count - kept.Count} non-game file(s) "
                    + "from a generated wildcard sweep.");
            }

            return kept;
        }

        [NotNull]
        private string RemapExtractedTreeToScratch([NotNull] string path)
        {
            if (Action == ActionType.Extract
                || MainConfig.ExtractScratchPath is null
                || MainConfig.SourcePath is null
                || string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            ModComponent parent = GetParentComponent();
            ModComponent installComponent = ResolveComponentThatOwnsExtract(parent);
            if (installComponent?.Instructions is null
                || !installComponent.Instructions.Any(i => i.Action == ActionType.Extract))
            {
                return path;
            }

            string usbRoot;
            string full;
            try
            {
                usbRoot = Path.GetFullPath(MainConfig.SourcePath.FullName);
                full = Path.GetFullPath(path);
            }
            catch (IOException)
            {
                return path;
            }

            if (!full.StartsWith(usbRoot, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            string relative = full.Substring(usbRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.Combine(MainConfig.ExtractScratchPath.FullName, relative);
        }

        /// <summary>
        /// Namespace <see cref="Option"/>s are themselves <see cref="ModComponent"/>s. Patcher
        /// instructions on an option set their parent to that option, but the Extract that created
        /// the on-disk tree lives on the outer component. Remapping <c>&lt;&lt;modDirectory&gt;&gt;</c>
        /// paths into the extract scratch must follow that outer Extract — otherwise Choose/Patcher
        /// looks for Installer.exe under the archive store after a successful extract to scratch
        /// (measured: K1 Ported Alien VO Replacements / PAVOR).
        /// </summary>
        [CanBeNull]
        private static ModComponent ResolveComponentThatOwnsExtract([CanBeNull] ModComponent parent)
        {
            if (parent is null || !(parent is Option))
            {
                return parent;
            }

            IReadOnlyList<ModComponent> all = MainConfig.AllComponents;
            if (all is null || all.Count == 0)
            {
                return parent;
            }

            foreach (ModComponent candidate in all)
            {
                if (candidate?.Options is null || candidate.Options.Count == 0)
                {
                    continue;
                }

                foreach (Option option in candidate.Options)
                {
                    if (option is null)
                    {
                        continue;
                    }

                    if (ReferenceEquals(option, parent) || option.Guid == parent.Guid)
                    {
                        return candidate;
                    }
                }
            }

            return parent;
        }

        [CanBeNull]
        private static string RedirectExtractDestinationToScratch([NotNull] string sourcePath, [CanBeNull] string destinationPath)
        {
            if (MainConfig.ExtractScratchPath is null)
            {
                return destinationPath;
            }

            string archiveDirectory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
            string dest = string.IsNullOrEmpty(destinationPath) ? archiveDirectory : destinationPath;
            string destFull;
            string archiveDirFull;
            try
            {
                destFull = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                archiveDirFull = Path.GetFullPath(archiveDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (IOException)
            {
                return destinationPath;
            }

            bool destIsArchiveDir = string.Equals(destFull, archiveDirFull, StringComparison.OrdinalIgnoreCase);
            bool destIsOnArchiveStore = false;
            string sourceRootFull = null;
            if (MainConfig.SourcePath != null)
            {
                sourceRootFull = Path.GetFullPath(MainConfig.SourcePath.FullName)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                destIsOnArchiveStore = destFull.Equals(sourceRootFull, StringComparison.OrdinalIgnoreCase)
                    || destFull.StartsWith(sourceRootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || destFull.StartsWith(sourceRootFull + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }

            if (!destIsArchiveDir && !destIsOnArchiveStore)
            {
                return destinationPath;
            }

            string relative;
            if (destIsArchiveDir || sourceRootFull is null || destFull.Equals(sourceRootFull, StringComparison.OrdinalIgnoreCase))
            {
                relative = Path.GetFileNameWithoutExtension(sourcePath);
            }
            else
            {
                relative = destFull.Substring(sourceRootFull.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            if (string.IsNullOrEmpty(relative))
            {
                return destinationPath;
            }

            string scratchDest = Path.Combine(MainConfig.ExtractScratchPath.FullName, relative);
            _ = Directory.CreateDirectory(scratchDest);
            return scratchDest;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0051:Method is too long", Justification = "<Pending>")]
        public async Task<ActionExitCode> ExtractFileAsync(
            DirectoryInfo argDestinationPath = null,
            [NotNull][ItemNotNull] IReadOnlyList<string> argSourcePaths = null
        )
        {
            if (_fileSystemProvider is null)
            {
                throw new InvalidOperationException("File system provider must be set before calling ExtractFileAsync. Call SetFileSystemProvider() first.");
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (argSourcePaths.IsNullOrEmptyCollection())
                {
                    argSourcePaths = RealSourcePaths;
                }

                if (argSourcePaths.IsNullOrEmptyCollection())
                {
                    throw new ArgumentNullException(nameof(argSourcePaths));
                }

                RealSourcePaths = argSourcePaths.ToList();
                foreach (string sourcePath in RealSourcePaths)
                {
                    string destinationPath = argDestinationPath?.FullName ?? RealDestinationPath?.FullName ?? Path.GetDirectoryName(sourcePath);
                    string originalDestination = destinationPath;
                    destinationPath = RedirectExtractDestinationToScratch(sourcePath, destinationPath);
                    if (!string.IsNullOrEmpty(destinationPath)
                        && !string.Equals(originalDestination, destinationPath, StringComparison.OrdinalIgnoreCase))
                    {
                        await Logger.LogAsync(
                            $"Extract destination redirected off archive store: '{originalDestination}' → '{destinationPath}'"
                        ).ConfigureAwait(false);
                    }
                    if (string.IsNullOrEmpty(destinationPath))
                    {
                        await Logger.LogErrorAsync($"Could not determine destination path for archive: {sourcePath}").ConfigureAwait(false);
                        return ActionExitCode.InvalidArchive;
                    }
                    try
                    {
                        List<string> extractedFiles = await _fileSystemProvider.ExtractArchiveAsync(sourcePath, destinationPath).ConfigureAwait(false);
                        await Logger.LogAsync($"Extracted {extractedFiles.Count} file(s) from '{Path.GetFileName(sourcePath)}' to '{destinationPath}'").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await Logger.LogExceptionAsync(ex).ConfigureAwait(false);
                        sw.Stop();
                        Services.TelemetryService.Instance.RecordFileOperation(
                            operationType: "extract",
                            success: false,
                            fileCount: RealSourcePaths.Count,
                            durationMs: sw.Elapsed.TotalMilliseconds,
                            errorMessage: ex.Message
                        );
                        return ActionExitCode.InvalidArchive;
                    }
                }

                sw.Stop();
                Services.TelemetryService.Instance.RecordFileOperation(
                    operationType: "extract",
                    success: true,
                    fileCount: RealSourcePaths.Count,
                    durationMs: sw.Elapsed.TotalMilliseconds
                );
                return ActionExitCode.Success;
            }
            catch (ArgumentNullException ex)
            {
                await Logger.LogExceptionAsync(ex).ConfigureAwait(false);
                sw.Stop();
                Services.TelemetryService.Instance.RecordFileOperation(
                    operationType: "extract",
                    success: false,
                    fileCount: RealSourcePaths?.Count ?? 0,
                    durationMs: sw.Elapsed.TotalMilliseconds,
                    errorMessage: ex.Message
                );
                return ActionExitCode.InvalidArchive;
            }
            catch (Exception ex)
            {
                await Logger.LogExceptionAsync(ex).ConfigureAwait(false);
                sw.Stop();
                Services.TelemetryService.Instance.RecordFileOperation(
                    operationType: "extract",
                    success: false,
                    fileCount: RealSourcePaths?.Count ?? 0,
                    durationMs: sw.Elapsed.TotalMilliseconds,
                    errorMessage: ex.Message
                );
                return ActionExitCode.UnknownError;
            }
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0051:Method is too long", Justification = "<Pending>")]
        public void DeleteDuplicateFile(
                DirectoryInfo directoryPath = null,
                string fileExtension = null,
                bool caseInsensitive = true,
                IReadOnlyList<string> compatibleExtensions = null
            )
        {
            if (_fileSystemProvider is null)
            {
                throw new InvalidOperationException("File system provider must be set before calling DeleteDuplicateFile. Call SetFileSystemProvider() first.");
            }

            if (directoryPath is null)
            {
                directoryPath = RealDestinationPath;
            }

            if (!(directoryPath is null) && !_fileSystemProvider.DirectoryExists(directoryPath.FullName) && MainConfig.CaseInsensitivePathing)
            {
                directoryPath = PathHelper.GetCaseSensitivePath(directoryPath);
            }

            if (directoryPath is null || !_fileSystemProvider.DirectoryExists(directoryPath.FullName))
            {
                throw new ArgumentException(message: "Invalid directory path.", nameof(directoryPath));
            }

            List<string> sourceExtensions = Source?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
            IReadOnlyList<string> tempCompatibleExtensions = compatibleExtensions ?? (!sourceExtensions.IsNullOrEmptyOrAllNull() ? sourceExtensions : null);
            compatibleExtensions = tempCompatibleExtensions?.ToList() ?? Game.TextureOverridePriorityList;
            if (string.IsNullOrEmpty(fileExtension))
            {
                fileExtension = Arguments;
            }

            // Recurse into subdirectories: duplicate .tga/.tpc/.dds pairs can be written by mods
            // into Override subfolders, not just the top level, so a top-directory-only scan
            // would silently leave those duplicates in place. Grouping is scoped per-directory
            // (see BuildDuplicateGroupKey) -- two same-named files in *different* subfolders are
            // not "duplicates" of each other, only same-named files within the same folder are.
            List<string> filesList = _fileSystemProvider.GetFilesInDirectory(directoryPath.FullName, "*.*", SearchOption.AllDirectories);
            Dictionary<string, List<string>> fileGroups = GroupFilesByBaseNameForCompatibleExtensions(
                _fileSystemProvider, filesList, compatibleExtensions, caseInsensitive);
            Dictionary<string, int> fileNameCounts = caseInsensitive
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<string>> group in fileGroups)
            {
                fileNameCounts[group.Key] = group.Value.Count;
            }
            foreach (string filePath in filesList)
            {
                if (!ShouldDeleteFile(filePath))
                {
                    continue;
                }

                try
                {
                    _ = _fileSystemProvider.DeleteFileAsync(filePath);
                    string fileName = _fileSystemProvider.GetFileName(filePath);
                    _ = Logger.LogAsync($"Deleted file: '{fileName}'");
                    string baseName = Path.GetFileNameWithoutExtension(fileName);
                    string groupKey = BuildDuplicateGroupKey(filePath, baseName);
                    int count = fileNameCounts[groupKey] - 1;
                    _ = Logger.LogVerboseAsync(
                        $"Leaving alone '{count.ToString(System.Globalization.CultureInfo.InvariantCulture)}' file(s) with the same name of '{baseName}'."
                    );
                }
                catch (Exception ex)
                {
                    Logger.LogException(ex);
                }
            }
            bool ShouldDeleteFile(string filePath)
            {
                string fileName = _fileSystemProvider?.GetFileName(filePath);
                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
                string fileExtensionFromFile = Path.GetExtension(fileName);
                string groupKey = BuildDuplicateGroupKey(filePath, fileNameWithoutExtension);
                if (string.IsNullOrEmpty(fileNameWithoutExtension))
                {
                    _ = Logger.LogWarningAsync(
                        $"Skipping '{fileName}' Reason: fileNameWithoutExtension is null/empty somehow?"
                    );
                }
                else if (!fileNameCounts.TryGetValue(groupKey, out int value))
                {
                    _ = Logger.LogVerboseAsync(
                        $"Skipping '{fileName}' Reason: Not present in dictionary, ergo does not have a desired extension."
                    );
                }
                else if (value <= 1)
                {
                    _ = Logger.LogVerboseAsync(
                        $"Skipping '{fileName}' Reason: '{fileNameWithoutExtension}' is the only file with this name."
                    );
                }
                else if (!string.Equals(fileExtensionFromFile, fileExtension, StringComparison.OrdinalIgnoreCase))
                {
                    string caseInsensitivity = caseInsensitive
                        ? " (case-insensitive)"
                        : string.Empty;
                    string message =
                        $"Skipping '{fileName}' Reason: '{fileExtensionFromFile}' is not the desired extension '{fileExtension}'{caseInsensitivity}";
                    _ = Logger.LogVerboseAsync(message);
                }
                else
                {
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Builds the key used to decide whether two files are "the same file, different
        /// extension": the containing directory plus the filename without its extension.
        /// Duplicate detection is intentionally scoped per-directory -- two same-named files that
        /// live in different subfolders of Override are not duplicates of one another.
        /// </summary>
        private static string BuildDuplicateGroupKey([NotNull] string filePath, [CanBeNull] string fileNameWithoutExtension)
        {
            string directoryPart = Path.GetDirectoryName(filePath) ?? string.Empty;
            return directoryPart + "|" + fileNameWithoutExtension;
        }

        /// <summary>
        /// Groups every file under a pre-enumerated file list by its containing directory plus
        /// filename (without extension), restricted to files whose extension is one of
        /// <paramref name="compatibleExtensions"/>. Shared between <see cref="DeleteDuplicateFile"/>
        /// (single-extension purge, driven by a per-instruction <c>Arguments</c> value) and
        /// <see cref="RunFinalDuplicateSweepAsync"/> (purges whatever extensions the guide's own
        /// DelDuplicate instructions named, run once after every component in a guide has
        /// installed) so both duplicate-detection paths agree on what counts as "the same file,
        /// different extension".
        /// </summary>
        private static Dictionary<string, List<string>> GroupFilesByBaseNameForCompatibleExtensions(
            [NotNull] Services.FileSystem.IFileSystemProvider fileSystemProvider,
            [NotNull][ItemNotNull] List<string> filesList,
            [NotNull] IReadOnlyList<string> compatibleExtensions,
            bool caseInsensitive)
        {
            Dictionary<string, List<string>> groups = caseInsensitive
                ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (string filePath in filesList)
            {
                string fileName = fileSystemProvider.GetFileName(filePath);
                string thisExtension = Path.GetExtension(fileName);
                bool compatibleExtensionFound = caseInsensitive
                    ? compatibleExtensions.Any(ext => ext.Equals(thisExtension, StringComparison.OrdinalIgnoreCase))
                    : compatibleExtensions.Contains(thisExtension, StringComparer.Ordinal);
                if (!compatibleExtensionFound)
                {
                    continue;
                }

                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
                if (string.IsNullOrEmpty(fileNameWithoutExtension))
                {
                    continue;
                }

                string groupKey = BuildDuplicateGroupKey(filePath, fileNameWithoutExtension);
                if (!groups.TryGetValue(groupKey, out List<string> group))
                {
                    group = new List<string>();
                    groups[groupKey] = group;
                }

                group.Add(filePath);
            }

            return groups;
        }

        /// <summary>
        /// Final post-install safety-net sweep for duplicate texture-override pairs.
        /// <para>
        /// <see cref="DeleteDuplicateFile"/> (the <c>DelDuplicate</c> instruction) runs once, at a
        /// fixed point in a guide's install order, and only purges duplicates that exist in
        /// <paramref name="directoryFullName"/> at that exact moment. Components that run
        /// <em>after</em> it can (and do) write fresh files that recreate duplicate extension pairs
        /// for the same basename -- those never get cleaned up because dedup never runs again.
        /// </para>
        /// <para>
        /// This method re-applies the same "does this basename have 2+ files across
        /// <paramref name="compatibleExtensions"/>" detection <see cref="DeleteDuplicateFile"/>
        /// already uses (via <see cref="GroupFilesByBaseNameForCompatibleExtensions"/>). For every
        /// duplicate group found, it deletes whichever files match an extension in
        /// <paramref name="purgeExtensions"/> -- the same extension(s) the guide's own
        /// <c>DelDuplicate</c> instructions already named via their <c>Arguments</c> value -- and
        /// keeps everything else. This mirrors the per-instruction path exactly instead of
        /// re-deriving a "which extension wins" rule from <paramref name="compatibleExtensions"/>'s
        /// order: that list is only ever a co-occurrence/membership set (e.g. ".dds"/".tpc"/".tga"
        /// are "the same override texture, different format"), not a survival-priority ordering.
        /// Stems with only one compatible extension present are left untouched entirely -- there is
        /// nothing to dedupe there, which is what correctly leaves already-resolved
        /// legitimate-exception stems alone.
        /// </para>
        /// Intended to be invoked once, by the overall install loop, after every component in the
        /// guide has finished installing.
        /// </summary>
        /// <param name="fileSystemProvider">Provider used to enumerate and delete files.</param>
        /// <param name="directoryFullName">Directory to sweep (e.g. the game's Override folder).</param>
        /// <param name="purgeExtensions">
        /// The extension(s) to remove from a duplicate group when found alongside at least one
        /// other compatible-extension file, collected from the guide's own DelDuplicate
        /// instructions. If empty, the sweep is a no-op.
        /// </param>
        /// <param name="compatibleExtensions">
        /// The co-occurrence set used to detect duplicate groups. Defaults to
        /// <see cref="Data.Game.TextureOverridePriorityList"/>.
        /// </param>
        /// <returns>The number of files deleted by the sweep.</returns>
        public static async Task<int> RunFinalDuplicateSweepAsync(
            [NotNull] Services.FileSystem.IFileSystemProvider fileSystemProvider,
            [NotNull] string directoryFullName,
            [NotNull] IReadOnlyCollection<string> purgeExtensions,
            [CanBeNull] IReadOnlyList<string> compatibleExtensions = null,
            bool caseInsensitive = true)
        {
            if (fileSystemProvider is null)
            {
                throw new ArgumentNullException(nameof(fileSystemProvider));
            }

            if (string.IsNullOrEmpty(directoryFullName))
            {
                throw new ArgumentException(message: "Invalid directory path.", nameof(directoryFullName));
            }

            if (purgeExtensions is null)
            {
                throw new ArgumentNullException(nameof(purgeExtensions));
            }

            if (purgeExtensions.Count == 0 || !fileSystemProvider.DirectoryExists(directoryFullName))
            {
                return 0;
            }

            IReadOnlyList<string> extensions = compatibleExtensions ?? Data.Game.TextureOverridePriorityList;
            if (extensions is null || extensions.Count == 0)
            {
                return 0;
            }

            List<string> filesList = fileSystemProvider.GetFilesInDirectory(directoryFullName, "*.*", SearchOption.AllDirectories);
            Dictionary<string, List<string>> fileGroups = GroupFilesByBaseNameForCompatibleExtensions(
                fileSystemProvider, filesList, extensions, caseInsensitive);

            StringComparison extensionComparison = caseInsensitive
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            int deletedCount = 0;
            foreach (List<string> group in fileGroups.Values)
            {
                if (group.Count <= 1)
                {
                    // Only one compatible extension present for this stem -- nothing to dedupe.
                    // This is also what naturally leaves already-single-extension stems (e.g.
                    // legitimate guide exceptions already resolved by their own DelDuplicate pass)
                    // untouched.
                    continue;
                }

                List<string> toDelete = group.Where(filePath => purgeExtensions.Any(ext =>
                    string.Equals(Path.GetExtension(fileSystemProvider.GetFileName(filePath)), ext, extensionComparison))).ToList();
                if (toDelete.Count == 0)
                {
                    continue;
                }

                if (toDelete.Count == group.Count)
                {
                    // Guard: never empty a duplicate group entirely. If every file in the group
                    // matches a purge extension (e.g. a guide purges both ".tpc" and ".tga" and a
                    // stem only ever had those two), leave the group alone rather than delete the
                    // last surviving file -- that would be strictly worse than doing nothing.
                    continue;
                }

                List<string> survivors = group.Except(toDelete, StringComparer.Ordinal).ToList();
                string survivorNames = string.Join(", ", survivors.Select(fileSystemProvider.GetFileName));

                foreach (string filePath in toDelete)
                {
                    try
                    {
                        await fileSystemProvider.DeleteFileAsync(filePath).ConfigureAwait(false);
                        string fileName = fileSystemProvider.GetFileName(filePath);
                        string stem = Path.GetFileNameWithoutExtension(fileName);
                        await Logger.LogWarningAsync(
                            $"Final duplicate sweep: removed '{fileName}' (stem '{stem}') because a later " +
                            $"component recreated a duplicate after the guide's own DelDuplicate instruction " +
                            $"already ran; keeping '{survivorNames}'."
                        ).ConfigureAwait(false);
                        deletedCount++;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogException(ex);
                    }
                }
            }

            return deletedCount;
        }

        /// <summary>
        /// Runs <see cref="RunFinalDuplicateSweepAsync"/> once after a guide finishes installing,
        /// re-applying every DelDuplicate purge that the installed components (and their selected
        /// options) actually executed, against the same directory each one resolved at run time.
        /// </summary>
        /// <returns>The total number of files the sweep deleted.</returns>
        internal static async Task<int> RunFinalDuplicateSweepAfterInstallAsync(
            [NotNull][ItemNotNull] IEnumerable<ModComponent> installedComponents)
        {
            if (installedComponents is null)
            {
                throw new ArgumentNullException(nameof(installedComponents));
            }

            var executed = new List<Instruction>();
            foreach (ModComponent component in installedComponents)
            {
                executed.AddRange(component.Instructions.Where(i => i != null && i.Action == ActionType.DelDuplicate));
                foreach (Option option in component.Options.Where(o => o != null && o.IsSelected))
                {
                    executed.AddRange(option.Instructions.Where(i => i != null && i.Action == ActionType.DelDuplicate));
                }
            }

            int deleted = 0;
            foreach (IGrouping<string, Instruction> byDirectory in executed
                .Where(i => i._fileSystemProvider != null
                    && i.RealDestinationPath != null
                    && !string.IsNullOrWhiteSpace(i.Arguments))
                .GroupBy(i => i.RealDestinationPath.FullName, StringComparer.OrdinalIgnoreCase))
            {
                var purgeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var compatibleExtensions = new List<string>();
                foreach (Instruction instruction in byDirectory)
                {
                    string extension = instruction.Arguments.Trim();
                    _ = purgeExtensions.Add(extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension);
                    if (instruction.Source != null)
                    {
                        compatibleExtensions.AddRange(instruction.Source.Where(e =>
                            !string.IsNullOrWhiteSpace(e)
                            && !compatibleExtensions.Contains(e, StringComparer.OrdinalIgnoreCase)));
                    }
                }

                deleted += await RunFinalDuplicateSweepAsync(
                        byDirectory.First()._fileSystemProvider,
                        byDirectory.Key,
                        purgeExtensions,
                        compatibleExtensions.Count > 0 ? compatibleExtensions : null)
                    .ConfigureAwait(false);
            }

            return deleted;
        }

        /// <summary>
        /// Executes a cleanlist operation: reads a CSV file where each line contains a mod name and files to delete,
        /// and deletes those files if the corresponding mod is selected.
        /// </summary>
        /// <param name="cleanlistPath">Path to the cleanlist file. If null, uses RealSourcePaths[0].</param>
        /// <param name="targetDirectory">Directory where files should be deleted from. If null, uses RealDestinationPath.</param>
        /// <param name="isModSelectedFunc">Function that checks if a mod name is selected. If null, always returns true.</param>
        /// <returns>ActionExitCode indicating success or failure.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0051:Method is too long", Justification = "<Pending>")]
        public async Task<ActionExitCode> ExecuteCleanListAsync(
            string cleanlistPath = null,
            DirectoryInfo targetDirectory = null,
            Func<string, bool> isModSelectedFunc = null
        )
        {
            if (_fileSystemProvider is null)
            {
                throw new InvalidOperationException("File system provider must be set before calling ExecuteCleanListAsync. Call SetFileSystemProvider() first.");
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            ActionExitCode exitCode = ActionExitCode.Success;
            int totalFilesDeleted = 0;

            try
            {
                // Determine cleanlist file path
                if (string.IsNullOrEmpty(cleanlistPath))
                {
                    if (RealSourcePaths is null || RealSourcePaths.Count == 0)
                    {
                        throw new ArgumentException("No cleanlist file path provided and RealSourcePaths is empty.", nameof(cleanlistPath));
                    }

                    cleanlistPath = RealSourcePaths[0];
                }

                // Determine target directory
                if (targetDirectory is null)
                {
                    targetDirectory = RealDestinationPath;
                }

                if (targetDirectory is null)
                {
                    throw new ArgumentException("No target directory specified for cleanlist operation.", nameof(targetDirectory));
                }

                // Check if cleanlist file exists. The guide hosts these under
                // mod-builds/scripts/, not the archive store; VFS dry-run cannot see them
                // at <<modDirectory>>\cleanlist_k1.txt.
                string cleanlistContent = null;
                if (_fileSystemProvider.FileExists(cleanlistPath))
                {
                    cleanlistContent = await _fileSystemProvider.ReadFileAsync(cleanlistPath).ConfigureAwait(false);
                }
                else
                {
                    string fallback = FindGuideScriptFile(Path.GetFileName(cleanlistPath));
                    if (string.IsNullOrEmpty(fallback) || !File.Exists(fallback))
                    {
                        await Logger.LogErrorAsync($"Cleanlist file not found: {cleanlistPath}").ConfigureAwait(false);
                        return ActionExitCode.FileNotFoundPost;
                    }

                    cleanlistPath = fallback;
                    cleanlistContent = await NetFrameworkCompatibility.ReadAllTextAsync(fallback).ConfigureAwait(false);
                    await Logger.LogVerboseAsync(
                        $"[CleanList] Using guide script '{fallback}' (not present in the extract tree).")
                        .ConfigureAwait(false);
                }

                await Logger.LogAsync($"Reading cleanlist from: {Path.GetFileName(cleanlistPath)}").ConfigureAwait(false);
                string[] lines = cleanlistContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                int processedMods = 0;
                int skippedMods = 0;

                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    // Parse CSV line: first field is mod name, rest are files
                    string[] parts = line.Split(',');
                    if (parts.Length < 2)
                    {
                        await Logger.LogWarningAsync($"Invalid cleanlist line (no files specified): {line}").ConfigureAwait(false);
                        continue;
                    }

                    string modName = parts[0].Trim();
                    var filesToDelete = new List<string>();

                    for (int i = 1; i < parts.Length; i++)
                    {
                        string fileName = parts[i].Trim();
                        if (!string.IsNullOrEmpty(fileName))
                        {
                            filesToDelete.Add(fileName);
                        }
                    }

                    // Ask the actual mod-selection state (isModSelectedFunc) whether the mod
                    // this row is about was selected/installed in this run. Previously, when
                    // the destination was the extracted payload directory ("payloadMode"),
                    // this check was bypassed in favor of a FileExists(Override/fileName)
                    // proxy on the theory that Override already having a same-named file
                    // implied the competing mod was selected. That proxy is unsound: another
                    // component (e.g. duplicate-texture dedup) can independently delete the
                    // Override copy before CleanList runs, making FileExists false even
                    // though the competing mod WAS selected — which caused this file's
                    // "losing" duplicate to be kept/re-added, undoing the dedup. Use
                    // isModSelectedFunc for every destination; see IsModSelected in
                    // ModComponent.cs for the fuzzy name/author matching that resolves a
                    // cleanlist row's free-text mod description to a selected component.
                    bool isSelected = isModSelectedFunc?.Invoke(modName) ?? true;

                    if (!isSelected)
                    {
                        await Logger.LogVerboseAsync($"Skipping cleanlist entry '{modName}' (mod not selected)").ConfigureAwait(false);
                        skippedMods++;
                        continue;
                    }

                    await Logger.LogAsync($"Processing cleanlist for '{modName}': {filesToDelete.Count} file(s) to delete").ConfigureAwait(false);
                    processedMods++;

                    // Delete each file
                    foreach (string fileName in filesToDelete)
                    {
                        string fullPath = Path.Combine(targetDirectory.FullName, fileName);

                        if (_fileSystemProvider.FileExists(fullPath))
                        {
                            try
                            {
                                await _fileSystemProvider.DeleteFileAsync(fullPath).ConfigureAwait(false);
                                await Logger.LogAsync($"  Deleted: {fileName}").ConfigureAwait(false);
                                totalFilesDeleted++;
                            }
                            catch (Exception ex)
                            {
                                await Logger.LogWarningAsync($"  Failed to delete {fileName}: {ex.Message}").ConfigureAwait(false);
                                if (exitCode == ActionExitCode.Success)
                                {
                                    exitCode = ActionExitCode.UnknownInnerError;
                                }
                            }
                        }
                        else
                        {
                            await Logger.LogVerboseAsync($"  File not found (skipping): {fileName}").ConfigureAwait(false);
                        }
                    }
                }

                await Logger.LogAsync($"Cleanlist operation complete: {totalFilesDeleted} file(s) deleted, {processedMods} mod(s) processed, {skippedMods} mod(s) skipped").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Logger.LogExceptionAsync(ex).ConfigureAwait(false);
                exitCode = ActionExitCode.UnknownError;
            }
            finally
            {
                sw.Stop();
                Services.TelemetryService.Instance.RecordFileOperation(
                    operationType: "cleanlist",
                    success: exitCode == ActionExitCode.Success,
                    fileCount: totalFilesDeleted,
                    durationMs: sw.Elapsed.TotalMilliseconds,
                    errorMessage: exitCode != ActionExitCode.Success ? exitCode.ToString() : null
                );
            }

            return exitCode;
        }

        [CanBeNull]
        private static string FindGuideScriptFile([CanBeNull] string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            string cwd = Directory.GetCurrentDirectory();
            for (int depth = 0; depth < 6 && !string.IsNullOrEmpty(cwd); depth++)
            {
                string underModBuilds = Path.Combine(cwd, "mod-builds", "scripts", fileName);
                if (File.Exists(underModBuilds))
                {
                    return underModBuilds;
                }

                string underScripts = Path.Combine(cwd, "scripts", fileName);
                if (File.Exists(underScripts))
                {
                    return underScripts;
                }

                cwd = Directory.GetParent(cwd)?.FullName;
            }

            return null;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0051:Method is too long", Justification = "<Pending>")]
        public ActionExitCode DeleteFile(
                [ItemNotNull][NotNull] IReadOnlyList<string> sourcePaths = null
            )
        {
            if (_fileSystemProvider is null)
            {
                throw new InvalidOperationException("File system provider must be set before calling DeleteFile. Call SetFileSystemProvider() first.");
            }

            if (sourcePaths is null)
            {
                sourcePaths = RealSourcePaths;
            }

            if (sourcePaths is null)
            {
                throw new ArgumentNullException(nameof(sourcePaths));
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            ActionExitCode exitCode = ActionExitCode.Success;
            try
            {
                foreach (string thisFilePath in sourcePaths)
                {
                    string realFilePath = thisFilePath;
                    if (MainConfig.CaseInsensitivePathing && !_fileSystemProvider.FileExists(realFilePath))
                    {
                        realFilePath = PathHelper.GetCaseSensitivePath(realFilePath).Item1;
                    }

                    string sourceRelDirPath = MainConfig.SourcePath is null
                        ? thisFilePath
                        : PathHelper.GetRelativePath(
                            MainConfig.SourcePath.FullName,
                            thisFilePath
                        );
                    if (!Path.IsPathRooted(realFilePath) || !_fileSystemProvider.FileExists(realFilePath))
                    {
                        // Overwrite=false (default): lenient mode, just log and continue
                        // Overwrite=true: strict mode, treat as error
                        if (Overwrite)
                        {
                            Logger.LogWarning($"Invalid wildcards or file does not exist: '{sourceRelDirPath}'");
                            exitCode = ActionExitCode.FileNotFoundPost;
                        }
                        else
                        {
                            Logger.LogVerbose($"File does not exist (skipping): '{sourceRelDirPath}'");
                        }
                        continue;
                    }
                    try
                    {
                        _ = _fileSystemProvider.DeleteFileAsync(realFilePath);
                        _ = Logger.LogAsync($"Deleting '{sourceRelDirPath}'...");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogException(ex);
                        if (exitCode == ActionExitCode.Success)
                        {
                            exitCode = ActionExitCode.UnknownInnerError;
                        }
                    }
                }
                if (sourcePaths.Count == 0)
                {
                    Logger.Log("No files to delete, skipping this instruction.");
                    // If Overwrite=true and no files were provided, this is an error condition
                    if (Overwrite)
                    {
                        exitCode = ActionExitCode.FileNotFoundPost;
                    }
                }
                else if (exitCode == ActionExitCode.Success && Overwrite)
                {
                    // If Overwrite=true and we processed files but none existed, ensure we return an error
                    // (exitCode would have been set to FileNotFoundPost in the loop if any file was missing)
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex);
                if (exitCode == ActionExitCode.Success)
                {
                    exitCode = ActionExitCode.UnknownInnerError;
                }
            }
            finally
            {
                sw.Stop();
                Services.TelemetryService.Instance.RecordFileOperation(
                    operationType: "delete",
                    success: exitCode == ActionExitCode.Success,
                    fileCount: sourcePaths?.Count ?? 0,
                    durationMs: sw.Elapsed.TotalMilliseconds,
                    errorMessage: exitCode != ActionExitCode.Success ? exitCode.ToString() : null
                );
            }
            return exitCode;
        }

        public Task<ActionExitCode> DeleteFileAsync([ItemNotNull][NotNull] IReadOnlyList<string> sourcePaths = null)
        {
            return Task.FromResult(DeleteFile(sourcePaths));
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0051:Method is too long", Justification = "<Pending>")]
        public ActionExitCode RenameFile(
            [ItemNotNull][NotNull] IReadOnlyList<string> sourcePaths = null
        )
        {
            if (_fileSystemProvider is null)
            {
                throw new InvalidOperationException("File system provider must be set before calling RenameFile. Call SetFileSystemProvider() first.");
            }

            if (sourcePaths.IsNullOrEmptyCollection())
            {
                sourcePaths = RealSourcePaths;
            }

            if (sourcePaths.IsNullOrEmptyCollection())
            {
                throw new ArgumentException("No source paths available for Rename instruction. Source paths may be empty, or wildcard patterns may not have matched any files.", nameof(sourcePaths));
            }

            ActionExitCode exitCode = ActionExitCode.Success;
            try
            {
                foreach (string sourcePath in sourcePaths)
                {
                    string fileName = Path.GetFileName(sourcePath);
                    string sourceRelDirPath = MainConfig.SourcePath is null
                        ? sourcePath
                        : PathHelper.GetRelativePath(
                            MainConfig.SourcePath.FullName,
                            sourcePath
                        );
                    if (!_fileSystemProvider.FileExists(sourcePath))
                    {
                        Logger.LogError($"'{sourceRelDirPath}' does not exist!");
                        if (exitCode == ActionExitCode.Success)
                        {
                            exitCode = ActionExitCode.FileNotFoundPost;
                        }
                        continue;
                    }
                    string destinationFilePath = Path.Combine(
                        Path.GetDirectoryName(sourcePath) ?? string.Empty,
                        Destination
                    );
                    string destinationRelDirPath = MainConfig.DestinationPath is null
                        ? destinationFilePath
                        : PathHelper.GetRelativePath(
                            MainConfig.DestinationPath.FullName,
                            destinationFilePath
                        );
                    if (_fileSystemProvider.FileExists(destinationFilePath))
                    {
                        if (!Overwrite)
                        {
                            exitCode = ActionExitCode.RenameTargetAlreadyExists;
                            _ = Logger.LogAsync(
                                $"File '{fileName}' already exists in {Path.GetDirectoryName(destinationRelDirPath)},"
                                + " skipping file. Reason: Overwrite set to False )"
                            );
                            continue;
                        }
                        _ = Logger.LogAsync(
                            $"Removing pre-existing file '{destinationRelDirPath}' Reason: Overwrite set to True"
                        );
                        _ = _fileSystemProvider.DeleteFileAsync(destinationFilePath);
                    }
                    try
                    {
                        _ = Logger.LogAsync($"Rename '{sourceRelDirPath}' to '{destinationRelDirPath}'");
                        _ = _fileSystemProvider.RenameFileAsync(sourcePath, Destination, Overwrite);
                    }
                    catch (IOException ex)
                    {
                        if (exitCode == ActionExitCode.Success)
                        {
                            exitCode = ActionExitCode.IOException;
                        }
                        Logger.LogException(ex);
                    }
                }
                return exitCode;
            }
            catch (Exception ex)
            {
                Logger.LogException(ex);
                if (exitCode == ActionExitCode.Success)
                {
                    exitCode = ActionExitCode.UnknownError;
                }
            }
            return exitCode;
        }

        public Task<ActionExitCode> RenameFileAsync([ItemNotNull][NotNull] IReadOnlyList<string> sourcePaths = null)
        {
            return Task.FromResult(RenameFile(sourcePaths));
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0051:Method is too long", Justification = "<Pending>")]
        public async Task<ActionExitCode> CopyFileAsync(
            [ItemNotNull][NotNull] IReadOnlyList<string> sourcePaths = null,
            [NotNull] DirectoryInfo destinationPath = null
        )
        {
            if (_fileSystemProvider is null)
            {
                throw new InvalidOperationException("File system provider must be set before calling CopyFileAsync. Call SetFileSystemProvider() first.");
            }

            if (sourcePaths.IsNullOrEmptyCollection())
            {
                sourcePaths = RealSourcePaths;
            }

            if (sourcePaths.IsNullOrEmptyCollection())
            {
                throw new ArgumentNullException(nameof(sourcePaths));
            }

            if (destinationPath is null)
            {
                destinationPath = RealDestinationPath;
            }

            if (destinationPath is null)
            {
                throw new ArgumentNullException(nameof(destinationPath));
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int maxCount = MainConfig.UseMultiThreadedIO
                ? 16
                : 1;
            using (var semaphore = new SemaphoreSlim(initialCount: 1, maxCount))
            {
                SemaphoreSlim localSemaphore = semaphore;
                async Task CopyIndividualFileAsync(string sourcePath)
                {
                    await localSemaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        string sourceRelDirPath = MainConfig.SourcePath is null
                            ? sourcePath
                            : PathHelper.GetRelativePath(
                                MainConfig.SourcePath.FullName,
                                sourcePath
                            );
                        string fileName = Path.GetFileName(sourcePath);
                        string destinationFilePath = MainConfig.CaseInsensitivePathing
                            ? PathHelper.GetCaseSensitivePath(
                                Path.Combine(destinationPath.FullName, fileName),
                                isFile: true
                            ).Item1
                            : Path.Combine(destinationPath.FullName, fileName);
                        string destinationRelDirPath = MainConfig.DestinationPath is null
                            ? destinationFilePath
                            : PathHelper.GetRelativePath(
                                MainConfig.DestinationPath.FullName,
                                destinationFilePath
                            );
                        if (_fileSystemProvider.FileExists(destinationFilePath))
                        {
                            if (!Overwrite)
                            {
                                await Logger.LogWarningAsync(
                                    $"File '{fileName}' already exists in {Path.GetDirectoryName(destinationRelDirPath)},"
                                    + " skipping file. Reason: Overwrite set to False )"
                                ).ConfigureAwait(false);
                                return;
                            }
                            await Logger.LogAsync(
                                $"File '{fileName}' already exists in {Path.GetDirectoryName(destinationRelDirPath)},"
                                + $" deleting pre-existing file '{destinationRelDirPath}' Reason: Overwrite set to True"
                            ).ConfigureAwait(false);
                            await _fileSystemProvider.DeleteFileAsync(destinationFilePath).ConfigureAwait(false);
                        }
                        await Logger.LogAsync($"Copy '{sourceRelDirPath}' to '{destinationRelDirPath}'").ConfigureAwait(false);
                        await _fileSystemProvider.CopyFileAsync(sourcePath, destinationFilePath, Overwrite).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await Logger.LogExceptionAsync(ex).ConfigureAwait(false);
                        throw;
                    }
                    finally
                    {
                        _ = localSemaphore.Release();
                    }
                }
                if (sourcePaths is null)
                {
                    throw new InvalidOperationException($"Source paths are null for instruction in CopyFileAsync");
                }

                var tasks = sourcePaths.Select(CopyIndividualFileAsync).ToList();
                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                    sw.Stop();
                    Services.TelemetryService.Instance.RecordFileOperation(
                        operationType: "copy",
                        success: true,
                        fileCount: sourcePaths.Count,
                        durationMs: sw.Elapsed.TotalMilliseconds
                    );
                    return ActionExitCode.Success;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    Services.TelemetryService.Instance.RecordFileOperation(
                        operationType: "copy",
                        success: false,
                        fileCount: sourcePaths.Count,
                        durationMs: sw.Elapsed.TotalMilliseconds,
                        errorMessage: ex.Message
                    );
                    return ActionExitCode.UnknownError;
                }
            }
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0051:Method is too long", Justification = "<Pending>")]
        public async Task<ActionExitCode> MoveFileAsync(
            [ItemNotNull][NotNull] IReadOnlyList<string> sourcePaths = null,
            [NotNull] DirectoryInfo destinationPath = null
        )
        {
            if (_fileSystemProvider is null)
            {
                throw new InvalidOperationException("File system provider must be set before calling MoveFileAsync. Call SetFileSystemProvider() first.");
            }

            if (sourcePaths.IsNullOrEmptyCollection())
            {
                sourcePaths = RealSourcePaths;
            }

            if (sourcePaths.IsNullOrEmptyCollection())
            {
                throw new ArgumentNullException(nameof(sourcePaths));
            }

            if (destinationPath is null)
            {
                destinationPath = RealDestinationPath;
            }

            if (destinationPath is null)
            {
                throw new ArgumentNullException(nameof(destinationPath));
            }

            await Logger.LogVerboseAsync($"[Instruction.MoveFileAsync] Starting move operation with {sourcePaths.Count} files").ConfigureAwait(false);
            await Logger.LogVerboseAsync($"[Instruction.MoveFileAsync] Destination: {destinationPath.FullName}").ConfigureAwait(false);
            await Logger.LogVerboseAsync($"[Instruction.MoveFileAsync] MainConfig.SourcePath: {MainConfig.SourcePath?.FullName ?? "NULL"}").ConfigureAwait(false);
            await Logger.LogVerboseAsync($"[Instruction.MoveFileAsync] MainConfig.DestinationPath: {MainConfig.DestinationPath?.FullName ?? "NULL"}").ConfigureAwait(false);
            await Logger.LogVerboseAsync($"[Instruction.MoveFileAsync] IsDryRun: {_fileSystemProvider.IsDryRun}").ConfigureAwait(false);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int maxCount = MainConfig.UseMultiThreadedIO
                ? 16
                : 1;
            using (var semaphore = new SemaphoreSlim(initialCount: 1, maxCount))
            {
                SemaphoreSlim localSemaphore = semaphore;
                async Task MoveIndividualFileAsync(string sourcePath)
                {
                    await localSemaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await Logger.LogVerboseAsync($"[Instruction.MoveIndividualFileAsync] Processing: {sourcePath}").ConfigureAwait(false);
                        string sourceRelDirPath = MainConfig.SourcePath is null
                            ? sourcePath
                            : PathHelper.GetRelativePath(
                                MainConfig.SourcePath.FullName,
                                sourcePath
                            );
                        await Logger.LogVerboseAsync($"[Instruction.MoveIndividualFileAsync] sourceRelDirPath: {sourceRelDirPath}").ConfigureAwait(false);
                        string fileName = Path.GetFileName(sourcePath);
                        await Logger.LogVerboseAsync($"[Instruction.MoveIndividualFileAsync] fileName: {fileName}").ConfigureAwait(false);
                        string destinationFilePath = MainConfig.CaseInsensitivePathing
                            ? PathHelper.GetCaseSensitivePath(
                                Path.Combine(destinationPath.FullName, fileName),
                                isFile: true
                            ).Item1
                            : Path.Combine(destinationPath.FullName, fileName);
                        await Logger.LogVerboseAsync($"[Instruction.MoveIndividualFileAsync] destinationFilePath: {destinationFilePath}").ConfigureAwait(false);
                        string destinationRelDirPath = MainConfig.DestinationPath is null
                            ? destinationFilePath
                            : PathHelper.GetRelativePath(
                                MainConfig.DestinationPath.FullName,
                                destinationFilePath
                            );
                        await Logger.LogVerboseAsync($"[Instruction.MoveIndividualFileAsync] destinationRelDirPath: {destinationRelDirPath}").ConfigureAwait(false);
                        if (_fileSystemProvider.FileExists(destinationFilePath))
                        {
                            await Logger.LogVerboseAsync($"[Instruction.MoveIndividualFileAsync] Destination file exists, Overwrite={Overwrite}").ConfigureAwait(false);
                            if (!Overwrite)
                            {
                                await Logger.LogWarningAsync(
                                    $"File '{fileName}' already exists in {Path.GetDirectoryName(destinationRelDirPath)},"
                                    + " skipping file. Reason: Overwrite set to False )"
                                ).ConfigureAwait(false);
                                return;
                            }
                            await Logger.LogAsync(
                                $"File '{fileName}' already exists in {Path.GetDirectoryName(destinationRelDirPath)},"
                                + $" deleting pre-existing file '{destinationRelDirPath}' Reason: Overwrite set to True"
                            ).ConfigureAwait(false);
                            await _fileSystemProvider.DeleteFileAsync(destinationFilePath).ConfigureAwait(false);
                        }
                        await Logger.LogAsync($"Move '{sourceRelDirPath}' to '{destinationRelDirPath}'").ConfigureAwait(false);
                        await Logger.LogVerboseAsync($"[Instruction.MoveIndividualFileAsync] Calling _fileSystemProvider.MoveFileAsync('{sourcePath}', '{destinationFilePath}', {Overwrite})").ConfigureAwait(false);
                        await _fileSystemProvider.MoveFileAsync(sourcePath, destinationFilePath, Overwrite).ConfigureAwait(false);
                        await Logger.LogVerboseAsync($"[Instruction.MoveIndividualFileAsync] Move completed successfully").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await Logger.LogExceptionAsync(ex).ConfigureAwait(false);
                        throw;
                    }
                    finally
                    {
                        _ = localSemaphore.Release();
                    }
                }
                var tasks = sourcePaths.Select(MoveIndividualFileAsync).ToList();
                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                    sw.Stop();
                    Services.TelemetryService.Instance.RecordFileOperation(
                        operationType: "move",
                        success: true,
                        fileCount: sourcePaths.Count,
                        durationMs: sw.Elapsed.TotalMilliseconds
                    );
                    return ActionExitCode.Success;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    Services.TelemetryService.Instance.RecordFileOperation(
                        operationType: "move",
                        success: false,
                        fileCount: sourcePaths.Count,
                        durationMs: sw.Elapsed.TotalMilliseconds,
                        errorMessage: ex.Message
                    );
                    return ActionExitCode.UnknownError;
                }
            }
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0051:Method is too long", Justification = "<Pending>")]
        public async Task<ActionExitCode> ExecuteTSLPatcherAsync()
        {
            if (_fileSystemProvider is null)
            {
                throw new InvalidOperationException("File system provider must be set before calling ExecuteTSLPatcherAsync. Call SetFileSystemProvider() first.");
            }

            try
            {
                foreach (string t in RealSourcePaths)
                {
                    DirectoryInfo tslPatcherDirectory = _fileSystemProvider.FileExists(t)
                        ? PathHelper.TryGetValidDirectoryInfo(_fileSystemProvider.GetDirectoryName(t))
                        : new DirectoryInfo(t);
                    if (tslPatcherDirectory is null || !_fileSystemProvider.DirectoryExists(tslPatcherDirectory.FullName))
                    {
                        throw new DirectoryNotFoundException($"The directory '{t}' could not be located on the disk.");
                    }

                    if (_fileSystemProvider is Services.FileSystem.VirtualFileSystemProvider vfsProvider)
                    {
                        string kotorDirectory = MainConfig.DestinationPath?.FullName;
                        if (string.IsNullOrWhiteSpace(kotorDirectory))
                        {
                            await Logger.LogAsync("[Simulation] Skipping TSLPatcher VFS simulation: no KOTOR directory configured.")
                                .ConfigureAwait(false);
                            continue;
                        }

                        await TslPatcherVfsSimulator.SimulateInstallAsync(
                            vfsProvider,
                            tslPatcherDirectory.FullName,
                            kotorDirectory
                        ).ConfigureAwait(false);
                        await Logger.LogAsync("[Simulation] Applied TSLPatcher VFS simulation from changes.ini")
                            .ConfigureAwait(false);
                        continue;
                    }

                    string fullInstallLogFile = Path.Combine(tslPatcherDirectory.FullName, path2: "installlog.rtf");
                    if (_fileSystemProvider.FileExists(fullInstallLogFile))
                    {
                        await _fileSystemProvider.DeleteFileAsync(fullInstallLogFile).ConfigureAwait(false);
                    }

                    fullInstallLogFile = Path.Combine(tslPatcherDirectory.FullName, path2: "installlog.txt");
                    if (_fileSystemProvider.FileExists(fullInstallLogFile))
                    {
                        await _fileSystemProvider.DeleteFileAsync(fullInstallLogFile).ConfigureAwait(false);
                    }

                    IniHelper.ReplaceIniPattern(tslPatcherDirectory, pattern: @"^\s*PlaintextLog\s*=\s*0\s*$", replacement: "PlaintextLog=1");
                    IniHelper.ReplaceIniPattern(tslPatcherDirectory, pattern: @"^\s*LookupGameFolder\s*=\s*1\s*$", replacement: "LookupGameFolder=0");
                    IniHelper.ReplaceIniPattern(tslPatcherDirectory, pattern: @"^\s*ConfirmMessage\s*=\s*.*$", replacement: "ConfirmMessage=N/A");

                    tslPatcherDirectory = SanitizeLinuxHoloNamespaces(tslPatcherDirectory);
                    EnsureInfoRtfBesideChangesIni(tslPatcherDirectory);
                    _ = IniHelper.DropDangling2daRowReferences(tslPatcherDirectory);

                    // Holo 1.5.1's Unix NSS builtin crashes ('str' object has no attribute 'info').
                    // The K1/K2 manuals recovered with wine nwnnsscomp.exe; do that here so fail-closed
                    // installs can complete the same CompileList mods instead of rolling back.
                    if (Services.UnixNssCompileRecovery.HostNeedsWineCompiler())
                    {
                        Services.UnixNssCompileRecovery.EnableSaveProcessedScripts(tslPatcherDirectory);
                        Services.UnixNssCompileRecovery.EnsureNwscriptInPatcherTree(tslPatcherDirectory);
                        _ = Services.UnixNssCompileRecovery.TryRewriteTokenFreeCompileList(tslPatcherDirectory, Arguments);
                    }

                    string engine = MainConfig.PatcherEngine ?? PatcherEngines.Holopatcher;
                    bool useKpatcher = string.Equals(engine, PatcherEngines.KPatcher, StringComparison.OrdinalIgnoreCase);
                    bool useOdyPatcher = PatcherEngines.IsBioFamily(engine);
                    bool useExternalPatcher = useKpatcher || useOdyPatcher;

                    // OdyPatcher rejects --flag=value (and shell/ProcessStartInfo quote collapsing of
                    // --flag="value" into --flag=value). Always use space-separated forms.
                    // Quote paths so spaces survive ProcessStartInfo argument parsing.
                    string gameDirArg = QuoteProcessArgument(MainConfig.DestinationPath?.FullName);
                    string tslPatchDataArg = QuoteProcessArgument(tslPatcherDirectory.FullName);
                    var argList = new List<string>
                    {
                        "--install",
                        "--game-dir",
                        gameDirArg,
                        "--tslpatchdata",
                        tslPatchDataArg,
                    };
                    if (useOdyPatcher)
                    {
                        // Headless: no GUI window, no confirmation prompt.
                        argList.Add("--cli");
                        argList.Add("-y");
                    }

                    if (!string.IsNullOrEmpty(Arguments))
                    {
                        argList.Add("--namespace-option-index");
                        argList.Add(Arguments.Trim());
                    }

                    string args = string.Join(separator: " ", argList);
                    string baseDir = UtilityHelper.GetBaseDirectory();
                    string resourcesDir = UtilityHelper.GetResourcesDirectory(baseDir);

                    (string holopatcherPath, bool usePythonVersion, bool found) = (null, false, false);
                    if (!useExternalPatcher)
                    {
                        (holopatcherPath, usePythonVersion, found) = await Services.InstallationService.FindHolopatcherAsync(resourcesDir, baseDir).ConfigureAwait(false);
                        if (!found)
                        {
                            throw new FileNotFoundException($"Could not load HoloPatcher from the '{resourcesDir}' directory!");
                        }
                    }

                    if (int.TryParse(Arguments.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int namespaceId))
                    {
                        string message = $"If asked to pick an option, select the {Serializer.ToOrdinal(namespaceId + 1)} from the top.";
                        if (CallbackObjects.InformationCallback != null)
                        {
                            _ = CallbackObjects.InformationCallback.ShowInformationDialog(message);
                        }

                        await Logger.LogWarningAsync(message).ConfigureAwait(false);
                    }

                    int exitCode;
                    string output;
                    string error;
                    if (useExternalPatcher)
                    {
                        await Logger.LogAsync($"Using {engine} CLI: {args}").ConfigureAwait(false);
                        (exitCode, output, error) = await Services.InstallationService.RunTslPatcherCliAsync(args, _fileSystemProvider).ConfigureAwait(false);
                    }
                    else
                    {
                        await Logger.LogAsync($"Using CLI to run command: '{holopatcherPath}' {args}").ConfigureAwait(false);
                        if (usePythonVersion)
                        {
                            (exitCode, output, error) = await Services.InstallationService.RunHolopatcherPyAsync(
                                    holopatcherPath,
                                    args
                                ).ConfigureAwait(false);
                        }
                        else if (_fileSystemProvider?.IsDryRun == true)
                        {
                            (exitCode, output, error) = await _fileSystemProvider.ExecuteProcessAsync(
                                holopatcherPath,
                                args
                            ).ConfigureAwait(false);
                        }
                        else
                        {
                            (exitCode, output, error) = await PlatformAgnosticMethods.ExecuteProcessAsync(
                                holopatcherPath,
                                args,
                                logLinePrefix: "[Patcher] "
                            ).ConfigureAwait(false);
                        }
                    }

                    await PipePatcherLogIntoModSyncAsync(tslPatcherDirectory.FullName, output, error).ConfigureAwait(false);
                    await Logger.LogAsync($"Patcher exited with exit code {exitCode}").ConfigureAwait(false);
                    bool nssRecovered = false;
                    string patcherText = (output ?? string.Empty) + Environment.NewLine + (error ?? string.Empty);
                    if (!useExternalPatcher && IsHoloListIndexParseFailure(patcherText))
                    {
                        // Holo 1.5.x rejects TSLPatcher's TypeId=ListIndex (struct id = list
                        // index). BioPatcher 1.0.0 parses it. Retry once so fail-closed
                        // installs are not stuck on a Holo config-reader gap.
                        var bioArgList = new List<string>
                        {
                            "--install",
                            "--cli",
                            "-y",
                            "--game-dir",
                            gameDirArg,
                            "--tslpatchdata",
                            tslPatchDataArg,
                        };
                        if (!string.IsNullOrEmpty(Arguments))
                        {
                            bioArgList.Add("--namespace-option-index");
                            bioArgList.Add(Arguments.Trim());
                        }

                        string bioArgs = string.Join(separator: " ", bioArgList);
                        await Logger.LogWarningAsync(
                                "HoloPatcher cannot parse TypeId=ListIndex. Retrying this patch with BioPatcher.")
                            .ConfigureAwait(false);
                        (string bioPath, bool bioFound) = await Services.InstallationService.FindOdyPatcherExecutableAsync(
                                baseDir,
                                resourcesDir)
                            .ConfigureAwait(false);
                        if (!bioFound)
                        {
                            await Logger.LogErrorAsync(
                                    "BioPatcher retry skipped: executable not found. Set --odypatcher-path or install biopatcher on PATH.")
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await Logger.LogAsync($"Using BioPatcher CLI: '{bioPath}' {bioArgs}").ConfigureAwait(false);
                            if (_fileSystemProvider?.IsDryRun == true)
                            {
                                (exitCode, output, error) = await _fileSystemProvider.ExecuteProcessAsync(bioPath, bioArgs)
                                    .ConfigureAwait(false);
                            }
                            else
                            {
                                (exitCode, output, error) = await PlatformAgnosticMethods.ExecuteProcessAsync(
                                        bioPath,
                                        bioArgs,
                                        logLinePrefix: "[Patcher] ")
                                    .ConfigureAwait(false);
                            }
                        }
                        await PipePatcherLogIntoModSyncAsync(tslPatcherDirectory.FullName, output, error)
                            .ConfigureAwait(false);
                        await Logger.LogAsync($"BioPatcher retry exited with exit code {exitCode}").ConfigureAwait(false);
                        patcherText = (output ?? string.Empty) + Environment.NewLine + (error ?? string.Empty);
                    }
                    if (exitCode != 0
                        && Services.UnixNssCompileRecovery.HostNeedsWineCompiler()
                        && Services.UnixNssCompileRecovery.IsBuiltinNssCrash(patcherText))
                    {
                        nssRecovered = Services.UnixNssCompileRecovery.TryInstallCompiledScripts(
                            tslPatcherDirectory,
                            MainConfig.DestinationPath?.FullName,
                            Arguments);
                        if (nssRecovered)
                        {
                            await Logger.LogAsync(
                                    "Recovered Holo Unix NSS builtin crash with wine nwnnsscomp.exe; compiled scripts are in Override.")
                                .ConfigureAwait(false);
                            exitCode = 0;
                        }
                    }

                    if (exitCode != 0)
                    {
                        bool intended = IsGuideDirectedTslPatchdataDeleteOutcome(patcherText);
                        if (!intended)
                        {
                            try
                            {
                                List<string> logErrors = await VerifyInstall().ConfigureAwait(false);
                                intended = logErrors.Count > 0
                                    && logErrors.All(IsGuideDirectedTslPatchdataDeleteError);
                            }
                            catch (Exception)
                            {
                                intended = false;
                            }
                        }

                        if (intended)
                        {
                            await Logger.LogAsync(
                                    "Patcher exited non-zero after a guide-directed tslpatchdata delete (intended missing-source error); treating as success.")
                                .ConfigureAwait(false);
                            exitCode = 0;
                        }
                        else
                        {
                            return ActionExitCode.PatcherError;
                        }
                    }

                    // The patcher reported success, but its exit code alone does not prove that every
                    // declared [InstallList] destination file (streamwaves/streamsounds/streammusic/
                    // movies/data — the folders it silently overwrites without going through Override or
                    // modules) actually landed on disk. This is visibility only: it never changes exitCode,
                    // it only makes an otherwise-silent data loss diagnosable.
                    try
                    {
                        IReadOnlyList<string> missingInstallListFiles = await InstallListDestinationVerifier.VerifyAsync(
                                _fileSystemProvider,
                                tslPatcherDirectory,
                                MainConfig.DestinationPath?.FullName,
                                Arguments)
                            .ConfigureAwait(false);
                        if (missingInstallListFiles.Count > 0)
                        {
                            string componentName = GetParentComponent()?.Name ?? tslPatcherDirectory.FullName;
                            await Logger.LogWarningAsync(
                                    $"[InstallList] Component '{componentName}' patcher run at '{tslPatcherDirectory.FullName}'"
                                    + " reported success, but the following declared InstallList destination file(s) are"
                                    + " missing after install: " + string.Join(", ", missingInstallListFiles))
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        await Logger.LogExceptionAsync(ex).ConfigureAwait(false);
                    }

                    try
                    {
                        List<string> installErrors = await VerifyInstall().ConfigureAwait(false);
                        if (nssRecovered)
                        {
                            installErrors = installErrors
                                .Where(line => !Services.UnixNssCompileRecovery.IsRecoveredNssSupportError(line))
                                .ToList();
                        }

                        installErrors = installErrors
                            .Where(line => !IsGuideDirectedTslPatchdataDeleteError(line))
                            .ToList();

                        if (installErrors.Count <= 0)
                        {
                            continue;
                        }

                        await Logger.LogAsync(string.Join(Environment.NewLine, installErrors)).ConfigureAwait(false);
                        return ActionExitCode.TSLPatcherError;
                    }
                    catch (Exception ex)
                    {
                        await Logger.LogExceptionAsync(ex).ConfigureAwait(false);
                        return ActionExitCode.TSLPatcherLogNotFound;
                    }
                }
                return ActionExitCode.Success;
            }
            catch (Exception ex)
            {
                await Logger.LogExceptionAsync(ex).ConfigureAwait(false);
                throw;
            }
        }
        public async Task<ActionExitCode> ExecuteProgramAsync(
            [ItemNotNull] IReadOnlyList<string> sourcePaths = null
        )
        {
            if (_fileSystemProvider is null)
            {
                throw new InvalidOperationException("File system provider must be set before calling ExecuteProgramAsync. Call SetFileSystemProvider() first.");
            }

            try
            {
                if (sourcePaths.IsNullOrEmptyCollection())
                {
                    sourcePaths = RealSourcePaths;
                }

                if (sourcePaths.IsNullOrEmptyCollection())
                {
                    // If RealSourcePaths is empty, try to resolve Source paths to check if files exist
                    if (Source != null && Source.Count > 0)
                    {
                        var resolvedPaths = Source.Select(UtilityHelper.ReplaceCustomVariables).ToList();
                        foreach (string resolvedPath in resolvedPaths)
                        {
                            if (!_fileSystemProvider.FileExists(resolvedPath))
                            {
                                await Logger.LogErrorAsync($"Executable not found: {resolvedPath}").ConfigureAwait(false);
                                return ActionExitCode.FileNotFoundPost;
                            }
                        }
                        // If files exist but weren't in RealSourcePaths, use resolved paths
                        sourcePaths = resolvedPaths;
                    }
                    else
                    {
                        throw new ArgumentNullException(nameof(sourcePaths));
                    }
                }

                ActionExitCode exitCode = ActionExitCode.Success;
                foreach (string sourcePath in sourcePaths)
                {
                    try
                    {
                        ActionExitCode? rerouted = await TryRunWindowsInstallerWithoutExecAsync(sourcePath)
                            .ConfigureAwait(false);
                        if (rerouted.HasValue)
                        {
                            if (rerouted.Value == ActionExitCode.Success)
                            {
                                continue;
                            }

                            return rerouted.Value;
                        }

                        (int childExitCode, string output, string error) =
                            await _fileSystemProvider.ExecuteProcessAsync(
                                sourcePath,
                                UtilityHelper.ReplaceCustomVariables(Arguments)
                            ).ConfigureAwait(false);
                        _ = Logger.LogAsync(output + Environment.NewLine + error);
                        if (childExitCode == 0)
                        {
                            continue;
                        }

                        exitCode = ActionExitCode.ChildProcessError;
                        break;
                    }
                    catch (FileNotFoundException ex)
                    {
                        await Logger.LogExceptionAsync(ex).ConfigureAwait(false);
                        return ActionExitCode.FileNotFoundPost;
                    }
                    catch (Exception ex)
                    {
                        await Logger.LogExceptionAsync(ex).ConfigureAwait(false);
                        return ActionExitCode.UnknownInnerError;
                    }
                }
                return exitCode;
            }
            catch (Exception ex)
            {
                await Logger.LogExceptionAsync(ex).ConfigureAwait(false);
                return ActionExitCode.UnknownError;
            }
        }
        /// <summary>
        /// Handles an <c>Execute</c> whose target is a Windows Inno Setup installer on a platform
        /// that cannot run it. Returns null when the executable should be launched normally.
        /// <para>
        /// TSLRCM, the foundation mod of the K2 build, is exactly this: an Inno Setup <c>.exe</c>
        /// that died with <c>Win32Exception ... Permission denied</c> on a native-Linux game tree.
        /// It is a prebuilt asset drop, so unpacking it and copying the game folders out is the
        /// whole install.
        /// </para>
        /// </summary>
        private async Task<ActionExitCode?> TryRunWindowsInstallerWithoutExecAsync([CanBeNull] string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)
                || !sourcePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || Services.InnoSetupInstallerService.HostRunsWindowsExecutables())
            {
                return null;
            }

            // This path calls InnoSetupInstallerService directly against the real game
            // directory instead of going through _fileSystemProvider — the abstraction every
            // other action type relies on to become a no-op under VirtualFileSystemProvider
            // during dry-run. Without this guard, dry-run validation genuinely re-extracts the
            // installer's full payload (dialog.tlk/2da files included) onto a live tree that may
            // already be many components further along, silently reintroducing the installer's
            // own bundled versions of files later components have already modified.
            if (_fileSystemProvider?.IsDryRun == true)
            {
                await Logger.LogVerboseAsync(
                    $"[DryRun] Skipping real Inno Setup extraction of '{Path.GetFileName(sourcePath)}': "
                    + "this path writes directly to the real game directory and cannot be simulated "
                    + "through the VFS, so it must not run during validation.").ConfigureAwait(false);
                return ActionExitCode.Success;
            }

            bool isInno = Services.InnoSetupInstallerService.IsInnoSetupInstaller(sourcePath);
            Services.ExeExecutionPlan plan = Services.InnoSetupInstallerService.PlanExeExecution(
                isWindows: false,
                isInnoSetup: isInno,
                innoExtractAvailable: Services.InnoSetupInstallerService.IsInnoExtractAvailable());

            if (plan == Services.ExeExecutionPlan.ExecuteDirectly)
            {
                return null;
            }

            if (plan == Services.ExeExecutionPlan.MissingInnoExtract)
            {
                await Logger.LogErrorAsync(
                    Services.InnoSetupInstallerService.MissingToolMessage(sourcePath)).ConfigureAwait(false);
                return ActionExitCode.ChildProcessError;
            }

            // Always the game root: the unpacked tree carries its own Override/modules/lips layout,
            // so anything else would nest the whole game folder inside a subdirectory.
            DirectoryInfo gameDirectory = MainConfig.DestinationPath;
            if (gameDirectory is null)
            {
                await Logger.LogErrorAsync(
                    $"Cannot unpack '{Path.GetFileName(sourcePath)}': no game directory is configured.")
                    .ConfigureAwait(false);
                return ActionExitCode.ChildProcessError;
            }

            bool installed = await Services.InnoSetupInstallerService
                .ExtractAndInstallAsync(sourcePath, gameDirectory).ConfigureAwait(false);

            return installed ? ActionExitCode.Success : ActionExitCode.ChildProcessError;
        }

        /// <summary>
        /// Linux HoloPatcher 1.5.1 imports <c>rte_editor.py</c> whenever a namespace <c>InfoName</c>
        /// is a <c>.rte</c>. That module calls <c>ctypes.windll</c> at import time and pops a blocking
        /// GUI <c>AttributeError</c>. Convert every namespace's info file to <c>.rtf</c> and fill
        /// missing <c>IniName</c>/<c>Description</c>/<c>InfoName</c> keys before Holo ever reads
        /// <c>namespaces.ini</c>. Holo 1.5.1 treats a missing <c>InfoName</c> as a blocking
        /// <c>KeyError</c> dialog even when <c>info.rtf</c> already sits beside <c>changes.ini</c>
        /// (K1 JC's Mandalorian Armor).
        /// Do not point Holo directly at a flattened namespace <c>DataPath</c> instead: its CLI
        /// requires <c>--tslpatchdata</c> to be the directory containing <c>namespaces.ini</c>, or it
        /// refuses with "No mod chosen: Select your mod directory first." (K1 Sentinel Sneak Attack).
        /// </summary>
        [NotNull]
        private static DirectoryInfo SanitizeLinuxHoloNamespaces([NotNull] DirectoryInfo tslPatcherDirectory)
        {
            if (tslPatcherDirectory is null || !tslPatcherDirectory.Exists)
            {
                return tslPatcherDirectory;
            }

            FileInfo namespacesIni;
            try
            {
                namespacesIni = tslPatcherDirectory
                    .GetFiles("namespaces.ini", SearchOption.AllDirectories)
                    .FirstOrDefault();
            }
            catch (Exception)
            {
                return tslPatcherDirectory;
            }

            if (namespacesIni is null || namespacesIni.Directory is null)
            {
                ConvertRteInfoDocuments(tslPatcherDirectory);
                return tslPatcherDirectory;
            }

            DirectoryInfo namespacesRoot = namespacesIni.Directory;
            Dictionary<string, Dictionary<string, string>> sections;
            try
            {
                using (var reader = new StreamReader(namespacesIni.FullName))
                {
                    sections = IniHelper.ParseNamespacesIni(reader);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Patcher] Could not parse '{namespacesIni.FullName}': {ex.Message}");
                ConvertRteInfoDocuments(tslPatcherDirectory);
                return tslPatcherDirectory;
            }

            if (sections is null || sections.Count == 0)
            {
                ConvertRteInfoDocuments(tslPatcherDirectory);
                return tslPatcherDirectory;
            }

            Dictionary<string, string> indexSection;
            sections.TryGetValue("Namespaces", out indexSection);
            var namespaceOrder = new List<string>();
            if (indexSection != null)
            {
                foreach (KeyValuePair<string, string> entry in indexSection.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(entry.Value) && !namespaceOrder.Contains(entry.Value, StringComparer.OrdinalIgnoreCase))
                    {
                        namespaceOrder.Add(entry.Value);
                    }
                }
            }

            foreach (KeyValuePair<string, Dictionary<string, string>> section in sections)
            {
                if (section.Key.Equals("Namespaces", StringComparison.OrdinalIgnoreCase) || section.Value is null)
                {
                    continue;
                }

                string dataPath = section.Value.TryGetValue("DataPath", out string dataPathValue)
                    ? dataPathValue.Trim()
                    : section.Key;
                string sectionDir = string.IsNullOrWhiteSpace(dataPath)
                    ? namespacesRoot.FullName
                    : Path.Combine(namespacesRoot.FullName, dataPath.Replace('/', Path.DirectorySeparatorChar));

                if (!section.Value.ContainsKey("IniName"))
                {
                    section.Value["IniName"] = "changes.ini";
                }

                if (!section.Value.ContainsKey("Description"))
                {
                    string name = section.Value.TryGetValue("Name", out string named) ? named : section.Key;
                    section.Value["Description"] = string.IsNullOrWhiteSpace(name) ? section.Key : name;
                }

                string infoName = section.Value.TryGetValue("InfoName", out string infoValue)
                    ? infoValue.Trim()
                    : "info.rtf";
                if (string.IsNullOrWhiteSpace(infoName))
                {
                    infoName = "info.rtf";
                }

                if (infoName.EndsWith(".rte", StringComparison.OrdinalIgnoreCase))
                {
                    ConvertRteInfoDocument(sectionDir, infoName);
                    infoName = Path.ChangeExtension(infoName, ".rtf");
                }
                else
                {
                    ConvertRteInfoDocuments(new DirectoryInfo(sectionDir));
                }

                // Holo 1.5.1 always reads this key (KeyError if absent). Write it even when the
                // info document already exists beside changes.ini.
                section.Value["InfoName"] = infoName;
                EnsureNamedInfoDocument(sectionDir, infoName);
            }

            try
            {
                WriteNamespacesIni(namespacesIni.FullName, sections, indexSection, namespaceOrder);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Patcher] Could not rewrite '{namespacesIni.FullName}': {ex.Message}");
            }

            // Do NOT point Holo at the selected namespace's flattened DataPath directly: its CLI
            // requires --tslpatchdata to be the directory containing namespaces.ini (with
            // --namespace-option-index selecting from it) or it refuses with "No mod chosen:
            // Select your mod directory first." and exits without patching anything. The .rte ->
            // .rtf conversion above already ran for every section (including the selected one),
            // which is what actually prevents the rte_editor/ctypes.windll import crash - Holo
            // reading namespaces.ini normally afterward is safe once no section names a .rte.
            ConvertRteInfoDocuments(tslPatcherDirectory);
            return tslPatcherDirectory;
        }

        private static void ConvertRteInfoDocuments([CanBeNull] DirectoryInfo directory)
        {
            if (directory is null || !directory.Exists)
            {
                return;
            }

            try
            {
                foreach (FileInfo rte in directory.GetFiles("*.rte", SearchOption.AllDirectories))
                {
                    ConvertRteInfoDocument(rte.DirectoryName, rte.Name);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Patcher] Could not convert .rte info documents under '{directory.FullName}': {ex.Message}");
            }
        }

        private static void ConvertRteInfoDocument([CanBeNull] string directory, [CanBeNull] string infoName)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(infoName))
            {
                return;
            }

            string rtePath = Path.Combine(directory, infoName);
            if (!infoName.EndsWith(".rte", StringComparison.OrdinalIgnoreCase))
            {
                rtePath = Path.Combine(directory, Path.ChangeExtension(infoName, ".rte"));
            }

            if (!File.Exists(rtePath))
            {
                return;
            }

            string rtfPath = Path.ChangeExtension(rtePath, ".rtf");
            const string MinimalRtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\par}";
            try
            {
                if (!File.Exists(rtfPath))
                {
                    File.WriteAllText(rtfPath, MinimalRtf);
                }

                File.Delete(rtePath);
                Logger.LogVerbose($"[Patcher] Converted '{Path.GetFileName(rtePath)}' to '{Path.GetFileName(rtfPath)}' so Linux HoloPatcher will not import rte_editor (ctypes.windll).");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Patcher] Could not convert '{rtePath}' to RTF: {ex.Message}");
            }
        }

        private static void EnsureNamedInfoDocument([CanBeNull] string directory, [CanBeNull] string infoName)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(infoName))
            {
                return;
            }

            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                string infoPath = Path.Combine(directory, infoName);
                if (File.Exists(infoPath))
                {
                    return;
                }

                const string MinimalRtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\par}";
                File.WriteAllText(infoPath, MinimalRtf);
                Logger.LogVerbose($"[Patcher] Wrote placeholder '{infoName}' in '{directory}' so Linux HoloPatcher has InfoName.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Patcher] Could not ensure info document '{infoName}' in '{directory}': {ex.Message}");
            }
        }

        private static void WriteNamespacesIni(
            [NotNull] string path,
            [NotNull] Dictionary<string, Dictionary<string, string>> sections,
            [CanBeNull] Dictionary<string, string> indexSection,
            [NotNull] IReadOnlyList<string> namespaceOrder)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[Namespaces]");
            if (indexSection != null)
            {
                foreach (KeyValuePair<string, string> entry in indexSection)
                {
                    sb.Append(entry.Key).Append('=').AppendLine(entry.Value);
                }
            }
            else
            {
                for (int i = 0; i < namespaceOrder.Count; i++)
                {
                    sb.Append("Namespace").Append(i + 1).Append('=').AppendLine(namespaceOrder[i]);
                }
            }

            foreach (KeyValuePair<string, Dictionary<string, string>> section in sections)
            {
                if (section.Key.Equals("Namespaces", StringComparison.OrdinalIgnoreCase) || section.Value is null)
                {
                    continue;
                }

                sb.AppendLine();
                sb.Append('[').Append(section.Key).AppendLine("]");
                foreach (KeyValuePair<string, string> entry in section.Value)
                {
                    sb.Append(entry.Key).Append('=').AppendLine(entry.Value);
                }
            }

            File.WriteAllText(path, sb.ToString());
        }

        /// <summary>
        /// TSLPatcher-family installers expect an information document beside every <c>changes.ini</c>
        /// (<c>info.rtf</c> by default, see <c>PatcherNamespace.DefaultInfoFilename</c>) and abort when it
        /// is absent. Some mods ship without one -- or ship only the namespace subfolders' copies -- which
        /// fails the install for a purely cosmetic file that is never read for patch data. Write a minimal
        /// placeholder for any <c>changes.ini</c> that lacks one.
        /// <para>
        /// The placeholder is a minimal well-formed RTF document rather than a zero-byte file: an empty
        /// file is not valid RTF and a strict reader can fail on it, which would trade one abort for
        /// another. Existing files are never touched -- a mod's real notes always win.
        /// </para>
        /// </summary>
        private static void EnsureInfoRtfBesideChangesIni([CanBeNull] DirectoryInfo tslPatcherDirectory)
        {
            if (tslPatcherDirectory is null || !tslPatcherDirectory.Exists)
            {
                return;
            }

            // Matches PatcherNamespace.DefaultInfoFilename in the patcher tree; duplicated as a
            // literal so Core does not take a dependency on the legacy HoloPatcher projects.
            const string InfoDocumentFilename = "info.rtf";
            const string MinimalRtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\par}";

            try
            {
                foreach (FileInfo changesIni in tslPatcherDirectory.GetFiles("changes.ini", SearchOption.AllDirectories))
                {
                    string directory = changesIni.DirectoryName;
                    if (string.IsNullOrEmpty(directory))
                    {
                        continue;
                    }

                    // A namespace may declare a different information filename, and some mods ship .rte
                    // instead of .rtf. Only synthesize when the folder has no information document at all.
                    if (Directory.EnumerateFiles(directory, "info.*", SearchOption.TopDirectoryOnly).Any())
                    {
                        continue;
                    }

                    string infoPath = Path.Combine(directory, InfoDocumentFilename);
                    File.WriteAllText(infoPath, MinimalRtf);
                    Logger.LogVerbose($"[Patcher] Wrote placeholder '{InfoDocumentFilename}' beside '{changesIni.FullName}' (mod shipped none).");
                }
            }
            catch (Exception ex)
            {
                // Never fail an install over a cosmetic file; the patcher will report it if it truly matters.
                Logger.LogWarning($"[Patcher] Could not ensure an info document beside changes.ini: {ex.Message}");
            }
        }

        [NotNull]
        /// <summary>
        /// Quote a path for <see cref="System.Diagnostics.ProcessStartInfo.Arguments"/> so spaces
        /// survive parsing. Do not use <c>--flag="value"</c>: that collapses to <c>--flag=value</c>,
        /// which OdyPatcher rejects.
        /// </summary>
        private static string QuoteProcessArgument([CanBeNull] string value)
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

        internal static bool IsHoloListIndexParseFailure([CanBeNull] string patcherText)
        {
            if (string.IsNullOrEmpty(patcherText))
            {
                return false;
            }

            return patcherText.IndexOf("Invalid TypeId", StringComparison.OrdinalIgnoreCase) >= 0
                && patcherText.IndexOf("ListIndex", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsGuideDirectedTslPatchdataDeleteOutcome([CanBeNull] string patcherText)
        {
            if (string.IsNullOrEmpty(patcherText))
            {
                return false;
            }

            bool completed = patcherText.IndexOf("Successfully completed", StringComparison.OrdinalIgnoreCase) >= 0
                || patcherText.IndexOf("Total patches:", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!completed)
            {
                return false;
            }

            List<string> errors = patcherText
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.IndexOf("[Error]", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Error: ", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("FileNotFoundError", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Could not locate resource to patch", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Could not load source file to patch", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            return errors.Count > 0 && errors.All(IsGuideDirectedTslPatchdataDeleteError);
        }

        private bool IsGuideDirectedTslPatchdataDeleteError([CanBeNull] string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            if (!HasGuideDirectedTslPatchdataDelete())
            {
                return false;
            }

            // Holo splits the missing-source pair: this line has no filename, the
            // next line names the deleted tslpatchdata file (e.g. keblastore.utm).
            if (line.IndexOf("Could not load source file to patch", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            string directions = _parentComponent?.Directions ?? string.Empty;
            return Regex.Matches(line, @"[\w.-]+\.\w+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                .Cast<Match>()
                .Select(m => m.Value)
                .Any(name =>
                    name.IndexOf("installlog", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("FileNotFound", StringComparison.OrdinalIgnoreCase) < 0
                    && (directions.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                        || ComponentDeletesTslPatchdataFile(name)));
        }

        private bool HasGuideDirectedTslPatchdataDelete()
        {
            string directions = _parentComponent?.Directions;
            if (!string.IsNullOrWhiteSpace(directions)
                && directions.IndexOf("tslpatchdata", StringComparison.OrdinalIgnoreCase) >= 0
                && directions.IndexOf("delete", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return _parentComponent?.Instructions?.Any(instruction =>
                       instruction != null
                       && instruction.Action == ActionType.Delete
                       && instruction.Source != null
                       && instruction.Source.Any(source =>
                           !string.IsNullOrWhiteSpace(source)
                           && source.IndexOf("tslpatchdata", StringComparison.OrdinalIgnoreCase) >= 0))
                   == true;
        }

        private bool ComponentDeletesTslPatchdataFile([NotNull] string fileName)
        {
            return _parentComponent?.Instructions?.Any(instruction =>
                       instruction != null
                       && instruction.Action == ActionType.Delete
                       && instruction.Source != null
                       && instruction.Source.Any(source =>
                           !string.IsNullOrWhiteSpace(source)
                           && source.IndexOf("tslpatchdata", StringComparison.OrdinalIgnoreCase) >= 0
                           && source.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0))
                   == true;
        }

        private async Task<List<string>> VerifyInstall([ItemNotNull] IReadOnlyList<string> sourcePaths = null)
        {
            if (_fileSystemProvider is null)
            {
                throw new InvalidOperationException("File system provider must be set before calling VerifyInstall. Call SetFileSystemProvider() first.");
            }

            if (sourcePaths.IsNullOrEmptyCollection())
            {
                sourcePaths = RealSourcePaths;
            }

            if (sourcePaths.IsNullOrEmptyCollection())
            {
                throw new ArgumentNullException(nameof(sourcePaths));
            }

            if (_fileSystemProvider.IsDryRun)
            {
                await Logger.LogVerboseAsync("Skipping install log verification for dry-run").ConfigureAwait(false);
                return new List<string>();
            }
            var allErrorLines = new List<string>();
            foreach (string sourcePath in sourcePaths)
            {
                string tslPatcherDirPath = _fileSystemProvider.GetDirectoryName(sourcePath)
                    ?? throw new DirectoryNotFoundException($"Could not retrieve parent directory of '{sourcePath}'.");
                string fullInstallLogFile = Path.Combine(tslPatcherDirPath, path2: "installlog.rtf");
                if (!_fileSystemProvider.FileExists(fullInstallLogFile))
                {
                    fullInstallLogFile = Path.Combine(tslPatcherDirPath, path2: "installlog.txt");
                    if (!_fileSystemProvider.FileExists(fullInstallLogFile))
                    {
                        throw new FileNotFoundException(message: "Install log file not found.", fullInstallLogFile);
                    }
                }
                string installLogContent = await _fileSystemProvider.ReadFileAsync(fullInstallLogFile).ConfigureAwait(false);
                foreach (string line in installLogContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if ((line.Contains("Error: ") || line.Contains("[Error]")) && !string.IsNullOrWhiteSpace(line))
                    {
                        allErrorLines.Add(line);
                    }
                }
            }
            await Logger.LogVerboseAsync("No errors found in TSLPatcher installation log file").ConfigureAwait(false);
            return allErrorLines;
        }

        /// <summary>
        /// Copies the patcher's install log (or leftover stdout/stderr) into the ModSync log
        /// stream with a <c>[Patcher]</c> prefix. Lines already captured from the process are
        /// skipped so live-prefixed stdout is not replayed.
        /// </summary>
        private static async Task PipePatcherLogIntoModSyncAsync(
            [CanBeNull] string tslPatcherDirectory,
            [CanBeNull] string stdout,
            [CanBeNull] string stderr)
        {
            var alreadyLogged = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in SplitPatcherLogLines(stdout))
            {
                if (line.Length > 0)
                {
                    _ = alreadyLogged.Add(line);
                }
            }

            foreach (string line in SplitPatcherLogLines(stderr))
            {
                if (line.Length > 0)
                {
                    _ = alreadyLogged.Add(line);
                }
            }

            string fileText = TryReadPatcherInstallLog(tslPatcherDirectory);
            IEnumerable<string> sourceLines = !string.IsNullOrWhiteSpace(fileText)
                ? SplitPatcherLogLines(fileText)
                : SplitPatcherLogLines(stdout).Concat(SplitPatcherLogLines(stderr));

            foreach (string line in sourceLines)
            {
                if (line.Length == 0 || alreadyLogged.Contains(line))
                {
                    continue;
                }

                await Logger.LogAsync("[Patcher] " + line).ConfigureAwait(false);
            }
        }

        [NotNull]
        private static IEnumerable<string> SplitPatcherLogLines([CanBeNull] string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                yield break;
            }

            foreach (string raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length > 0)
                {
                    yield return line;
                }
            }
        }

        [CanBeNull]
        private static string TryReadPatcherInstallLog([CanBeNull] string tslPatcherDirectory)
        {
            if (string.IsNullOrWhiteSpace(tslPatcherDirectory) || !Directory.Exists(tslPatcherDirectory))
            {
                return null;
            }

            string txt = Path.Combine(tslPatcherDirectory, "installlog.txt");
            if (File.Exists(txt))
            {
                try
                {
                    return File.ReadAllText(txt);
                }
                catch (Exception ex)
                {
                    Logger.LogVerbose($"[Patcher] Could not read '{txt}': {ex.Message}");
                }
            }

            string rtf = Path.Combine(tslPatcherDirectory, "installlog.rtf");
            if (File.Exists(rtf))
            {
                try
                {
                    return File.ReadAllText(rtf);
                }
                catch (Exception ex)
                {
                    Logger.LogVerbose($"[Patcher] Could not read '{rtf}': {ex.Message}");
                }
            }

            return null;
        }
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName][CanBeNull] string propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        [NotNull]
        [ItemNotNull]
        public IReadOnlyList<Option> GetChosenOptions() => _parentComponent?.Options.Where(
                x => x != null && x.IsSelected && Source.Contains(x.Guid.ToString(), StringComparer.OrdinalIgnoreCase)
            ).ToArray() ?? Array.Empty<Option>();
    }
}
