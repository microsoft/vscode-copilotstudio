namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Dataverse.Solutions;
    using Microsoft.Agents.ObjectModel.NodeGenerators.TestTools;
    using Microsoft.Agents.ObjectModel.UnitTests.TestTools;
    using Microsoft.Agents.ObjectModel.Yaml;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.FileProviders;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.DependencyInjection;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using Moq;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class WorkspaceCompilerTests
    {
        [Fact]
        public void SameBotStructure_ComparingSolutionFiles_WithWorkspace()
        {
            // arrange
            var services = new ServiceCollection();
            services.Install(new McsLspModule());
            MockCoreWorkspaceBuilder(services);
            var serviceProvider = services.BuildServiceProvider();
            var compiler = serviceProvider.GetRequiredService<IWorkspaceCompiler<DefinitionBase>>();
            var language = serviceProvider.GetRequiredService<ILanguageAbstraction>();

            // act
            var workspaceParentDirectory = Path.GetFullPath(Path.Combine("TestData", "Workspace"));
            var workspacePath = SystemToAgentDirectoryPath(Path.Combine(workspaceParentDirectory, "LocalWorkspace"));
            var documents = ReadAllMcsLspDocuments(workspacePath, language);
            var compilation = compiler.Compile(documents, workspacePath);

            // assert
            // BotDefinition exists and has no error
            Assert.NotNull(compilation.Model);
            Assert.Empty(compilation.Errors);
            var errors = ValidationHelper.GetComponentsWithErrors(compilation.Model, FeatureConfigurationMocks.AllEnabledFeatures);
            if (errors.Any())
            {
                throw new Exception($"Bot has errors. {JsonSerializer.Serialize(errors, ElementSerializer.CreateOptions())}");
            }

            // solution reader outputs same bot structure
            var reader = new SolutionFileReader(new PhysicalFileProvider(Path.Combine(workspaceParentDirectory, "SolutionExport")));
            var bots = reader.FindBotsInFolder();
            var bot = Assert.Single(bots);
            var botDef = reader.GetBotDefinition(bot.SchemaName.Value) ?? throw new Xunit.Sdk.NotNullException();

            // Manually compare the structure instead of using BotDefinition.Equals(other, NodeComparison.Structural) to improve debuggability.
            // We can generate better insights on test failures by comparing children one by one
            // and we can ignore things that we don't need when loading bot from workspace.
            var workspaceBotComponents = compilation.Model.Descendants(x => false).ToArray();
            var solutionBotComponents = botDef.Descendants(x => false).ToArray();

            // TODO : Establish more criteria for validating workspace bot components
            // solution bot has ConnectionReference that doesn't exist in workspace bot
            Assert.Equal(workspaceBotComponents.Length, solutionBotComponents.Length - 1);
        }

        private static void MockCoreWorkspaceBuilder(ServiceCollection services)
        {
            services.AddSingleton(Mock.Of<ILspLogger>());
            services.AddSingleton(Mock.Of<IClientInformation>());
            services.AddSingleton(Mock.Of<ILspServices>());
            services.AddSingleton(Mock.Of<ILspTransport>());

            // give real file access for knowledge files
            var mockFileProvider = new Mock<IClientWorkspaceFileProvider>();
            mockFileProvider
                .Setup(x => x.GetDirectoryContents(It.IsAny<DirectoryPath>()))
                .Returns((DirectoryPath path) =>
                {
                    var systemPath = path.ToString();
                    if (!Directory.Exists(systemPath))
                    {
                        return NotFoundDirectoryContents.Singleton;
                    }
                    var physicalProvider = new PhysicalFileProvider(systemPath);
                    return physicalProvider.GetDirectoryContents(string.Empty);
                });
            mockFileProvider
                .Setup(x => x.GetFileInfo(It.IsAny<FilePath>()))
                .Returns((FilePath path) =>
                {
                    var systemPath = path.ToString();
                    var directory = Path.GetDirectoryName(systemPath);
                    if (directory == null || !Directory.Exists(directory))
                    {
                        return new NotFoundFileInfo(Path.GetFileName(systemPath));
                    }
                    return new PhysicalFileProvider(directory).GetFileInfo(Path.GetFileName(systemPath));
                });

            services.AddSingleton(mockFileProvider.Object);
        }

        private static IReadOnlyDictionary<FilePath, LspDocument> ReadAllMcsLspDocuments(DirectoryPath workspacePath, ILanguageAbstraction mcsLanguage)
        {
            var files = Directory.EnumerateFiles(workspacePath.ToString(), "*.yaml", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(workspacePath.ToString(), "*.yml", SearchOption.AllDirectories));
            var documents = new Dictionary<FilePath, LspDocument>();
            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                var documentPath = SystemToAgentFilePath(Path.GetFullPath(file));
                var document = mcsLanguage.CreateDocument(
                    documentPath,
                    text,
                    CultureInfo.InvariantCulture,
                    workspacePath);
                documents.Add(documentPath, document);
            }

            return documents;
        }

        [Fact]
        public void Compile_MergesNewLocalPromptModelDefinition()
        {
            var services = new ServiceCollection();
            services.Install(new McsLspModule());
            MockCoreWorkspaceBuilder(services);
            var serviceProvider = services.BuildServiceProvider();
            var compiler = serviceProvider.GetRequiredService<IWorkspaceCompiler<DefinitionBase>>();
            var language = serviceProvider.GetRequiredService<ILanguageAbstraction>();

            var tempRoot = Path.Combine(Path.GetTempPath(), "WorkspaceCompilerPromptTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempRoot);
                File.WriteAllText(Path.Combine(tempRoot, "agent.mcs.yml"), "instructions: test agent");

                var modelId = Guid.NewGuid();
                var promptFolder = Path.Combine(tempRoot, "prompts", $"promptE2-{modelId}");
                Directory.CreateDirectory(promptFolder);
                File.WriteAllText(Path.Combine(promptFolder, "metadata.yml"), $"aIModelId: {modelId}\nname: prompt E2\ntemplateId: {Guid.Empty}\n");
                File.WriteAllText(Path.Combine(promptFolder, "prompt.json"), "{\"name\":\"prompt E2\",\"instruction\":\"answer {{question}}\",\"model\":\"gpt-41-mini\",\"inputs\":[{\"id\":\"question\",\"type\":\"text\"}],\"output\":{\"formats\":[\"text\"]}}");

                var workspacePath = SystemToAgentDirectoryPath(tempRoot);
                var documents = ReadAllMcsComponentDocuments(workspacePath, language);
                var compilation = compiler.Compile(documents, workspacePath);

                var definition = Assert.IsType<BotDefinition>(compilation.Model);
                Assert.Contains(definition.AIModelDefinitions, aiModel => aiModel.Id.HasValue && aiModel.Id.Value == modelId);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [Fact]
        public void Compile_DoesNotWriteToCloudCache()
        {
            var services = new ServiceCollection();
            services.Install(new McsLspModule());
            MockCoreWorkspaceBuilder(services);
            var serviceProvider = services.BuildServiceProvider();
            var compiler = serviceProvider.GetRequiredService<IWorkspaceCompiler<DefinitionBase>>();
            var language = serviceProvider.GetRequiredService<ILanguageAbstraction>();

            var tempRoot = Path.Combine(Path.GetTempPath(), "WorkspaceCompilerCacheTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempRoot);
                File.WriteAllText(Path.Combine(tempRoot, "agent.mcs.yml"), "instructions: test agent");

                var modelId = Guid.NewGuid();
                var promptFolder = Path.Combine(tempRoot, "prompts", $"promptE2-{modelId}");
                Directory.CreateDirectory(promptFolder);
                File.WriteAllText(Path.Combine(promptFolder, "metadata.yml"), $"aIModelId: {modelId}\nname: prompt E2\ntemplateId: {Guid.Empty}\n");
                File.WriteAllText(Path.Combine(promptFolder, "prompt.json"), "{\"name\":\"prompt E2\",\"instruction\":\"answer {{question}}\",\"model\":\"gpt-41-mini\",\"inputs\":[{\"id\":\"question\",\"type\":\"text\"}],\"output\":{\"formats\":[\"text\"]}}");

                var cacheDirectory = Path.Combine(tempRoot, ".mcs");
                Directory.CreateDirectory(cacheDirectory);
                var cachePath = Path.Combine(cacheDirectory, "botdefinition.json");
                File.WriteAllText(cachePath, "{\"sentinel\":true}");
                var cacheBytesBefore = File.ReadAllBytes(cachePath);

                var workspacePath = SystemToAgentDirectoryPath(tempRoot);
                var documents = ReadAllMcsComponentDocuments(workspacePath, language);
                compiler.Compile(documents, workspacePath);

                Assert.Equal(cacheBytesBefore, File.ReadAllBytes(cachePath));
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        private static IReadOnlyDictionary<FilePath, LspDocument> ReadAllMcsComponentDocuments(DirectoryPath workspacePath, ILanguageAbstraction mcsLanguage)
        {
            var files = Directory.EnumerateFiles(workspacePath.ToString(), "*.mcs.yml", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(workspacePath.ToString(), "*.mcs.yaml", SearchOption.AllDirectories));
            var documents = new Dictionary<FilePath, LspDocument>();
            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                var documentPath = SystemToAgentFilePath(Path.GetFullPath(file));
                var document = mcsLanguage.CreateDocument(documentPath, text, CultureInfo.InvariantCulture, workspacePath);
                documents.Add(documentPath, document);
            }

            return documents;
        }

        // Verify that the filenames round-trip. 
        [Fact]
        public void Verify_BotComponent_to_Filenames()
        {
            // arrange
            var services = new ServiceCollection();
            services.Install(new McsLspModule());
            MockCoreWorkspaceBuilder(services);
            var serviceProvider = services.BuildServiceProvider();
            var compiler = serviceProvider.GetRequiredService<IWorkspaceCompiler<DefinitionBase>>();
            var language = serviceProvider.GetRequiredService<ILanguageAbstraction>();

            // act
            var workspaceParentDirectory = Path.GetFullPath(Path.Combine("TestData", "Workspace"));
            var workspacePath = SystemToAgentDirectoryPath(Path.Combine(workspaceParentDirectory, "LocalWorkspace"));
            var documents = ReadAllMcsLspDocuments(workspacePath, language);
            var compilation = compiler.Compile(documents, workspacePath);

            var botDefinition = Assert.IsType<BotDefinition>(compilation.Model);
            var botEntity = botDefinition.Entity;

            var rootFolder = workspacePath.ToString();

            foreach(var botComponent in botDefinition.Components)
            {
                if (botComponent is FileAttachmentComponent)
                {
                    // $$$ Not working yet... rules aren't consistent. 
                    continue;
                }

                var pathResolver = new LspComponentPathResolver();
                string relativeFilename = pathResolver.GetComponentPath(botComponent, botDefinition);
                string actualFullPath = workspacePath.GetChildFilePath(relativeFilename).ToString();

                // Get expected                
                var expectedFullPath = GetBotSourceUri(botComponent).ToFilePath().ToString();                
                Assert.StartsWith(rootFolder, expectedFullPath);

                // Much easier to compare these
                var actualRelativePath = actualFullPath.Substring(rootFolder.Length);
                var expectedRelativePath = expectedFullPath.Substring(rootFolder.Length);

                Assert.Equal(expectedRelativePath, actualRelativePath);
            }

        }

        // $$$ IS there a better way to do this?
        // Like get it from teh McsDocument?
        private static Uri GetBotSourceUri(BotComponentBase botComponent)
        {
            var root = botComponent.Children().First();

            // $$$ - this is because it's missing from CompileBotDefinition. 
            if (root is KnowledgeSourceConfiguration knowledge)
            {
                return knowledge.Source!.Syntax!.SourceUri;
            }

            var uri = root.Syntax!.SourceUri;

            return uri;
        }

        [Fact]
        public void YamlDisplayNameAndDescription()
        {
            var world = new World();
            var doc = world.AddFile("topic2.mcs.yml");
            var element  = world.GetFileElement(doc);

            var parent = (BotComponentBase)element.Parent!;
            Assert.Equal("Topic2DisplayName", parent.DisplayName);
            Assert.Equal("This is description line 1. ", parent.Description);
        }

        [Fact]
        public void WorkspaceWithTranslations()
        {
            // arrange
            var services = new ServiceCollection();
            services.Install(new McsLspModule());
            MockCoreWorkspaceBuilder(services);
            var serviceProvider = services.BuildServiceProvider();
            var compiler = serviceProvider.GetRequiredService<IWorkspaceCompiler<DefinitionBase>>();
            var language = serviceProvider.GetRequiredService<ILanguageAbstraction>();

            // act
            var workspaceParentDirectory = Path.GetFullPath(Path.Combine("TestData", "Workspace"));
            var workspacePath = SystemToAgentDirectoryPath(Path.Combine(workspaceParentDirectory, "LocalWorkspace"));
            var documents = ReadAllMcsLspDocuments(workspacePath, language);
            var compilation = compiler.Compile(documents, workspacePath);

            // assert
            Assert.NotNull(compilation.Model);
            Assert.Empty(compilation.Errors);

            // Verify translation components are loaded
            var translationComponents = compilation.Model.Components
                .OfType<TranslationsComponent>()
                .ToArray();

            Assert.NotEmpty(translationComponents);
            Assert.Single(translationComponents);

            // Verify translations have correct schema names with .topic. infix
            Assert.All(translationComponents, tc => Assert.Contains(".topic.", tc.SchemaNameString));

            // Verify specific translation files are loaded
            var schemaNames = translationComponents.Select(tc => tc.SchemaNameString).ToArray();
            Assert.Contains(schemaNames, s => s.EndsWith("Greeting.pt-BR"));
        }

        [Theory]
        [InlineData("kind: foo", null, null)]
        [InlineData("", null, null)]
        [InlineData("# Name: display1 ", "display1", null)]
        [InlineData("# line1", null, "line1")]
        [InlineData("# line1 \n# line2", null, "line1 \nline2")]
        [InlineData("# Name: d1\n# Name: d2", "d1", "Name: d2")]
        [InlineData("#\n# Name: display1", null, null)] // must start on row0
        [InlineData(" # Name: display1", null, null)] // must start in column0
        [InlineData("// Name: display1", null, null)] // only use yaml comments
        public void YamlHeaders(string lines, string? displayName, string? description)
        {
            var lines2 = lines.Split('\n');

            CodeSerializer.ParseYamlHeader(lines2, out var actualDisplayName, out var actualDescription);

            if (actualDescription != null)
            {
                actualDescription = actualDescription.Replace("\r", "");
            }

            Assert.Equal(displayName, actualDisplayName);
            Assert.Equal(description, actualDescription);
        }

        [Fact]
        public void WorkspaceWithChildAgents()
        {
            var dir = Path.GetFullPath(Path.Combine("TestData", "WorkspaceWithSubAgents"));

            World world = new World(dir);
            var workspace = world.GetWorkspace();

            workspace.BuildCompilationModel();

            foreach(var element in workspace.Definition.DescendantsAndSelf())
            {
                var diagnostics = element.Diagnostics.ToArray();
                Assert.Empty(diagnostics);
            }
        }

        // A Component collection is 2 sepoarate 
        [Fact]
        public void WorkspaceWithComponentCollections()
        {
            var dir = Path.GetFullPath(Path.Combine("TestData", "WorkspaceWithCC"));

            World world = new World(dir);
            var workspace = world.GetWorkspace(Path.Combine(dir, "Agent 111"));

            foreach (var element in workspace.Definition.DescendantsAndSelf())
            {
                var diagnostics = element.Diagnostics.ToArray();
                Assert.Empty(diagnostics);
            }

            var definition = Assert.IsType<BotDefinition>(workspace.Definition);
            Assert.Contains(definition.ComponentCollections, collection => collection.SchemaName.Value == "bot_componentcollection_my_cc_333");
        }

        [Fact]
        public async Task InvokeFlowAction_BindsMatchingFileRecord_DoesNotReportInvalidBindingError()
        {
            var flow = await BuildFileInputFlowAsync(FlowId);
            var errorCodes = CompileTopicBindingFlowAndGetErrorCodes(flow, BuildFileBindingTopicYaml(contentBytesKind: "File"));
            Assert.DoesNotContain("InvalidBindingInvokeAction", errorCodes);
        }

        [Fact]
        public async Task InvokeFlowAction_BindsMismatchedContentBytes_ReportsInvalidBindingError()
        {
            var flow = await BuildFileInputFlowAsync(FlowId);
            var errorCodes = CompileTopicBindingFlowAndGetErrorCodes(flow, BuildFileBindingTopicYaml(contentBytesKind: "String"));
            Assert.Contains("InvalidBindingInvokeAction", errorCodes);
        }

        private const string FlowIdString = "12345678-1234-1234-1234-123456789abc";
        private static readonly Guid FlowId = new Guid(FlowIdString);

        private static async Task<CloudFlowDefinition> BuildFileInputFlowAsync(Guid workflowId)
        {
            var clientData = @"
            {
                ""properties"": {
                    ""definition"": {
                        ""triggers"": {
                            ""manual"": {
                                ""inputs"": {
                                    ""schema"": {
                                        ""properties"": {
                                            ""file"": {
                                                ""type"": ""object"",
                                                ""x-ms-content-hint"": ""FILE"",
                                                ""title"": ""File content 1"",
                                                ""properties"": {
                                                    ""name"": { ""type"": ""string"", ""title"": ""File Name"" },
                                                    ""contentBytes"": { ""type"": ""string"", ""format"": ""byte"", ""title"": ""File Content"" }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        },
                        ""actions"": {}
                    }
                }
            }";

            var mockDataverse = new MockDataverseClient();
            mockDataverse.SetWorkflowsForAgent(new[]
            {
                new SyncDataverseClient.WorkflowMetadata { WorkflowId = workflowId, Name = "FileFlow", ClientData = clientData, StateCode = 1 }
            });

            using var tempWorkspace = new TempDirectory();
            var workspaceFolder = new DirectoryPath(tempWorkspace.Path.Replace("\\", "/"));
            var filesystem = new InMemoryFileWriter();
            var synchronizer = new WorkspaceSynchronizer(
                new SyncMcsFileParser(LspProjectorService.Instance),
                (IFileAccessorFactory)filesystem,
                Mock.Of<IIslandControlPlaneService>(),
                Mock.Of<ISyncProgress>(),
                new LspComponentPathResolver());

            var workflows = await synchronizer.GetWorkflowsAsync(workspaceFolder, mockDataverse, new AgentSyncInfo { AgentId = Guid.NewGuid() }, filesystem, CancellationToken.None);
            return workflows.Workflows[0];
        }

        private static string BuildFileBindingTopicYaml(string contentBytesKind) => $@"kind: AdaptiveDialog
beginDialog:
  kind: OnRecognizedIntent
  id: main
  intent:
    triggerQueries:
      - test
  actions:
    - kind: InvokeFlowAction
      id: invokeFlowAction_file
      flowId: {FlowIdString}
      input:
        binding:
          file: =Topic.uploadedFile
inputType:
  properties:
    uploadedFile:
      displayName: uploadedFile
      type:
        kind: Record
        properties:
          name:
            displayName: name
            type: String
          contentBytes:
            displayName: contentBytes
            type:
              kind: {contentBytesKind}
";

        private static IReadOnlyList<string> CompileTopicBindingFlowAndGetErrorCodes(CloudFlowDefinition flow, string topicYaml)
        {
            return CompileTopicBindingFlow(flow, topicYaml).Model.DescendantsAndSelf()
                .SelectMany(element => element.Diagnostics)
                .Select(diagnostic => (diagnostic as BindingIncorrectTypeError)?.ErrorCode?.Value.ToString()
                    ?? (diagnostic as PropertyError)?.ErrorCode?.Value.ToString()
                    ?? string.Empty)
                .ToArray();
        }

        private static Compilation<DefinitionBase> CompileTopicBindingFlow(CloudFlowDefinition flow, string topicYaml)
        {
            var entity = new BotEntity.Builder
            {
                SchemaName = "cr123_agent",
                CdsBotId = Guid.Parse("00000000-0000-0000-0000-000000000010"),
                AuthenticationMode = BotAuthenticationMode.Integrated,
            }.Build();
            var botDefinition = new BotDefinition.Builder { Entity = entity, Flows = { flow } }.Build();
            var botDefinitionJson = JsonSerializer.Serialize(botDefinition, ElementSerializer.CreateOptions());

            var services = new ServiceCollection();
            services.Install(new McsLspModule());
            MockCoreWorkspaceBuilder(services);
            var serviceProvider = services.BuildServiceProvider();
            var compiler = serviceProvider.GetRequiredService<IWorkspaceCompiler<DefinitionBase>>();
            var language = serviceProvider.GetRequiredService<ILanguageAbstraction>();

            var workspacePath = new DirectoryPath("c:/agent/");
            var documents = new Dictionary<FilePath, LspDocument>();

            void AddDocument(string relativePath, string text)
            {
                var documentPath = new FilePath("c:/agent/" + relativePath);
                documents[documentPath] = language.CreateDocument(documentPath, text, CultureInfo.InvariantCulture, workspacePath);
            }

            AddDocument(".mcs/botdefinition.json", botDefinitionJson);
            AddDocument("settings.mcs.yml", "schemaName: cr123_agent\ncdsBotId: 00000000-0000-0000-0000-000000000010");
            AddDocument("topics/InvokeFlowTopic.mcs.yml", topicYaml);

            var compilation = compiler.Compile(documents, workspacePath);

            return compilation;
        }

        private static FilePath SystemToAgentFilePath(string path)
        {
            return new FilePath(path.Replace('\\', '/'));
        }

        private static DirectoryPath SystemToAgentDirectoryPath(string path)
        {
            return new DirectoryPath(path.Replace('\\', '/'));
        }
    }
}
