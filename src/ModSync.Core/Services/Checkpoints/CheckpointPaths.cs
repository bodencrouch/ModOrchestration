// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using ModSync.Core;
using ModSync.Core.Installation;
using ModSync.Core.Utility;
using Newtonsoft.Json;

namespace ModSync.Core.Services.Checkpoints
{
    public static class CheckpointPaths
    {
        public const string CheckpointFolderName = ".modsync";
        private const string CheckpointsDirectoryName = "checkpoints";
        private const string SessionsDirectoryName = "sessions";
        private const string ObjectsDirectoryName = "objects";

        public static string GetRoot(string gameDirectory)
        {
            if (string.IsNullOrWhiteSpace(gameDirectory))
            {
                throw new ArgumentException("Game directory must be provided.", nameof(gameDirectory));
            }

            return Path.Combine(gameDirectory, CheckpointFolderName);
        }

        public static string GetCheckpointsRoot(string gameDirectory) =>
            Path.Combine(GetRoot(gameDirectory), CheckpointsDirectoryName);

        public static string GetGitDirectory(string gameDirectory) =>
            Path.Combine(GetCheckpointsRoot(gameDirectory), ".git");

        public static string GetSessionsDirectory(string gameDirectory) =>
            Path.Combine(GetCheckpointsRoot(gameDirectory), SessionsDirectoryName);

        public static string GetObjectsDirectory(string gameDirectory) =>
            Path.Combine(GetCheckpointsRoot(gameDirectory), ObjectsDirectoryName);

        public static string EnsureRoot(DirectoryInfo gameDirectory)
        {
            string root = GetRoot(gameDirectory.FullName);
            _ = Directory.CreateDirectory(root);
            return root;
        }
    }

    public sealed class CheckpointManager
    {
        private const string SessionFileName = "install_session.json";
        private const string BackupFileName = "last_good_backup.zip";
        private const string TempWorkingFolderPrefix = "ModSync_Backup_";
        private const string TempRestoreFolderPrefix = "ModSync_Restore_";

        private static readonly JsonSerializerSettings s_serializerSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
        };

        private readonly SemaphoreSlim _saveSemaphore = new SemaphoreSlim(1, 1);
        private InstallSessionState _state;
        private string _sessionPath = string.Empty;

        public InstallSessionState State => _state;

        public string BackupPath { get; private set; } = string.Empty;

        public async Task InitializeAsync([NotNull] IList<ModComponent> components, [NotNull] DirectoryInfo destinationPath)
        {
            if (components is null)
            {
                throw new ArgumentNullException(nameof(components));
            }

            if (destinationPath is null)
            {
                throw new ArgumentNullException(nameof(destinationPath));
            }

            _sessionPath = GetSessionFilePath(destinationPath);
            EnsureFolderExists(destinationPath);

            if (File.Exists(_sessionPath))
            {
                InstallSessionState existingState = null;
                try
                {
                    string json = await NetFrameworkCompatibility.ReadAllTextAsync(_sessionPath, Encoding.UTF8).ConfigureAwait(false);
                    existingState = JsonConvert.DeserializeObject<InstallSessionState>(json, s_serializerSettings);
                }
                catch (Exception ex) when (ex is JsonException || ex is IOException)
                {
                    Logger.LogWarning($"Failed to read existing session at '{_sessionPath}': {ex.Message}");
                }

                // Only adopt a session written for this same destination. A session copied in
                // from a different install (e.g. the whole game folder was copied elsewhere)
                // must not silently donate its Completed/Skipped markers to an unrelated install.
                bool destinationMatches = existingState is null
                    || string.IsNullOrWhiteSpace(existingState.DestinationPath)
                    || PathsEqual(existingState.DestinationPath, destinationPath.FullName);

                if (existingState != null && destinationMatches && ValidateLoadedState(existingState))
                {
                    _state = existingState;
                    SyncComponentsWithState(components);
                    return;
                }
            }

            _state = CreateNewState(components, destinationPath.FullName);
            SyncInitialComponentState(components);

            await SaveAsync().ConfigureAwait(false);
        }

        public async Task SaveAsync()
        {
            if (_state is null || string.IsNullOrEmpty(_sessionPath))
            {
                return;
            }

            await _saveSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                string tempPath = _sessionPath + ".tmp";
                string json = JsonConvert.SerializeObject(_state, s_serializerSettings);
                await NetFrameworkCompatibility.WriteAllTextAsync(tempPath, json, Encoding.UTF8).ConfigureAwait(false);
                File.Copy(tempPath, _sessionPath, overwrite: true);
                File.Delete(tempPath);
            }
            finally
            {
                _ = _saveSemaphore.Release();
            }
        }

        public async Task DeleteSessionAsync()
        {
            if (string.IsNullOrEmpty(_sessionPath))
            {
                return;
            }

            await _saveSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (File.Exists(_sessionPath))
                {
                    File.Delete(_sessionPath);
                }
            }
            finally
            {
                _ = _saveSemaphore.Release();
            }
        }

        /// <summary>
        /// Deletes <c>install_session.json</c> for a destination without initializing a manager instance.
        /// Does not remove Git checkpoint history under <c>.modsync/checkpoints</c>.
        /// </summary>
        public static Task DeleteSessionFileAsync([NotNull] DirectoryInfo destinationPath)
        {
            if (destinationPath is null)
            {
                throw new ArgumentNullException(nameof(destinationPath));
            }

            string sessionPath = GetSessionFilePath(destinationPath);
            if (File.Exists(sessionPath))
            {
                File.Delete(sessionPath);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Probes an existing install session for incomplete selected work that can be resumed.
        /// Identity is destination path plus overlapping component GUIDs (no build fingerprint required).
        /// </summary>
        [NotNull]
        public static async Task<ResumableSessionInfo> ProbeResumableSessionAsync(
            [NotNull] DirectoryInfo destinationPath,
            [NotNull] IEnumerable<ModComponent> selectedComponents)
        {
            if (destinationPath is null)
            {
                throw new ArgumentNullException(nameof(destinationPath));
            }

            if (selectedComponents is null)
            {
                throw new ArgumentNullException(nameof(selectedComponents));
            }

            string sessionPath = GetSessionFilePath(destinationPath);
            if (!File.Exists(sessionPath))
            {
                return ResumableSessionInfo.None;
            }

            InstallSessionState state;
            try
            {
                string json = await NetFrameworkCompatibility.ReadAllTextAsync(sessionPath, Encoding.UTF8).ConfigureAwait(false);
                state = JsonConvert.DeserializeObject<InstallSessionState>(json, s_serializerSettings);
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException)
            {
                // A corrupted or unreadable session file (e.g. truncated by a crash mid-write)
                // is treated the same as "no session" -- fail closed rather than surfacing an
                // unhandled exception on the wizard's navigation path.
                Logger.LogWarning($"Failed to read resumable session at '{sessionPath}': {ex.Message}");
                return ResumableSessionInfo.None;
            }

            if (state is null || !ValidateLoadedState(state))
            {
                return ResumableSessionInfo.None;
            }

            if (!string.IsNullOrWhiteSpace(state.DestinationPath) &&
                !PathsEqual(state.DestinationPath, destinationPath.FullName))
            {
                return ResumableSessionInfo.None;
            }

            var selected = selectedComponents
                .Where(c => c != null && c.IsSelected && !c.WidescreenOnly)
                .ToList();
            if (selected.Count == 0)
            {
                return ResumableSessionInfo.None;
            }

            int overlapping = 0;
            int completed = 0;
            int remaining = 0;

            foreach (ModComponent component in selected)
            {
                if (!state.Components.TryGetValue(component.Guid, out ComponentSessionEntry entry))
                {
                    continue;
                }

                overlapping++;
                if (entry.State == ModComponent.ComponentInstallState.Completed ||
                    entry.State == ModComponent.ComponentInstallState.Skipped)
                {
                    completed++;
                }
                else
                {
                    remaining++;
                }
            }

            // Need overlap with the prior session and at least one incomplete selected component.
            bool isResumable = overlapping > 0 && remaining > 0;
            return new ResumableSessionInfo(isResumable, completed, remaining, overlapping);
        }

        private static bool PathsEqual(string left, string right)
        {
            string a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        [NotNull]
        public ComponentSessionEntry GetComponentEntry(Guid componentId)
        {
            if (_state is null)
            {
                throw new InvalidOperationException("Install session not initialized.");
            }

            if (_state.Components.TryGetValue(componentId, out ComponentSessionEntry entry))
            {
                return entry;
            }

            throw new KeyNotFoundException($"ModComponent {componentId} not found in session state");
        }

        public void UpdateComponentState([NotNull] ModComponent component)
        {
            if (component is null)
            {
                throw new ArgumentNullException(nameof(component));
            }

            ComponentSessionEntry entry = GetComponentEntry(component.Guid);
            entry.State = component.InstallState;
            entry.LastStartedUtc = component.LastStartedUtc;
            entry.LastCompletedUtc = component.LastCompletedUtc;
        }

        public void UpdateBackupPath(string backupPath)
        {
            if (_state is null)
            {
                throw new InvalidOperationException("Install session not initialized.");
            }

            _state.BackupPath = backupPath ?? string.Empty;
        }

        public async Task EnsureSnapshotAsync([NotNull] DirectoryInfo source, CancellationToken cancellationToken)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            BackupPath = GetBackupPath(source);
            UpdateBackupPath(BackupPath);

            if (File.Exists(BackupPath))
            {
                return;
            }

            await CreateSnapshotAsync(source, cancellationToken).ConfigureAwait(false);
        }

        public async Task PromoteSnapshotAsync([NotNull] DirectoryInfo source, CancellationToken cancellationToken)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            BackupPath = GetBackupPath(source);
            UpdateBackupPath(BackupPath);
            await CreateSnapshotAsync(source, cancellationToken).ConfigureAwait(false);
        }

        public Task SaveSnapshotAsync(CancellationToken cancellationToken)
        {
            return PromoteSnapshotAsync(new DirectoryInfo(_state.DestinationPath), cancellationToken);
        }

        public async Task MarkComponentCompletedAsync(Guid componentId)
        {
            ComponentSessionEntry entry = GetComponentEntry(componentId);
            entry.State = ModComponent.ComponentInstallState.Completed;
            entry.LastCompletedUtc = DateTimeOffset.UtcNow;
            await SaveAsync().ConfigureAwait(false);
        }

        public async Task RestoreSnapshotAsync([NotNull] DirectoryInfo destination, CancellationToken cancellationToken)
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            BackupPath = GetBackupPath(destination);
            if (!File.Exists(BackupPath))
            {
                throw new FileNotFoundException("Backup snapshot not found", BackupPath);
            }

            string tempExtract = Path.Combine(Path.GetTempPath(), TempRestoreFolderPrefix + Guid.NewGuid());
            _ = Directory.CreateDirectory(tempExtract);

            try
            {
                Directory.Delete(tempExtract, recursive: true);

                await Task.Run(() => ZipFile.ExtractToDirectory(BackupPath, tempExtract, Encoding.UTF8), cancellationToken).ConfigureAwait(false);

                foreach (FileSystemInfo fsi in destination.EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(fsi.Name, CheckpointPaths.CheckpointFolderName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    SafeDelete(fsi);
                }

                CopyDirectory(new DirectoryInfo(tempExtract), destination, cancellationToken, skipFolder: CheckpointPaths.CheckpointFolderName);
            }
            finally
            {
                if (Directory.Exists(tempExtract))
                {
                    Directory.Delete(tempExtract, recursive: true);
                }
            }
        }

        private void SyncComponentsWithState([NotNull] IList<ModComponent> components)
        {
            foreach (ModComponent component in components)
            {
                if (!_state.Components.TryGetValue(component.Guid, out ComponentSessionEntry entry))
                {
                    entry = new ComponentSessionEntry
                    {
                        ComponentId = component.Guid,
                    };
                    _state.Components[component.Guid] = entry;
                }

                component.InstallState = entry.State;
                component.LastStartedUtc = entry.LastStartedUtc;
                component.LastCompletedUtc = entry.LastCompletedUtc;
            }
        }

        private void SyncInitialComponentState([NotNull] IList<ModComponent> components)
        {
            foreach (ModComponent component in components)
            {
                var entry = new ComponentSessionEntry
                {
                    ComponentId = component.Guid,
                    State = component.InstallState,
                    LastStartedUtc = component.LastStartedUtc,
                    LastCompletedUtc = component.LastCompletedUtc,
                };
                _state.Components[component.Guid] = entry;
            }
        }

        private static InstallSessionState CreateNewState([NotNull] IList<ModComponent> components, string destinationPath)
        {
            return new InstallSessionState
            {
                Version = "2.0",
                SessionId = Guid.NewGuid(),
                CreatedUtc = DateTimeOffset.UtcNow,
                DestinationPath = destinationPath,
                ComponentOrder = components.Select(component => component.Guid).ToList(),
                Components = new Dictionary<Guid, ComponentSessionEntry>(),
                CurrentRevision = 0,
            };
        }

        private static string GetSessionFilePath(DirectoryInfo destinationPath)
        {
            string folder = CheckpointPaths.GetRoot(destinationPath.FullName);
            return Path.Combine(folder, SessionFileName);
        }

        private static void EnsureFolderExists(DirectoryInfo destinationPath)
        {
            _ = Directory.CreateDirectory(CheckpointPaths.GetRoot(destinationPath.FullName));
        }

        private static bool ValidateLoadedState(InstallSessionState state) =>
            state != null && state.ComponentOrder != null && state.Components != null;

        private async Task CreateSnapshotAsync(DirectoryInfo source, CancellationToken cancellationToken)
        {
            string tempWorking = Path.Combine(Path.GetTempPath(), TempWorkingFolderPrefix + Guid.NewGuid());
            _ = Directory.CreateDirectory(tempWorking);

            try
            {
                CopyDirectory(source, new DirectoryInfo(tempWorking), cancellationToken, skipFolder: CheckpointPaths.CheckpointFolderName);

                if (File.Exists(BackupPath))
                {
                    File.Delete(BackupPath);
                }

                await Task.Run(() => ZipFile.CreateFromDirectory(tempWorking, BackupPath, CompressionLevel.Fastest, includeBaseDirectory: false), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (Directory.Exists(tempWorking))
                {
                    Directory.Delete(tempWorking, recursive: true);
                }
            }
        }

        private static void CopyDirectory(DirectoryInfo source, DirectoryInfo destination, CancellationToken cancellationToken, string skipFolder)
        {
            if (!destination.Exists)
            {
                destination.Create();
            }

            foreach (FileInfo file in source.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string targetPath = Path.Combine(destination.FullName, file.Name);
                _ = file.CopyTo(targetPath, overwrite: true);
            }

            foreach (DirectoryInfo dir in source.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(dir.Name, skipFolder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var targetDir = new DirectoryInfo(Path.Combine(destination.FullName, dir.Name));
                CopyDirectory(dir, targetDir, cancellationToken, skipFolder);
            }
        }

        private static void SafeDelete(FileSystemInfo fsi)
        {
            if (fsi is DirectoryInfo directory)
            {
                directory.Delete(recursive: true);
            }
            else
            {
                fsi.Delete();
            }
        }

        private static string GetBackupPath(DirectoryInfo destination)
        {
            string folder = CheckpointPaths.GetRoot(destination.FullName);
            _ = Directory.CreateDirectory(folder);
            return Path.Combine(folder, BackupFileName);
        }
    }
}
