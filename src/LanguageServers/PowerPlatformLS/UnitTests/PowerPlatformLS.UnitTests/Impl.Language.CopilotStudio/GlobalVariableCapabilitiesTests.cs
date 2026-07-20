namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp;
    using System.IO;
    using System.Text.Json;
    using Xunit;

    public class GlobalVariableCapabilitiesTests
    {
        private static readonly string WorkspacePath = Path.GetFullPath(Path.Combine("TestData", "Workspace", "GlobalVarWorkspace"));
        private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        [Fact]
        public void InitializeResult_AdvertisesReferencesAndRenameProviders()
        {
            var world = new World(WorkspacePath);
            world.GetWorkspace();
            var manager = new InitializeManager(
                world.GetRequiredService<ICapabilitiesProvider>(),
                world.GetRequiredService<ILspLogger>(),
                world.GetRequiredService<IClientInformation>(),
                world.GetRequiredService<IClientInformationInitializer>());

            var capabilities = manager.GetInitializeResult().Capabilities;

            Assert.True(capabilities.ReferencesProvider);
            Assert.NotNull(capabilities.RenameProvider);
            Assert.True(capabilities.RenameProvider!.PrepareProvider);

            Assert.True(capabilities.DefinitionProvider);
            Assert.NotNull(capabilities.CompletionProvider);
            Assert.NotNull(capabilities.CodeActionProvider);
            Assert.NotNull(capabilities.SemanticTokensProvider);
            Assert.NotNull(capabilities.Workspace);
        }

        [Fact]
        public void ServerCapabilities_SerializesReferencesAndRenameProviders()
        {
            var capabilities = new ServerCapabilities
            {
                ReferencesProvider = true,
                RenameProvider = new RenameOptions { PrepareProvider = true },
            };

            var json = JsonSerializer.Serialize(capabilities, CamelCase);

            Assert.Contains("\"referencesProvider\":true", json);
            Assert.Contains("\"renameProvider\":{\"prepareProvider\":true}", json);
        }

        [Fact]
        public void RenameParams_RoundTripsFromWire()
        {
            const string json = "{\"textDocument\":{\"uri\":\"file:///c:/agent/topics/a.mcs.yml\"},\"position\":{\"line\":3,\"character\":7},\"newName\":\"Renamed\"}";

            var parameters = JsonSerializer.Deserialize<RenameParams>(json, CamelCase);

            Assert.NotNull(parameters);
            Assert.Equal("Renamed", parameters!.NewName);
            Assert.Equal(3, parameters.Position.Line);
            Assert.Equal(7, parameters.Position.Character);
        }

        [Fact]
        public void ReferenceParams_RoundTripsFromWire()
        {
            const string json = "{\"textDocument\":{\"uri\":\"file:///c:/agent/topics/a.mcs.yml\"},\"position\":{\"line\":1,\"character\":2},\"context\":{\"includeDeclaration\":true}}";

            var parameters = JsonSerializer.Deserialize<ReferenceParams>(json, CamelCase);

            Assert.NotNull(parameters);
            Assert.True(parameters!.Context.IncludeDeclaration);
        }

        [Fact]
        public void PrepareRenameResult_SerializesPlaceholderAndRange()
        {
            var result = new PrepareRenameResult
            {
                Range = new Range { Start = new Position { Line = 1, Character = 2 }, End = new Position { Line = 1, Character = 6 } },
                Placeholder = "Var1",
            };

            var json = JsonSerializer.Serialize(result, CamelCase);

            Assert.Contains("\"placeholder\":\"Var1\"", json);
            Assert.Contains("\"range\":", json);
        }
    }
}
