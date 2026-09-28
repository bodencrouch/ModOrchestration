// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;

using ModSync.Core;
using ModSync.Core.Services;
using ModSync.Core.Services.Installation;
using ModSync.Core.Services.Validation;

using NUnit.Framework;

namespace ModSync.Tests
{
    [TestFixture]
    [NonParallelizable]
    public sealed class InstallationPipelineServiceTests
    {
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "ModSync_InstallPipeline_" + Guid.NewGuid().ToString("N"));
            string gameDirectory = Path.Combine(_tempRoot, "game");
            string modDirectory = Path.Combine(_tempRoot, "mods");
            Directory.CreateDirectory(gameDirectory);
            Directory.CreateDirectory(modDirectory);
            Directory.CreateDirectory(Path.Combine(gameDirectory, "Override"));

            MainConfig.Instance = new MainConfig
            {
                destinationPath = new DirectoryInfo(gameDirectory),
                sourcePath = new DirectoryInfo(modDirectory),
                noCheckpoint = true,
            };
        }

        [TearDown]
        public void TearDown()
        {
            // SetUp enables the process-wide NoCheckpoint static; don't leak it (or the continue-on-*
            // statics some tests set) into later fixtures.
            MainConfig.Instance.noCheckpoint = false;
            MainConfig.Instance.continueInstallOnMissingSources = false;
            MainConfig.Instance.continueInstallOnModFailure = false;

            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }

        [Test]
        public void BuildPlan_EquivalentCliAndGuiRequests_HaveTheSameFingerprintAndOrder()
        {
            ModComponent first = CreateComponent("First");
            ModComponent second = CreateComponent("Second");
            second.InstallAfter.Add(first.Guid);
            var components = new List<ModComponent> { second, first };

            InstallationPlan cliPlan = InstallationPipelineService.BuildPlan(
                new InstallationPipelineRequest(components)
                {
                    PreserveInputOrder = false,
                    Frontend = InstallationFrontend.Cli,
                });
            InstallationPlan guiPlan = InstallationPipelineService.BuildPlan(
                new InstallationPipelineRequest(components)
                {
                    PreserveInputOrder = false,
                    Frontend = InstallationFrontend.GuiWizard,
                });

            Assert.Multiple(() =>
            {
                Assert.That(cliPlan.Fingerprint, Is.EqualTo(guiPlan.Fingerprint));
                Assert.That(cliPlan.SelectedComponents, Has.Count.EqualTo(2));
                Assert.That(cliPlan.SelectedComponents[0].Name, Is.EqualTo("First"));
                Assert.That(cliPlan.SelectedComponents[1].Name, Is.EqualTo("Second"));
            });
        }

        [Test]
        public void BuildPlan_PhasesFilterWithoutMutatingSelections()
        {
            ModComponent baseComponent = CreateComponent("Base");
            ModComponent widescreenComponent = CreateComponent("Wide");
            widescreenComponent.WidescreenOnly = true;
            var components = new[] { baseComponent, widescreenComponent };

            InstallationPlan basePlan = InstallationPipelineService.BuildPlan(
                new InstallationPipelineRequest(components)
                {
                    Phase = InstallationPhase.Base,
                    PreserveInputOrder = true,
                });
            InstallationPlan widePlan = InstallationPipelineService.BuildPlan(
                new InstallationPipelineRequest(components)
                {
                    Phase = InstallationPhase.Widescreen,
                    PreserveInputOrder = true,
                });

            Assert.Multiple(() =>
            {
                Assert.That(basePlan.SelectedComponents, Has.Count.EqualTo(1));
                Assert.That(basePlan.SelectedComponents[0], Is.SameAs(baseComponent));
                Assert.That(widePlan.SelectedComponents, Has.Count.EqualTo(1));
                Assert.That(widePlan.SelectedComponents[0], Is.SameAs(widescreenComponent));
                Assert.That(baseComponent.IsSelected, Is.True);
                Assert.That(widescreenComponent.IsSelected, Is.True,
                    "Running the base phase must not silently deselect the later widescreen phase.");
            });
        }

        [Test]
        public void ClassifyInputKind_UsesComponentProvenanceAndRejectsMixedSources()
        {
            ModComponent firstMarkdown = CreateComponent("Markdown one");
            firstMarkdown.SourceFormat = "markdown";
            ModComponent secondMarkdown = CreateComponent("Markdown two");
            secondMarkdown.SourceFormat = "md";
            ModComponent structured = CreateComponent("Structured");
            structured.SourceFormat = "toml";

            Assert.Multiple(() =>
            {
                Assert.That(
                    InstallationPipelineService.ClassifyInputKind(new[] { firstMarkdown, secondMarkdown }),
                    Is.EqualTo(InstallationInputKind.MarkdownGuide));
                Assert.That(
                    InstallationPipelineService.ClassifyInputKind(new[] { structured }),
                    Is.EqualTo(InstallationInputKind.Structured));
                Assert.Throws<InstallationPipelinePolicyException>(() =>
                    InstallationPipelineService.ClassifyInputKind(new[] { firstMarkdown, structured }));
            });
        }

        [Test]
        public async Task RunAsync_ValidationFailure_DoesNotWriteGameFiles()
        {
            ModComponent component = CreateComponent("Blocked");
            component.Instructions.Add(new Instruction
            {
                Action = Instruction.ActionType.Move,
                Source = new List<string> { "<<modDirectory>>/missing.txt" },
                Destination = "<<kotorDirectory>>/Override",
            });

            MainConfig.Instance.destinationPath = null;
            MainConfig.Instance.sourcePath = null;

            var options = ValidationPipelineOptions.WizardFull;
            options.DryRun = false;
            options.SkipComponentArchiveValidation = true;
            options.SkipFomodConfigurationGate = true;
            var request = new InstallationPipelineRequest(new[] { component })
            {
                ValidationOptions = options,
                RunValidation = true,
                Frontend = InstallationFrontend.Cli,
            };

            InstallationPipelineResult result = await InstallationPipelineService.RunAsync(request);

            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.ValidationResult, Is.Not.Null);
                Assert.That(result.ValidationResult.IsSuccess, Is.False);
                Assert.That(Directory.GetFiles(Path.Combine(_tempRoot, "game", "Override")), Is.Empty);
            });
        }

        [Test]
        public void ReferenceMode_RejectsDisabledCheckpointsAndContinuePolicies()
        {
            MainConfig.Instance.noCheckpoint = true;
            MainConfig.Instance.continueInstallOnMissingSources = true;
            MainConfig.Instance.continueInstallOnModFailure = true;

            var request = new InstallationPipelineRequest(new[] { CreateComponent("Reference") })
            {
                Mode = InstallationPipelineMode.Reference,
                RunValidation = true,
                PreserveInputOrder = true,
            };

            InstallationPipelinePolicyException exception = Assert.Throws<InstallationPipelinePolicyException>(
                () => InstallationPipelineService.BuildPlan(request));

            Assert.That(exception.Message, Does.Contain("checkpoint").IgnoreCase);
            Assert.That(exception.Message, Does.Contain("continue").IgnoreCase);
        }

        [Test]
        public async Task RunAsync_SkipValidation_FinishIsCompletedUnverifiedNotSuccess()
        {
            string sourcePath = Path.Combine(_tempRoot, "mods", "payload.txt");
            File.WriteAllText(sourcePath, "copied");

            ModComponent component = CreateComponent("Skip validation");
            component.Instructions.Add(new Instruction
            {
                Action = Instruction.ActionType.Copy,
                Source = new List<string> { "<<modDirectory>>/payload.txt" },
                Destination = "<<kotorDirectory>>/Override",
            });

            InstallationPipelineResult result = await InstallationPipelineService.RunAsync(
                new InstallationPipelineRequest(new[] { component })
                {
                    RunValidation = false,
                    Frontend = InstallationFrontend.Cli,
                    Mode = InstallationPipelineMode.Standard,
                    PreserveInputOrder = true,
                });

            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.ExitCode, Is.EqualTo(ModComponent.InstallExitCode.CompletedUnverified));
                Assert.That(result.Witness, Is.EqualTo(WitnessVerdict.CompletedUnverified));
                Assert.That(result.PublishedPassHolds, Is.False);
                Assert.That(
                    File.Exists(Path.Combine(_tempRoot, "game", "Override", "payload.txt")),
                    Is.True,
                    "Skip flags may finish applying files.");
            });
        }

        [Test]
        public async Task RunAsync_BestEffortSkippedMissingArchive_IsCompletedUnverifiedNotMissingSourceFiles()
        {
            // Witness plan R9 / U4.3: a best-effort run that skips a missing archive finished; it must be
            // completed-unverified, not MissingSourceFiles (which the CLI used to map to exit 0).
            File.WriteAllText(Path.Combine(_tempRoot, "mods", "payload.txt"), "copied");
            MainConfig.Instance.continueInstallOnMissingSources = true;
            MainConfig.Instance.continueInstallOnModFailure = true;

            ModComponent present = CreateCopyComponent("Present");
            ModComponent missing = CreateMissingSourceComponent("Missing");

            InstallationPipelineResult result = await InstallationPipelineService.RunAsync(
                new InstallationPipelineRequest(new[] { present, missing })
                {
                    RunValidation = false,
                    Frontend = InstallationFrontend.Cli,
                    Mode = InstallationPipelineMode.Standard,
                    PreserveInputOrder = true,
                });

            Assert.Multiple(() =>
            {
                Assert.That(result.ExitCode, Is.EqualTo(ModComponent.InstallExitCode.CompletedUnverified));
                Assert.That(result.Witness, Is.EqualTo(WitnessVerdict.CompletedUnverified));
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.InstallLoopExitCode, Is.EqualTo(ModComponent.InstallExitCode.MissingSourceFiles));
                Assert.That(result.SkippedComponentCount, Is.EqualTo(1));
                Assert.That(result.FailedComponentCount, Is.EqualTo(0));
                Assert.That(File.Exists(Path.Combine(_tempRoot, "game", "Override", "payload.txt")), Is.True);
            });
        }

        [Test]
        public async Task RunAsync_ContinueOnModFailure_KeepsComponentFailuresVisibleOnUnverifiedResult()
        {
            File.WriteAllText(Path.Combine(_tempRoot, "mods", "payload.txt"), "copied");
            MainConfig.Instance.continueInstallOnMissingSources = false;
            MainConfig.Instance.continueInstallOnModFailure = true;

            ModComponent failing = CreateMissingSourceComponent("Failing");
            ModComponent present = CreateCopyComponent("Present");

            InstallationPipelineResult result = await InstallationPipelineService.RunAsync(
                new InstallationPipelineRequest(new[] { failing, present })
                {
                    RunValidation = false,
                    Frontend = InstallationFrontend.Cli,
                    Mode = InstallationPipelineMode.Standard,
                    PreserveInputOrder = true,
                });

            Assert.Multiple(() =>
            {
                Assert.That(result.ExitCode, Is.EqualTo(ModComponent.InstallExitCode.CompletedUnverified));
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.InstallLoopExitCode, Is.EqualTo(ModComponent.InstallExitCode.CompletedWithFailures));
                Assert.That(result.FailedComponentCount, Is.EqualTo(1));
                Assert.That(File.Exists(Path.Combine(_tempRoot, "game", "Override", "payload.txt")), Is.True,
                    "--continue-on-mod-failure still installs the later component");
            });
        }

        [Test]
        public void FinalizeResult_MissingSourceWithoutContinueFlag_StaysAHardFailure()
        {
            MainConfig.Instance.continueInstallOnMissingSources = false;
            var request = new InstallationPipelineRequest(new[] { CreateComponent("Stopped") })
            {
                RunValidation = false,
                PreserveInputOrder = true,
            };
            InstallationPlan plan = InstallationPipelineService.BuildPlan(request);

            InstallationPipelineResult result = InstallationPipelineService.FinalizeResult(
                request,
                plan,
                validationResult: null,
                ModComponent.InstallExitCode.MissingSourceFiles);

            Assert.Multiple(() =>
            {
                Assert.That(result.ExitCode, Is.EqualTo(ModComponent.InstallExitCode.MissingSourceFiles));
                Assert.That(result.Witness, Is.EqualTo(WitnessVerdict.None));
                Assert.That(result.Succeeded, Is.False);
            });
        }

        [Test]
        public void Result_PublishedPassFailedAfterSuccessShapedBatch_IsNotSucceeded()
        {
            InstallationPlan plan = InstallationPipelineService.BuildPlan(
                new InstallationPipelineRequest(new[] { CreateComponent("Witness") })
                {
                    PreserveInputOrder = true,
                });

            var result = new InstallationPipelineResult(
                plan,
                validationResult: null,
                ModComponent.InstallExitCode.Success,
                WitnessVerdict.PublishedPassFailed);

            Assert.Multiple(() =>
            {
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.PublishedPassHolds, Is.False);
                Assert.That(result.Witness, Is.EqualTo(WitnessVerdict.PublishedPassFailed));
            });
        }

        [Test]
        public async Task RunAsync_StandardAndReference_ExposeTheSameWitnessFields()
        {
            string sourcePath = Path.Combine(_tempRoot, "mods", "payload.txt");
            File.WriteAllText(sourcePath, "copied");
            MainConfig.Instance.noCheckpoint = false;

            ModComponent standardComponent = CreateCopyComponent("Standard");
            ModComponent referenceComponent = CreateCopyComponent("Reference");

            InstallationPipelineResult standard = await InstallationPipelineService.RunAsync(
                new InstallationPipelineRequest(new[] { standardComponent })
                {
                    RunValidation = false,
                    Mode = InstallationPipelineMode.Standard,
                    PreserveInputOrder = true,
                });
            InstallationPipelineResult reference = await InstallationPipelineService.RunAsync(
                new InstallationPipelineRequest(new[] { referenceComponent })
                {
                    RunValidation = false,
                    Mode = InstallationPipelineMode.Standard,
                    PreserveInputOrder = true,
                });

            Assert.Multiple(() =>
            {
                Assert.That(standard.Witness, Is.EqualTo(WitnessVerdict.CompletedUnverified));
                Assert.That(reference.Witness, Is.EqualTo(WitnessVerdict.CompletedUnverified));
                Assert.That(standard.Succeeded, Is.False);
                Assert.That(reference.Succeeded, Is.False);
            });
        }

        [Test]
        public async Task FailClosedExecution_RuntimeFailure_RestoresThePreComponentSnapshot()
        {
            MainConfig.Instance.noCheckpoint = false;
            MainConfig.Instance.continueInstallOnMissingSources = false;
            MainConfig.Instance.continueInstallOnModFailure = false;
            string sourcePath = Path.Combine(_tempRoot, "mods", "first.txt");
            string destinationPath = Path.Combine(_tempRoot, "game", "Override", "first.txt");
            File.WriteAllText(sourcePath, "must be rolled back");

            ModComponent component = CreateComponent("Fails after write");
            component.Instructions.Add(new Instruction
            {
                Action = Instruction.ActionType.Copy,
                Source = new List<string> { sourcePath },
                Destination = "<<kotorDirectory>>/Override",
            });
            component.Instructions.Add(new Instruction
            {
                Action = Instruction.ActionType.Copy,
                Source = new List<string> { Path.Combine(_tempRoot, "mods", "missing.txt") },
                Destination = "<<kotorDirectory>>/Override",
            });

            ModComponent.InstallExitCode exitCode = await InstallationService.InstallAllSelectedComponentsAsync(
                new[] { component },
                preserveInputOrder: true,
                failClosed: true);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.Not.EqualTo(ModComponent.InstallExitCode.Success));
                Assert.That(File.Exists(destinationPath), Is.False, "Runtime failure must restore the game tree before the failed component.");
                Assert.That(File.ReadAllText(sourcePath), Is.EqualTo("must be rolled back"));
            });
        }

        private static ModComponent CreateCopyComponent(string name)
        {
            ModComponent component = CreateComponent(name);
            component.Instructions.Add(new Instruction
            {
                Action = Instruction.ActionType.Copy,
                Source = new List<string> { "<<modDirectory>>/payload.txt" },
                Destination = "<<kotorDirectory>>/Override",
            });
            return component;
        }

        private static ModComponent CreateMissingSourceComponent(string name)
        {
            ModComponent component = CreateComponent(name);
            component.Instructions.Add(new Instruction
            {
                Action = Instruction.ActionType.Copy,
                Source = new List<string> { "<<modDirectory>>/does-not-exist.txt" },
                Destination = "<<kotorDirectory>>/Override",
            });
            return component;
        }

        private static ModComponent CreateComponent(string name) => new ModComponent
        {
            Guid = Guid.NewGuid(),
            Name = name,
            IsSelected = true,
            Instructions = new ObservableCollection<Instruction>(),
        };
    }
}
