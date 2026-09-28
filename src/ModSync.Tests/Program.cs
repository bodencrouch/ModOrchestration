// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;

using ModSync.Core.CLI;

using ModSync.Tests.Fixtures;

namespace ModSync.Tests
{

    public static class Program
    {
        public static int Main(string[] args)
        {
            // Fixture-only verbs are handled here rather than in ModSync.Core's parser: they build
            // test material and have no place in the shipped CLI.
            if (args != null && args.Length > 0)
            {
                if (string.Equals(args[0], "make-mock-kotor", StringComparison.OrdinalIgnoreCase))
                {
                    return MockKotorInstall.RunCli(Slice(args));
                }

                if (string.Equals(args[0], "make-mock-mods", StringComparison.OrdinalIgnoreCase))
                {
                    return MockModArchives.RunCli(Slice(args));
                }

                if (string.Equals(args[0], "make-mock-build", StringComparison.OrdinalIgnoreCase))
                {
                    return MockBuildFile.RunCli(Slice(args));
                }
            }

            return ModBuildConverter.Run(args);
        }

        private static string[] Slice(string[] args)
        {
            var rest = new string[args.Length - 1];
            Array.Copy(args, 1, rest, 0, rest.Length);
            return rest;
        }
    }
}
