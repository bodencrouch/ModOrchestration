// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.IO;

using ModSync.Core.TSLPatcher;

using NUnit.Framework;

namespace ModSync.Tests
{
    [TestFixture]
    public class ConfirmMessageTestReplace
    {
        private string _testDirectoryPath;

        [SetUp]
        public void SetUp()
        {
            _testDirectoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            _ = Directory.CreateDirectory(_testDirectoryPath);
        }

        [TearDown]
        public void TearDown()
        {
            if (_testDirectoryPath != null)
            {
                Directory.Delete(_testDirectoryPath, recursive: true);
            }
        }

        [Test]
        public void DisableConfirmations_NullDirectory_ThrowsArgumentNullException() => _ = Assert.Throws<ArgumentNullException>(() => IniHelper.ReplaceIniPattern(directory: null, pattern: @"^\s*ConfirmMessage\s*=\s*.*$", replacement: "ConfirmMessage=N/A"));

        [Test]
        public void DisableConfirmations_NoIniFiles_ThrowsInvalidOperationException()
        {
            Assert.That(_testDirectoryPath, Is.Not.Null);
            var directory = new DirectoryInfo(_testDirectoryPath);

            _ = Assert.Throws<InvalidOperationException>(() => IniHelper.ReplaceIniPattern(directory, pattern: @"^\s*ConfirmMessage\s*=\s*.*$", replacement: "ConfirmMessage=N/A"));
        }

        [Test]
        public void DisableConfirmations_ConfirmMessageExists_ReplacesWithN_A()
        {
            Assert.That(_testDirectoryPath, Is.Not.Null);
            const string iniFileName = "sample.ini";
            const string content = "[Settings]\nConfirmMessage=suffer the consequences by proceeding. Continue anyway?";

            File.WriteAllText(Path.Combine(_testDirectoryPath, iniFileName), content);

            var directory = new DirectoryInfo(_testDirectoryPath);

            IniHelper.ReplaceIniPattern(directory, pattern: @"^\s*ConfirmMessage\s*=\s*.*$", replacement: "ConfirmMessage=N/A");

            string modifiedContent = File.ReadAllText(Path.Combine(_testDirectoryPath, iniFileName));
            Assert.That(modifiedContent, Does.Contain("ConfirmMessage=N/A"));
        }

        [Test]
        public void DropDangling2daRowReferences_CommentsMissingChangeRowSections()
        {
            Assert.That(_testDirectoryPath, Is.Not.Null);
            const string ini = @"[2DAList]
Table0=baseitems.2da

[baseitems.2da]
ChangeRow0=blaster_pistol
ChangeRow12=repeating_blaster

[blaster_pistol]
RowIndex=12
numdice=1
";
            File.WriteAllText(Path.Combine(_testDirectoryPath, "pistol_rifle.ini"), ini);

            int dropped = IniHelper.DropDangling2daRowReferences(new DirectoryInfo(_testDirectoryPath));
            string after = File.ReadAllText(Path.Combine(_testDirectoryPath, "pistol_rifle.ini"));

            Assert.Multiple(() =>
            {
                Assert.That(dropped, Is.EqualTo(1));
                Assert.That(after, Does.Contain("ChangeRow0=blaster_pistol"));
                Assert.That(after, Does.Contain(";ChangeRow12=repeating_blaster"));
            });
        }
    }

}
