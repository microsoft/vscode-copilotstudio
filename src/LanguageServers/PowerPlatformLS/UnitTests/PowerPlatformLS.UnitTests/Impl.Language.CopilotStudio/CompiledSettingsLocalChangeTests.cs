namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Yaml;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.FileProviders;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.DependencyInjection;
    using Moq;
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class CompiledSettingsLocalChangeTests
    {
        private const string BotSchemaName = "crf9a_cliagent_S4i9ES";
        private const string WorkspaceRoot = "c:/agent";
        private static readonly AgentFilePath SettingsPath = new AgentFilePath("settings.mcs.yml");

        private const string LosslessInstruction = "Help the user.";
        private const string NonBreakingSpaceLastLine = "Alpha\n\u00A0";
        private const string NonBreakingSpaceThirdLine = "Alpha\nBravo\n\u00A0Charlie";
        private const string LoneCarriageReturn = "You are an agent\rSecond line";
        private const string CustomerRepro = "You are an \u00A0\n \u200B\u200C\u200B\u200C\u200B\u200C\u00A0\u200B\u200C\u200B\u200C\u200B\u200C\u200B\u200C\n\u00A0\u200B\u200C";

        [Theory]
        [InlineData("lossless instruction, no diagnostics", LosslessInstruction, false)]
        [InlineData("lossless instruction with diagnostics", LosslessInstruction, true)]
        [InlineData("non-breaking space is the whole last line", NonBreakingSpaceLastLine, false)]
        [InlineData("non-breaking space starts the third line", NonBreakingSpaceThirdLine, false)]
        [InlineData("lone CR line break", LoneCarriageReturn, false)]
        [InlineData("customer repro from botdefinition.json", CustomerRepro, false)]
        [InlineData("non-breaking space last line plus diagnostics", NonBreakingSpaceLastLine, true)]
        [InlineData("non-breaking space third line plus diagnostics", NonBreakingSpaceThirdLine, true)]
        [InlineData("lone CR line break plus diagnostics", LoneCarriageReturn, true)]
        [InlineData("customer repro plus diagnostics", CustomerRepro, true)]
        public async Task GetLocalChanges_OnCompiledWorkspace_ImmediatelyAfterClone_ReportsNoSettingsChange(
            string scenario,
            string instructionValue,
            bool withDiagnostics)
        {
            var workspace = CompileProjectedWorkspace(CreateAgentSettingsJson(instructionValue, withDiagnostics));

            var changes = await GetLocalChangesAsync(workspace);
            var settingsChanges = changes.Where(change => change.SchemaName == "entity").ToArray();

            Assert.True(settingsChanges.Length == 0, BuildFailureReport(scenario, workspace, changes));
        }

        [Fact]
        public void CompiledEntity_WithLossyInstructionAndDiagnostics_DefeatsBothProjectionAndStructuralComparison()
        {
            var workspace = CompileProjectedWorkspace(CreateAgentSettingsJson(NonBreakingSpaceLastLine, withDiagnostics: true));

            var compiledEntity = CompiledSettings(workspace);
            var cloudEntity = CloudSettings(workspace);

            Assert.NotEmpty(CollectDiagnostics(compiledEntity));
            Assert.Empty(CollectDiagnostics(cloudEntity));

            Assert.False(
                compiledEntity.Equals(cloudEntity, NodeComparison.Structural),
                "Expected the compiled entity to differ structurally from the cloud cache. If this now passes, the "
                + "ObjectModel behaviour changed and the settings projection fallback may no longer be needed.");

            Assert.False(
                string.Equals(ProjectSettingsFile(compiledEntity), ProjectSettingsFile(cloudEntity), StringComparison.Ordinal),
                "Expected the cloud entity to project to different YAML than the compiled entity because the block "
                + "scalar round-trip is lossy. If this now passes, pick an instruction value that still round-trips "
                + "lossily so the fallback stays covered.");

            Assert.True(
                RoundTrip(compiledEntity).Equals(RoundTrip(cloudEntity), NodeComparison.Structural),
                "Expected both sides to agree once each is round-tripped through the settings projection.");
        }

        [Theory]
        [InlineData("model series edited", "series: GPT56Reasoning", "series: Sonnet46")]
        [InlineData("display name edited", "displayName: CLI Agent", "displayName: Renamed Agent")]
        [InlineData("greeting text edited", "greetingText: Hello!", "greetingText: Welcome!")]
        public async Task GetLocalChanges_OnCompiledWorkspace_RealEditAlongsideLossyInstructionAndDiagnostics_ReportsSettingsChange(
            string scenario,
            string find,
            string replace)
        {
            var workspace = CompileProjectedWorkspace(
                CreateAgentSettingsJson(NonBreakingSpaceLastLine, withDiagnostics: true),
                settings => Replace(settings, find, replace));

            Assert.NotEmpty(CollectDiagnostics(CompiledSettings(workspace)));

            var changes = await GetLocalChangesAsync(workspace);
            var settingsChange = Assert.Single(changes.Where(change => change.SchemaName == "entity"));

            Assert.True(
                settingsChange.ChangeType == ChangeType.Update,
                $"Scenario '{scenario}': a persisted edit to settings.mcs.yml was not reported as an update. "
                + $"Changes: {string.Join(", ", changes.Select(change => $"{change.ChangeType} {change.SchemaName} -> {change.Uri}"))}");
            Assert.Equal(SettingsPath.ToString(), settingsChange.Uri);
        }

        private sealed record CompiledWorkspace(DefinitionBase Compiled, DefinitionBase Cloud, string ProjectedSettings);

        private static BotEntity CompiledSettings(CompiledWorkspace workspace)
            => ((BotDefinition)workspace.Compiled).Entity!.WithOnlySettingsYamlProperties();

        private static BotEntity CloudSettings(CompiledWorkspace workspace)
            => ((BotDefinition)workspace.Cloud).Entity!.WithOnlySettingsYamlProperties();

        private static BotEntity RoundTrip(BotEntity entity)
            => CodeSerializer.Deserialize<BotEntity>(ProjectSettingsFile(entity))!.WithOnlySettingsYamlProperties();

        private static async Task<ImmutableArray<Change>> GetLocalChangesAsync(CompiledWorkspace workspace)
        {
            using var fileAccessorFactory = new InMemoryFileAccessorFactory();
            var workspaceFolder = new DirectoryPath(WorkspaceRoot + "/");
            var fileAccessor = fileAccessorFactory.Create(workspaceFolder);
            WorkspaceSynchronizer.WriteCloudCache(fileAccessor, workspace.Cloud);
            await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-1", CancellationToken.None);
            await fileAccessor.WriteAsync(SettingsPath, workspace.ProjectedSettings, CancellationToken.None);

            var synchronizer = new WorkspaceSynchronizer(
                new SyncMcsFileParser(LspProjectorService.Instance),
                fileAccessorFactory,
                Mock.Of<IIslandControlPlaneService>(),
                Mock.Of<ISyncProgress>(),
                new LspComponentPathResolver());

            var (_, changes) = await synchronizer.GetLocalChangesAsync(
                workspaceFolder,
                workspace.Compiled,
                CancellationToken.None);

            return changes;
        }

        private static CompiledWorkspace CompileProjectedWorkspace(
            string agentSettingsJson,
            Func<string, string>? editSettingsFile = null)
        {
            var cloudDefinition = CreateCloudDefinition(agentSettingsJson);
            var projectedSettings = ProjectSettingsFile(((BotDefinition)cloudDefinition).Entity!);
            if (editSettingsFile != null)
            {
                projectedSettings = editSettingsFile(projectedSettings);
            }

            var (compiler, language) = BuildCompiler();
            var documents = new Dictionary<FilePath, LspDocument>();
            AddDocument(documents, language, "settings.mcs.yml", projectedSettings);

            var compilation = compiler.Compile(documents, new DirectoryPath(WorkspaceRoot));
            Assert.NotNull(compilation.Model);

            return new CompiledWorkspace(WithCloudBotId(compilation.Model!, cloudDefinition), cloudDefinition, projectedSettings);
        }

        private static DefinitionBase WithCloudBotId(DefinitionBase compiled, DefinitionBase cloud)
        {
            if (compiled is not BotDefinition compiledBot || compiledBot.Entity == null)
            {
                return compiled;
            }

            var builder = compiledBot.Entity.ToBuilder();
            builder.CdsBotId = ((BotDefinition)cloud).Entity!.CdsBotId;
            return compiledBot.WithEntity(builder.Build());
        }

        private static string ProjectSettingsFile(BotEntity entity)
        {
            using var writer = new StringWriter();
            using (YamlSerializationContext.UseStandardSerializationContextIfNotDefined(throwOnInvalidYaml: false))
            {
                YamlSerializer.SerializeWithoutKind(writer, entity.WithOnlySettingsYamlProperties());
            }

            return writer.ToString();
        }

        private static string Replace(string settings, string find, string replace)
        {
            Assert.Contains(find, settings, StringComparison.Ordinal);
            return settings.Replace(find, replace, StringComparison.Ordinal);
        }

        private static IReadOnlyList<string> CollectDiagnostics(BotElement element)
            => element.DescendantsAndSelf()
                .SelectMany(node => node.Diagnostics.Select(diagnostic => $"{node.GetType().Name}: {diagnostic}"))
                .ToArray();

        private static string BuildFailureReport(string scenario, CompiledWorkspace workspace, ImmutableArray<Change> changes)
            => $"""
            Scenario '{scenario}': a freshly cloned workspace reported a settings.mcs.yml change before the user edited anything.
            Changes             : {string.Join(", ", changes.Select(change => $"{change.ChangeType} {change.SchemaName} -> {change.Uri}"))}
            Compiled diagnostics: {string.Join(" | ", CollectDiagnostics(CompiledSettings(workspace)))}
            Cloud diagnostics   : {string.Join(" | ", CollectDiagnostics(CloudSettings(workspace)))}
            Cloud projection    : {Escape(ProjectSettingsFile(CloudSettings(workspace)))}
            Compiled projection : {Escape(ProjectSettingsFile(CompiledSettings(workspace)))}
            """;

        private static string Escape(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                builder.Append(character switch
                {
                    '\r' => "\\r",
                    '\n' => "\\n",
                    '\t' => "\\t",
                    ' ' => "\u00B7",
                    _ when character < 0x20 || character > 0x7E => $"\\u{(int)character:X4}",
                    _ => character.ToString(),
                });
            }

            return builder.ToString();
        }

        private static string CreateAgentSettingsJson(string instructionValue, bool withDiagnostics)
        {
            var segments = withDiagnostics
                ? $$"""{ "$kind": "StaticSegment", "value": {{JsonSerializer.Serialize(instructionValue)}} }, { "$kind": "StaticSegment" }"""
                : $$"""{ "$kind": "StaticSegment", "value": {{JsonSerializer.Serialize(instructionValue)}} }""";

            return $$"""
            {
              "$kind": "AgentSettings",
              "model": { "$kind": "ModelConfig", "series": "GPT56Reasoning" },
              "instructions": {
                "$kind": "Instructions",
                "segments": [ {{segments}} ]
              },
              "greetingText": "Hello!",
              "web": { "$kind": "WebSettings", "enableWebSearch": false }
            }
            """;
        }

        private static DefinitionBase CreateCloudDefinition(string agentSettingsJson)
        {
            var json = $$"""
            {
              "$kind": "BotDefinition",
              "entity": {
                "$kind": "BotEntity",
                "cdsBotId": "22222222-2222-2222-2222-222222222222",
                "schemaName": "{{BotSchemaName}}",
                "displayName": "CLI Agent",
                "accessControlPolicy": "GroupMembership",
                "authenticationMode": "Integrated",
                "authenticationTrigger": "Always",
                "template": "cliagent-1.0.0",
                "language": 1033,
                "configuration": {
                  "$kind": "BotConfiguration",
                  "channels": [ { "$kind": "ChannelDefinition", "id": "MsTeams", "channelId": "MsTeams" } ],
                  "publishOnCreate": false,
                  "publishOnImport": true,
                  "isLightweightBot": false,
                  "recognizer": { "$kind": "CLICopilotRecognizer" },
                  "agentSettings": {{agentSettingsJson}},
                  "deferredProvisioning": false
                }
              }
            }
            """;

            using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
            {
                return JsonSerializer.Deserialize<DefinitionBase>(json, ElementSerializer.CreateOptions())!;
            }
        }

        private static (IWorkspaceCompiler<DefinitionBase> compiler, ILanguageAbstraction language) BuildCompiler()
        {
            var services = new ServiceCollection();
            services.Install(new McsLspModule());
            services.AddSingleton(Mock.Of<ILspLogger>());
            services.AddSingleton(Mock.Of<IClientInformation>());
            services.AddSingleton(Mock.Of<ILspServices>());
            services.AddSingleton(Mock.Of<ILspTransport>());

            var mockFileProvider = new Mock<IClientWorkspaceFileProvider>();
            mockFileProvider
                .Setup(x => x.GetDirectoryContents(It.IsAny<DirectoryPath>()))
                .Returns((DirectoryPath path) => new PhysicalFileProvider(Directory.GetCurrentDirectory()).GetDirectoryContents(string.Empty));
            services.AddSingleton(mockFileProvider.Object);

            var serviceProvider = services.BuildServiceProvider();
            return (serviceProvider.GetRequiredService<IWorkspaceCompiler<DefinitionBase>>(),
                    serviceProvider.GetRequiredService<ILanguageAbstraction>());
        }

        private static void AddDocument(Dictionary<FilePath, LspDocument> documents, ILanguageAbstraction language, string relativePath, string text)
        {
            var path = new FilePath(WorkspaceRoot + "/" + relativePath);
            var document = language.CreateDocument(path, text, CultureInfo.InvariantCulture, new DirectoryPath(WorkspaceRoot));
            documents.Add(path, document);
        }
    }
}
