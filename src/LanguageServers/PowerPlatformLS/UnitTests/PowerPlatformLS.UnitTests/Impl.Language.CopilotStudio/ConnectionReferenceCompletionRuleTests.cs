// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using Xunit;

    public class ConnectionReferenceCompletionRuleTests
    {
        private const string WorkspaceRoot = "c:/agent";
        private const string CacheRelativePath = ".mcs/.connections-cache.json";
        private const string DeclaredReference = "pref_agent.shared_office365.abc123";
        private const string UndeclaredReference = "pref_agent.shared_sharepoint.def456";
        private const string DeclareCommand = "microsoft-copilot-studio.declareConnectionReference";

        [Fact]
        public void ReplacesEntireValue_NoPrefixDoubling()
        {
            var factory = CreateFactoryWithCache();
            var text = "  connectionReference: pref_agent.";

            var item = Complete(factory, text, text.Length).Single(i => i.Label == DeclaredReference);

            Assert.NotNull(item.TextEdit);
            Assert.Equal(DeclaredReference, item.TextEdit!.NewText);

            var valueStart = text.IndexOf("pref_agent.", StringComparison.Ordinal);
            Assert.Equal(0, item.TextEdit.Range.Start.Line);
            Assert.Equal(valueStart, item.TextEdit.Range.Start.Character);
            Assert.Equal(text.Length, item.TextEdit.Range.End.Character);

            var applied = text.Substring(0, item.TextEdit.Range.Start.Character)
                + item.TextEdit.NewText
                + text.Substring(item.TextEdit.Range.End.Character);
            Assert.Equal("  connectionReference: pref_agent.shared_office365.abc123", applied);
        }

        [Fact]
        public void ReplacesEmptyValue_AtInsertionPoint()
        {
            var factory = CreateFactoryWithCache();
            var text = "  connectionReference: ";

            var item = Complete(factory, text, text.Length).Single(i => i.Label == DeclaredReference);

            Assert.NotNull(item.TextEdit);
            Assert.Equal(text.Length, item.TextEdit!.Range.Start.Character);
            Assert.Equal(text.Length, item.TextEdit.Range.End.Character);
        }

        [Fact]
        public void DeclaredReference_HasNoDeclareCommandAndSortsFirst()
        {
            var factory = CreateFactoryWithCache();
            var text = "  connectionReference: ";

            var item = Complete(factory, text, text.Length).Single(i => i.Label == DeclaredReference);

            Assert.Null(item.Command);
            Assert.Equal("0", item.SortText);
            Assert.Contains("bound", item.Detail, StringComparison.Ordinal);
        }

        [Fact]
        public void UndeclaredReference_EmitsDeclareCommandAndSortsLast()
        {
            var factory = CreateFactoryWithCache();
            var text = "  connectionReference: ";

            var item = Complete(factory, text, text.Length).Single(i => i.Label == UndeclaredReference);

            Assert.NotNull(item.Command);
            Assert.Equal(DeclareCommand, item.Command!.Command);
            Assert.Equal(UndeclaredReference, Assert.Single(item.Command.Arguments!));
            Assert.Equal("1", item.SortText);
            Assert.Contains("not declared", item.Detail, StringComparison.Ordinal);
        }

        private static IReadOnlyList<CompletionItem> Complete(InMemoryFileAccessorFactory factory, string text, int index)
        {
            var root = new DirectoryPath(WorkspaceRoot);
            var document = new McsLspDocument(new FilePath(WorkspaceRoot + "/actions/Foo.mcs.yml"), text, root);
            var context = new RequestContext(new FakeLanguage(), new Workspace(root), document, index);
            var rule = new ConnectionReferenceCompletionRule(factory);
            return rule.ComputeCompletion(context, new CompletionContext()).ToList();
        }

        private static InMemoryFileAccessorFactory CreateFactoryWithCache()
        {
            var factory = new InMemoryFileAccessorFactory();
            var accessor = factory.Create(new DirectoryPath(WorkspaceRoot));
            var json = "{"
                + "\"schemaVersion\":\"2\","
                + "\"connections\":["
                + $"{{\"connectionReferenceLogicalName\":\"{DeclaredReference}\",\"connectorName\":\"shared_office365\",\"boundConnectionExists\":true,\"isDeclared\":true}},"
                + $"{{\"connectionReferenceLogicalName\":\"{UndeclaredReference}\",\"connectorName\":\"shared_sharepoint\",\"boundConnectionExists\":false,\"isDeclared\":false}}"
                + "]}";

            using var stream = accessor.OpenWrite(new AgentFilePath(CacheRelativePath));
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(json);
            return factory;
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
