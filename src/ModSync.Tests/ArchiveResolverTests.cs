// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ModSync.Core.Services;
using ModSync.Core.Utility;

using NUnit.Framework;

using SharpCompress.Archives.Zip;
using SharpCompress.Common;

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

        [Test]
        public void ExplicitDamagedVariantInGuide_NarrowsLooseFileCandidates()
        {
            IReadOnlyList<FileInfo> library = Library(
                "HD Computer Panel.7z",
                "Damaged Version For Malachor.7z",
                "[TSL]_Animated_Computer_Panel_v2.0.0.7z");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Neglected Computer Panel",
                new[] { "https://deadlystream.com/files/file/2063-neglected-computer-panel/" },
                library,
                ArchiveResolver.GameMarker.Kotor2,
                new[] { "Download only the Damaged version, it contains files for both." });

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("Damaged Version For Malachor.7z"));
        }

        /// <summary>
        /// Measured on the 2026-09-07 K1 full run. The guide's download note for "HD Canderous
        /// Ordo" shares no tokens with the small, correct 'Canderous Ordo.rar' archive, but two of
        /// its generic words ("version", "textures") happen to also appear in the name of a much
        /// larger, completely unrelated archive elsewhere in the reference library. Scoring that
        /// coincidental 2-of-9-token overlap as a "unique" guide-directive hit silently bound the
        /// component to the wrong, unrelated archive and moved its entire (960-file) payload into
        /// the game's Override folder.
        /// </summary>
        [Test]
        public void DownloadOnlyDirective_DoesNotBindToAnUnrelatedLargeArchiveOnCoincidentalWordOverlap()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Canderous Ordo.rar",
                "Ultimate High Resolution Texture Pack - TPC Version-1100-1-1-1670426755.rar");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "HD Canderous Ordo",
                new[] { "https://deadlystream.com/files/file/1123-hd-canderous-ordo/" },
                library,
                ArchiveResolver.GameMarker.Kotor1,
                new[]
                {
                    "Download only the version marked 'new clothes,' which includes both clothing "
                    + "and body textures. We get our head texture from the below mod. Remember to "
                    + "also download the patch.",
                });

            Assert.That(
                result.Archive?.Name,
                Is.Not.EqualTo("Ultimate High Resolution Texture Pack - TPC Version-1100-1-1-1670426755.rar"),
                result.Reason);
            Assert.That(result.Tier, Is.Not.EqualTo(ArchiveResolutionTier.GuideDirective), result.Reason);
        }

        [Test]
        public void BetterTwilekHeads_ResolvesSpentFolderTwinToK1Archive()
        {
            IReadOnlyList<FileInfo> library = Library(
                "K1 Twi'lek Heads v1.3.3.7z",
                "TSL Twi'lek Heads v1.3.2.7z",
                "hd_twilek_female.rar");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Better Twi'lek Heads",
                new[] { "https://deadlystream.com/files/file/1430-k1-better-twilek-male-heads/" },
                library,
                ArchiveResolver.GameMarker.Kotor1);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("K1 Twi'lek Heads v1.3.3.7z"));
        }

        /// <summary>
        /// Measured on the 2026-08-24 K1 Holo run: the library had a leftover folder whose
        /// name equals the guide heading, next to the real <c>K1 Twi'lek Heads v1.3.3.7z</c>.
        /// ExactName latched onto the folder and Holo applied that folder's stale 11-patch
        /// Slim ini (textures + <c>twilek_m04.tpc</c>, no <c>n_komadh</c> / <c>n_xorh</c> /
        /// <c>twilek_m05</c> models). The 7z's Option A is 22 patches and includes the models.
        /// </summary>
        [Test]
        public void BetterTwilekHeads_HeadingNamedFolder_DoesNotBeatTheK1Archive()
        {
            IReadOnlyList<FileInfo> library = Library(
                "K1 Twi'lek Heads v1.3.3.7z",
                "TSL Twi'lek Heads v1.3.2.7z",
                "Better Twi'lek Heads",
                "hd_twilek_female.rar");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Better Twi'lek Heads",
                new[] { "https://deadlystream.com/files/file/1430-k1-better-twilek-male-heads/" },
                library,
                ArchiveResolver.GameMarker.Kotor1);

            Assert.Multiple(() =>
            {
                Assert.That(result.IsResolved, Is.True, result.Reason);
                Assert.That(result.Archive.Name, Is.EqualTo("K1 Twi'lek Heads v1.3.3.7z"), result.Reason);
            });
        }

        [Test]
        public void BetterTwilekHeads_FolderOnly_StillResolvesWhenNoArchive()
        {
            IReadOnlyList<FileInfo> library = Library("Better Twi'lek Heads");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Better Twi'lek Heads",
                new[] { "https://deadlystream.com/files/file/1430-k1-better-twilek-male-heads/" },
                library,
                ArchiveResolver.GameMarker.Kotor1);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("Better Twi'lek Heads"));
        }

        [Test]
        public void DarthMalaksArmor_NexusId9_PicksArmourSpelling()
        {
            IReadOnlyList<FileInfo> library = Library(
                "TSL_Darth_Malaks_Armour_PMBM05_Reskin-9-1-0.7z",
                "N_DarthMalak01.tga");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Darth Malak's Armor",
                new[] { "http://www.nexusmods.com/kotor2/mods/9/?" },
                library,
                ArchiveResolver.GameMarker.Kotor2);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("TSL_Darth_Malaks_Armour_PMBM05_Reskin-9-1-0.7z"));
        }

        [Test]
        public void RelightingTsl_ApplyAllFiles_KeepsEveryArchive()
        {
            IReadOnlyList<FileInfo> library = Library(
                "relightingtsl_102PERfklnt_1.0.zip",
                "relightingtsl_003EBOg_1.2.zip",
                "relightingtsl_298TELk_1.0.zip",
                "relightingtsl_101PERt_2.1.zip");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Relighting TSL",
                new[] { "https://deadlystream.com/files/file/2752-relighting-tsl-early-release/" },
                library,
                ArchiveResolver.GameMarker.Kotor2,
                new[]
                {
                    "Download and apply all files, unless NOT using TSLRCM "
                    + "(in which case, skip relightingtsl_298TELk_1.0.zip).",
                });

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.AdditionalArchives.Count, Is.EqualTo(3), result.Reason);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                result.Archive.Name,
            };
            foreach (FileInfo extra in result.AdditionalArchives)
            {
                _ = names.Add(extra.Name);
            }

            Assert.That(names.Count, Is.EqualTo(4));
        }

        [Test]
        public void ExplicitReskinFriendlyRecommendation_NarrowsPatcherVariants()
        {
            IReadOnlyList<FileInfo> library = Library(
                "TSL Transparent Cockpit Windows v1_1_2 - Reskin Friendly.7z",
                "TSL Transparent Cockpit Windows v1_1_2 - Enhanced Reflections.7z");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Transparent Cockpit Windows TSL",
                new[] { "https://deadlystream.com/files/file/2355-transparent-cockpit-windows-for-tsl/" },
                library,
                ArchiveResolver.GameMarker.Kotor2,
                new[] { "I recommend the reskin-friendly version; the reflectivity version is untested." });

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Does.Contain("Reskin Friendly"));
        }

        [Test]
        public void MainFileNotCompatches_PrefersUnsuffixedMainVariant()
        {
            IReadOnlyList<FileInfo> library = Library(
                "HQSkyboxesII_TSL_M478EP.7z",
                "HQSkyboxesII_TSL_1k.7z",
                "HQSkyboxesII_TSL_M478EP_1k.7z",
                "HQSkyboxesII_TSL_JediTemple.7z",
                "HQSkyboxesII_TSL.7z");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "HQ Skyboxes II",
                new[] { "https://deadlystream.com/files/file/1793-high-quality-skyboxes-ii/" },
                library,
                ArchiveResolver.GameMarker.Kotor2,
                new[]
                {
                    "Download just the main file (HQSkyboxesII_TSL.7z or HQSkyboxesII_TSL_1k.7z), "
                    + "not any of the compatches. Whether you use the 1k version is your choice.",
                });

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("HQSkyboxesII_TSL.7z"));
        }

        [Test]
        public void ExplicitResolutionRecommendation_SelectsThatVariant()
        {
            IReadOnlyList<FileInfo> library = Library(
                "KOTOR2-Citadel-Station-Backdrop_v2.0.0_2k.zip",
                "KOTOR2-Citadel-Station-Backdrop_v2.0.0_4k.zip");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "JC's Citadel Station Backdrop",
                new[] { "https://deadlystream.com/files/file/1217-jcs-citadel-station-backdrop/" },
                library,
                ArchiveResolver.GameMarker.Kotor2,
                new[] { "Download the 2K version for compatibility and lower memory use." });

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Does.EndWith("_2k.zip"));
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
        /// Contiguous containment is word-order sensitive, so a guide heading whose words are
        /// rearranged in the filename never matched. "Trandoshans Rescaled" does not occur inside
        /// "Rescaled Trandoshans", yet they are plainly the same mod.
        /// </summary>
        [Test]
        public void ReorderedWords_ResolveViaTokenSubset()
        {
            IReadOnlyList<FileInfo> library = Library("Rescaled Trandoshans.zip", "Unrelated Mod.zip");

            // A slug that carries no usable words, so only the component name drives the match and the
            // word-order-insensitive tier is genuinely the one under test.
            ArchiveResolution result = Resolve(
                "Trandoshans Rescaled",
                new List<string> { "https://deadlystream.com/files/file/1234-x" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("Rescaled Trandoshans.zip"));
            Assert.That(result.Tier, Is.EqualTo(ArchiveResolutionTier.UniqueTokenSubset));
        }

        /// <summary>
        /// The looser word-order-insensitive tier must not become a back door for the wrong game:
        /// with only the KOTOR 2 archive present it must still resolve to nothing.
        /// </summary>
        [Test]
        public void TokenSubset_StillRefusesTheWrongGame()
        {
            IReadOnlyList<FileInfo> library = Library("KOTOR2-Thematic-Companions_v1.0.3.zip");

            ArchiveResolution result = Resolve(
                "Thematic KOTOR Companions",
                new List<string> { "https://deadlystream.com/files/file/9999-thematic" },
                library);

            Assert.That(
                result.IsResolved,
                Is.False,
                "Word-order-insensitive matching must not bypass the wrong-game discard.");
        }

        /// <summary>
        /// When the words match several distinct mods, the looser tier must report rather than pick.
        /// </summary>
        [Test]
        public void TokenSubset_AmbiguousAcrossDistinctMods_IsReported()
        {
            // Both contain every word of the name and neither contains it contiguously, so the
            // word-order-insensitive tier matches both and no further signal separates them.
            IReadOnlyList<FileInfo> library = Library(
                "Rescaled Trandoshans Redux.zip",
                "Rescaled Trandoshans Alternate.zip");

            ArchiveResolution result = Resolve(
                "Trandoshans Rescaled",
                new List<string> { "https://deadlystream.com/files/file/9999-x" },
                library);

            Assert.That(result.IsResolved, Is.False);
            Assert.That(result.Candidates, Has.Count.EqualTo(2));
        }

        /// <summary>
        /// The real `Cloaked Jedi Robes` case, which blocked run 8's validation via two dependents.
        /// The component name alone is contained in three different mods' archives, so containment
        /// correctly refuses. The guide URL disambiguates — but only once DeadlyStream's file-id
        /// prefix is stripped, otherwise the slug normalizes to "1378jcsfashionline..." and matches
        /// nothing at all.
        /// </summary>
        [Test]
        public void DeadlyStreamSlug_DisambiguatesWhereTheNameCannot()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Effixian's Qel-Droma Robes Reskin for JC's Cloaked Jedi Robes.zip",
                "HD Robe Icons for JC's Cloaked Jedis and Effix's Extra Robes.zip",
                "JC's Fashion Line I - Cloaked Jedi Robes for K1 v1.4.7z");

            ArchiveResolution result = Resolve(
                "Cloaked Jedi Robes",
                new List<string>
                {
                    "https://deadlystream.com/files/file/1378-jcs-fashion-line-i-cloaked-jedi-robes-for-k1/",
                },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(
                result.Archive.Name,
                Is.EqualTo("JC's Fashion Line I - Cloaked Jedi Robes for K1 v1.4.7z"),
                "The slug identifies the mod exactly; the other two archives are different mods that "
                + "merely mention it.");
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

        [Test]
        public void JcsMinorFixes_PrefersMainArchiveOverCompatibilityPatches()
        {
            IReadOnlyList<FileInfo> library = Library(
                "JC's Minor Fixes for K1 v1.1.zip",
                "JC's Minor Fixes - Compatibility Patch-1060-3-0-1629717168.rar",
                "JC's Minor Fixes - Compatibility Patch-1282-4-1-1629713341.rar");

            ArchiveResolution result = Resolve(
                "JC's Minor Fixes",
                new List<string> { "https://deadlystream.com/files/file/1333-jcs-minor-fixes-for-k1/" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("JC's Minor Fixes for K1 v1.1.zip"));
        }

        [Test]
        public void KorribanSithArt_NexusIdNarrowedByArchiveTitleInsideComponentName()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Door Mural-1632-1-0-1713374023.zip",
                "Sith Art-1632-1-1713373365.zip");

            ArchiveResolution result = Resolve(
                "Korriban Sith Art",
                new List<string> { "https://www.nexusmods.com/kotor/mods/1632" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("Sith Art-1632-1-1713373365.zip"));
            Assert.That(result.Tier, Is.EqualTo(ArchiveResolutionTier.NexusModId));
        }

        [Test]
        public void UniqueSithGovernor_ResolvesToUsg7z_ViaAcronym()
        {
            IReadOnlyList<FileInfo> library = Library(
                "USG.7z",
                "Ajunta Pall Unique Appearance.zip",
                "Heyorange's Sith Uniform Reformation 1.0.zip");

            ArchiveResolution result = Resolve(
                "Unique Sith Governor",
                new List<string> { "https://deadlystream.com/files/file/2302-unique-sith-governor/" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("USG.7z"));
            Assert.That(result.Tier, Is.EqualTo(ArchiveResolutionTier.AcronymExact));
        }

        [Test]
        public void HdDarthMalak_PicksMalakRar_NotVurtOrSaber()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Malak.rar",
                "Darth_Malaks_Lightsaber_K1.zip",
                "N_DarthMalak01  (Vurt's KotOR Visual Resurgence) 2026.rar.rar");

            ArchiveResolution result = Resolve(
                "HD Darth Malak",
                new List<string> { "https://deadlystream.com/files/file/980-hd-darth-malak/" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("Malak.rar"));
        }

        [Test]
        public void HqCockpitSkyboxes_PrefersMediumWhenGuideSaysMedium()
        {
            IReadOnlyList<FileInfo> library = Library(
                "High Quality Cockpit Skyboxes S.zip",
                "High Quality Cockpit Skyboxes M.zip");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "HQ Cockpit Skyboxes",
                new List<string> { "http://deadlystream.com/files/file/938-high-quality-cockpit-skyboxes/" },
                library,
                ArchiveResolver.GameMarker.Kotor1,
                new List<string>
                {
                    "I recommend the Medium texture option for the best balance of quality and size/performance.",
                });

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("High Quality Cockpit Skyboxes M.zip"));
        }

        [Test]
        public void TarisReskin_PrefersMainZipOverPatch()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Taris_Reskin-10-1-0.zip",
                "Taris Reskin Patch.7z");

            ArchiveResolution result = Resolve(
                "Taris Reskin",
                new List<string> { "http://www.nexusmods.com/kotor/mods/10/" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("Taris_Reskin-10-1-0.zip"));
        }

        [Test]
        public void GalaxyMapFixPack_DiscardsTslPrefixAndKeepsK1()
        {
            IReadOnlyList<FileInfo> library = Library(
                "TSLGalaxyMapFixPack.zip",
                "K1 Galaxy Map Fix Pack.zip");

            ArchiveResolution result = Resolve(
                "Galaxy Map Fix Pack",
                new List<string> { "http://deadlystream.com/files/file/1068-k1-galaxy-map-fix-pack/" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("K1 Galaxy Map Fix Pack.zip"));
        }

        [Test]
        public void UcoPatches_KeepsCompatibilitySet_NotLite()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Ultimate Character Overhaul -REDUX- ( LITE ) - TPC Version-1282-4-1-1628550322.rar",
                "JC's Mandalorian Armor - Compatibility Patch-1282-4-1-1629713289.rar",
                "JC's Minor Fixes - Compatibility Patch-1282-4-1-1629713341.rar",
                "KOTOR 1 Community Patch - Compatibility Patch-1282-4-1-1629713397.rar",
                "Miscellaneous Compatibility Patches-1282-4-1-1629713437.rar",
                "Republic Soldier's New Shade - Compatibility Patch-1282-4-1-1629713494.rar");

            ArchiveResolution result = Resolve(
                "Ultimate Character Overhaul Patches",
                new List<string> { "https://www.nexusmods.com/kotor/mods/1282?tab=files" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Does.Contain("Compatibility Patch").Or.Contain("Compatibility Patches"));
            Assert.That(result.AdditionalArchives.Count, Is.EqualTo(4));
            Assert.That(
                new[] { result.Archive.Name }.Concat(result.AdditionalArchives.Select(a => a.Name)),
                Has.None.Contain("LITE"));
        }

        [Test]
        public void HdNpcPortraits_PicksNewestVersion()
        {
            IReadOnlyList<FileInfo> library = Library(
                "hd_npc_portraits-v1.1.7z",
                "hd_npc_portraits-v2.0.7z");

            ArchiveResolution result = Resolve(
                "HD NPC Portraits",
                new List<string> { "https://deadlystream.com/files/file/1213-hd-npc-portraits/" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("hd_npc_portraits-v2.0.7z"));
        }

        [Test]
        public void QuanonsGammoreanSlug_ResolvesToQuanonGammoreans()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Quanon_Gammoreans.rar",
                "Quanons_HK47_Reskin.rar",
                "Taris_Reskin-10-1-0.zip");

            ArchiveResolution result = Resolve(
                "Gammorean Reskin Pack",
                new List<string> { "http://deadlystream.com/files/file/1023-quanons-gammorean-reskin-pack/" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("Quanon_Gammoreans.rar"));
        }

        [Test]
        public void ArchiveVsExtractedFolderTwin_ResolvesToTheArchive()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Taris_Reskin-10-1-0.zip",
                "Taris_Reskin-10-1-0");

            ArchiveResolution result = Resolve(
                "Taris Reskin",
                new List<string> { "http://www.nexusmods.com/kotor/mods/10/" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("Taris_Reskin-10-1-0.zip"));
        }

        [Test]
        public void JcsMinorFixes_PicksK1V11Zip_NotCompatPatchOrExtractedFolder()
        {
            IReadOnlyList<FileInfo> library = Library(
                "JC's Minor Fixes for K1 v1.1.zip",
                "JC's Minor Fixes - Compatibility Patch-1060-3-0-1629717168.rar",
                "JC's Minor Fixes - Compatibility Patch-1282-4-1-1629713341.rar",
                "JC's Minor Fixes for K1 v1.1",
                "JC's Minor Fixes - Compatibility Patch-1282-4-1-1629713341-extracted");

            ArchiveResolution result = Resolve(
                "JC's Minor Fixes",
                new List<string> { "https://deadlystream.com/files/file/1258-jcs-minor-fixes-for-k1/" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("JC's Minor Fixes for K1 v1.1.zip"));
        }

        [Test]
        public void UltimateKorriban_Nexus1367_PicksRarNotFolder()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Ultimate Korriban High Resolution - TPC Version-1367-1-2-1668960810.rar",
                "Ultimate Korriban High Resolution - TPC Version-1367-1-2-1668960810");

            ArchiveResolution result = Resolve(
                "Ultimate Korriban High Resolution",
                new List<string> { "https://www.nexusmods.com/kotor/mods/1367" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(
                result.Archive.Name,
                Is.EqualTo("Ultimate Korriban High Resolution - TPC Version-1367-1-2-1668960810.rar"));
        }

        [Test]
        public void FourGbPatcher_DoesNotResolveTo3cFdPatcher()
        {
            IReadOnlyList<FileInfo> library = Library("3C-FD Patcher", "some-other-mod.zip");

            ArchiveResolution result = Resolve("4GB Patcher", new List<string>(), library);

            Assert.That(
                result.IsResolved && result.Archive.Name == "3C-FD Patcher",
                Is.False,
                "A single leftover token 'patcher' must not pick an unrelated installer.");
        }

        [Test]
        public void K1PrefixArchive_VsUnprefixedFolder_ResolvesToTheArchive()
        {
            IReadOnlyList<FileInfo> library = Library(
                "[K1]_Taris_Dueling_Arena_Adjustment_v1.4.7z",
                "Taris_Dueling_Arena_Adjustment_v1.4");

            ArchiveResolution result = Resolve(
                "Taris Dueling Arena Adjustment",
                new List<string> { "https://deadlystream.com/files/file/taris-dueling-arena-adjustment/" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("[K1]_Taris_Dueling_Arena_Adjustment_v1.4.7z"));
        }

        [Test]
        public void KotorBracketPrefix_VsUnprefixedFolder_ResolvesToTheArchive()
        {
            IReadOnlyList<FileInfo> library = Library(
                "[KotOR] Swoop Bike Upgrades 1.1.7z",
                "Swoop Bike Upgrades 1.1");

            ArchiveResolution result = Resolve(
                "Swoop Bike Upgrades",
                new List<string> { "https://deadlystream.com/files/file/swoop-bike-upgrades/" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("[KotOR] Swoop Bike Upgrades 1.1.7z"));
        }

        [Test]
        public void RobesWithShadowsForK1_PrefersJcK1Port_NotGenericK2File()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Robes_With_Shadows_JC_K1_v1.2.0.7z",
                "Robes with Shadows.7z");

            ArchiveResolution result = Resolve(
                "Robes with Shadows for K1 (JC's Port)",
                new List<string> { "https://deadlystream.com/files/file/2357-robes-with-shadows-for-k1-jcs-port/" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("Robes_With_Shadows_JC_K1_v1.2.0.7z"));
        }

        [Test]
        public void KeblaYurtHd_PicksCommF02_NotRevampFolder()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Kebla Yurt (CommF02).rar",
                "Kebla Yurt Revamp");

            ArchiveResolution result = Resolve(
                "Kebla Yurt HD",
                new List<string> { "https://deadlystream.com/files/file/2471-kebla-yurt-hd/" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("Kebla Yurt (CommF02).rar"));
        }

        [Test]
        public void KeblaYurtRenovation_PicksRevampFolder_NotHdRar()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Kebla Yurt (CommF02).rar",
                "Kebla Yurt Revamp");

            ArchiveResolution result = Resolve(
                "Kebla Yurt Renovation",
                new List<string> { "https://deadlystream.com/files/file/2785-kebla-yurt-renovation/" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("Kebla Yurt Revamp"));
        }

        [Test]
        public void ShaleenaLashoweMouth_ResolvesViaDelimitedInitials()
        {
            IReadOnlyList<FileInfo> library = Library(
                "K1 SL Mouth Adjustment v1.1.1.7z",
                "K1 SL Mouth Adjustment v1.1.1",
                "Soldier Appearance Overhaul.zip");

            ArchiveResolution result = Resolve(
                "Shaleena/Lashowe Mouth Adjustment",
                new List<string> { "https://deadlystream.com/files/file/1480-k1-shaleenalashowe-mouth-adjustment/" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("K1 SL Mouth Adjustment v1.1.1.7z"));
        }

        [Test]
        public void ProtocolDroidsHd_ResolvesToDrdProtHd()
        {
            IReadOnlyList<FileInfo> library = Library(
                "DrdProtHD.rar",
                "DrdProtHD",
                "C_DrdWar.rar",
                "War Droid Mk 1 HD.zip");

            ArchiveResolution result = Resolve(
                "Protocol Droids HD",
                new List<string> { "https://deadlystream.com/files/file/2056-protocol-droid-hd/" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("DrdProtHD.rar"));
        }

        [Test]
        public void VurtEbonHawkRetexture_ResolvesViaEhInitials()
        {
            IReadOnlyList<FileInfo> library = Library(
                "vurt_k1_eh_retexture_v10.rar",
                "vurt_k1_eh_retexture_v10",
                "vurt_k1_eh_retexture",
                "N_DarthMalak01  (Vurt's KotOR Visual Resurgence) 2026.rar.rar",
                "Ultimate Ebon Hawk Repairs for K1.7z");

            ArchiveResolution result = Resolve(
                "Vurt's K1 Hi-Res Ebon Hawk Retexture",
                new List<string>
                {
                    "https://www.gamefront.com/games/knights-of-the-old-republic/file/vurt-s-k1-hi-res-ebon-hawk-retexture",
                },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("vurt_k1_eh_retexture_v10.rar"));
        }

        [Test]
        public void SenniVekMod_ResolvesToSvrViaDescriptionPhrase()
        {
            IReadOnlyList<FileInfo> library = Library("SVR1.2.7z", "SVR1.2", "Some Other Mod.zip");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Senni Vek Mod",
                new List<string> { "https://deadlystream.com/files/file/1090-senni-vek-mod/" },
                library,
                ArchiveResolver.GameMarker.Kotor1,
                new List<string>
                {
                    "The second option of this mod, the Senni Vek Restoration, restores the initial character. Senni Vek's Ambush removes Hulas.",
                });

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("SVR1.2.7z"));
        }

        [Test]
        public void ManaanFastTravel_PicksTaxiAfterDiscardingUltimateAndDuncan()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Manaan taxi (English).zip",
                "Manaan taxi (English)",
                "Ultimate Manaan High Resolution - TPC Version-1366-1-1-1669479766.rar",
                "Duncan on Manaan.7z");

            ArchiveResolution result = Resolve(
                "Manaan Fast Travel System",
                new List<string> { "https://deadlystream.com/files/file/2739-manaan-fast-travel-system/" },
                library);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("Manaan taxi (English).zip"));
        }

        [Test]
        public void CrashedRepublicCruiser_ResolvesViaAuthorPrefixLdr()
        {
            IReadOnlyList<FileInfo> library = Library(
                "ldr_repshipunknownworld.zip",
                "ldr_repshipunknownworld",
                "Some Other Quest.zip");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "A Crashed Republic Cruiser on a Nameless World",
                new List<string> { "https://deadlystream.com/files/file/1878-a-crashed-republic-cruiser-on-a-nameless-world/" },
                library,
                ArchiveResolver.GameMarker.Kotor1,
                extraSignals: null,
                author: "LDR");

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("ldr_repshipunknownworld.zip"));
        }

        [Test]
        public void ClassicClassAttackBonus_DiscardsWeakerConsularsBundle()
        {
            IReadOnlyList<FileInfo> library = Library(
                "CK-Classic Class Attack Bonus.zip",
                "CK-Classic Class Attack Bonus and Weaker Consulars.zip",
                "Classic Class Attack");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Classic Class Attack Bonus",
                new List<string> { "https://deadlystream.com/files/file/2812-classic-class-attack-bonus/" },
                library,
                ArchiveResolver.GameMarker.Kotor2);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("CK-Classic Class Attack Bonus.zip"));
        }

        [Test]
        public void RounderG0T0_NexusIdMatchesWhenTitleEndsWithDigit()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Smoother G0-T0-1296-1-0-1750625306.7z",
                "Smoother G0-T0-1296-1-0-1750625306");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Rounder G0-T0",
                new List<string> { "https://www.nexusmods.com/kotor2/mods/1296" },
                library,
                ArchiveResolver.GameMarker.Kotor2);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("Smoother G0-T0-1296-1-0-1750625306.7z"));
            Assert.That(result.Tier, Is.EqualTo(ArchiveResolutionTier.NexusModId));
        }

        [Test]
        public void CharacterTexturesModelFixes_Prefers2xTpcKotORArchiveOverFolders()
        {
            string root = Path.Combine(Path.GetTempPath(), "ModSync_CharTex_" + Guid.NewGuid());
            _ = Directory.CreateDirectory(root);
            try
            {
                // Hollow folder: only a nested archive + docs (no loose game payload).
                string hollow = Path.Combine(root, "CharacterTexturesModelFixes-Redrob41-2x-tpc");
                _ = Directory.CreateDirectory(hollow);
                File.WriteAllText(Path.Combine(hollow, "readme.txt"), "docs");
                File.WriteAllBytes(
                    Path.Combine(hollow, "Upscale+ Character Fixes - KotOR V0.52 (2x tpc).7z"),
                    new byte[] { 1, 2, 3 });

                // Empty title folder (must not win ExactName).
                _ = Directory.CreateDirectory(Path.Combine(root, "Character Textures & Model Fixes (Redrob41)"));

                string kotorArchive = Path.Combine(root, "Upscale+ Character Fixes - KotOR V0.52 (2x tpc).7z");
                string tslArchive = Path.Combine(root, "Upscale+ Character Fixes - TSL V0.52 (2x tpc).7z");
                File.WriteAllBytes(kotorArchive, new byte[] { 1 });
                File.WriteAllBytes(tslArchive, new byte[] { 1 });

                var library = new List<FileInfo>
                {
                    new FileInfo(Path.Combine(root, "Character Textures & Model Fixes (Redrob41)")),
                    new FileInfo(hollow),
                    new FileInfo(tslArchive),
                    new FileInfo(kotorArchive),
                };

                ArchiveResolution result = ArchiveResolver.Resolve(
                    "Character Textures & Model Fixes",
                    new List<string> { "https://deadlystream.com/files/file/2659-4x-upscale-character-textures-model-fixes/" },
                    library,
                    ArchiveResolver.GameMarker.Kotor1,
                    new List<string>
                    {
                        "Strongly recommend the 2x .tpc version; the fidelity loss for 2x is minimal.",
                    });

                Assert.That(result.IsResolved, Is.True, result.Reason);
                Assert.That(
                    result.Archive.Name,
                    Is.EqualTo("Upscale+ Character Fixes - KotOR V0.52 (2x tpc).7z"));
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Test]
        public void EmptyBeamEffectsFolder_DoesNotResolveAsArchive()
        {
            string root = Path.Combine(Path.GetTempPath(), "ModSync_Beam_" + Guid.NewGuid());
            _ = Directory.CreateDirectory(root);
            try
            {
                _ = Directory.CreateDirectory(Path.Combine(root, "Hires Beam Effects"));
                var library = new List<FileInfo>
                {
                    new FileInfo(Path.Combine(root, "Hires Beam Effects")),
                };

                ArchiveResolution result = ArchiveResolver.Resolve(
                    "Hi-Res Beam Effects",
                    new List<string> { "https://deadlystream.com/files/file/260-k1-hi-res-beam-effects/" },
                    library,
                    ArchiveResolver.GameMarker.Kotor1);

                Assert.That(
                    result.IsResolved,
                    Is.False,
                    "Empty folder must not resolve; fail loudly when the archive is missing.");
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Test]
        public void Pavor_PrefersAcronymArchiveOverOneWordReplacementsHit()
        {
            IReadOnlyList<FileInfo> library = Library(
                "K1 PAVOR v1.3.2.7z",
                "Quarterstaff Replacements.rar");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "K1 Ported Alien VO Replacements",
                new List<string> { "https://deadlystream.com/files/file/1426-k1-ported-alien-vo-replacements/" },
                library,
                ArchiveResolver.GameMarker.Kotor1);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("K1 PAVOR v1.3.2.7z"));
        }

        [Test]
        public void CitadelStationBackdrop_2kVs4kWithoutGuideSize_StaysUnresolved()
        {
            IReadOnlyList<FileInfo> library = Library(
                "KOTOR2-Citadel-Station-Backdrop_v2.0.0_2k.zip",
                "KOTOR2-Citadel-Station-Backdrop_v2.0.0_4k.zip");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "JC's Citadel Station Backdrop",
                new List<string> { "https://deadlystream.com/files/file/1217-jcs-citadel-station-backdrop/" },
                library,
                ArchiveResolver.GameMarker.Kotor2);

            Assert.That(
                result.IsResolved,
                Is.False,
                "2k vs 4k with no guide size signal must stay unresolved, not guessed.");
        }

        /// <summary>
        /// Measured wrong-game mis-selection. The K1 component "Trandoshans Rescaled" resolved to
        /// "Rescaled Trandoshans.zip" -- a different mod, by a different author, FOR KOTOR 2 --
        /// because word-order-insensitive matching treats the two names as the same thing. The
        /// archive the build actually declares was sitting in the same library, and the install
        /// died applying a TSL appearance.2da to a K1 game (KeyError 'driveanimrun_pc').
        /// </summary>
        [Test]
        public void TrandoshansRescaled_K1Build_DoesNotPickTheReorderedKotor2Archive()
        {
            IReadOnlyList<FileInfo> library = Library(
                "[K1]_Trandoshans_Rescale.7z",
                "Rescaled Trandoshans.zip");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Trandoshans Rescaled",
                new List<string>(),
                library,
                ArchiveResolver.GameMarker.Kotor1);

            Assert.Multiple(() =>
            {
                Assert.That(
                    result.Archive?.Name,
                    Is.Not.EqualTo("Rescaled Trandoshans.zip"),
                    "A pure word-reordering must never outrank an archive marked for this build's game.");
                Assert.That(result.IsResolved, Is.True, result.Reason);
                Assert.That(result.Archive.Name, Is.EqualTo("[K1]_Trandoshans_Rescale.7z"));
            });
        }

        /// <summary>
        /// The same library seen from a TSL build: the marked K1 archive is discarded outright and
        /// the reordering is allowed, because nothing contradicts it.
        /// </summary>
        [Test]
        public void RescaledTrandoshans_K2Build_StillReachesTheUnmarkedArchive()
        {
            IReadOnlyList<FileInfo> library = Library(
                "[K1]_Trandoshans_Rescale.7z",
                "Rescaled Trandoshans.zip");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Rescaled Trandoshans",
                new List<string>(),
                library,
                ArchiveResolver.GameMarker.Kotor2);

            Assert.That(result.IsResolved, Is.True, result.Reason);
            Assert.That(result.Archive.Name, Is.EqualTo("Rescaled Trandoshans.zip"));
        }

        /// <summary>
        /// The marker preference must not fire when the marked archive is a different mod: two
        /// marked candidates leave the tier undecided and the ordinary chain continues.
        /// </summary>
        [Test]
        public void TargetGameMarker_DoesNotFireWhenTwoMarkedArchivesMatch()
        {
            IReadOnlyList<FileInfo> library = Library(
                "[K1]_Trandoshans_Rescale.7z",
                "[K1]_Trandoshans_Rescaled_HD.7z");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Trandoshans Rescaled",
                new List<string>(),
                library,
                ArchiveResolver.GameMarker.Kotor1);

            Assert.That(
                result.Archive?.Name,
                Is.Not.EqualTo("[K1]_Trandoshans_Rescale.7z"),
                "Two marked candidates are ambiguous, not a unique marker hit.");
        }

        /// <summary>
        /// TSLPatcher's <c>LookupGameNumber</c> looks authoritative (1 = KOTOR, 2 = TSL) but is not:
        /// measured over the reference library, 11 of the 26 archives whose filename unambiguously
        /// marks them KOTOR 1 and which declare the field say <c>2</c>. Acting on it rejected 20
        /// correct archives in one K1 build. This test pins that the resolver ignores it, so nobody
        /// re-adds the veto on the strength of how sensible it sounds.
        /// </summary>
        [Test]
        public void ChangesIniLookupGameNumber_IsNotTreatedAsAGameSignal()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ModSync_ResolverIni_" + Guid.NewGuid());
            _ = Directory.CreateDirectory(directory);
            try
            {
                // A genuine K1 mod that declares LookupGameNumber=2, exactly like
                // "JC's Mandalorian Armor for K1" and "KOTOR1-Thematic-Companions" do on disk.
                string archivePath = Path.Combine(directory, "Rescaled Trandoshans Pack.zip");
                WriteZipWithChangesIni(archivePath, lookupGameNumber: 2);

                ArchiveResolution result = ArchiveResolver.Resolve(
                    "Rescaled Trandoshans Pack",
                    new List<string>(),
                    new List<FileInfo> { new FileInfo(archivePath) },
                    ArchiveResolver.GameMarker.Kotor1);

                Assert.That(
                    result.IsResolved,
                    Is.True,
                    "changes.ini is not evidence of the target game; the match must stand.");
            }
            finally
            {
                TryDelete(directory);
            }
        }

        // ---------------------------------------------------------------------------------
        // Cases measured end-to-end on the real K1 build with the real archive library, via
        //   convert -i mod-builds/content/k1/full.md --auto-generate-local
        //     --source-path .../kotor_mod_archives
        // Every one of these resolved to the WRONG archive before the fixes below.
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The component is the base mod, so the "Compatibility Patch" download must not outrank the
        /// real archive. It did, because <c>ComponentLooksLikeAPatch</c> treated any name ending in
        /// "Patch" as a compatibility patch and disabled the guard meant to catch this.
        /// </summary>
        [Test]
        public void KotorCommunityPatch_PicksTheBaseArchive_NotTheCompatibilityPatch()
        {
            IReadOnlyList<FileInfo> library = Library(
                "K1_Community_Patch_v1.10.0.zip",
                "KOTOR 1 Community Patch - Compatibility Patch-1282-4-1-1629713397.rar",
                "KOTOR 2 Community Patch - Compatibility Patch-1060-3-0-1629717259.rar");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "KOTOR Community Patch",
                new List<string> { "https://deadlystream.com/files/file/kotor-1-community-patch/" },
                library,
                ArchiveResolver.GameMarker.Kotor1);

            Assert.Multiple(() =>
            {
                Assert.That(result.IsResolved, Is.True, result.Reason);
                Assert.That(result.Archive.Name, Is.EqualTo("K1_Community_Patch_v1.10.0.zip"));
            });
        }

        /// <summary>
        /// "kashyyyk" is one word of four, and the archive that carries it is a different mod
        /// entirely. KillCzerkaJerk.zip carries three of the four.
        /// </summary>
        [Test]
        public void KillTheCzerkaJerk_PrefersTheModCoveringMostOfTheName()
        {
            IReadOnlyList<FileInfo> library = Library(
                "KillCzerkaJerk.zip",
                "[K1]_Control_Panel_For_Kashyyyk_Shadowlands_Forcefield_v1.1.7z",
                "Fixed Better Czerka Salvager.zip");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Kill the Czerka Jerk on Kashyyyk",
                new List<string>(),
                library,
                ArchiveResolver.GameMarker.Kotor1);

            Assert.Multiple(() =>
            {
                Assert.That(result.IsResolved, Is.True, result.Reason);
                Assert.That(result.Archive.Name, Is.EqualTo("KillCzerkaJerk.zip"));
            });
        }

        /// <summary>
        /// "revamped" alone matched an unrelated effects mod. Among the archives that cover more of
        /// the name, only "Ajunta's Swords" introduces no other product.
        /// </summary>
        [Test]
        public void AjuntaPallsSwords_PrefersTheSwordsArchive_NotRevampedFx()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Ajunta&#39;s Swords.7z",
                "Revamped FX.rar",
                "Ajunta Pall Unique Appearance.zip",
                "[K1]_Legends_Ajunta_Pall&#39;s_Blade_v1.0.2b.7z");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Ajunta Pall&#39;s Swords Revamped",
                new List<string>(),
                library,
                ArchiveResolver.GameMarker.Kotor1);

            Assert.Multiple(() =>
            {
                Assert.That(
                    result.Archive?.Name,
                    Is.Not.EqualTo("Revamped FX.rar"),
                    "One shared word is not an identification.");
                Assert.That(result.IsResolved, Is.True, result.Reason);
                Assert.That(result.Archive.Name, Is.EqualTo("Ajunta&#39;s Swords.7z"));
            });
        }

        /// <summary>
        /// The author token "quanons" appeared in exactly one archive -- his HK-47 reskin, a
        /// different mod. The right archive keeps the author AND covers the subject.
        /// </summary>
        [Test]
        public void QuanonsCanderousOrdo_PicksTheCanderousReskin_NotTheHk47One()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Quanon_CandOrdo_Reskin.rar",
                "Quanons_HK47_Reskin.rar",
                "Quanon_Gammoreans.rar",
                "Canderous Ordo.rar",
                "Canderous Patch.rar");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Quanon&#39;s Canderous Ordo",
                new List<string> { "https://deadlystream.com/files/file/quanons-canderous-ordo/" },
                library,
                ArchiveResolver.GameMarker.Kotor1);

            Assert.Multiple(() =>
            {
                Assert.That(
                    result.Archive?.Name,
                    Is.Not.EqualTo("Quanons_HK47_Reskin.rar"),
                    "The author's other mod is not this mod.");
                Assert.That(result.IsResolved, Is.True, result.Reason);
                Assert.That(result.Archive.Name, Is.EqualTo("Quanon_CandOrdo_Reskin.rar"));
            });
        }

        /// <summary>
        /// "Skyboxes II" is a different product from "Skyboxes", so a seven-file "model fixes" patch
        /// must not answer for the sequel. Name similarity alone cannot find the right archive here
        /// -- it spells "High Quality" as "HQ" with no separator -- so this pins only the negative.
        /// </summary>
        [Test]
        public void HighQualitySkyboxesII_NeverAnswersWithTheNonSequelPatch()
        {
            IReadOnlyList<FileInfo> library = Library(
                "HQSkyboxesII_K1.7z",
                "HQSkyboxesII_K1_Yavin4.7z",
                "High quality skyboxes model fixes.rar",
                "High Quality Cockpit Skyboxes M.zip");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "High Quality Skyboxes II",
                new List<string>(),
                library,
                ArchiveResolver.GameMarker.Kotor1);

            Assert.That(
                result.Archive?.Name,
                Is.Not.EqualTo("High quality skyboxes model fixes.rar"),
                "A non-sequel patch must never answer for a sequel component.");
        }

        /// <summary>
        /// The real fix for that component: its guide text names the file outright. Verbatim
        /// DownloadInstructions from mod-builds/content/k1/full.md.
        /// </summary>
        [Test]
        public void HighQualitySkyboxesII_UsesTheFilenameTheGuideNames()
        {
            IReadOnlyList<FileInfo> library = Library(
                "HQSkyboxesII_K1.7z",
                "HQSkyboxesII_K1_Yavin4.7z",
                "HQSkyboxesII_TSL.7z",
                "High quality skyboxes model fixes.rar");

            const string DownloadInstructions =
                "Unless using one of the mods for which Kex has developed skyboxes (*not* recommended, "
                + "as they're almost certainly not compatible with this build) simply download the "
                + "'HQSkyboxesII_K1.7z' file.";

            ArchiveResolution result = ArchiveResolver.Resolve(
                "High Quality Skyboxes II",
                new List<string>(),
                library,
                ArchiveResolver.GameMarker.Kotor1,
                new List<string> { DownloadInstructions });

            Assert.Multiple(() =>
            {
                Assert.That(result.IsResolved, Is.True, result.Reason);
                Assert.That(result.Archive.Name, Is.EqualTo("HQSkyboxesII_K1.7z"));
            });
        }

        /// <summary>
        /// Prose naming two different archives is not an instruction to pick one of them, so the
        /// tier abstains rather than guessing between a main file and its patch.
        /// </summary>
        [Test]
        public void FilenameInProse_AbstainsWhenTheGuideNamesTwoFiles()
        {
            IReadOnlyList<FileInfo> library = Library(
                "HQSkyboxesII_K1.7z",
                "High quality skyboxes model fixes.rar");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Totally Unrelated Component Name",
                new List<string>(),
                library,
                ArchiveResolver.GameMarker.Kotor1,
                new List<string>
                {
                    "Download 'HQSkyboxesII_K1.7z' and also 'High quality skyboxes model fixes.rar'.",
                });

            Assert.That(
                result.IsResolved,
                Is.False,
                "Two named files is ambiguous, not an answer.");
        }

        /// <summary>
        /// The surrounding sentence must not be swallowed into the filename match: an earlier
        /// version allowed spaces and matched "simply download the 'HQSkyboxesII_K1.7z", which
        /// exists nowhere, so the tier silently never fired.
        /// </summary>
        [Test]
        public void FilenameInProse_IsNotSwallowedBySurroundingWords()
        {
            IReadOnlyList<FileInfo> library = Library("HQSkyboxesII_K1.7z");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Totally Unrelated Component Name",
                new List<string>(),
                library,
                ArchiveResolver.GameMarker.Kotor1,
                new List<string> { "simply download the HQSkyboxesII_K1.7z file." });

            Assert.Multiple(() =>
            {
                Assert.That(result.IsResolved, Is.True, result.Reason);
                Assert.That(result.Archive.Name, Is.EqualTo("HQSkyboxesII_K1.7z"));
            });
        }

        /// <summary>
        /// Regression pin. Counting the function word "with" as identity scored
        /// "Assassins with Lightsabers" above the correct archive.
        /// </summary>
        [Test]
        public void SherrukAttacksWithLightsabers_IsNotBeatenByAFunctionWord()
        {
            IReadOnlyList<FileInfo> library = Library(
                "sherruksabers.7z",
                "Assassins with Lightsabers");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "Sherruk Attacks with Lightsabers",
                new List<string>(),
                library,
                ArchiveResolver.GameMarker.Kotor1);

            Assert.Multiple(() =>
            {
                Assert.That(result.IsResolved, Is.True, result.Reason);
                Assert.That(result.Archive.Name, Is.EqualTo("sherruksabers.7z"));
            });
        }

        /// <summary>
        /// Regression pin. "High" and "Quality" describe half the library; counting them as identity
        /// made unrelated "High Quality ..." archives outscore the correct one.
        /// </summary>
        [Test]
        public void HighQualityStarfields_IsNotBeatenByGenericQualityWords()
        {
            IReadOnlyList<FileInfo> library = Library(
                "K1_HDStarsAndNebulas_1_3.zip",
                "High Quality Blasters 1.1.zip",
                "High quality skyboxes model fixes.rar");

            ArchiveResolution result = ArchiveResolver.Resolve(
                "High Quality Starfields and Nebulas",
                new List<string>(),
                library,
                ArchiveResolver.GameMarker.Kotor1);

            Assert.Multiple(() =>
            {
                Assert.That(result.IsResolved, Is.True, result.Reason);
                Assert.That(result.Archive.Name, Is.EqualTo("K1_HDStarsAndNebulas_1_3.zip"));
            });
        }

        /// <summary>
        /// A folder the patcher has already been run in (installlog.txt, backup/, uninstall/) is a
        /// finished install, not a mod source. "HQ Skyboxes II" in the reference library is exactly
        /// that -- 290 files including the patcher's backups -- and it was outranking the clean
        /// archive.
        /// </summary>
        [Test]
        public void SpentInstallFolder_IsNotOfferedAsAModSource()
        {
            string root = Path.Combine(Path.GetTempPath(), "ModSync_SpentFolder_" + Guid.NewGuid());
            _ = Directory.CreateDirectory(root);
            try
            {
                string spent = Path.Combine(root, "Widescreen Fix");
                _ = Directory.CreateDirectory(Path.Combine(spent, "backup"));
                _ = Directory.CreateDirectory(Path.Combine(spent, "uninstall"));
                _ = Directory.CreateDirectory(Path.Combine(spent, "tslpatchdata"));
                File.WriteAllText(Path.Combine(spent, "installlog.txt"), "done");

                ArchiveResolution result = ArchiveResolver.Resolve(
                    "Widescreen Fix",
                    new List<string>(),
                    new List<FileInfo> { new FileInfo(spent) },
                    ArchiveResolver.GameMarker.Kotor1);

                Assert.That(
                    result.IsResolved,
                    Is.False,
                    "A folder the patcher already ran in must not be selected.");
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static void WriteZipWithChangesIni(string archivePath, int lookupGameNumber)
        {
            string ini =
                "[Settings]\r\nFileExists=1\r\nWindowCaption=Test\r\n"
                + "LookupGameFolder=0\r\nLookupGameNumber=" + lookupGameNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\r\n";

            using (var archive = ZipArchive.CreateArchive())
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(ini);
                _ = archive.AddEntry("tslpatchdata/changes.ini", new MemoryStream(bytes), closeStream: true);
                byte[] texture = System.Text.Encoding.UTF8.GetBytes("tga");
                _ = archive.AddEntry("tslpatchdata/p_attnh1.tga", new MemoryStream(texture), closeStream: true);

                using (FileStream stream = File.Create(archivePath))
                {
                    archive.SaveTo(stream, new SharpCompress.Writers.Zip.ZipWriterOptions(CompressionType.None));
                }
            }
        }

        /// <summary>
        /// A component URL that the download index maps to exactly one on-disk archive is
        /// a second payload. A page that lists several files is not unique and is ignored.
        /// </summary>
        [Test]
        public void UniqueOnDiskArchivesPerUrl_AttachesOnlySingletonUrlHits()
        {
            IReadOnlyList<FileInfo> library = Library(
                "MainMod.zip",
                "FollowUp.rar",
                "MainMod-Translation.zip");

            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [UrlNormalizer.Normalize("https://example.com/files/file/main/")] =
                    new List<string> { "MainMod.zip", "MainMod-Translation.zip" },
                [UrlNormalizer.Normalize("https://mega.example/file/abc#key")] =
                    new List<string> { "FollowUp.rar" },
            };

            List<FileInfo> hits = ArchiveResolver.UniqueOnDiskArchivesPerUrl(
                new List<string>
                {
                    "https://example.com/files/file/main/",
                    "https://mega.example/file/abc#key",
                },
                library,
                index);

            Assert.That(hits.Select(a => a.Name), Is.EquivalentTo(new[] { "FollowUp.rar" }));
        }

        [Test]
        public void UniqueOnDiskArchivesPerUrl_IncludesLooseGameFileFromSingletonUrl()
        {
            IReadOnlyList<FileInfo> library = Library("N_SomeLoose01.tga");
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [UrlNormalizer.Normalize("https://deadlystream.example/files/file/2787-loose-retexture/")] =
                    new List<string> { "N_SomeLoose01.tga" },
            };

            List<FileInfo> hits = ArchiveResolver.UniqueOnDiskArchivesPerUrl(
                new List<string> { "https://deadlystream.example/files/file/2787-loose-retexture/" },
                library,
                index);

            Assert.That(hits.Select(a => a.Name), Is.EquivalentTo(new[] { "N_SomeLoose01.tga" }));
        }

        [Test]
        public void UniqueOnDiskArchivesPerUrl_RejectsNexusIdMismatch()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Ultimate Korriban High Resolution - TPC Version-1367-1-2-1668960810.rar",
                "Taris_Reskin-10-1-0.zip");
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [UrlNormalizer.Normalize("http://www.nexusmods.com/kotor/mods/10/")] =
                    new List<string>
                    {
                        "Ultimate Korriban High Resolution - TPC Version-1367-1-2-1668960810.rar",
                    },
            };

            List<FileInfo> hits = ArchiveResolver.UniqueOnDiskArchivesPerUrl(
                new List<string> { "http://www.nexusmods.com/kotor/mods/10/" },
                library,
                index);

            Assert.That(hits, Is.Empty);
        }

        [Test]
        public void AttachUrlMappedPayloads_AddsPatchFollowUpBesideResolvedMain()
        {
            IReadOnlyList<FileInfo> library = Library("K1_Community_Main.zip", "Heading Patch.rar");
            ArchiveResolution resolved = new ArchiveResolution
            {
                Archive = library[0],
                Tier = ArchiveResolutionTier.UniqueTokenSubset,
                Reason = "main",
            };
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [UrlNormalizer.Normalize("https://deadlystream.example/files/file/1258-main/")] =
                    new List<string> { "K1_Community_Main.zip", "K1_Community_Main-FR.zip" },
                [UrlNormalizer.Normalize("https://mega.example/file/patch#key")] =
                    new List<string> { "Heading Patch.rar" },
            };

            ArchiveResolution attached = ArchiveResolver.AttachUrlMappedPayloads(
                resolved,
                new List<string>
                {
                    "https://deadlystream.example/files/file/1258-main/",
                    "https://mega.example/file/patch#key",
                },
                library,
                index);

            Assert.Multiple(() =>
            {
                Assert.That(attached.IsResolved, Is.True);
                Assert.That(attached.Archive.Name, Is.EqualTo("K1_Community_Main.zip"));
                Assert.That(
                    attached.AdditionalArchives.Select(a => a.Name),
                    Is.EquivalentTo(new[] { "Heading Patch.rar" }));
            });
        }

        [Test]
        public void AttachUrlMappedPayloads_PromotesSingletonUrlWhenUnresolved()
        {
            IReadOnlyList<FileInfo> library = Library("N_SomeLoose01.tga", "Unrelated.zip");
            ArchiveResolution unresolved = new ArchiveResolution
            {
                Tier = ArchiveResolutionTier.Unresolved,
                Reason = "No tier produced a unique match.",
            };
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [UrlNormalizer.Normalize("https://deadlystream.example/files/file/2787-loose/")] =
                    new List<string> { "N_SomeLoose01.tga" },
            };

            ArchiveResolution attached = ArchiveResolver.AttachUrlMappedPayloads(
                unresolved,
                new List<string> { "https://deadlystream.example/files/file/2787-loose/" },
                library,
                index);

            Assert.Multiple(() =>
            {
                Assert.That(attached.IsResolved, Is.True, attached.Reason);
                Assert.That(attached.Archive.Name, Is.EqualTo("N_SomeLoose01.tga"));
            });
        }

        [Test]
        public void AttachUrlMappedPayloads_PrefersUrlArchiveOverHeadingFolder()
        {
            IReadOnlyList<FileInfo> library = Library("K2 Swoops to K1", "[K1] Swoop from K2 to K1.rar");
            ArchiveResolution folderHit = new ArchiveResolution
            {
                Archive = library[0],
                Tier = ArchiveResolutionTier.ExactName,
                Reason = "folder",
            };
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [UrlNormalizer.Normalize("https://deadlystream.example/files/file/swoops/")] =
                    new List<string> { "[K1] Swoop from K2 to K1.rar" },
            };

            ArchiveResolution attached = ArchiveResolver.AttachUrlMappedPayloads(
                folderHit,
                new List<string> { "https://deadlystream.example/files/file/swoops/" },
                library,
                index);

            Assert.Multiple(() =>
            {
                Assert.That(attached.IsResolved, Is.True);
                Assert.That(attached.Archive.Name, Is.EqualTo("[K1] Swoop from K2 to K1.rar"));
            });
        }

        [Test]
        public void AttachUrlMappedPayloads_PrefersUrlArchiveOverLooseFileFromSameUrl()
        {
            IReadOnlyList<FileInfo> library = Library(
                "Malak.rar",
                "N_DarthMalak01.tga",
                "N_DarthMalak01  (Vurt's KotOR Visual Resurgence) 2026.rar.rar");
            ArchiveResolution looseHit = new ArchiveResolution
            {
                Archive = library[1],
                Tier = ArchiveResolutionTier.UniqueTokenSubset,
                Reason = "loose",
            };
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [UrlNormalizer.Normalize("https://deadlystream.example/files/file/980-hd-malak/")] =
                    new List<string>
                    {
                        "Malak.rar",
                        "N_DarthMalak01.tga",
                        "N_DarthMalak01  (Vurt's KotOR Visual Resurgence) 2026.rar.rar",
                    },
            };

            ArchiveResolution attached = ArchiveResolver.AttachUrlMappedPayloads(
                looseHit,
                new List<string> { "https://deadlystream.example/files/file/980-hd-malak/" },
                library,
                index,
                "HD Darth Malak");

            Assert.Multiple(() =>
            {
                Assert.That(attached.IsResolved, Is.True);
                Assert.That(attached.Archive.Name, Is.EqualTo("Malak.rar"), attached.Reason);
            });
        }

        [Test]
        public void AttachUrlMappedPayloads_DiscardsUnnamedFuzzyGuessInFavorOfUrlNamedArchive()
        {
            // "HD Astromech Droids": SignificantTokens drops the 2-character "HD" token, so
            // UniqueTokenSubset (mis)matches the generic leftover tokens "astromech"/"droids"
            // against an entirely unrelated archive that the guide never names anywhere. The
            // component's own deadlystream URL names the correct archive instead. The wrong guess
            // must be discarded, not attached alongside the correct archive as an "additional" one.
            IReadOnlyList<FileInfo> library = Library(
                "SH_Refurbished Astromech Droids.7z",
                "DrdAstro HD.rar");
            ArchiveResolution wrongFuzzyGuess = new ArchiveResolution
            {
                Archive = library[0],
                Tier = ArchiveResolutionTier.UniqueTokenSubset,
                Reason = "Archive name contains every word of 'HD Astromech Droids'.",
            };
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [UrlNormalizer.Normalize("https://deadlystream.example/files/file/1600-drdastro-hd/")] =
                    new List<string> { "DrdAstro HD.rar" },
            };

            ArchiveResolution attached = ArchiveResolver.AttachUrlMappedPayloads(
                wrongFuzzyGuess,
                new List<string> { "https://deadlystream.example/files/file/1600-drdastro-hd/" },
                library,
                index,
                "HD Astromech Droids");

            Assert.Multiple(() =>
            {
                Assert.That(attached.IsResolved, Is.True, attached.Reason);
                Assert.That(attached.Archive.Name, Is.EqualTo("DrdAstro HD.rar"), attached.Reason);
                Assert.That(attached.Tier, Is.EqualTo(ArchiveResolutionTier.ResourceIndex));
                Assert.That(attached.AdditionalArchives, Is.Empty, attached.Reason);
            });
        }

        [Test]
        public void AttachUrlMappedPayloads_KeepsCorrectlyNamedMatchWhenOnlyASecondUrlHasIndexCoverage()
        {
            // The two lookups the download index is built from (hash-keyed, checked earlier in
            // ResolveCore, and URL-keyed, checked here) routinely disagree on per-URL coverage - that
            // is why ResolveByName ran at all for a component whose main URL has no index entry. A
            // correctly name-resolved main archive must not be discarded just because a SEPARATE
            // patch URL happens to be indexed while the main URL happens not to be.
            IReadOnlyList<FileInfo> library = Library("Correct Main Mod.zip", "Correct Main Mod Patch.rar");
            ArchiveResolution correctlyResolvedMain = new ArchiveResolution
            {
                Archive = library[0],
                Tier = ArchiveResolutionTier.UniqueTokenSubset,
                Reason = "Archive name contains every word of 'Correct Main Mod'.",
            };
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                // Only the patch URL is indexed; the main URL is absent (no entry at all).
                [UrlNormalizer.Normalize("https://mega.example/file/patch#key")] =
                    new List<string> { "Correct Main Mod Patch.rar" },
            };

            ArchiveResolution attached = ArchiveResolver.AttachUrlMappedPayloads(
                correctlyResolvedMain,
                new List<string>
                {
                    "https://deadlystream.example/files/file/9001-main/",
                    "https://mega.example/file/patch#key",
                },
                library,
                index,
                "Correct Main Mod");

            Assert.Multiple(() =>
            {
                Assert.That(attached.IsResolved, Is.True, attached.Reason);
                Assert.That(attached.Archive.Name, Is.EqualTo("Correct Main Mod.zip"), attached.Reason);
                Assert.That(attached.Tier, Is.EqualTo(ArchiveResolutionTier.UniqueTokenSubset), attached.Reason);
                Assert.That(
                    attached.AdditionalArchives.Select(a => a.Name),
                    Is.EquivalentTo(new[] { "Correct Main Mod Patch.rar" }),
                    attached.Reason);
            });
        }

        private static void TryDelete(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Ignore cleanup errors.
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore cleanup errors.
            }
        }
    }
}
