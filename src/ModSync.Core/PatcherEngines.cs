// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

namespace ModSync.Core
{
    /// <summary>Values for <see cref="MainConfig.PatcherEngine"/> (persisted in settings).</summary>
    public static class PatcherEngines
    {
        public const string Holopatcher = "Holopatcher";
        public const string KPatcher = "KPatcher";
        public const string OdyPatcher = "OdyPatcher";

        /// <summary>BioPatcher is the successor name of OdyPatcher; both share the same CLI surface.</summary>
        public const string BioPatcher = "BioPatcher";

        /// <summary>
        /// True when <paramref name="engine"/> selects the OdyPatcher/BioPatcher external CLI family.
        /// </summary>
        public static bool IsBioFamily(string engine) =>
            string.Equals(engine, OdyPatcher, System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(engine, BioPatcher, System.StringComparison.OrdinalIgnoreCase);
    }
}
