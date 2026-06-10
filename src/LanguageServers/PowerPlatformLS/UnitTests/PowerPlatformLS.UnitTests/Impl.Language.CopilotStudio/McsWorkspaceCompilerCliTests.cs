namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Yaml;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
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
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using Xunit;

    /// <summary>
    /// LSP-workspace-compiler tests for the CLI three-layer <c>.mcs.yml</c> layout
    /// (TDD D31, Node S1). These close the review's Finding-C gap: the sync parser had
    /// shape-aware coverage, but nothing exercised the language host / VS Code push path
    /// (<see cref="Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models.McsWorkspaceCompiler"/>).
    /// They prove the compiler detects the CLI authoring shape from <c>settings.mcs.yml</c>
    /// and threads it into the file parser so <c>behaviors/</c>, <c>capabilities/tools/</c>,
    /// and <c>capabilities/knowledge/</c> components resolve to their CLI schema names; that
    /// <c>.sync.yaml</c> overlays route to generic YAML (never the MCS compiler); and that
    /// the classic path is unchanged.
    /// </summary>
    public class McsWorkspaceCompilerCliTests
    {
        private const string CliBotSchemaName = "test_cliagent";

        // Minimal CLI BotDefinition: the typed AgentSettings block is the CliCopilot
        // discriminator (D8/D15), mirroring the sync-side fixtures.
        private const string CliBotDefinitionJson = """
        {
          "$kind": "BotDefinition",
          "entity": {
            "$kind": "BotEntity",
            "schemaName": "test_cliagent",
            "template": "cliagent-1.0.0",
            "configuration": {
              "$kind": "BotConfiguration",
              "recognizer": { "$kind": "CLICopilotRecognizer" },
              "agentSettings": {
                "$kind": "AgentSettings",
                "instructions": {
                  "$kind": "Instructions",
                  "segments": [
                    { "$kind": "StaticSegment", "value": "You are a helpful agent." }
                  ]
                },
                "model": { "$kind": "ModelConfig", "series": "gpt-4o" }
              }
            }
          }
        }
        """;

        // Classic BotDefinition: a default- template prefix -> classic shape (no AgentSettings).
        private const string ClassicBotDefinitionJson = """
        {
          "$kind": "BotDefinition",
          "entity": {
            "$kind": "BotEntity",
            "schemaName": "cree9_classicagent",
            "template": "default-1.0.0",
            "configuration": { "$kind": "BotConfiguration" }
          }
        }
        """;

        [Fact]
        public void CliThreeLayerWorkspace_CompilesComponents_WithCliSchemaNames()
        {
            var (compiler, language) = BuildCompiler();

            var entity = ReadEntity(CliBotDefinitionJson);
            Assert.Equal(AuthoringShape.CliCopilot, AgentClassifier.DetectAuthoringShape(entity));

            var documents = new Dictionary<FilePath, LspDocument>();
            AddDocument(documents, language, "settings.mcs.yml", CodeSerializer.Serialize(entity));
            AddDocument(documents, language, "behaviors/weather.mcs.yml",
                "kind: InlineAgentSkill\ncontent: |\n  ---\n  name: w\n  ---\n  Skill body\n");
            AddDocument(documents, language, "capabilities/tools/Getarow.mcs.yml",
                "kind: ConnectorTool\nconnectionReference: shared_x\noperationId: GetRow\n");
            AddDocument(documents, language, "capabilities/knowledge/Wikipedia.mcs.yml",
                "kind: PublicSiteSearchSource\nsite: https://www.wikipedia.org/\n");

            var compilation = compiler.Compile(documents, new DirectoryPath("c:/agent"));

            Assert.NotNull(compilation.Model);
            Assert.Empty(compilation.Errors);

            var schemaNames = compilation.Model.Components.Select(c => c.SchemaNameString).ToList();

            // behaviors/ + capabilities/tools/ resolve to the CLI-shape schema names; the
            // classic (no-shape) path would not find these rules and would error instead.
            Assert.Contains($"{CliBotSchemaName}.skill.weather", schemaNames);
            Assert.Contains($"{CliBotSchemaName}.tool.Getarow", schemaNames);

            // The capabilities/knowledge/ layer also compiles to a typed CLI-named component
            // (a third component beyond the skill + tool), proving all three layers resolve.
            Assert.Contains(schemaNames, s =>
                s != null
                && s.StartsWith($"{CliBotSchemaName}.", StringComparison.Ordinal)
                && s != $"{CliBotSchemaName}.skill.weather"
                && s != $"{CliBotSchemaName}.tool.Getarow");
        }

        [Fact]
        public void SyncYamlOverlays_RouteToGenericYaml_NotMcsCompiler()
        {
            // .sync.yaml connection overlays must NOT be MCS components, so they never reach
            // the MCS workspace compiler (no spurious "not a valid MCS component" diagnostics).
            AssertLanguage("infrastructure/connections/shared_x.sync.yaml", LanguageType.Yaml);
            AssertLanguage("agent.sync.yaml", LanguageType.Yaml);

            // CLI three-layer component files DO route to the MCS language host.
            AssertLanguage("behaviors/weather.mcs.yml", LanguageType.CopilotStudio);
            AssertLanguage("capabilities/tools/Getarow.mcs.yml", LanguageType.CopilotStudio);
            AssertLanguage("settings.mcs.yml", LanguageType.CopilotStudio);
        }

        [Fact]
        public void ClassicWorkspace_ShapeDetection_AndCompile_Unchanged()
        {
            var (compiler, language) = BuildCompiler();

            var entity = ReadEntity(ClassicBotDefinitionJson);
            Assert.Equal(AuthoringShape.Classic, AgentClassifier.DetectAuthoringShape(entity));

            // A classic agent.mcs.yml entity + a classic topic compiles via the unchanged
            // default (no-shape) path; the topic keeps its classic schema name.
            var documents = new Dictionary<FilePath, LspDocument>();
            AddDocument(documents, language, "settings.mcs.yml", CodeSerializer.Serialize(entity));
            AddDocument(documents, language, "agent.mcs.yml", "instructions: random something\n");
            AddDocument(documents, language, "topics/Greeting.mcs.yml",
                "kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  intent:\n    triggerQueries:\n      - hello\n");

            var compilation = compiler.Compile(documents, new DirectoryPath("c:/agent"));

            Assert.NotNull(compilation.Model);
            Assert.Empty(compilation.Errors);
            Assert.Contains(compilation.Model.Components, c => c.SchemaNameString == "cree9_classicagent.topic.Greeting");
        }

        private static void AssertLanguage(string relativePath, LanguageType expected)
        {
            var resolved = WorkspacePath.TryGetLanguageType(new FilePath(relativePath), out var languageType);
            Assert.True(resolved, $"No language route for '{relativePath}'.");
            Assert.Equal(expected, languageType);
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
            var path = new FilePath("c:/agent/" + relativePath);
            var document = language.CreateDocument(path, text, CultureInfo.InvariantCulture, new DirectoryPath("c:/agent"));
            documents.Add(path, document);
        }

        private static BotEntity ReadEntity(string botDefinitionJson)
        {
            DefinitionBase? definition;
            using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
            {
                definition = JsonSerializer.Deserialize<DefinitionBase>(botDefinitionJson, ElementSerializer.CreateOptions());
            }

            var entity = (definition as BotDefinition)?.Entity;
            return entity ?? throw new InvalidOperationException("Test fixture did not produce a BotEntity.");
        }
    }
}
