// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers;
    using Moq;
    using System;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class GetCloudCacheFileHandlerTests
    {
        private const string WorkspaceRoot = "c:/agent";

        [Fact]
        public async Task UnresolvableWorkflowClientDataReturnsEmptyContentInsteadOfNotFound()
        {
            var response = await HandleAsync($"Mcs.Workflow.{Guid.NewGuid()}");

            Assert.Equal(200, response.Code);
            Assert.Equal(string.Empty, response.Content);
        }

        [Fact]
        public async Task UnresolvableWorkflowMetadataReturnsEmptyContentInsteadOfNotFound()
        {
            var response = await HandleAsync($"Mcs.Workflow.{Guid.NewGuid()}.metadata");

            Assert.Equal(200, response.Code);
            Assert.Equal(string.Empty, response.Content);
        }

        [Fact]
        public async Task UnresolvableNonWorkflowSchemaStillReturnsNotFound()
        {
            var response = await HandleAsync("agent1.topic.DoesNotExist");

            Assert.Equal(404, response.Code);
        }

        private static async Task<GetFileResponse> HandleAsync(string schemaName)
        {
            var root = new DirectoryPath(WorkspaceRoot);
            var handler = new GetCloudCacheFileHandler(Mock.Of<ILspLogger>());
            var context = new RequestContext(new FakeLanguage(), new Workspace(root), null, 0);
            var request = new GetFileRequest
            {
                WorkspaceUri = new Uri(WorkspaceRoot),
                SchemaName = schemaName,
            };

            return await handler.HandleRequestAsync(request, context, CancellationToken.None);
        }

        private sealed class FakeLanguage : ILanguageAbstraction
        {
            public LanguageType LanguageType => LanguageType.CopilotStudio;

            public LspDocument CreateDocument(FilePath path, string text, CultureInfo culture, DirectoryPath workspacePath)
                => throw new NotImplementedException();

            public bool IsValidAgentDirectory(DirectoryPath directory, out DirectoryPath validDirectory)
            {
                validDirectory = directory;
                return false;
            }
        }
    }
}
