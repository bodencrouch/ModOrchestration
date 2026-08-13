// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System.Collections.Generic;
using System.IO;
using System.Linq;

using ModSync.Core.Services;

using NUnit.Framework;

namespace ModSync.Tests
{
    /// <summary>
    /// The archive resolver replaced a scored best-guess (exact 100 / contains 50 /
    /// reverse-contains 25, newest wins). Scoring cannot express "I do not know", and picking the
    /// best-scoring candidate installs the wrong mod silently — measured in the reference library,
    /// where reverse containment mapped "Gammorean Reskin Pack" to `Quanons_HK47_Reskin.rar`, and
    /// where "Thematic KOTOR Companions" scores identically against the KOTOR 1 and KOTOR 2 archives.
    /// <para>
    /// These tests pin the two properties that matter: the chain NARROWS rather than ranks, and
    /// anything still ambiguous is reported rather than picked.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class ArchiveResolverTests
    {
        private static IReadOnlyList<FileInfo> Library(params string[] names)
        {
            // The resolver reads only file names, so these need not exist on disk.
            return names.Select(n => new FileInfo(Path.Combine(Path.GetTempPath(), n))).ToList();
        }

        private static ArchiveResolution Resolve(
            string componentName,
            IReadOnlyList<string> urls,
            IReadOnlyList<FileInfo> library,
            ArchiveResolver.GameMarker game = ArchiveResolver.GameMarker.Kotor1)
        {
            return ArchiveResolver.Resolve(componentName, urls, library, game);
        }

        /// <summary>
        /// The measured catastrophic case. Both archives match the component name equally well; only
        /// the game marker separates them, and installing the KOTOR 2 archive into a KOTOR 1 build
        /// would corrupt it while every step reported success.
        /// </summary>
        [Test]
        public void ThematicCompanions_PicksTheKotor1Archive_NotTheKotor2One()
        {
            IReadOnlyList<FileInfo> library = Library(
                "KOTOR1-Thematic-Companions_v1.0.1.zip",
                "KOTOR2-Thematic-Companions_v1.0.3.zip");

            ArchiveResolution result = Resolve("Thematic KOTOR Companions", new List<string> { "https://deadlystream.com/files/file/thematic-companions" }, library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("KOTOR1-Thematic-Companions_v1.0.1.zip"));
        }

        /// <summary>
        /// The inverse, and the more dangerous direction: when ONLY the other game's archive is
        /// present, the resolver must report nothing rather than settle for the single candidate it
        /// can see.
        /// </summary>
        [Test]
        public void OnlyTheWrongGamesArchivePresent_ResolvesToNothing()
        {
            IReadOnlyList<FileInfo> library = Library("KOTOR2-Thematic-Companions_v1.0.3.zip");

            ArchiveResolution result = Resolve("Thematic KOTOR Companions", new List<string> { "https://deadlystream.com/files/file/thematic-companions" }, library);

            Assert.That(
                result.IsResolved,
                Is.False,
                "A KOTOR 2 archive must never satisfy a KOTOR 1 component, even when it is the only candidate.");
        }

        [Test]
        public void RepairAffectsStunDroid_DiscardsTheTslVariant()
        {
            IReadOnlyList<FileInfo> library = Library(
                "[K1] Repair Affects Stun Droid.zip",
                "[TSL] Repair Affects Stun Droid.zip",
                "Repair Affects Stun Droid TSL.zip");

            ArchiveResolution result = Resolve("Repair Affects Stun Droid", new List<string> { "https://deadlystream.com/files/file/repair-affects-stun-droid" }, library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("[K1] Repair Affects Stun Droid.zip"));
        }

        /// <summary>
        /// The old scorer's reverse-containment rule ("the archive name is a substring of the search
        /// term") produced this exact mis-mapping. Nothing here legitimately matches, so the answer
        /// must be "unresolved".
        /// </summary>
        [Test]
        public void GammoreanReskinPack_DoesNotMapToAnUnrelatedReskin()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Quanons_HK47_Reskin.rar",
                "Taris_Reskin-10-1-0.zip",
                "Taris Reskin Patch.zip");

            ArchiveResolution result = Resolve("Gammorean Reskin Pack", new List<string> { "https://deadlystream.com/files/file/gammorean-reskin-pack" }, library);

            Assert.That(result.IsResolved, Is.False, $"Resolved to '{result.Archive?.Name}' when nothing matches.");
        }

        /// <summary>
        /// A Nexus page hosts a main download plus compatibility patches, all carrying the same mod
        /// id, so the id alone is ambiguous (id 1282 matches six archives in the library). The
        /// component name narrows them to exactly one.
        /// </summary>
        [Test]
        public void NexusModId_AmbiguousAlone_IsNarrowedByComponentName()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Ultimate Character Overhaul -REDUX- ( LITE ) - TPC Version-1282-4-1-1628550322.rar",
                "JC's Mandalorian Armor - Compatibility Patch-1282-4-1-1629713289.rar",
                "JC's Minor Fixes - Compatibility Patch-1282-4-1-1629713341.rar",
                "KOTOR 1 Community Patch - Compatibility Patch-1282-4-1-1629713397.rar",
                "Miscellaneous Compatibility Patches-1282-4-1-1629713437.rar");

            ArchiveResolution result = Resolve(
                "Ultimate Character Overhaul",
                new List<string> { "https://www.nexusmods.com/kotor/mods/1282" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(
                result.Archive.Name,
                Is.EqualTo("Ultimate Character Overhaul -REDUX- ( LITE ) - TPC Version-1282-4-1-1628550322.rar"));
            Assert.That(result.Tier, Is.EqualTo(ArchiveResolutionTier.NexusModId));
        }

        /// <summary>
        /// When the id is ambiguous and the name does not narrow it, the candidates are reported so a
        /// human can decide. Silently taking the first would install an arbitrary patch as the mod.
        /// </summary>
        [Test]
        public void NexusModId_StillAmbiguous_ReportsCandidatesInsteadOfPicking()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Something Else - Compatibility Patch-1282-4-1-1629713289.rar",
                "Another Thing - Compatibility Patch-1282-4-1-1629713341.rar");

            ArchiveResolution result = Resolve(
                "Ultimate Character Overhaul",
                new List<string> { "https://www.nexusmods.com/kotor/mods/1282" },
                library);

            Assert.That(result.IsResolved, Is.False);
            Assert.That(result.Candidates, Has.Count.EqualTo(2));
            Assert.That(result.Reason, Does.Contain("Ambiguous"));
        }

        /// <summary>
        /// The bare mod id must be matched as its own delimited field. Substring-matching "90" would
        /// also hit the 10-digit unix timestamp Nexus appends to every download name.
        /// </summary>
        [Test]
        public void NexusModId_DoesNotMatchDigitsInsideTheTimestamp()
        {
            IReadOnlyList<FileInfo> library = Library("Unrelated Mod-4321-1-0-1590000000.rar");

            ArchiveResolution result = Resolve(
                "Random Turret Minigame Remover",
                new List<string> { "https://www.nexusmods.com/kotor/mods/90" },
                library);

            Assert.That(result.IsResolved, Is.False);
        }

        /// <summary>
        /// The common case behind most unresolved components: the guide's name matches the archive,
        /// but the URL slug does not.
        /// </summary>
        [Test]
        public void ComponentName_ResolvesWhenTheUrlSlugDoesNot()
        {
            IReadOnlyList<FileInfo> library = Library(
                "JC's Fashion Line I - Cloaked Jedi Robes for K1 v1.4.7z",
                "Some Other Mod.zip");

            ArchiveResolution result = Resolve(
                "Cloaked Jedi Robes",
                new List<string> { "https://deadlystream.com/files/file/1234-jcs-fashion-line-i" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("JC's Fashion Line I - Cloaked Jedi Robes for K1 v1.4.7z"));
            Assert.That(result.Tier, Is.EqualTo(ArchiveResolutionTier.UniqueContainment));
        }

        /// <summary>
        /// An archive and its extracted folder are one logical mod and must not read as two competing
        /// candidates, which would make every already-extracted mod look ambiguous.
        /// </summary>
        [Test]
        public void ArchiveAndItsExtractedFolder_CountAsOneCandidate()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Vision Enhancement K1.zip",
                "Vision Enhancement K1");

            ArchiveResolution result = Resolve(
                "Vision Enhancement K1",
                new List<string> { "https://deadlystream.com/files/file/vision-enhancement" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
        }

        /// <summary>
        /// "K2 Swoops to K1" names both games; a naive marker check would read the leading "K2" and
        /// discard a component that legitimately belongs to a KOTOR 1 build.
        /// </summary>
        [Test]
        public void NameMentioningBothGames_IsNotDiscarded()
        {
            Assert.That(
                ArchiveResolver.MarkerOf("K2 Swoops to K1"),
                Is.EqualTo(ArchiveResolver.GameMarker.None));

            Assert.That(
                ArchiveResolver.IsWrongGame("K2 Swoops to K1.zip", ArchiveResolver.GameMarker.Kotor1),
                Is.False);
        }

        /// <summary>
        /// Markers are read as delimited tokens, so digits inside a version string do not flip the
        /// verdict.
        /// </summary>
        [Test]
        public void VersionDigits_DoNotProduceAGameMarker()
        {
            Assert.That(
                ArchiveResolver.MarkerOf("Some Mod v1.2.1-2-0-1669476173.rar"),
                Is.EqualTo(ArchiveResolver.GameMarker.None));
        }
    }
}
