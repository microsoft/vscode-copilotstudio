// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers;
    using Moq;
    using System;
    using System.Globalization;
    using System.IO;
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

        [Fact]
        public async Task StandaloneWorkflowMetadataIsReadFromTheAgentCache()
        {
            var workflowId = Guid.NewGuid();
            var root = NewWorkspaceRoot();

            try
            {
                SeedCachedWorkflow(root, workflowId, "{ \"a\": 1 }");

                var response = await HandleWithBuiltWorkspaceAsync($"Mcs.Workflow.{workflowId}.metadata", root);

                Assert.Equal(200, response.Code);
                Assert.Contains("name: My Flow", response.Content, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task StandaloneWorkflowDefinitionIsReadFromTheAgentCache()
        {
            var workflowId = Guid.NewGuid();
            var root = NewWorkspaceRoot();

            try
            {
                SeedCachedWorkflow(root, workflowId, "{ \"a\": 1 }");

                var response = await HandleWithBuiltWorkspaceAsync($"Mcs.Workflow.{workflowId}", root);

                Assert.Equal(200, response.Code);
                Assert.Contains("\"a\": 1", response.Content, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task AWorkflowTheCacheDoesNotHoldStillReturnsEmptyContent()
        {
            var root = NewWorkspaceRoot();

            try
            {
                var response = await HandleAsync($"Mcs.Workflow.{Guid.NewGuid()}.metadata", root);

                Assert.Equal(200, response.Code);
                Assert.Equal(string.Empty, response.Content);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static string NewWorkspaceRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "mcs-cache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            return root;
        }

        private static void SeedCachedWorkflow(string root, Guid workflowId, string definition)
        {
            var accessor = new FileAccessorFactory().Create(new DirectoryPath(root.Replace('\\', '/') + "/"));

            StandaloneWorkflowCache.Write(accessor, new SyncDataverseClient.WorkflowMetadata
            {
                WorkflowId = workflowId,
                Name = "My Flow",
                ClientData = definition,
            });
        }

        private static async Task<GetFileResponse> HandleWithBuiltWorkspaceAsync(string schemaName, string workspaceRoot)
        {
            var world = new World(workspaceRoot);
            var workspace = world.GetWorkspace();
            workspace.BuildCompilationModel();

            var handler = new GetCloudCacheFileHandler(Mock.Of<ILspLogger>());
            var context = new RequestContext(world.GetRequiredService<ILanguageAbstraction>(), workspace, null, 0);
            var request = new GetFileRequest
            {
                WorkspaceUri = new Uri(workspaceRoot.Replace('\\', '/')),
                SchemaName = schemaName,
            };

            return await handler.HandleRequestAsync(request, context, CancellationToken.None);
        }

        private static async Task<GetFileResponse> HandleAsync(string schemaName, string workspaceRoot)
        {
            var normalized = workspaceRoot.Replace('\\', '/');
            var handler = new GetCloudCacheFileHandler(Mock.Of<ILspLogger>());
            var context = new RequestContext(new FakeLanguage(), new Workspace(new DirectoryPath(normalized)), null, 0);
            var request = new GetFileRequest
            {
                WorkspaceUri = new Uri(normalized),
                SchemaName = schemaName,
            };

            return await handler.HandleRequestAsync(request, context, CancellationToken.None);
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
