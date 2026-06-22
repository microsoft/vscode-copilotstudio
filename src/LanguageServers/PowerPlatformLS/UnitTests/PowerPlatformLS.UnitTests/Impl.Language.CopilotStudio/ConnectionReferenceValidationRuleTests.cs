// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Validation;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Validation;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using Xunit;

    public class ConnectionReferenceValidationRuleTests
    {
        private const string WorkspaceRoot = "c:/agent";
        private const string CacheRelativePath = ".mcs/.connections-cache.json";

        private const string BoundReference = "pref_agent.shared_msnweather.shared-msnweather-bound";
        private const string UnboundReference = "pref_agent.shared_office365.shared-office365-unbound";
        private const string UndeclaredReference = "pref_agent.shared_sharepoint.shared-sharepoint-undeclared";
        private const string UnknownReference = "pref_agent.shared_mystery.shared-mystery-unknown";

        [Fact]
        public void ReportsErrorForUnknownReference()
        {
            var factory = CreateFactoryWithCache();
            var text = string.Join("\n", new[]
            {
                "kind: TaskDialog",
                $"  connectionReference: {UnknownReference}",
            });

            var diagnostics = Validate(factory, text);

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("UnknownConnectionReference", diagnostic.Code);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.NotNull(diagnostic.Range);
            var range = diagnostic.Range!.Value;
            Assert.Equal(1, range.Start.Line);
            Assert.Equal(text.Split('\n')[1].IndexOf(UnknownReference, StringComparison.Ordinal), range.Start.Character);
            Assert.Equal(range.Start.Character + UnknownReference.Length, range.End.Character);
        }

        [Fact]
        public void ReportsWarningForUnboundReference()
        {
            var factory = CreateFactoryWithCache();
            var text = $"  connectionReference: {UnboundReference}";

            var diagnostics = Validate(factory, text);

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("UnboundConnectionReference", diagnostic.Code);
            Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        }

        [Fact]
        public void ReportsErrorForUndeclaredReference()
        {
            var factory = CreateFactoryWithCache();
            var text = $"  connectionReference: {UndeclaredReference}";

            var diagnostics = Validate(factory, text);

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("UndeclaredConnectionReference", diagnostic.Code);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        }

        [Fact]
        public void DoesNotReportForBoundReference()
        {
            var factory = CreateFactoryWithCache();
            var text = $"  connectionReference: {BoundReference}";

            var diagnostics = Validate(factory, text);

            Assert.Empty(diagnostics);
        }

        [Theory]
        [InlineData("'")]
        [InlineData("\"")]
        public void ReportsWarningForQuotedUnboundReference(string quote)
        {
            var factory = CreateFactoryWithCache();
            var text = $"  connectionReference: {quote}{UnboundReference}{quote}";

            var diagnostics = Validate(factory, text);

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("UnboundConnectionReference", diagnostic.Code);
            Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
            var range = diagnostic.Range!.Value;
            Assert.Equal(text.IndexOf(UnboundReference, StringComparison.Ordinal), range.Start.Character);
            Assert.Equal(range.Start.Character + UnboundReference.Length, range.End.Character);
        }

        [Theory]
        [InlineData("'")]
        [InlineData("\"")]
        public void DoesNotReportForQuotedBoundReference(string quote)
        {
            var factory = CreateFactoryWithCache();
            var text = $"  connectionReference: {quote}{BoundReference}{quote}";

            var diagnostics = Validate(factory, text);

            Assert.Empty(diagnostics);
        }

        [Fact]
        public void DoesNotReportWhenCacheMissing()
        {
            var factory = new InMemoryFileAccessorFactory();
            var text = $"  connectionReference: {UnknownReference}";

            var diagnostics = Validate(factory, text);

            Assert.Empty(diagnostics);
        }

        [Fact]
        public void IgnoresUnrelatedKeys()
        {
            var factory = CreateFactoryWithCache();
            var text = string.Join("\n", new[]
            {
                $"  connectionReferenceLogicalName: {UnknownReference}",
                "  connectionReferences:",
            });

            var diagnostics = Validate(factory, text);

            Assert.Empty(diagnostics);
        }

        private static IReadOnlyList<Diagnostic> Validate(InMemoryFileAccessorFactory factory, string text)
        {
            var root = new DirectoryPath(WorkspaceRoot);
            var document = new McsLspDocument(new FilePath(WorkspaceRoot + "/actions/Foo.mcs.yml"), text, root);
            var context = new RequestContext(new FakeLanguage(), new Workspace(root), document, 0);
            IValidationRule<McsLspDocument> rule = new ConnectionReferenceValidationRule(factory);
            return rule.ComputeValidation(context, document).ToList();
        }

        private static InMemoryFileAccessorFactory CreateFactoryWithCache()
        {
            var factory = new InMemoryFileAccessorFactory();
            var accessor = factory.Create(new DirectoryPath(WorkspaceRoot));
            var json = "{"
                + "\"schemaVersion\":\"1\","
                + "\"connections\":["
                + $"{{\"connectionReferenceLogicalName\":\"{BoundReference}\",\"boundConnectionExists\":true}},"
                + $"{{\"connectionReferenceLogicalName\":\"{UnboundReference}\",\"boundConnectionExists\":false}},"
                + $"{{\"connectionReferenceLogicalName\":\"{UndeclaredReference}\",\"boundConnectionExists\":false,\"isDeclared\":false}}"
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
