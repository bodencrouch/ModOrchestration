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
    }
}
