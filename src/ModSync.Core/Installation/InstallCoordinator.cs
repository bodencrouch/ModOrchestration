// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using JetBrains.Annotations;

using ModSync.Core;
using ModSync.Core.Services.Checkpoints;

namespace ModSync.Core.Installation
{
    public sealed class InstallCoordinator : IDisposable
    {
        private static readonly object s_checkpointServicesLock = new object();
        private static readonly Dictionary<string, List<Services.GitCheckpointService>> s_checkpointServicesByDirectory =
            new Dictionary<string, List<Services.GitCheckpointService>>(StringComparer.OrdinalIgnoreCase);

        private string _checkpointServiceDirectory;

        public InstallCoordinator()
        {
            CheckpointManager = new CheckpointManager();
        }

        public CheckpointManager CheckpointManager { get; }
        public Services.GitCheckpointService CheckpointService { get; private set; }

        public async Task<ResumeResult> InitializeAsync(
            [NotNull] IList<ModComponent> components,
            [NotNull] DirectoryInfo destinationPath,
            CancellationToken cancellationToken,
            bool preserveInputOrder = false,
            bool enableGitCheckpoints = true)
        {
            await CheckpointManager.InitializeAsync(components, destinationPath).ConfigureAwait(false);
            if (!MainConfig.NoCheckpoint)
            {
                // Always take a fresh snapshot here (PromoteSnapshotAsync), never
                // EnsureSnapshotAsync's lazy "reuse whatever's already on disk" path.
                // EnsureSnapshotAsync only checks File.Exists(BackupPath) — it has no way to
                // know whether an existing zip actually reflects the current game-directory
                // state. A relaunch after a killed/crashed prior run (or after a checkpoint
                // restore) leaves a stale zip sitting there from whatever state existed when
                // it was written, possibly mid-corruption from that prior run. Fail-closed
                // restore trusts this zip completely (RestoreSnapshotAsync does a real
                // wipe-and-replace from it) — reusing a stale one means "restoring the
                // pre-component snapshot" silently restores the wrong snapshot instead, which
                // is indistinguishable from success until someone diffs the result. A full
                // recursive copy + zip on every launch costs real time on large/slow installs,
                // but a silently-wrong snapshot costs correctness, which is the bar this whole
                // pipeline exists to hold.
                //
                // On slow external disks this can look like a hang (near-zero CPU, futex wait
                // on I/O) with no log lines after "Starting installation...".
                await Logger.LogAsync(
                    "Creating install backup snapshot (full game-directory copy + zip). "
                    + "This can take several minutes on large installs or slow disks; "
                    + "use --no-checkpoint to skip."
                ).ConfigureAwait(false);
                await CheckpointManager.PromoteSnapshotAsync(destinationPath, cancellationToken).ConfigureAwait(false);
                await Logger.LogAsync("Install backup snapshot ready.").ConfigureAwait(false);
            }

            ReleaseCheckpointService();

            if (MainConfig.NoCheckpoint || !enableGitCheckpoints)
            {
                // Git checkpoints disabled (--no-checkpoint, or the caller opted out): skip the
                // git-based baseline snapshot entirely (it re-syncs the whole game directory and is
                // prohibitively slow on large installs / slow storage). No rollback capability
                // is available for this session, but the install_session.json resume record is
                // still written so the wizard can offer Resume/Start over.
                await CheckpointManager.SaveAsync().ConfigureAwait(false);
                List<ModComponent> orderedNoCheckpoint = preserveInputOrder
                    ? components.ToList()
                    : GetOrderedInstallList(components);
                return new ResumeResult(CheckpointManager.State.SessionId, orderedNoCheckpoint);
            }

            // Initialize Git-based checkpoint system
            await Logger.LogAsync(
                "Initializing Git checkpoint baseline (copies the full game tree into .modsync). "
                + "This can also take several minutes; use --no-checkpoint to skip."
            ).ConfigureAwait(false);
            CheckpointService = new Services.GitCheckpointService(destinationPath.FullName);
            _checkpointServiceDirectory = NormalizeDirectoryKey(destinationPath.FullName);
            RegisterCheckpointService(_checkpointServiceDirectory, CheckpointService);
            try
            {
                string baselineCommitId = await CheckpointService.InitializeAsync(cancellationToken).ConfigureAwait(false);
                CheckpointManager.State.BaselineCheckpointId = baselineCommitId;

                await CheckpointManager.SaveAsync().ConfigureAwait(false);
                List<ModComponent> ordered = preserveInputOrder
                    ? components.ToList()
                    : GetOrderedInstallList(components);
                return new ResumeResult(CheckpointManager.State.SessionId, ordered);
            }
            catch
            {
                ReleaseCheckpointService();
                throw;
            }
        }

        public static List<ModComponent> GetOrderedInstallList([NotNull][ItemNotNull] IList<ModComponent> components)
        {
            if (components is null)
            {
                throw new ArgumentNullException(nameof(components));
            }

            var componentMap = components.ToDictionary(c => c.Guid);

            var adjacency = new Dictionary<Guid, List<Guid>>();
            var indegree = new Dictionary<Guid, int>();

            foreach (ModComponent component in components)
            {
                adjacency[component.Guid] = new List<Guid>();
                indegree[component.Guid] = 0;
            }

            foreach (ModComponent component in components)
            {
                foreach (Guid dependency in component.Dependencies)
                {
                    if (!componentMap.ContainsKey(dependency))
                    {
                        continue;
                    }

                    adjacency[dependency].Add(component.Guid);
                    indegree[component.Guid]++;
                }

                foreach (Guid installAfter in component.InstallAfter)
                {
                    if (!componentMap.ContainsKey(installAfter))
                    {
                        continue;
                    }

                    adjacency[installAfter].Add(component.Guid);
                    indegree[component.Guid]++;
                }

                foreach (Guid installBefore in component.InstallBefore)
                {
                    if (!componentMap.ContainsKey(installBefore))
                    {
                        continue;
                    }

                    aggAdjacency(component.Guid, installBefore);
                }
            }

            var queue = new Queue<Guid>(indegree.Where(kvp => kvp.Value == 0).Select(kvp => kvp.Key));
            var ordered = new List<ModComponent>();

            while (queue.Count > 0)
            {
                Guid current = queue.Dequeue();
                ordered.Add(componentMap[current]);

                foreach (Guid dependent in adjacency[current])
                {
                    indegree[dependent]--;
                    if (indegree[dependent] == 0)
                    {
                        queue.Enqueue(dependent);
                    }
                }
            }

            if (ordered.Count != components.Count)
            {
                foreach (ModComponent component in components)
                {
                    if (!ordered.Contains(component))
                    {
                        ordered.Add(component);
                    }
                }
            }

            return ordered;

            void aggAdjacency(Guid from, Guid to)
            {
                adjacency[from].Add(to);
                indegree[to]++;
            }
        }

        public static void MarkBlockedDescendants([NotNull] IList<ModComponent> orderedComponents, Guid failedComponentId)
        {
            var visited = new HashSet<Guid>();
            var stack = new Stack<Guid>();
            stack.Push(failedComponentId);

            Dictionary<Guid, List<Guid>> dependentsMap = BuildDependentsMap(orderedComponents);

            while (stack.Count > 0)
            {
                Guid current = stack.Pop();
                if (!dependentsMap.TryGetValue(current, out List<Guid> dependents))
                {
                    continue;
                }

                foreach (Guid dependentId in dependents)
                {
                    if (visited.Add(dependentId))
                    {
                        ModComponent dependent = orderedComponents.FirstOrDefault(c => c.Guid == dependentId);
                        if (dependent != null && dependent.InstallState == ModComponent.ComponentInstallState.Pending)
                        {
                            dependent.InstallState = ModComponent.ComponentInstallState.Blocked;
                        }

                        stack.Push(dependentId);
                    }
                }
            }
        }

        private static Dictionary<Guid, List<Guid>> BuildDependentsMap(IList<ModComponent> components)
        {
            var map = new Dictionary<Guid, List<Guid>>();
            var componentMap = components.ToDictionary(c => c.Guid);

            foreach (ModComponent component in components)
            {
                void addEdge(Guid from, Guid to)
                {
                    if (!componentMap.ContainsKey(to))
                    {
                        return;
                    }

                    if (!map.TryGetValue(from, out List<Guid> list))
                    {
                        list = new List<Guid>();
                        map[from] = list;
                    }
                    if (!list.Contains(to))
                    {
                        list.Add(to);
                    }
                }

                foreach (Guid dependency in component.Dependencies)
                {
                    addEdge(dependency, component.Guid);
                }

                foreach (Guid installAfter in component.InstallAfter)
                {
                    addEdge(installAfter, component.Guid);
                }

                foreach (Guid installBefore in component.InstallBefore)
                {
                    addEdge(component.Guid, installBefore);
                }
            }

            return map;
        }

        public static void ClearSessionForTests(DirectoryInfo directoryInfo)
        {
            if (directoryInfo is null)
            {
                return;
            }

            string sessionFolder = CheckpointPaths.GetRoot(directoryInfo.FullName);
            if (!Directory.Exists(sessionFolder))
            {
                return;
            }

            try
            {
                DisposeCheckpointServicesForDirectory(directoryInfo.FullName);
                Directory.Delete(sessionFolder, recursive: true);
            }
            catch (IOException ex)
            {
                Logger.LogException(ex, $"Failed to delete install session folder '{sessionFolder}' due to an IO error during test cleanup.");
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.LogException(ex, $"Failed to delete install session folder '{sessionFolder}' due to insufficient permissions during test cleanup.");
            }
        }

        public void Dispose()
        {
            ReleaseCheckpointService();
        }

        private void ReleaseCheckpointService()
        {
            if (CheckpointService is null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(_checkpointServiceDirectory))
            {
                UnregisterCheckpointService(_checkpointServiceDirectory, CheckpointService);
            }

            CheckpointService.Dispose();
            CheckpointService = null;
            _checkpointServiceDirectory = null;
        }

        private static void RegisterCheckpointService(string directoryKey, Services.GitCheckpointService checkpointService)
        {
            lock (s_checkpointServicesLock)
            {
                if (!s_checkpointServicesByDirectory.TryGetValue(directoryKey, out List<Services.GitCheckpointService> services))
                {
                    services = new List<Services.GitCheckpointService>();
                    s_checkpointServicesByDirectory[directoryKey] = services;
                }

                services.Add(checkpointService);
            }
        }

        private static void UnregisterCheckpointService(string directoryKey, Services.GitCheckpointService checkpointService)
        {
            lock (s_checkpointServicesLock)
            {
                if (!s_checkpointServicesByDirectory.TryGetValue(directoryKey, out List<Services.GitCheckpointService> services))
                {
                    return;
                }

                _ = services.Remove(checkpointService);
                if (services.Count == 0)
                {
                    _ = s_checkpointServicesByDirectory.Remove(directoryKey);
                }
            }
        }

        private static void DisposeCheckpointServicesForDirectory(string directoryPath)
        {
            string directoryKey = NormalizeDirectoryKey(directoryPath);
            List<Services.GitCheckpointService> services;
            lock (s_checkpointServicesLock)
            {
                if (!s_checkpointServicesByDirectory.TryGetValue(directoryKey, out services))
                {
                    return;
                }

                _ = s_checkpointServicesByDirectory.Remove(directoryKey);
            }

            foreach (Services.GitCheckpointService checkpointService in services)
            {
                checkpointService.Dispose();
            }
        }

        private static string NormalizeDirectoryKey(string directoryPath)
        {
            return Path.GetFullPath(directoryPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

    }
}
