namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.FileProjection;
    using Microsoft.Agents.ObjectModel.Syntax;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Exceptions;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System;
    using System.IO;
    using System.Linq;
    using Xunit;

    public class McsFileParserTests
    {
        [Theory]
        [InlineData("AdaptiveDialog.mcs.yml", true)]
        [InlineData("ClosedListEntity.mcs.yml", true)]
        [InlineData("UnsupportedBotElement.mcs.yml", false)]
        public void CompileFileTest(string fileName, bool isSupported)
        {
            var world = new World();
            var doc = world.AddFile(fileName, elementCheck: isSupported);

            var parser = new McsFileParser();
            var context = new ProjectionContext();

            var result = parser.CompileFile(doc.As<McsLspDocument>(), context);

            if (isSupported)
            {
                Assert.Null(result.error);
            }
            else
            {
                Assert.IsType<UnsupportedBotElementException>(result.error);
            }
        }

        [Fact]
        public void CompileFileNullFileModel()
        {
            var parser = new McsFileParser();

            var mcsDoc = new McsLspDocument(new FilePath("NullFileModel.mcs.yml"), "", new DirectoryPath("c:/agent"));
            var context = new ProjectionContext();

            var result = parser.CompileFile(mcsDoc, context);

            Assert.Null(result.component);
            Assert.IsType<InvalidDataException>(result.error);
        }

        [Theory]
        [InlineData("AdaptiveDialog.mcs.yml", typeof(DialogComponent), "AdaptiveDialog", null)]  // have empty display name in .mcs.yml file, then use filename fallback
        [InlineData("ClosedListEntity.mcs.yml", typeof(CustomEntityComponent), "ClosedListEntity", null)] 
        [InlineData("Workspace/LocalWorkspace/variables/Var1.mcs.yml", typeof(GlobalVariableComponent), "Var1", null)]
        [InlineData("Workspace/LocalWorkspace/topics/NoNameThankYou.mcs.yml", typeof(DialogComponent), "NoNameThankYou", null)]
        [InlineData("Workspace/LocalWorkspace/knowledge/Wikipedia.mcs.yml", typeof(KnowledgeSourceComponent), "Wikipedia", "This knowledge source provides information found in wikipedia.\nThe information is retrieved by performing a search on wikipedia.")] // use display name/description from .mcs.yml file
        [InlineData("Workspace/LocalWorkspace/trigger/Whenanewemailarrives.23ef.mcs.yml", typeof(ExternalTriggerComponent), "When a new email arrives (V3)", "This is a trigger description.\nTriggered when a new email arrives in a specified folder.")]
        [InlineData("Workspace/LocalWorkspace/skills/CopilotStudioEchoSkill.mcs.yml", typeof(SkillComponent), "CopilotStudioEchoSkill", "This is a sample Agent that can be called by Copilot Studio.\nCopilot Studio echo skill.")]
        [InlineData("Workspace/LocalWorkspace/translations/Greeting.pt-BR.mcs.yml", typeof(TranslationsComponent), "Greeting (pt-BR)", "Greeting topic in Brazilian Portuguese")]
        public void CompileFileWithSupportedFile(string fileName, Type expectedComponentType, string expectedDisplayName, string expectedDescription)
        {
            var world = new World();
            var doc = world.AddFile(fileName, elementCheck: true);

            var parser = new McsFileParser();
            var context = new ProjectionContext(BotName: "agent1");

            var result = parser.CompileFile(doc.As<McsLspDocument>(), context);

            Assert.Null(result.error);
            Assert.NotNull(result.component);
            Assert.IsType(expectedComponentType, result.component);
            Assert.StartsWith("agent1.", result.component!.SchemaNameString);
            Assert.Equal(expectedDisplayName, result.component.DisplayName);
            if (expectedDescription != null)
            {
                Assert.Equal(expectedDescription, result.component.Description);
            }
        }

        [Theory]
        [InlineData("AdaptiveDialog.mcs.yml", "agent1.topic.AdaptiveDialog")]
        [InlineData("Workspace/LocalWorkspace/variables/Var1.mcs.yml", "agent1.GlobalVariableComponent.Var1")]
        [InlineData("Workspace/LocalWorkspace/knowledge/Wikipedia.mcs.yml", "agent1.knowledge.Wikipedia")]
        [InlineData("Workspace/LocalWorkspace/translations/Greeting.pt-BR.mcs.yml", "agent1.topic.Greeting.pt-BR")]
        public void CompileFileUsesCorrectSchemaName(
            string fileName,
            string expectedSchemaName)
        {
            var world = new World();
            var doc = world.AddFile(fileName, elementCheck: true);

            var parser = new McsFileParser();
            var context = new ProjectionContext(BotName: "agent1");

            var result = parser.CompileFile(doc.As<McsLspDocument>(), context);

            Assert.Null(result.error);
            Assert.Equal(expectedSchemaName, result.component!.SchemaNameString);
        }

        [Fact]
        public void CompileFileModelSucceedsWithoutPath()
        {
            var parser = new McsFileParser();
            var dialog = new AdaptiveDialog();

            var result = parser.CompileFileModel("agent1.topic.test", dialog);

            Assert.Null(result.error);
            Assert.NotNull(result.component);
            Assert.Equal("agent1.topic.test", result.component!.SchemaNameString);
        }

        [Fact]
        public void CompileFileModel_NullModel_ReturnsUnsupportedBotElementException()
        {
            // Normative: for user-visible LS behavior, CompileFileModel maps null to UnsupportedBotElementException
            // to preserve existing error classification; CompileFile continues to use InvalidDataException.
            var parser = new McsFileParser();

            var result = parser.CompileFileModel("agent1.topic.test", null);

            Assert.Null(result.component);
            Assert.IsType<UnsupportedBotElementException>(result.error);
        }

        [Fact]
        public void CompileFileModel_Throws_WhenSchemaNameIsNull()
        {
            // Normative: legacy CompileFileModel propagated constructor failures instead of converting to error results.
            // Alternative: returning (null, error) could be part of a broader merge/push error-handling redesign.
            var parser = new McsFileParser();
            var dialog = new AdaptiveDialog();

#pragma warning disable NX0002 // Intentionally passing null to test ArgumentNullException behavior
            Assert.Throws<ArgumentNullException>(() => parser.CompileFileModel(null!, dialog));
#pragma warning restore NX0002
        }

        [Theory]
        [InlineData("ElementWithValidMetadata.mcs.yml", "Book 1.xlsx", "This knowledge source provides information found in SharePoint.\nThe information is retrieved by performing a search.", "Note: This is comment note for source")]
        [InlineData("ElementWithInvalidField.mcs.yml", "Book1.xlsx", "This knowledge source provides information found in Book1.xlsx SharePoint.", null)]
        public void TestElementWithMetadata(string fileName, string expectedDisplayName, string expectedDescription, string expectedComment)
        {
            var world = new World();
            var doc = world.AddFile(fileName);

            var parser = new McsFileParser();
            var context = new ProjectionContext();

            var result = parser.CompileFile(doc.As<McsLspDocument>(), context);

            Assert.Null(result.error);
            Assert.NotNull(result.component);
            Assert.Equal(BotElementKind.KnowledgeSourceComponent, result.component!.Kind);

            if (!string.IsNullOrEmpty(expectedComment))
            {
                var syntax = result.component?.RootElement?.Syntax;
                if (syntax != null)
                {
                    var comment = syntax.EnumerateChildren(true, _ => true).OfType<SyntaxTrivia>().FirstOrDefault();

                    if (comment != null)
                    {
                        var lines = comment.Tokens.EnumerateTokens()
                            .Where(t =>
                                t.Kind != Agents.ObjectModel.Syntax.Tokens.SyntaxTokenKind.CarriageReturnLineFeed &&
                                t.Kind != Agents.ObjectModel.Syntax.Tokens.SyntaxTokenKind.LineFeed)
                            .Select(t => t.RawText);

                        Assert.Contains(expectedComment, string.Join("", lines));
                    }
                }
            }

            var extensionData = result.component!.RootElement?.ExtensionData;
            Assert.NotNull(extensionData);

            if (extensionData!.Properties.TryGetValue("mcs.metadata", out var metadataValue))
            {
                var metadataRecord = metadataValue as RecordDataValue;
                Assert.NotNull(metadataRecord);

                var allowedKeys = new[] { "componentName", "description" };
                Assert.All(metadataRecord!.Properties.Keys, k => Assert.Contains(k, allowedKeys));

                if (metadataRecord.Properties.TryGetValue("componentName", out var displayNameValue))
                {
                    var displayName = displayNameValue as StringDataValue;
                    Assert.NotNull(displayName);
                    Assert.Equal(expectedDisplayName, displayName!.Value);
                }

                if (metadataRecord.Properties.TryGetValue("description", out var descriptionValue))
                {
                    var description = descriptionValue as StringDataValue;
                    Assert.NotNull(description);
                    Assert.Equal(expectedDescription, description!.Value);
                }
            }
            else
            {
                Assert.True(false, "mcs.metadata not found in extensionData");
            }
        }
    }
}
