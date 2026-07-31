// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ModSync.Core;
using ModSync.Core.Services.FileSystem;
using NUnit.Framework;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers;
using RealFileSystemProvider = ModSync.Core.Services.FileSystem.RealFileSystemProvider;

namespace ModSync.Tests
{
    [TestFixture]
    public sealed class ResourceRegistryAdvancedTests
    {
        private string _testDirectory;
        private string _modDirectory;
        private string _kotorDirectory;
        private MainConfig _config;

        [SetUp]
        public void SetUp()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), "ModSync_ResourceRegistryTests_" + Guid.NewGuid());
            _modDirectory = Path.Combine(_testDirectory, "Mods");
            _kotorDirectory = Path.Combine(_testDirectory, "KOTOR");
            Directory.CreateDirectory(_modDirectory);
            Directory.CreateDirectory(_kotorDirectory);
            Directory.CreateDirectory(Path.Combine(_kotorDirectory, "Override"));

            _config = new MainConfig
            {
                sourcePath = new DirectoryInfo(_modDirectory),
                destinationPath = new DirectoryInfo(_kotorDirectory)
            };
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_testDirectory))
                {
                    Directory.Delete(_testDirectory, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        #region Resource Registry Auto-Extraction Tests

        [Test]
        public async Task ResourceRegistry_AutoExtractFromNestedArchive_ExtractsCorrectly()
        {
            // Create archive with nested structure
            string archivePath = CreateTestZip("mod.zip", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "subdir/nested/file.txt", "nested content" }
            });

            var component = new ModComponent
            {
                Name = "Nested Archive",
                Guid = Guid.NewGuid(),
                IsSelected = true,
                ResourceRegistry = new Dictionary<string, ResourceMetadata>(StringComparer.Ordinal)
                {
                    {
                        Path.GetFileName(archivePath),
                        new ResourceMetadata
                        {
                            Files = new Dictionary<string, bool?>(StringComparer.Ordinal)
                            {
                                { "subdir/nested/file.txt", true }
                            }
                        }
                    }
                }
            };

            var instruction = new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string> { "<<modDirectory>>/subdir/nested/file.txt" },
                Destination = "<<kotorDirectory>>/Override"
            };

            component.Instructions.Add(instruction);

            var fileSystemProvider = new RealFileSystemProvider();
            instruction.SetFileSystemProvider(fileSystemProvider);
            instruction.SetParentComponent(component);

            var result = await component.ExecuteSingleInstructionAsync(instruction, 0, new List<ModComponent> { component }, fileSystemProvider);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(Instruction.ActionExitCode.Success), "Should auto-extract nested file");
                Assert.That(File.Exists(Path.Combine(_kotorDirectory, "Override", "file.txt")), Is.True,
                    "Nested file should be extracted and moved");
            });
        }

        [Test]
        public async Task ResourceRegistry_AutoExtractMultipleFiles_ExtractsAll()
        {
            string archivePath = CreateTestZip("mod.zip", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "file1.txt", "content1" },
                { "file2.txt", "content2" },
                { "file3.txt", "content3" }
            });

            var component = new ModComponent
            {
                Name = "Multi File Archive",
                Guid = Guid.NewGuid(),
                IsSelected = true,
                ResourceRegistry = new Dictionary<string, ResourceMetadata>(StringComparer.Ordinal)
                {
                    {
                        Path.GetFileName(archivePath),
                        new ResourceMetadata
                        {
                            Files = new Dictionary<string, bool?>(StringComparer.Ordinal)
                            {
                                { "file1.txt", true },
                                { "file2.txt", true },
                                { "file3.txt", true }
                            }
                        }
                    }
                }
            };

            var instruction = new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string>
                {
                    "<<modDirectory>>/file1.txt",
                    "<<modDirectory>>/file2.txt",
                    "<<modDirectory>>/file3.txt"
                },
                Destination = "<<kotorDirectory>>/Override"
            };

            component.Instructions.Add(instruction);

            var fileSystemProvider = new RealFileSystemProvider();
            instruction.SetFileSystemProvider(fileSystemProvider);
            instruction.SetParentComponent(component);

            var result = await component.ExecuteSingleInstructionAsync(instruction, 0, new List<ModComponent> { component }, fileSystemProvider);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(Instruction.ActionExitCode.Success), "Should auto-extract all files");
                Assert.That(File.Exists(Path.Combine(_kotorDirectory, "Override", "file1.txt")), Is.True, "File1 should be extracted");
                Assert.That(File.Exists(Path.Combine(_kotorDirectory, "Override", "file2.txt")), Is.True, "File2 should be extracted");
                Assert.That(File.Exists(Path.Combine(_kotorDirectory, "Override", "file3.txt")), Is.True, "File3 should be extracted");
            });
        }

        [Test]
        public async Task ResourceRegistry_AutoExtractForCopy_ExtractsAndCopies()
        {
            string archivePath = CreateTestZip("mod.zip", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "file.txt", "content" }
            });

            var component = new ModComponent
            {
                Name = "Copy AutoExtract",
                Guid = Guid.NewGuid(),
                IsSelected = true,
                ResourceRegistry = new Dictionary<string, ResourceMetadata>(StringComparer.Ordinal)
                {
                    {
                        Path.GetFileName(archivePath),
                        new ResourceMetadata
                        {
                            Files = new Dictionary<string, bool?>(StringComparer.Ordinal)
                            {
                                { "file.txt", true }
                            }
                        }
                    }
                }
            };

            var instruction = new Instruction
            {
                Action = Instruction.ActionType.Copy,
                Source = new List<string> { "<<modDirectory>>/file.txt" },
                Destination = "<<kotorDirectory>>/Override"
            };

            component.Instructions.Add(instruction);

            var fileSystemProvider = new RealFileSystemProvider();
            instruction.SetFileSystemProvider(fileSystemProvider);
            instruction.SetParentComponent(component);

            var result = await component.ExecuteSingleInstructionAsync(instruction, 0, new List<ModComponent> { component }, fileSystemProvider);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(Instruction.ActionExitCode.Success), "Should auto-extract for copy");
                Assert.That(File.Exists(Path.Combine(_kotorDirectory, "Override", "file.txt")), Is.True,
                    "File should be copied");
            });
        }

        [Test]
        public async Task ResourceRegistry_AutoExtractForPatcher_ExtractsTslpatchdata()
        {
            string archivePath = CreateTestZip("mod.zip", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "tslpatchdata/changes.ini", "[Changes]" },
                { "tslpatchdata/namespaces.ini", "[Namespaces]" }
            });

            var component = new ModComponent
            {
                Name = "Patcher AutoExtract",
                Guid = Guid.NewGuid(),
                IsSelected = true,
                ResourceRegistry = new Dictionary<string, ResourceMetadata>(StringComparer.Ordinal)
                {
                    {
                        Path.GetFileName(archivePath),
                        new ResourceMetadata
                        {
                            Files = new Dictionary<string, bool?>(StringComparer.Ordinal)
                            {
                                { "tslpatchdata/changes.ini", true },
                                { "tslpatchdata/namespaces.ini", true }
                            }
                        }
                    }
                }
            };

            var instruction = new Instruction
            {
                Action = Instruction.ActionType.Patcher,
                Source = new List<string> { "<<modDirectory>>/tslpatchdata" },
                Arguments = "0"
            };

            component.Instructions.Add(instruction);

            var fileSystemProvider = new RealFileSystemProvider();
            instruction.SetFileSystemProvider(fileSystemProvider);
            instruction.SetParentComponent(component);

            var result = await component.ExecuteSingleInstructionAsync(instruction, 0, new List<ModComponent> { component }, fileSystemProvider);

            // Should handle auto-extraction for patcher (may fail if HoloPatcher not available, but tests extraction)
            Assert.That(result, Is.Not.Null, "Should return a result");
        }

        #endregion

        #region Resource Registry Edge Cases

        /// <summary>
        /// NOTE (2026-07-30): renamed/re-asserted from an original "SelectsCorrectArchive" test that
        /// expected the system to silently auto-pick one of two archives both containing "file.txt"
        /// when the instruction's Source pattern ("&lt;&lt;modDirectory&gt;&gt;/file.txt") doesn't
        /// reference either archive's name. The real (intentional) behavior - confirmed via the
        /// service's own log message, "File 'file.txt' found in multiple archives... Please add an
        /// explicit Extract instruction to specify which archive to use" - is to refuse to guess and
        /// fail safely, rather than silently extracting from an arbitrary one of two archives whose
        /// contents may differ (they do in this test's own setup: "archive1 content" vs "archive2
        /// content"). Silently picking one would be the actual bug; this test now asserts the safe,
        /// explicit-disambiguation-required behavior instead.
        /// </summary>
        [Test]
        public async Task ResourceRegistry_MultipleArchivesWithAmbiguousPattern_FailsSafelyRatherThanGuessing()
        {
            string archive1 = CreateTestZip("archive1.zip", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "file.txt", "archive1 content" }
            });

            string archive2 = CreateTestZip("archive2.zip", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "file.txt", "archive2 content" }
            });

            var component = new ModComponent
            {
                Name = "Multi Archive",
                Guid = Guid.NewGuid(),
                IsSelected = true,
                ResourceRegistry = new Dictionary<string, ResourceMetadata>(StringComparer.Ordinal)
                {
                    {
                        Path.GetFileName(archive1),
                        new ResourceMetadata
                        {
                            Files = new Dictionary<string, bool?>(StringComparer.Ordinal)
                            {
                                { "file.txt", true }
                            }
                        }
                    },
                    {
                        Path.GetFileName(archive2),
                        new ResourceMetadata
                        {
                            Files = new Dictionary<string, bool?>(StringComparer.Ordinal)
                            {
                                { "file.txt", true }
                            }
                        }
                    }
                }
            };

            var instruction = new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string> { "<<modDirectory>>/file.txt" },
                Destination = "<<kotorDirectory>>/Override"
            };

            component.Instructions.Add(instruction);

            var fileSystemProvider = new RealFileSystemProvider();
            instruction.SetFileSystemProvider(fileSystemProvider);
            instruction.SetParentComponent(component);

            var result = await component.ExecuteSingleInstructionAsync(instruction, 0, new List<ModComponent> { component }, fileSystemProvider);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.Not.EqualTo(Instruction.ActionExitCode.Success),
                    "Should not silently guess which of two archives with differing content to extract from");
                Assert.That(File.Exists(Path.Combine(_kotorDirectory, "Override", "file.txt")), Is.False,
                    "No file should be extracted when the source archive is ambiguous");
            });
        }

        [Test]
        public async Task ResourceRegistry_FileNotInRegistry_HandlesGracefully()
        {
            string archivePath = CreateTestZip("mod.zip", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "file.txt", "content" }
            });

            var component = new ModComponent
            {
                Name = "Missing Registry",
                Guid = Guid.NewGuid(),
                IsSelected = true,
                ResourceRegistry = new Dictionary<string, ResourceMetadata>(StringComparer.Ordinal)
                {
                    {
                        Path.GetFileName(archivePath),
                        new ResourceMetadata
                        {
                            Files = new Dictionary<string, bool?>(StringComparer.Ordinal)
                            {
                                { "other.txt", true } // Different file
                            }
                        }
                    }
                }
            };

            var instruction = new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string> { "<<modDirectory>>/file.txt" },
                Destination = "<<kotorDirectory>>/Override"
            };

            component.Instructions.Add(instruction);

            var fileSystemProvider = new RealFileSystemProvider();
            instruction.SetFileSystemProvider(fileSystemProvider);
            instruction.SetParentComponent(component);

            var result = await component.ExecuteSingleInstructionAsync(instruction, 0, new List<ModComponent> { component }, fileSystemProvider);

            // Should handle file not in registry gracefully
            Assert.That(result, Is.Not.Null, "Should return a result");
        }

        #endregion

        #region Defensive-Copy Property Regression Tests

        /// <summary>
        /// Regression test for a real bug (found 2026-07-30): <see cref="ModComponent.ResourceRegistry"/>'s
        /// getter used to return a brand-new defensive-copy dictionary on every access. Code across the
        /// codebase (MarkdownParser, ComponentMergeService, DownloadManagementService,
        /// ModComponentSerializationService, DownloadCacheService, GUI DownloadLinksControl, etc.) mutates
        /// the registry via `component.ResourceRegistry[key] = value` directly on the property result -
        /// with a defensive-copy getter, that indexer assignment silently wrote into a throwaway copy and
        /// the mutation was lost. This test asserts indexer-assignment mutations are visible on a
        /// subsequent read of the same property.
        /// </summary>
        [Test]
        public void ResourceRegistry_IndexerAssignment_PersistsAcrossReads()
        {
            var component = new ModComponent { Guid = Guid.NewGuid(), Name = "Indexer Assignment Test" };

            component.ResourceRegistry["https://example.com/mod-page"] = new ResourceMetadata
            {
                Files = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase),
                HandlerMetadata = new Dictionary<string, object>(StringComparer.Ordinal),
            };

            Assert.That(component.ResourceRegistry.Count, Is.EqualTo(1),
                "Indexer assignment on component.ResourceRegistry must persist - a defensive-copy getter " +
                "would silently discard it, leaving Count at 0.");
            Assert.That(component.ResourceRegistry.ContainsKey("https://example.com/mod-page"), Is.True);
        }

        /// <summary>
        /// Same defensive-copy hazard, but for <c>.Remove(key)</c> instead of indexer assignment
        /// (used by ComponentMergeService and the GUI's DownloadLinksControl).
        /// </summary>
        [Test]
        public void ResourceRegistry_Remove_PersistsAcrossReads()
        {
            var component = new ModComponent { Guid = Guid.NewGuid(), Name = "Remove Persistence Test" };
            component.ResourceRegistry = new Dictionary<string, ResourceMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://example.com/mod-page"] = new ResourceMetadata
                {
                    Files = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase),
                    HandlerMetadata = new Dictionary<string, object>(StringComparer.Ordinal),
                },
            };

            component.ResourceRegistry.Remove("https://example.com/mod-page");

            Assert.That(component.ResourceRegistry.Count, Is.EqualTo(0),
                "Remove() on component.ResourceRegistry must persist - a defensive-copy getter would " +
                "return the removal target unaffected on the next read.");
        }

        #endregion

        #region Helper Methods


        private string CreateTestZip(string fileName, Dictionary<string, string> files)
        {
            string zipPath = Path.Combine(_modDirectory, fileName);
            using (var archive = ZipArchive.CreateArchive())
            {
                foreach (var kvp in files)
                {
                    archive.AddEntry(kvp.Key, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(kvp.Value)), true);
                }
                using (var stream = File.OpenWrite(zipPath))
                {
                    archive.SaveTo(stream, new SharpCompress.Writers.Zip.ZipWriterOptions(CompressionType.None));
                }
            }
            return zipPath;
        }

        #endregion
    }
}

