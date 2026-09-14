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
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class CompiledTopicLocalChangeTests
    {
        private const string TopicSchemaName = "cr160_hrAssistant.topic.MasterQuestion";
        private const string WorkspaceRoot = "c:/agent";

        private const string ClassicBotDefinitionJson = """
        {
          "$kind": "BotDefinition",
          "entity": {
            "$kind": "BotEntity",
            "schemaName": "cr160_hrAssistant",
            "template": "default-1.0.0",
            "configuration": { "$kind": "BotConfiguration" }
          }
        }
        """;

        private const string TriggerMissingIntentDialog =
            "kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  actions:\n    - kind: SendActivity\n      id: sendOne\n      activity: Hello\n";

        private const string CompleteTriggerDialog =
            "kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n  intent:\n    triggerQueries:\n      - hello\n  actions:\n    - kind: SendActivity\n      id: sendOne\n      activity: Hello\n";

        private const string CustomerDialog =
            "kind: AdaptiveDialog\r\n activity: \"{Topic.answer.text}\"\r\n\r\ninputType: {}\r\noutputType: {}";

        private const string CustomerExtras =
            "\"managedProperties\": { \"$kind\": \"ManagedProperties\", \"isCustomizable\": false, \"solutionId\": \"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\" },"
            + "\"auditInfo\": { \"$kind\": \"AuditInfo\", \"createdTimeUtc\": \"2025-12-10T16:04:02Z\", \"modifiedTimeUtc\": \"2025-12-11T17:15:42Z\" },"
            + "\"shareContext\": { \"$kind\": \"ContentShareContext\" },"
            + "\"state\": \"Inactive\", \"status\": \"Inactive\","
            + "\"publisherUniqueName\": \"DefaultPublisherorg6ce34330\",";

        [Theory]
        [InlineData("customer component verbatim, compiled path", CustomerDialog, CustomerExtras)]
        [InlineData("customer dialog without the metadata", CustomerDialog, "")]
        [InlineData("customer metadata on a well-formed dialog", CompleteTriggerDialog, CustomerExtras)]
        public async Task GetLocalChanges_OnCompiledWorkspace_CustomerTopicMetadata_ReportsNoChange(
            string scenario,
            string dialogYaml,
            string extraJson)
            => await AssertNoCompiledChangeAsync(scenario, dialogYaml, extraJson);

        [Fact]
        public void CompiledTopic_CarryingValidationDiagnostics_IsStructurallyUnequalButSerializesIdentically()
        {
            var (compiled, cloud) = CompileProjectedTopic(TriggerMissingIntentDialog);

            var compiledRoot = compiled.RootElement!;
            var cloudRoot = cloud.RootElement!;

            Assert.False(
                compiledRoot.Equals(cloudRoot, NodeComparison.Structural),
                "Expected the compiled topic to differ structurally from the cloud cache; if this now passes the "
                + "ObjectModel behaviour changed and the serialization fallback may no longer be needed.");

            Assert.Equal(Serialize(cloudRoot), Serialize(compiledRoot));
        }

        [Theory]
        [InlineData("trigger missing its intent (compiles with a PropertyError)", TriggerMissingIntentDialog)]
        [InlineData("complete trigger (no diagnostics)", CompleteTriggerDialog)]
        public async Task GetLocalChanges_OnCompiledWorkspace_ImmediatelyAfterClone_ReportsNoChange(
            string scenario,
            string dialogYaml)
            => await AssertNoCompiledChangeAsync(scenario, dialogYaml, extraJson: "");

        [Theory]
        [InlineData("message edited on a topic carrying a PropertyError", TriggerMissingIntentDialog, "", "activity: Hello", "activity: Goodbye")]
        [InlineData("action id edited on a topic carrying a PropertyError", TriggerMissingIntentDialog, "", "id: sendOne", "id: sendTwo")]
        [InlineData("message edited on a topic carrying a PropertyError and customer metadata", TriggerMissingIntentDialog, CustomerExtras, "activity: Hello", "activity: Goodbye")]
        [InlineData("message edited on a clean topic (control)", CompleteTriggerDialog, "", "activity: Hello", "activity: Goodbye")]
        [InlineData("trigger query edited on a clean topic (control)", CompleteTriggerDialog, "", "- hello", "- goodbye")]
        public async Task GetLocalChanges_OnCompiledWorkspace_AfterRealEditToTopicFile_StillReportsTopicUpdate(
            string scenario,
            string dialogYaml,
            string extraJson,
            string find,
            string replace)
        {
            var (compiledDefinition, cloudDefinition) = CompileProjectedWorkspace(
                dialogYaml,
                extraJson,
                topicFile =>
                {
                    Assert.Contains(find, topicFile, StringComparison.Ordinal);
                    return topicFile.Replace(find, replace, StringComparison.Ordinal);
                });

            var changes = await GetLocalChangesAsync(compiledDefinition, cloudDefinition);
            var topicChange = Assert.Single(changes.Where(c => c.SchemaName == TopicSchemaName));

            Assert.True(
                topicChange.ChangeType == ChangeType.Update,
                $"Scenario '{scenario}': a persisted edit to the topic file was not reported as an update. "
                + $"Changes: {string.Join(", ", changes.Select(c => $"{c.ChangeType} {c.SchemaName} -> {c.Uri}"))}");
        }

        [Fact]
        public void CompiledTopic_CarryingValidationDiagnosticsAndARealEdit_SerializesDifferentlyFromTheCloudCache()
        {
            var (compiledDefinition, cloudDefinition) = CompileProjectedWorkspace(
                TriggerMissingIntentDialog,
                extraJson: "",
                editTopicFile: topicFile => topicFile.Replace("activity: Hello", "activity: Goodbye", StringComparison.Ordinal));

            var compiledRoot = compiledDefinition.Components
                .First(c => string.Equals(c.SchemaNameString, TopicSchemaName, StringComparison.Ordinal)).RootElement!;
            var cloudRoot = cloudDefinition.Components
                .First(c => string.Equals(c.SchemaNameString, TopicSchemaName, StringComparison.Ordinal)).RootElement!;

            Assert.NotEqual(Serialize(cloudRoot), Serialize(compiledRoot));
        }

        private static async Task AssertNoCompiledChangeAsync(string scenario, string dialogYaml, string extraJson)
        {
            var (compiledDefinition, cloudDefinition) = CompileProjectedWorkspace(dialogYaml, extraJson);

            var changes = await GetLocalChangesAsync(compiledDefinition, cloudDefinition);
            var topicChanges = changes.Where(c => c.SchemaName == TopicSchemaName).ToArray();

            Assert.True(
                topicChanges.Length == 0,
                $"Scenario '{scenario}': the topic was reported as changed on a freshly cloned workspace. "
                + $"Changes: {string.Join(", ", changes.Select(c => $"{c.ChangeType} {c.SchemaName} -> {c.Uri}"))}");
        }

        private static async Task<ImmutableArray<Change>> GetLocalChangesAsync(DefinitionBase compiledDefinition, DefinitionBase cloudDefinition)
        {
            using var fileAccessorFactory = new InMemoryFileAccessorFactory();
            var workspaceFolder = new DirectoryPath(WorkspaceRoot + "/");
            var fileAccessor = fileAccessorFactory.Create(workspaceFolder);
            WorkspaceSynchronizer.WriteCloudCache(fileAccessor, cloudDefinition);
            await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-1", CancellationToken.None);

            var synchronizer = new WorkspaceSynchronizer(
                new SyncMcsFileParser(LspProjectorService.Instance),
                fileAccessorFactory,
                Mock.Of<IIslandControlPlaneService>(),
                Mock.Of<ISyncProgress>(),
                new LspComponentPathResolver());

            var (_, changes) = await synchronizer.GetLocalChangesAsync(
                workspaceFolder,
                compiledDefinition,
                CancellationToken.None);

            return changes;
        }


        private static (BotComponentBase compiled, BotComponentBase cloud) CompileProjectedTopic(string dialogYaml)
        {
            var (compiledDefinition, cloudDefinition) = CompileProjectedWorkspace(dialogYaml, extraJson: "");

            var compiled = compiledDefinition.Components
                .First(c => string.Equals(c.SchemaNameString, TopicSchemaName, StringComparison.Ordinal));
            var cloud = cloudDefinition.Components
                .First(c => string.Equals(c.SchemaNameString, TopicSchemaName, StringComparison.Ordinal));

            return (compiled, cloud);
        }

        private static (DefinitionBase compiled, DefinitionBase cloud) CompileProjectedWorkspace(
            string dialogYaml,
            string extraJson,
            Func<string, string>? editTopicFile = null)
        {
            var cloudDefinition = CreateCloudDefinition(dialogYaml, extraJson);
            var cloudTopic = cloudDefinition.Components
                .First(c => string.Equals(c.SchemaNameString, TopicSchemaName, StringComparison.Ordinal));

            string projectedTopicFile;
            using (var writer = new StringWriter())
            {
                CodeSerializer.SerializeAsMcsYml(writer, cloudTopic);
                projectedTopicFile = writer.ToString();
            }

            if (editTopicFile != null)
            {
                projectedTopicFile = editTopicFile(projectedTopicFile);
            }

            var (compiler, language) = BuildCompiler();
            var entity = ((BotDefinition)cloudDefinition).Entity!;
            var documents = new Dictionary<FilePath, LspDocument>();
            AddDocument(documents, language, "settings.mcs.yml", CodeSerializer.Serialize(entity));
            AddDocument(documents, language, "topics/MasterQuestion.mcs.yml", projectedTopicFile);

            var compilation = compiler.Compile(documents, new DirectoryPath(WorkspaceRoot));
            Assert.NotNull(compilation.Model);

            return (compilation.Model!, cloudDefinition);
        }

        private static DefinitionBase CreateCloudDefinition(string dialogYaml, string extraJson)
        {
            var entityJson = ClassicBotDefinitionJson[(ClassicBotDefinitionJson.IndexOf("\"entity\"", StringComparison.Ordinal))..].TrimEnd();
            entityJson = entityJson[..entityJson.LastIndexOf('}')].TrimEnd();

            var json = $$"""
            {
              "$kind": "BotDefinition",
              {{entityJson}},
              "components": [
                {
                  "$kind": "DialogComponent",
                  "id": "11111111-1111-1111-1111-111111111111",
                  "version": 3565336,
                  "displayName": "Master Question",
                  {{extraJson}}
                  "schemaName": {{JsonSerializer.Serialize(TopicSchemaName)}},
                  "dialog": {{JsonSerializer.Serialize(dialogYaml)}}
                }
              ]
            }
            """;

            using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
            {
                return JsonSerializer.Deserialize<DefinitionBase>(json, ElementSerializer.CreateOptions())!;
            }
        }

        private static string Serialize(BotElement element)
        {
            using var writer = new StringWriter();
            CodeSerializer.Serialize(writer, element);
            return writer.ToString();
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
