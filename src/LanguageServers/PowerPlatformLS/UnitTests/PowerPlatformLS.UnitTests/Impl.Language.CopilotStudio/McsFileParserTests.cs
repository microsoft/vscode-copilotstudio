namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.Agents.ObjectModel.FileProjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Exceptions;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using System;
    using System.IO;
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
        [InlineData("AdaptiveDialog.mcs.yml", typeof(DialogComponent), "AdaptiveDialog", "AdaptiveDialog")]  // have empty display name/description in .mcs.yml file, then use filename/description fallback
        [InlineData("ClosedListEntity.mcs.yml", typeof(CustomEntityComponent), "ClosedListEntity", "ClosedListEntity")] 
        [InlineData("Workspace/LocalWorkspace/variables/Var1.mcs.yml", typeof(GlobalVariableComponent), "Var1", "Var1")]
        [InlineData("Workspace/LocalWorkspace/knowledge/Wikipedia.mcs.yml", typeof(KnowledgeSourceComponent), "Wikipedia", "This knowledge source provides information found in wikipedia.")] // use display name/description from .mcs.yml file
        [InlineData("Workspace/LocalWorkspace/trigger/Whenanewemailarrives.23ef.mcs.yml", typeof(ExternalTriggerComponent), "When a new email arrives (V3)", "Triggered when a new email arrives in a specified folder.")]
        [InlineData("Workspace/LocalWorkspace/skills/CopilotStudioEchoSkill.mcs.yml", typeof(SkillComponent), "CopilotStudioEchoSkill", "This is a sample Agent that can be called by Copilot Studio")]
        [InlineData("Workspace/LocalWorkspace/translations/Greeting.pt-BR.mcs.yml", typeof(TranslationsComponent), "Greeting (pt-BR)", "Greeting topic in Brazilian Portuguese")]
        [InlineData("Workspace/LocalWorkspace/topics/NoNameThankYou.mcs.yml", typeof(DialogComponent), "NoNameThankYou", "NoNameThankYou")]
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
            Assert.Equal(expectedDescription, result.component.Description);
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
        [InlineData("agent1.topic.topic2", "topic2")]
        [InlineData("agent1.topic2", "topic2")]
        [InlineData("topic2", "topic2")]
        [InlineData("agent1.knowledge.wikipedia", "wikipedia")]
        [InlineData("agent1.variables.var1", "var1")]
        [InlineData("agent1.skills.echo", "echo")]
        [InlineData("agent1.trigger.oncreate", "oncreate")]
        [InlineData("", "")]
        public void CompileFileUsesFallbackNameWhenDisplayNameAndDescriptionMissing(string schemaName, string expectedFallback)
        {
            var parser = new McsFileParser();
            var dialog = new AdaptiveDialog();
            var result = parser.CompileFileModel(schemaName, dialog);

            Assert.Null(result.error);
            Assert.NotNull(result.component);

            var dialogComponent = Assert.IsType<DialogComponent>(result.component);

            Assert.Equal(schemaName, dialogComponent.SchemaNameString);
            Assert.Equal(expectedFallback, dialogComponent.DisplayName);
            Assert.Equal(expectedFallback, dialogComponent.Description);
        }
    }
}
