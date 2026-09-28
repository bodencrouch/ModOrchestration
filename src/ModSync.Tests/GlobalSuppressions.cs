// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

// Do not apply NUnit's obsolete TimeoutAttribute assembly-wide. It aborts the test thread at
// 120 seconds, including correctly named LongRunning tests, and can leave MainConfig/static parser
// state half-mutated for the next test. Bounded tests use CancelAfter; agent/CI runners enforce a
// process timeout when classifying long-running tests.

[assembly: SuppressMessage("Usage", "MA0004:Use Task.ConfigureAwait(false)", Justification = "<Pending>", Scope = "member", Target = "~M:ModSync.Tests.VirtualFileSystemWildcardTests.RunBothProviders(System.Collections.Generic.List{ModSync.Core.Instruction},System.String,System.String)~System.Threading.Tasks.Task{System.ValueTuple{ModSync.Core.Services.FileSystem.VirtualFileSystemProvider,ModSync.Core.Services.FileSystem.RealFileSystemProvider}}")]
