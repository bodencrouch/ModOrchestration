// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System.Collections.ObjectModel;
using System.Linq;

using ModSync.Core;
using ModSync.Core.Parsing;

using NUnit.Framework;

namespace ModSync.Tests
{
    /// <summary>
    /// Regression coverage for Extract source parsing.
    ///
    /// The Extract patterns previously captured their source with a lazy <c>[\w\s\-_/\\]+?</c> followed
    /// by a fully optional "to &lt;destination&gt;" group. Nothing forced the quantifier to expand, so the
    /// engine satisfied the match with a SINGLE character: "extract redrob's mod from its archive"
    /// produced source "r", which became the required archive "&lt;&lt;modDirectory&gt;&gt;\r" and failed the
    /// K1_auto install at pre-install validation. Every input was affected, not just possessive phrasing
    /// ("Unzip KOTOR1CP.zip ..." yielded "K").
    /// </summary>
    [TestFixture]
    public sealed class ExtractSourceParsingTests
    {
        [TestCase("To do so, first extract redrob's mod from its archive, then continue.")]
        [TestCase("Extract the mod, navigate to the 'TSLPatchdata' folder.")]
        [TestCase("Unzip the mod, enter the NPC Replacement folder.")]
        [TestCase("Unzip KOTOR1CP.zip to the Override.")]
        [TestCase("You must extract all archives before installing mod content.")]
        public void ExtractParsing_NeverProducesSingleCharacterSource(string prose)
        {
            ObservableCollection<Instruction> instructions = Parse(prose);

            foreach (Instruction instruction in instructions.Where(i => i.Action == Instruction.ActionType.Extract))
            {
                foreach (string source in instruction.Source ?? Enumerable.Empty<string>())
                {
                    string leaf = source.Replace("<<modDirectory>>", string.Empty).Trim('\\', '/', '*');
                    Assert.That(
                        leaf.Length,
                        Is.Not.EqualTo(1),
                        $"Extract source truncated to a single character ('{source}') from prose: {prose}");
                }
            }
        }

        [Test]
        public void PossessiveModPhrase_DoesNotFabricateAnArchiveNamedR()
        {
            ObservableCollection<Instruction> instructions =
                Parse("To do so, first extract redrob's mod from its archive, then run cleaner.bat.");

            Assert.That(
                instructions.SelectMany(i => i.Source ?? Enumerable.Empty<string>()),
                Has.None.EqualTo(@"<<modDirectory>>\r"),
                "Possessive phrasing must resolve to the component's own archive, not a file named 'r'.");
        }

        [Test]
        public void ExplicitArchiveName_IsCapturedWholeIncludingExtension()
        {
            ObservableCollection<Instruction> instructions = Parse("Unzip KOTOR1CP.zip to the Override.");

            Assert.That(
                instructions.SelectMany(i => i.Source ?? Enumerable.Empty<string>()),
                Has.Some.Contains("KOTOR1CP.zip"),
                "An explicitly named archive must be captured in full, extension included.");
        }

        /// <summary>
        /// Destination inference scans the whole processing unit, so a two-clause sentence used to attach
        /// the move clause's "to your override" to the Delete instruction. ComponentValidation rejects a
        /// Destination on Choose/Extract/Delete, which failed the whole K1_auto install at validation.
        /// </summary>
        [TestCase("Delete po_pzaalbar3.tga before moving the files to your override.")]
        [TestCase("Remove the readme before you move everything to Override.")]
        [TestCase("Delete N_CommM02.tpc, then move the rest into your override folder.")]
        public void DeleteInstruction_NeverCarriesADestination(string prose)
        {
            ObservableCollection<Instruction> instructions = Parse(prose);

            foreach (Instruction instruction in instructions.Where(
                i => i.Action == Instruction.ActionType.Delete
                    || i.Action == Instruction.ActionType.Extract
                    || i.Action == Instruction.ActionType.Choose))
            {
                Assert.That(
                    string.IsNullOrEmpty(instruction.Destination),
                    Is.True,
                    $"{instruction.Action} must not carry a Destination (got '{instruction.Destination}') from prose: {prose}");
            }
        }

        [Test]
        public void DetranRenameProse_EmitsRenameWithDestinationFilename()
        {
            ObservableCollection<Instruction> instructions = Parse(
                "Make a copy of the file and rename it PMBJ01.tga, then move all files to override.");

            Instruction rename = instructions.FirstOrDefault(i =>
                i.Action == Instruction.ActionType.Rename || i.Action == Instruction.ActionType.Copy);
            Assert.That(rename, Is.Not.Null, "Expected Rename/Copy from Detran prose. Got: "
                + string.Join("; ", instructions.Select(i => $"{i.Action}:{string.Join(',', i.Source ?? new System.Collections.Generic.List<string>())}->{i.Destination}")));
            Assert.That(rename.Destination, Does.Contain("PMBJ01").IgnoreCase);
        }

        [Test]
        public void EbonHawkCopyAsProse_EmitsRenameOrCopyWithSourceAndDest()
        {
            ObservableCollection<Instruction> instructions = Parse(
                "Once the mod is extracted, copy the file 'LDA_EHawk01' and make a duplicate of it. Rename this duplicate to 'M36_EHawk01.tga' and then move all files to the override.");

            Instruction rename = instructions.FirstOrDefault(i =>
                (i.Action == Instruction.ActionType.Rename || i.Action == Instruction.ActionType.Copy)
                && (i.Destination ?? string.Empty).IndexOf("M36_EHawk01", System.StringComparison.OrdinalIgnoreCase) >= 0);

            Assert.That(rename, Is.Not.Null, "Expected copy-as to M36_EHawk01. Got: "
                + string.Join("; ", instructions.Select(i => $"{i.Action}:{string.Join(',', i.Source ?? new System.Collections.Generic.List<string>())}->{i.Destination}")));
            Assert.That(
                string.Join(",", rename.Source ?? new System.Collections.Generic.List<string>()),
                Does.Contain("LDA_EHawk01").IgnoreCase,
                "Copy-as must retain the quoted source stem, not a bare modDirectory wildcard.");
        }

        /// <summary>
        /// Republic Soldier Fix's guide note (k1 full.md) narrows a folder-wide move to only the
        /// files whose names begin with two named prefixes: "move all files beginning "PFBBL" and
        /// "PMBBL" from the mod's Player Clothing folder to your override." Before the prefix-filter
        /// clause parsing gap was closed, the whole "beginning ... and ..." clause (plus the folder
        /// name) fell through to the generic folder fallback and got bound as a single garbled folder
        /// name with a blanket "Player Clothing\*" wildcard - pulling in all 18 size-variant files
        /// (PFBBL/PFBBM/PFBBS/PMBBL/PMBBM/PMBBS) instead of only the 6 matching the 2 named prefixes,
        /// per the manual install ledger's recorded file list for this step. Uses the full two-sentence
        /// note body (not just the isolated second sentence) because an earlier "only install ..."
        /// clause in the first sentence rewrites Source from the whole remaining unit text, and the
        /// fix must still find the prefix clause and its folder inside that rewritten text.
        /// </summary>
        [Test]
        public void RepublicSoldierFixProse_PrefixFilterNarrowsToNamedPrefixesNotBlanketWildcard()
        {
            const string fullNote =
                "Move the files from the mod's Override folder to your game's override folder. " +
                "For compatibility, you will need to only install the player clothing texture " +
                "replacement for Soldier-class characters: move all files beginning \"PFBBL\" and " +
                "\"PMBBL\" from the mod's Player Clothing folder to your override.";

            ObservableCollection<Instruction> instructions = Parse(fullNote);

            System.Collections.Generic.List<string> playerClothingSources = instructions
                .Where(i => i.Action == Instruction.ActionType.Move)
                .SelectMany(i => i.Source ?? new System.Collections.Generic.List<string>())
                .Where(s => s.IndexOf("Player Clothing", System.StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            string dump = string.Join("; ", instructions.Select(i =>
                $"{i.Action}:[{string.Join(",", i.Source ?? new System.Collections.Generic.List<string>())}]->{i.Destination}"));

            // Exactly the 2 named prefixes, scoped under Player Clothing - not a superset that would
            // still pass with the blanket wildcard's extra size variants mixed in alongside them.
            Assert.That(
                playerClothingSources,
                Is.EquivalentTo(new[]
                {
                    @"<<modDirectory>>\Player Clothing\PFBBL*",
                    @"<<modDirectory>>\Player Clothing\PMBBL*",
                }),
                $"Expected exactly the PFBBL/PMBBL-prefixed sources under Player Clothing, nothing else. Got: {dump}");
        }

        private static ObservableCollection<Instruction> Parse(string prose)
        {
            var parser = new NaturalLanguageInstructionParser();
            var component = new ModComponent { Name = "Test Component" };
            return parser.ParseInstructions(prose, null, component);
        }
    }
}
