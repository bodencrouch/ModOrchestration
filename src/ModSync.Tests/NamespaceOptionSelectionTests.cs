// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;

using ModSync.Core;
using ModSync.Core.Services;

using NUnit.Framework;

namespace ModSync.Tests
{
    [TestFixture]
    public sealed class NamespaceOptionSelectionTests
    {
        [Test]
        public void SelectNamespaceOptions_SelectsMainByDefault()
        {
            var component = new ModComponent
            {
                Name = "A Crashed Republic Cruiser on a Nameless World",
                Directions = "Run the installer to install the mod.",
                Options =
                {
                    new Option
                    {
                        Guid = Guid.NewGuid(),
                        Name = "A Crashed Republic Cruiser on a Nameless World",
                        Description = "The default installation for the mod.",
                        IsSelected = false,
                    },
                    new Option
                    {
                        Guid = Guid.NewGuid(),
                        Name = "SithSpecter's High Quality Blasters (OPTIONAL)",
                        Description = "Adds integration with SithSpecter's High Quality Blasters mod.",
                        IsSelected = false,
                    },
                },
            };

            AutoInstructionGenerator.SelectNamespaceOptionsFromGuide(
                component,
                new List<ModComponent> { component });

            Assert.That(component.Options[0].IsSelected, Is.True, "Main/default option must be selected");
            Assert.That(component.Options[1].IsSelected, Is.False, "Optional without condition mod must stay off");
        }

        [Test]
        public void SelectNamespaceOptions_BasePlusOptional_WhenConditionPresent()
        {
            var hqBlasters = new ModComponent { Name = "High Quality Blasters", Guid = Guid.NewGuid() };
            var loadscreens = new ModComponent { Name = "Loadscreens in Color", Guid = Guid.NewGuid() };
            var component = new ModComponent
            {
                Name = "A Crashed Republic Cruiser on a Nameless World",
                Directions =
                    "Run the installer to install the mod, then re-run it twice more, once for each of the "
                    + "optional installs, if using Loadscreens in Color/HQ Blasters.",
                Options =
                {
                    new Option
                    {
                        Guid = Guid.NewGuid(),
                        Name = "A Crashed Republic Cruiser on a Nameless World",
                        Description = "The default installation for the mod.",
                        IsSelected = false,
                    },
                    new Option
                    {
                        Guid = Guid.NewGuid(),
                        Name = "SithSpecter's High Quality Blasters (OPTIONAL)",
                        Description = "Adds integration with SithSpecter's High Quality Blasters mod.",
                        IsSelected = false,
                    },
                    new Option
                    {
                        Guid = Guid.NewGuid(),
                        Name = "SithSpecter's Colored Loadscreens (OPTIONAL)",
                        Description = "Adds integration with SithSpecter's Colored Loadscreens mod.",
                        IsSelected = false,
                    },
                },
            };

            AutoInstructionGenerator.SelectNamespaceOptionsFromGuide(
                component,
                new List<ModComponent> { component, hqBlasters, loadscreens });

            Assert.Multiple(() =>
            {
                Assert.That(component.Options[0].IsSelected, Is.True, "Base install");
                Assert.That(component.Options[1].IsSelected, Is.True, "HQ Blasters optional when present");
                Assert.That(component.Options[2].IsSelected, Is.True, "Loadscreens optional when present");
            });
        }

        [Test]
        public void SelectNamespaceOptions_OptionalNotSelected_WhenConditionModAbsent()
        {
            var component = new ModComponent
            {
                Name = "K1 Ported Alien VO Replacements",
                Directions =
                    "Install the main mod, then re-run the patcher and select the K1CP compatibility "
                    + "install option and install it as well, if using K1CP.",
                Options =
                {
                    new Option
                    {
                        Guid = Guid.NewGuid(),
                        Name = "Main",
                        Description = "The default installation for the mod.",
                        IsSelected = false,
                    },
                    new Option
                    {
                        Guid = Guid.NewGuid(),
                        Name = "K1CP Compatibility (OPTIONAL)",
                        Description = "Compatibility patch for the KOTOR 1 Community Patch.",
                        IsSelected = false,
                    },
                },
            };

            AutoInstructionGenerator.SelectNamespaceOptionsFromGuide(
                component,
                new List<ModComponent> { component });

            Assert.That(component.Options[0].IsSelected, Is.True);
            Assert.That(
                component.Options[1].IsSelected,
                Is.False,
                "K1CP optional must stay off when K1CP is not in the build");
        }

        [Test]
        public void SelectNamespaceOptions_ExcludesExtraTextures_WhenGuideRecommendsNotRerunning()
        {
            var component = new ModComponent
            {
                Name = "JC's Mandalorian Armor",
                Directions =
                    "Install Option A. I recommend NOT re-running the patcher to install the extra textures, "
                    + "as upscaled textures installed in UCO are much higher-quality than these.",
                Options =
                {
                    new Option
                    {
                        Guid = Guid.NewGuid(),
                        Name = "Option A: Unmasked",
                        Description = "The unmasked Mandalorian armor.",
                        IsSelected = false,
                    },
                    new Option
                    {
                        Guid = Guid.NewGuid(),
                        Name = "Option B: Masked",
                        Description = "The masked Mandalorian armor.",
                        IsSelected = false,
                    },
                    new Option
                    {
                        Guid = Guid.NewGuid(),
                        Name = "Extra Textures",
                        Description = "Additional lower-resolution Mandalorian textures.",
                        IsSelected = false,
                    },
                },
            };

            AutoInstructionGenerator.SelectNamespaceOptionsFromGuide(
                component,
                new List<ModComponent> { component });

            Assert.Multiple(() =>
            {
                Assert.That(component.Options[0].IsSelected, Is.True, "Option A must be selected");
                Assert.That(component.Options[1].IsSelected, Is.False, "Option B must stay off");
                Assert.That(
                    component.Options[2].IsSelected,
                    Is.False,
                    "Guide says not to install Extra Textures; mentioning the name must not select it");
            });
        }

        /// <summary>
        /// Reproduces a confirmed false-positive: the component's own generic words
        /// ("Weather", "Overhaul", "Effects") also appear in an unrelated sentence about a
        /// different, unrelated mod's Patch subfolder. Before the fix, those shared generic
        /// words made <c>GuideExcludesNamespace</c> match every namespace option (since each
        /// option's name inherits the component's own generic words), zeroing out the whole
        /// Choose. After the fix, tokens shared with the component's own name are stripped
        /// before exclude-matching, so the unrelated sentence no longer excludes anything and
        /// the primary option is selected normally.
        /// </summary>
        [Test]
        public void SelectNamespaceOptions_DoesNotExcludeOnGenericTokenSharedWithComponentName()
        {
            var component = new ModComponent
            {
                Name = "Sample Weather Overhaul Effects",
                Directions =
                    "Ignore the Patch folder unless using Other Weather Overhaul Effects for Base (untested).",
                Options =
                {
                    new Option
                    {
                        Guid = Guid.NewGuid(),
                        Name = "Sample Weather Overhaul Effects + Rain Sounds",
                        Description = "Sample Weather Overhaul Effects + Rain Sounds",
                        IsSelected = false,
                    },
                    new Option
                    {
                        Guid = Guid.NewGuid(),
                        Name = "Sample Weather Overhaul Effects",
                        Description = "The default installation for the mod.",
                        IsSelected = false,
                    },
                },
            };

            AutoInstructionGenerator.SelectNamespaceOptionsFromGuide(
                component,
                new List<ModComponent> { component });

            Assert.Multiple(() =>
            {
                Assert.That(
                    component.Options[0].IsSelected,
                    Is.False,
                    "Rain Sounds variant is not named/requested by the guide");
                Assert.That(
                    component.Options[1].IsSelected,
                    Is.True,
                    "Primary option must not be false-positive excluded by generic words "
                    + "it shares with the component's own name");
            });
        }

        /// <summary>
        /// Measured 2026-08-31 on both K2 Holo and K2 Bio: the guide names six Tweak Pack
        /// options and says to run the individual installer once per option. Mutex scoring
        /// kept only "Saedhe's Head" (score 1015) and dropped the rest.
        /// </summary>
        [Test]
        public void SelectNamespaceOptions_TslrcmTweakPack_KeepsEachNamedRun()
        {
            var component = new ModComponent
            {
                Name = "TSLRCM Tweak Pack",
                Directions =
                    "Don't use the complete installer, instead selecting the individual component "
                    + "installer—this is critical for compatibility, not just to choose specific options. "
                    + "The installer for this mod will need to be run 6 times, once to install each of "
                    + "the options we'll be using: Kaevee Removal Parts 1 & 2, Saedhe's Head, "
                    + "Kreia-Atris Dialogue Tweak, Trayus Mandalore Conversation, and Trayus Sith Lord Masks. "
                    + "Most of the other options for this mod are untested with the mod builds, but will "
                    + "likely function--I still recommend against using them, since compatibility isn't "
                    + "100% guaranteed, but you can try. The one exception is Atton at the End, which is "
                    + "completely incompatible with one of the most important mods in this build, which "
                    + "is a much more comprehensive implementation of Atton's dialogue at endgame.",
                Options =
                {
                    OptionNamed("1 - Kaevee Removal, Part 1"),
                    OptionNamed("1 - Kaevee Removal, Part 2"),
                    OptionNamed("2 - Saedhe's Head"),
                    OptionNamed("3 - Ravager Mandalore Changes"),
                    OptionNamed("4 - Atton at the End"),
                    OptionNamed("5 - Kreia-Atris Dialogue Tweak"),
                    OptionNamed("6 - Trayus Mandalore Conversation"),
                    OptionNamed("Extras - 1 - Trayus Sith Lord Masks"),
                    OptionNamed("Extras - 2 - Gand Warrior's Awareness Check"),
                },
            };

            AutoInstructionGenerator.SelectNamespaceOptionsFromGuide(
                component,
                new List<ModComponent> { component });

            Assert.Multiple(() =>
            {
                Assert.That(component.Options[0].IsSelected, Is.True, "Kaevee Removal Part 1");
                Assert.That(component.Options[1].IsSelected, Is.True, "Kaevee Removal Part 2");
                Assert.That(component.Options[2].IsSelected, Is.True, "Saedhe's Head");
                Assert.That(component.Options[3].IsSelected, Is.False, "Ravager is not one of the six");
                Assert.That(component.Options[4].IsSelected, Is.False, "Atton at the End is incompatible");
                Assert.That(component.Options[5].IsSelected, Is.True, "Kreia-Atris Dialogue Tweak");
                Assert.That(component.Options[6].IsSelected, Is.True, "Trayus Mandalore Conversation");
                Assert.That(component.Options[7].IsSelected, Is.True, "Trayus Sith Lord Masks");
                Assert.That(component.Options[8].IsSelected, Is.False, "Gand Warrior is not one of the six");
            });
        }

        [Test]
        public void SelectNamespaceOptions_RecommendsNamedOption_DropsSiblingNamespaces()
        {
            var component = new ModComponent
            {
                Name = "Example Restored Sequence",
                Directions =
                    "I recommend the \"With Additional Scene\" option for maximum restored content "
                    + "and internal consistency for the sequence.",
                Options =
                {
                    OptionNamed("INSTALL: Short Sequence (No Added Scene)"),
                    OptionNamed("INSTALL: With Additional Scene"),
                    OptionNamed("INSTALL: Minor Fixes Only"),
                },
            };

            AutoInstructionGenerator.SelectNamespaceOptionsFromGuide(
                component,
                new List<ModComponent> { component });

            Assert.Multiple(() =>
            {
                Assert.That(component.Options[0].IsSelected, Is.False, "Vanilla Sequence is the other main install");
                Assert.That(component.Options[1].IsSelected, Is.True, "Guide recommends With Additional Scene");
                Assert.That(component.Options[2].IsSelected, Is.False, "Minor Fixes Only is not recommended");
            });
        }

        [Test]
        public void SelectNamespaceOptions_RecommendUsingNamed_DropsSiblingNamespaces()
        {
            var pack = new ModComponent { Name = "Restored Content Pack", Guid = Guid.NewGuid() };
            var component = new ModComponent
            {
                Name = "Example Clan Support",
                Directions =
                    "Strongly recommend using the Editor Cut for balance and immersion's sake.",
                Options =
                {
                    new Option
                    {
                        Guid = Guid.NewGuid(),
                        Name = "Example Clan Support (Without Pack)",
                        Description = "Install this version only if you do not have Restored Content Pack installed.",
                        IsSelected = false,
                    },
                    new Option
                    {
                        Guid = Guid.NewGuid(),
                        Name = "Example Clan Support (With Pack)",
                        Description = "Install this version only if you have Restored Content Pack installed.",
                        IsSelected = false,
                    },
                    new Option
                    {
                        Guid = Guid.NewGuid(),
                        Name = "Example Clan Support - The Editor Cut",
                        Description = "Alternate lite version. Requires Restored Content Pack.",
                        IsSelected = false,
                    },
                },
            };

            AutoInstructionGenerator.SelectNamespaceOptionsFromGuide(
                component,
                new List<ModComponent> { pack, component });

            Assert.Multiple(() =>
            {
                Assert.That(component.Options[0].IsSelected, Is.False, "Without Pack is for trees that lack the pack");
                Assert.That(component.Options[1].IsSelected, Is.False, "With Pack is not the recommended cut");
                Assert.That(component.Options[2].IsSelected, Is.True, "Guide recommends using the Editor Cut");
            });
        }

        [Test]
        public void SelectNamespaceOptions_DropsAbsentOnlyOption_WhenNamedPackIsInBuild()
        {
            var pack = new ModComponent { Name = "Restored Content Pack", Guid = Guid.NewGuid() };
            var component = new ModComponent
            {
                Name = "Example Clan Support",
                Directions = "Run the installer to install the mod.",
                Options =
                {
                    new Option
                    {
                        Guid = Guid.NewGuid(),
                        Name = "Example Clan Support (Without Pack)",
                        Description = "Install this version only if you do not have Restored Content Pack installed.",
                        IsSelected = false,
                    },
                    new Option
                    {
                        Guid = Guid.NewGuid(),
                        Name = "Example Clan Support (With Pack)",
                        Description = "Install this version only if you have Restored Content Pack installed.",
                        IsSelected = false,
                    },
                },
            };

            AutoInstructionGenerator.SelectNamespaceOptionsFromGuide(
                component,
                new List<ModComponent> { pack, component });

            Assert.Multiple(() =>
            {
                Assert.That(component.Options[0].IsSelected, Is.False, "Without Pack must stay off when the pack is in the build");
                Assert.That(component.Options[1].IsSelected, Is.True, "With Pack remains when the pack is present");
            });
        }

        [Test]
        public void SelectNamespaceOptions_ThematicCompanions_OtherwiseSimplyInstallStandard()
        {
            var component = new ModComponent
            {
                Name = "Thematic KOTOR 2 Companions",
                Directions =
                    "If you would like to have Visas's class as Sith Assassin, install the "
                    + "\"Standard + Sith Assassin Visas\" option. Otherwise, simply install \"Standard.\"",
                Options =
                {
                    OptionNamed("Standard"),
                    OptionNamed("Standard + Sith Assassin Visas"),
                },
            };

            AutoInstructionGenerator.SelectNamespaceOptionsFromGuide(
                component,
                new List<ModComponent> { component });

            Assert.Multiple(() =>
            {
                Assert.That(component.Options[0].IsSelected, Is.True, "Otherwise, simply install Standard");
                Assert.That(
                    component.Options[1].IsSelected,
                    Is.False,
                    "If you would like is optional, not the default");
            });
        }

        [Test]
        public void SelectNamespaceOptions_InstallNamedMainAndApplyNamedOptional()
        {
            var glass = new ModComponent { Name = "Window Glass Reskin", Guid = Guid.NewGuid() };
            var component = new ModComponent
            {
                Name = "Example Ending Sequence",
                Directions =
                    "If you are NOT playing the game on a 4:3 aspect ratio monitor, regardless of "
                    + "what your aspect ratio is, install the 16:9 main install option. Then re-run "
                    + "the patcher and apply the Transparent Cockpit Windows - Retexture Friendly "
                    + "option, if using the Window Glass Reskin mod.",
                Options =
                {
                    OptionNamed("MAIN INSTALL - 4:3 Display"),
                    OptionNamed("MAIN INSTALL - 16:9 Display"),
                    OptionNamed("OPTION: Transparent Cockpit Windows - Reskin-Friendly"),
                    OptionNamed("OPTION: Transparent Cockpit Windows - Enhanced Reflections"),
                    OptionNamed("Convert to 16:9"),
                    OptionNamed("Convert to 4:3"),
                },
            };

            AutoInstructionGenerator.SelectNamespaceOptionsFromGuide(
                component,
                new List<ModComponent> { glass, component });

            Assert.Multiple(() =>
            {
                Assert.That(component.Options[0].IsSelected, Is.False, "4:3 main is the other aspect");
                Assert.That(component.Options[1].IsSelected, Is.True, "Guide names the 16:9 main install");
                Assert.That(component.Options[2].IsSelected, Is.True, "Guide applies the retexture/reskin cockpit option");
                Assert.That(component.Options[3].IsSelected, Is.False, "Enhanced Reflections is the other cockpit option");
                Assert.That(component.Options[4].IsSelected, Is.False, "Convert is not the main install");
                Assert.That(component.Options[5].IsSelected, Is.False, "Convert 4:3 is not requested");
            });
        }

        private static Option OptionNamed(string name)
        {
            return new Option
            {
                Guid = Guid.NewGuid(),
                Name = name,
                Description = name,
                IsSelected = false,
            };
        }
    }
}
