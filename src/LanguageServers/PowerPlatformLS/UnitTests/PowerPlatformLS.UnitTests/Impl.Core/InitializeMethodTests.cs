
namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Completion;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Xunit;
    using Range = PowerPlatformLS.Contracts.Lsp.Models.Range;

    public class InitializeMethodTests
    {
        [Fact]
        public async Task SuccessInit_OnValidContext_Async()
        {
            // Add Mcs language by default
            await using var context = new TestHost();
            var response = await context.InitializeLanguageServerAsync() as JsonRpcResponse;
            var initResult = JsonRpc.GetValidResult<InitializeResult>(response);
            Assert.Equal("Power Platform Pro-Code Language Server", initResult.ServerInfo?.Name);
            string[] expectedTriggerCharacters = [".", ":", " ", "\n"];
            Assert.NotNull(initResult.Capabilities.CompletionProvider?.TriggerCharacters);
            // ! : Assert
            TestAssert.StringArrayEqual(expectedTriggerCharacters, initResult.Capabilities.CompletionProvider?.TriggerCharacters!);
        }

        [Fact]
        public async Task SuccessDefaultTriggerCharacters_OnNoRules_Async()
        {
            // force no added rules ([])
            await using var context = new TestHost([]);
            var response = await context.InitializeLanguageServerAsync() as JsonRpcResponse;
            var initResult = JsonRpc.GetValidResult<InitializeResult>(response);

            // should match CapabilitiesProvider.AdditionalTriggerCharacters
            Assert.NotNull(initResult.Capabilities.CompletionProvider?.TriggerCharacters);
            string[] expectedTriggerCharacters = [":", " "];
            // ! : Assert
            TestAssert.StringArrayEqual(expectedTriggerCharacters, initResult.Capabilities.CompletionProvider?.TriggerCharacters!);
        }

        [Fact]
        public async Task SuccessTriggerCharacters_OnCustomRule_Async()
        {
            await using var context = new TestHost([new TestRuleModule()]);
            var response = await context.InitializeLanguageServerAsync() as JsonRpcResponse;
            var initResult = JsonRpc.GetValidResult<InitializeResult>(response);

            // should match CapabilitiesProvider.AdditionalTriggerCharacters
            Assert.NotNull(initResult.Capabilities.CompletionProvider?.TriggerCharacters);
            string[] expectedTriggerCharacters = ["test", ":", " "];
            // ! : Assert
            TestAssert.StringArrayEqual(expectedTriggerCharacters, initResult.Capabilities.CompletionProvider?.TriggerCharacters!);
        }

        [Fact]
        public async Task Success_InitWithWorkspace_Async()
        {
            await using var context = new TestHost();
            var workspacePath = Path.GetFullPath("TestData/Workspace/LocalWorkspace");
            var initResponse = await context.InitializeLanguageServerAsync(workspacePath) as JsonRpcResponse;
            var initResult = JsonRpc.GetValidResult<InitializeResult>(initResponse);
            Assert.NotNull(initResult);

            var settingsFilePath = Path.Combine(workspacePath, "settings.mcs.yml");
            var openResult = await context.OpenFileAsync(settingsFilePath, new Uri(settingsFilePath));
            Assert.Empty(openResult.Diagnostics);

            var changeParams = new OnDidChangeParams
            {
                TextDocument = new VersionedTextDocumentIdentifier
                {
                    Uri = new Uri(settingsFilePath),
                    Version = 2
                },
                ContentChanges = new[]
                {
                    new TextDocumentChangeEvent
                    {
                        Range = new Range
                        {
                            Start = new Position { Line = 3, Character = 30 },
                            End = new Position { Line = 3, Character = 30 }
                        },
                        Text = "1"
                    }
                }
            };
            context.TestStream.WriteMessage(JsonRpc.CreateMessage(LspMethods.DidChange, changeParams));
            var changeResponse = await context.GetResponseAsync([LspMethods.Diagnostics]);
            var changeResult = JsonRpc.GetValidParams<DiagnosticsParams>(changeResponse as LspJsonRpcMessage);
            Assert.Single(changeResult.Diagnostics);
            Assert.Contains("AuthenticationMode", changeResult.Diagnostics.First().Message);
        }

        private class TestRuleModule : ILspModule
        {
            public void ConfigureServices(IServiceCollection services)
            {
                services.AddCompletionRulesProcessor<LspDocument>();
                services.AddSingleton<ICompletionRule<LspDocument>, TestRule>();
            }

            private class TestRule : ICompletionRule<LspDocument>
            {
                public IEnumerable<string> CharacterTriggers => new[] { "test", ":" };

                public IEnumerable<CompletionItem> ComputeCompletion(RequestContext requestContext, CompletionContext triggerContext)
                {
                    throw new System.NotImplementedException();
                }
            }
        }
    }
}
