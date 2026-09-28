// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

namespace ModSync.Core.Installation
{
    /// <summary>
    /// Result of probing <c>install_session.json</c> for an incomplete install that can be resumed.
    /// </summary>
    public readonly struct ResumableSessionInfo
    {
        public ResumableSessionInfo(
            bool isResumable,
            int completedSelectedCount,
            int remainingSelectedCount,
            int overlappingComponentCount)
        {
            IsResumable = isResumable;
            CompletedSelectedCount = completedSelectedCount;
            RemainingSelectedCount = remainingSelectedCount;
            OverlappingComponentCount = overlappingComponentCount;
        }

        public bool IsResumable { get; }

        public int CompletedSelectedCount { get; }

        public int RemainingSelectedCount { get; }

        public int OverlappingComponentCount { get; }

        public static ResumableSessionInfo None { get; } = new ResumableSessionInfo(
            isResumable: false,
            completedSelectedCount: 0,
            remainingSelectedCount: 0,
            overlappingComponentCount: 0);
    }
}
