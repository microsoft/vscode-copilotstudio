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
    using Moq;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
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
                    var physicalProvider = new PhysicalFileProvider(path.ToString());
                    return physicalProvider.GetDirectoryContents(string.Empty);
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
