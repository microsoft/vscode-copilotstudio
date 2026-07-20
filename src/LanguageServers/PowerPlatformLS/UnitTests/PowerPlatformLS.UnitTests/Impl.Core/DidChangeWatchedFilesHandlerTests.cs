// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp.Handlers;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp.Uris;
    using Moq;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    /// <summary>
    /// Regression coverage for the AI-prompt watched-file path. The broad <c>**/prompt.json</c>
    /// watcher can fire for unrelated projects that merely contain a <c>prompts/</c> folder, so the
    /// handler must only rebuild when the file actually sits inside an agent root. Without the
    /// guard, resolving the workspace would create an empty (phantom) Copilot Studio workspace.
    /// </summary>
    public class DidChangeWatchedFilesHandlerTests
    {
        [Fact]
        public async Task NewPromptOutsideAgentDirectory_DoesNotResolveWorkspaceOrPublish()
        {
            var language = new FakePromptLanguage(isValidAgentDirectory: false);
            var diagnosticPublisher = new Mock<IDiagnosticsPublisher>();
            var handler = CreateHandler(language, diagnosticPublisher.Object);

            await handler.HandleNotificationAsync(CreatePromptChange(), default, CancellationToken.None);

            Assert.False(language.ResolveWorkspaceCalled);
            diagnosticPublisher.Verify(
                publisher => publisher.PublishAllDiagnosticsAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
                Times.Never);
        }

        [Fact]
        public async Task NewPromptInsideAgentDirectory_ResolvesWorkspaceAndPublishes()
        {
            var language = new FakePromptLanguage(isValidAgentDirectory: true);
            var diagnosticPublisher = new Mock<IDiagnosticsPublisher>();
            diagnosticPublisher
                .Setup(publisher => publisher.PublishAllDiagnosticsAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
                .Returns(Task.CompletedTask);
            var handler = CreateHandler(language, diagnosticPublisher.Object);

            await handler.HandleNotificationAsync(CreatePromptChange(), default, CancellationToken.None);

            Assert.True(language.ResolveWorkspaceCalled);
            diagnosticPublisher.Verify(
                publisher => publisher.PublishAllDiagnosticsAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
                Times.Once);
        }

        private static DidChangeWatchedFilesHandler CreateHandler(ILanguageAbstraction language, IDiagnosticsPublisher diagnosticPublisher)
        {
            return new DidChangeWatchedFilesHandler(
                Mock.Of<ILspLogger>(),
                null!,
                diagnosticPublisher,
                Mock.Of<IClientWorkspaceFileProvider>(),
                new FakeLanguageProvider(language));
        }

        private static DidChangeWatchedFilesParams CreatePromptChange()
        {
            return new DidChangeWatchedFilesParams
            {
                Changes = new[]
                {
                    new FileEvent
                    {
                        Uri = new Uri("file:///c:/projects/app/prompts/MyPrompt-11111111-1111-1111-1111-111111111111/prompt.json"),
                        Type = FileChangeType.Created
                    }
                }
            };
        }

        private sealed class FakeLanguageProvider : ILanguageProvider
        {
            private readonly ILanguageAbstraction _language;

            public FakeLanguageProvider(ILanguageAbstraction language) => _language = language;

            bool ILanguageProvider.TryGetLanguage(LanguageType languageType, [NotNullWhen(true)] out ILanguageAbstraction? language)
            {
                language = _language;
                return true;
            }

            bool ILanguageProvider.TryGetLanguageForDocument(LspUri uri, [NotNullWhen(true)] out ILanguageAbstraction? language)
            {
                language = _language;
                return true;
            }
        }

        private sealed class FakePromptLanguage : ILanguageAbstraction
        {
            private readonly bool _isValidAgentDirectory;

            public FakePromptLanguage(bool isValidAgentDirectory) => _isValidAgentDirectory = isValidAgentDirectory;

            public bool ResolveWorkspaceCalled { get; private set; }

            public LanguageType LanguageType => LanguageType.CopilotStudio;

            public IEnumerable<Workspace> Workspaces => Array.Empty<Workspace>();

            public LspDocument CreateDocument(FilePath path, string text, CultureInfo culture, DirectoryPath workspacePath)
                => throw new NotImplementedException();

            public bool IsValidAgentDirectory(DirectoryPath documentDirectory, out DirectoryPath validDirectory)
            {
                validDirectory = documentDirectory;
                return _isValidAgentDirectory;
            }

            public Workspace ResolveWorkspace(DirectoryPath directoryPath)
            {
                ResolveWorkspaceCalled = true;
                return new Workspace(directoryPath);
            }
        }
    }
}
