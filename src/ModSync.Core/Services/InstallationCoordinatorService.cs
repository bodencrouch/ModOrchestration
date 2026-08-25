// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using ModSync.Core.Services.ImmutableCheckpoint;

namespace ModSync.Core.Services
{
    /// <summary>
    /// Compatibility boundary for the legacy checkpoint browser. Installation execution lives only in
    /// <c>InstallationPipelineService</c>; this type must never grow another component execution loop.
    /// </summary>
    public class InstallationCoordinatorService
    {
        public static Task RollbackInstallationAsync(
            IProgress<InstallProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(
                "The legacy checkpoint browser does not use the active InstallCoordinator checkpoint format. No files were changed.");
        }

        public static Task<List<CheckpointSession>> ListAvailableSessionsAsync() =>
            Task.FromResult(new List<CheckpointSession>());

        public static Task RestoreToCheckpointAsync(
            string sessionId,
            string checkpointId,
            string destinationPath,
            IProgress<InstallProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(
                "The legacy checkpoint browser does not use the active InstallCoordinator checkpoint format. No files were changed.");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0048:File name must match type name", Justification = "Legacy dialog contract")]
    public class InstallationErrorEventArgs : EventArgs
    {
        public ModComponent Component { get; set; }
        public ModComponent.InstallExitCode ErrorCode { get; set; }
        public Exception Exception { get; set; }
        public bool CanRollback { get; set; }
        public bool RollbackRequested { get; set; }
        public string SessionId { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0048:File name must match type name", Justification = "Legacy dialog contract")]
    public class InstallProgress
    {
        public InstallPhase Phase { get; set; }
        public string Message { get; set; }
        public int Current { get; set; }
        public int Total { get; set; }
        public string ComponentName { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0048:File name must match type name", Justification = "Legacy dialog contract")]
    public enum InstallPhase
    {
        Initializing,
        InstallingComponent,
        CreatingCheckpoint,
        RollingBack,
        Completed,
    }
}
