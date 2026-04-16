namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.DependencyInjection;
    using Moq;
    using System;
    using System.Globalization;
    using Xunit;

    public class McsLspDocumentTests
    {
        [Fact]
        public void InvalidRelativeFilePath_ParsesUsingKind()
        {
            var serviceCollection = new ServiceCollection();
            serviceCollection.Install(new McsLspModule());
            serviceCollection.AddSingleton<IClientInformation, ClientInformation>();
            serviceCollection.AddSingleton(Mock.Of<ILspLogger>());
            serviceCollection.AddSingleton(Mock.Of<ILspServices>());
            serviceCollection.AddSingleton(Mock.Of<IClientWorkspaceFileProvider>());
            serviceCollection.AddSingleton(Mock.Of<ILspTransport>());
            var serviceProvider = serviceCollection.BuildServiceProvider();
            var mcsLanguage = serviceProvider.GetRequiredService<ILanguageAbstraction>();

            LspDocument CreateDocument(string text)
            {
                var path = new FilePath("c:/workspace/invalid/file.mcs.yml");
                return mcsLanguage.CreateDocument(path, text, CultureInfo.InvariantCulture, new DirectoryPath("c:/workspace"));
            }

            var document = CreateDocument(string.Empty);
            var semanticDoc = Assert.IsAssignableFrom<LspDocument<BotElement>>(document);
            var fileElement = Assert.IsAssignableFrom<BotElement>(semanticDoc.FileModel);
            Assert.False(document.ParsingInfo.HasError);
        }

        // Ensure we can convert between SyntaxNode and BotElement.
        [Fact]
        public void Success_RoundTripElementToSyntaxConversion()
        {
            var world = new World();
            var doc = world.AddFile("AdaptiveDialog.mcs.yml");

            var rootElement = world.GetFileElement(doc);

            // Get cursor position for an element,
            // and then convert to SyntaxNode / BotElement
            var text = doc.Text;
            int index = text.IndexOf("sendMessage_8hdfd8") + 1;

            // ! not null
            var fileSyntax = rootElement.Syntax!;
            SyntaxNode syntaxNodeAtCursor = fileSyntax.GetSyntaxNodeAtPosition(index);

            Assert.Equal("sendMessage_8hdfd8", ((SyntaxToken)syntaxNodeAtCursor).RawText);

            // ! not-null
            var span = syntaxNodeAtCursor.Parent!.FullSpan;
            var whole = text.Substring(span.Start, span.Length).Trim();
            Assert.Equal("id: sendMessage_8hdfd8", whole);

            var e3 = syntaxNodeAtCursor.GetElement();

            Assert.IsType<Microsoft.Agents.ObjectModel.SendActivity>(e3);
            var e4 = (Microsoft.Agents.ObjectModel.SendActivity)e3;
            Assert.Equal("sendMessage_8hdfd8", e4.Id);
        }

        [Fact]
        public void Success_RoundTripExpressionElementToSyntaxConversion()
        {
            var world = new World();
            var doc = world.AddFile("topic2.mcs.yml");

            var rootElement = world.GetFileElement(doc);

            var text = doc.Text;
            int index = text.IndexOf("125") + 1;

            // ! not null
            var fileSyntax = rootElement.Syntax!;
            SyntaxNode syntaxNodeAtCursor = fileSyntax.GetSyntaxNodeAtPosition(index);

            Assert.Equal("=125<456", ((SyntaxToken)syntaxNodeAtCursor).RawText);

            // ! not null
            var span = syntaxNodeAtCursor.Parent!.FullSpan;
            var whole = text.Substring(span.Start, span.Length).Trim();
            Assert.Equal("condition: =125<456", whole);

            var e3 = syntaxNodeAtCursor.GetElement();

            Assert.IsType<BoolExpression>(e3);

            var e4 = (ConditionItem?)e3.Parent;
            Assert.NotNull(e4);

            // ! not null
            Assert.Equal("conditionItem_tlGIVo", e4!.Id);
        }
    }
}
