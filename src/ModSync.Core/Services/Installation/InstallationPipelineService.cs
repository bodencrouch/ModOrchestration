// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using JetBrains.Annotations;

using ModSync.Core.Installation;
using ModSync.Core.Services.Checkpoints;
using ModSync.Core.Services.Validation;
using Newtonsoft.Json;

namespace ModSync.Core.Services.Installation
{
    /// <summary>
    /// Identifies the adapter invoking the shared pipeline. The frontend is diagnostic metadata only and
    /// deliberately does not participate in plan construction or fingerprinting.
    /// </summary>
    public enum InstallationFrontend
    {
        Unknown,
        Cli,
        GuiWizard,
        GuiLegacy,
        GuiWidescreen,
        GuiTool,
    }

    public enum InstallationPipelineMode
    {
        Standard,
        Reference,
    }

    public enum InstallationInputKind
    {
        Structured,
        MarkdownGuide,
    }

    public enum InstallationPhase
    {
        AllSelected,
        Base,
        Widescreen,
    }

    /// <summary>
    /// Complete request accepted by every CLI and GUI installation adapter.
    /// </summary>
    public sealed class InstallationPipelineRequest
    {
        public InstallationPipelineRequest([NotNull][ItemNotNull] IEnumerable<ModComponent> components)
        {
            if (components is null)
            {
                throw new ArgumentNullException(nameof(components));
            }

            Components = new ReadOnlyCollection<ModComponent>(components.ToList());
        }

        [NotNull]
        [ItemNotNull]
        public IReadOnlyList<ModComponent> Components { get; }

        public InstallationFrontend Frontend { get; set; }

        public InstallationPipelineMode Mode { get; set; }

        public InstallationInputKind InputKind { get; set; }

        public InstallationPhase Phase { get; set; } = InstallationPhase.AllSelected;

        public bool RunValidation { get; set; } = true;

        public bool PreserveInputOrder { get; set; }

        [CanBeNull]
        public ValidationPipelineOptions ValidationOptions { get; set; }

        [CanBeNull]
        public InstallationValidationPipeline.ValidationProgressHandler ValidationProgress { get; set; }

        [CanBeNull]
        public Action<int, int, string> InstallationProgress { get; set; }

        [CanBeNull]
        public string ProfileOverride { get; set; }

        public bool? ManagedDeploymentOverride { get; set; }

        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;
    }

    /// <summary>
    /// Immutable ordered plan used by validation, fingerprint reporting, and execution.
    /// </summary>
    public sealed class InstallationPlan
    {
        internal InstallationPlan(
            [NotNull][ItemNotNull] IReadOnlyList<ModComponent> orderedComponents,
            [NotNull][ItemNotNull] IReadOnlyList<ModComponent> selectedComponents,
            [NotNull] string fingerprint)
        {
            OrderedComponents = orderedComponents;
            SelectedComponents = selectedComponents;
            Fingerprint = fingerprint;
        }

        [NotNull]
        [ItemNotNull]
        public IReadOnlyList<ModComponent> OrderedComponents { get; }

        [NotNull]
        [ItemNotNull]
        public IReadOnlyList<ModComponent> SelectedComponents { get; }

        [NotNull]
        public string Fingerprint { get; }
    }

    /// <summary>
    /// Two-layer install witness. Process success is never this value.
    /// </summary>
    public enum WitnessVerdict
    {
        None,
        MidRunSubsetPass,
        MidRunSubsetFail,
        PublishedPass,
        PublishedPassFailed,
        CompletedUnverified,
        PassWithPackingDissent,
    }

    public sealed class InstallationPipelineResult
    {
        internal InstallationPipelineResult(
            [NotNull] InstallationPlan plan,
            [CanBeNull] ValidationPipelineResult validationResult,
            ModComponent.InstallExitCode exitCode,
            WitnessVerdict witness = WitnessVerdict.None)
        {
            Plan = plan;
            ValidationResult = validationResult;
            ExitCode = exitCode;
            Witness = witness;
        }

        [NotNull]
        public InstallationPlan Plan { get; }

        [CanBeNull]
        public ValidationPipelineResult ValidationResult { get; }

        public ModComponent.InstallExitCode ExitCode { get; }

        public WitnessVerdict Witness { get; }

        public bool PublishedPassHolds =>
            Witness == WitnessVerdict.PublishedPass
            || Witness == WitnessVerdict.PassWithPackingDissent;

        public bool Succeeded =>
            PublishedPassHolds
            || (Witness == WitnessVerdict.None
                && (ValidationResult == null || ValidationResult.IsSuccess)
                && ExitCode == ModComponent.InstallExitCode.Success);
    }

    public sealed class InstallationPipelinePolicyException : InvalidOperationException
    {
        public InstallationPipelinePolicyException([NotNull] string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Single application-level validation and installation orchestrator. Frontends create requests and
    /// render results; they do not call validation or installation services independently.
    /// </summary>
    public static class InstallationPipelineService
    {
        public static InstallationInputKind ClassifyInputKind(
            [NotNull][ItemNotNull] IEnumerable<ModComponent> components)
        {
            if (components is null)
            {
                throw new ArgumentNullException(nameof(components));
            }

            List<ModComponent> materialized = components.ToList();
            bool anyMarkdown = materialized.Any(component =>
                ModComponentSerializationService.MarkdownFormatAliases.Contains(
                    component.SourceFormat ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase));
            if (!anyMarkdown)
            {
                return InstallationInputKind.Structured;
            }

            if (materialized.Any(component =>
                    !ModComponentSerializationService.MarkdownFormatAliases.Contains(
                        component.SourceFormat ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase)))
            {
                throw new InstallationPipelinePolicyException(
                    "Cannot install a mixed set of Markdown-guide and structured/unknown-provenance components. "
                    + "Load one canonical instruction source before installing.");
            }

            return InstallationInputKind.MarkdownGuide;
        }

        [NotNull]
        public static InstallationPlan BuildPlan([NotNull] InstallationPipelineRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            ValidatePolicy(request);

            if (request.Components.Count == 0)
            {
                throw new InstallationPipelinePolicyException("Installation requires at least one component.");
            }

            List<ModComponent> phaseComponents = SelectPhaseComponents(request);
            List<ModComponent> ordered = request.PreserveInputOrder
                ? phaseComponents
                : InstallCoordinator.GetOrderedInstallList(phaseComponents);
            List<ModComponent> selected = ordered.Where(component => component.IsSelected).ToList();
            if (selected.Count == 0)
            {
                throw new InstallationPipelinePolicyException("Installation requires at least one selected component.");
            }

            string fingerprint = ComputeFingerprint(selected);
            return new InstallationPlan(
                new ReadOnlyCollection<ModComponent>(ordered),
                new ReadOnlyCollection<ModComponent>(selected),
                fingerprint);
        }

        [NotNull]
        public static async Task<InstallationPipelineResult> RunAsync(
            [NotNull] InstallationPipelineRequest request)
        {
            ValidatePolicy(request);
            await PrepareAsync(request).ConfigureAwait(false);
            InstallationPlan plan = BuildPlan(request);
            request.CancellationToken.ThrowIfCancellationRequested();

            ValidationPipelineResult validationResult = null;
            if (request.RunValidation)
            {
                ValidationPipelineOptions options = request.ValidationOptions
                    ?? ValidationPipelineOptions.WizardFull;
                if (options.MainConfig == null)
                {
                    options.MainConfig = MainConfig.Instance;
                }
                options.CancellationToken = request.CancellationToken;
                validationResult = await InstallationValidationPipeline.RunAsync(
                    plan.OrderedComponents,
                    options,
                    request.ValidationProgress).ConfigureAwait(false);
                if (!validationResult.IsSuccess)
                {
                    return new InstallationPipelineResult(
                        plan,
                        validationResult,
                        ModComponent.InstallExitCode.InvalidOperation);
                }
            }

            ModComponent.InstallExitCode exitCode = await InstallationService.InstallAllSelectedComponentsAsync(
                plan.OrderedComponents,
                request.InstallationProgress,
                request.CancellationToken,
                request.ProfileOverride,
                request.ManagedDeploymentOverride,
                preserveInputOrder: true,
                failClosed: request.Mode == InstallationPipelineMode.Reference).ConfigureAwait(false);

            return FinalizeResult(request, plan, validationResult, exitCode);
        }

        [NotNull]
        private static InstallationPipelineResult FinalizeResult(
            [NotNull] InstallationPipelineRequest request,
            [NotNull] InstallationPlan plan,
            [CanBeNull] ValidationPipelineResult validationResult,
            ModComponent.InstallExitCode exitCode)
        {
            if (IsUnverifiedFinish(request)
                && (exitCode == ModComponent.InstallExitCode.Success
                    || exitCode == ModComponent.InstallExitCode.CompletedWithFailures))
            {
                return new InstallationPipelineResult(
                    plan,
                    validationResult,
                    ModComponent.InstallExitCode.CompletedUnverified,
                    WitnessVerdict.CompletedUnverified);
            }

            return new InstallationPipelineResult(plan, validationResult, exitCode);
        }

        private static bool IsUnverifiedFinish([NotNull] InstallationPipelineRequest request)
        {
            MainConfig config = MainConfig.Instance;
            return !request.RunValidation
                || MainConfig.NoCheckpoint
                || (config != null
                    && (config.continueInstallOnMissingSources || config.continueInstallOnModFailure));
        }

        private static async Task PrepareAsync([NotNull] InstallationPipelineRequest request)
        {
            if (request.InputKind != InstallationInputKind.MarkdownGuide)
            {
                return;
            }

            MainConfig config = MainConfig.Instance;
            if (config == null || MainConfig.SourcePath == null)
            {
                throw new InstallationPipelinePolicyException(
                    "Reference installation requires a configured mod source directory.");
            }

            List<ModComponent> phaseComponents = SelectPhaseComponents(request);
            int generated = await ComponentProcessingService
                .TryGenerateFromLocalArchivesAsync(phaseComponents)
                .ConfigureAwait(false);
            await Logger.LogAsync(
                $"Markdown guide preparation generated local-archive instructions for {generated} component(s).")
                .ConfigureAwait(false);

            DirectMarkdownInstallPreflightResult preflight = DirectMarkdownInstallPreflight.Apply(
                phaseComponents);
            await Logger.LogAsync(
                $"Markdown guide preparation: drafted={preflight.DraftedFromProse}, "
                + $"4GB skipped={preflight.SkippedFourGb}, widescreen skipped={preflight.SkippedWidescreen}.")
                .ConfigureAwait(false);
            if (!preflight.IsReady)
            {
                HashSet<string> alreadyCompleted = CompletedComponentNamesFromSession(
                    request.Components);
                List<string> blocking = preflight.UnresolvedComponents
                    .Where(name => !alreadyCompleted.Contains(name))
                    .ToList();
                if (alreadyCompleted.Count > 0
                    && blocking.Count < preflight.UnresolvedComponents.Count)
                {
                    await Logger.LogAsync(
                        "Markdown guide preparation: "
                        + $"{preflight.UnresolvedComponents.Count - blocking.Count} unresolved component(s) "
                        + "already completed in the install session will be skipped.")
                        .ConfigureAwait(false);
                }

                if (blocking.Count > 0)
                {
                    throw new InstallationPipelinePolicyException(
                        $"Markdown guide preparation found {blocking.Count} selected component(s) "
                        + "without executable actions: "
                        + string.Join(", ", blocking.Take(20)));
                }
            }
        }

        [NotNull]
        private static HashSet<string> CompletedComponentNamesFromSession(
            [NotNull] IReadOnlyList<ModComponent> components)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DirectoryInfo destination = MainConfig.DestinationPath;
            if (destination == null || components.Count == 0)
            {
                return names;
            }

            string sessionPath = Path.Combine(
                CheckpointPaths.GetRoot(destination.FullName),
                "install_session.json");
            if (!File.Exists(sessionPath))
            {
                return names;
            }

            string json = File.ReadAllText(sessionPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (json.Length > 0 && json[0] == '\uFEFF')
            {
                json = json.Substring(1);
            }

            InstallSessionState state = JsonConvert.DeserializeObject<InstallSessionState>(json);
            if (state?.ComponentOrder == null || state.Components == null
                || state.ComponentOrder.Count != components.Count)
            {
                return names;
            }

            for (int i = 0; i < components.Count; i++)
            {
                if (state.Components.TryGetValue(state.ComponentOrder[i], out ComponentSessionEntry entry)
                    && entry.State == ModComponent.ComponentInstallState.Completed
                    && !string.IsNullOrWhiteSpace(components[i].Name))
                {
                    _ = names.Add(components[i].Name);
                }
            }

            return names;
        }

        [NotNull]
        [ItemNotNull]
        private static List<ModComponent> SelectPhaseComponents(
            [NotNull] InstallationPipelineRequest request)
        {
            IEnumerable<ModComponent> components = request.Components;
            switch (request.Phase)
            {
                case InstallationPhase.Base:
                    components = components.Where(component => !component.WidescreenOnly);
                    break;
                case InstallationPhase.Widescreen:
                    components = components.Where(component => component.WidescreenOnly);
                    break;
            }

            return components.ToList();
        }

        private static void ValidatePolicy([NotNull] InstallationPipelineRequest request)
        {
            if (request.Mode != InstallationPipelineMode.Reference)
            {
                return;
            }

            var violations = new List<string>();
            MainConfig config = MainConfig.Instance;
            if (!request.RunValidation)
            {
                violations.Add("validation cannot be skipped");
            }

            if (!request.PreserveInputOrder)
            {
                violations.Add("guide input order must be preserved");
            }

            if (config == null || MainConfig.NoCheckpoint)
            {
                violations.Add("checkpoint protection must be enabled");
            }

            if (config != null
                && (config.continueInstallOnMissingSources || config.continueInstallOnModFailure))
            {
                violations.Add("continue-on-error policies must be disabled");
            }

            if (violations.Count > 0)
            {
                throw new InstallationPipelinePolicyException(
                    "Reference installation policy rejected the request: " + string.Join("; ", violations) + ".");
            }
        }

        [NotNull]
        private static string ComputeFingerprint([NotNull][ItemNotNull] IReadOnlyList<ModComponent> components)
        {
            var canonical = new StringBuilder();
            for (int componentIndex = 0; componentIndex < components.Count; componentIndex++)
            {
                ModComponent component = components[componentIndex];
                canonical.Append(componentIndex).Append('|')
                    .Append(component.Guid.ToString("D")).Append('|')
                    .Append(component.Name ?? string.Empty).Append('|')
                    .Append(component.WidescreenOnly ? '1' : '0').AppendLine();

                AppendInstructions(canonical, component.Instructions, "root");
                for (int optionIndex = 0; optionIndex < component.Options.Count; optionIndex++)
                {
                    AppendInstructions(
                        canonical,
                        component.Options[optionIndex].Instructions,
                        "option:" + optionIndex);
                }
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static void AppendInstructions(
            [NotNull] StringBuilder canonical,
            [NotNull][ItemNotNull] IEnumerable<Instruction> instructions,
            [NotNull] string scope)
        {
            int instructionIndex = 0;
            foreach (Instruction instruction in instructions)
            {
                canonical.Append(scope).Append('|').Append(instructionIndex).Append('|')
                    .Append(instruction.Action).Append('|')
                    .Append(string.Join("\u001f", instruction.Source ?? new List<string>())).Append('|')
                    .Append(instruction.Destination ?? string.Empty).AppendLine();
                instructionIndex++;
            }
        }
    }
}
