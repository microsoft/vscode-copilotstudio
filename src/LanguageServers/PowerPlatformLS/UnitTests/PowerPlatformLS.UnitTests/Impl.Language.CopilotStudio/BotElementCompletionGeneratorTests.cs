namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion.Generators;
    using Schema = Microsoft.Agents.ObjectModel.Schema;
    using Xunit;

    public class BotElementCompletionGeneratorTests
    {
        [Fact]
        public void TryGenerateCompletionSnippets_GptComponentSchemaNameReference_UsesBotDefinitionComponents()
        {
            var sut = new BotElementCompletionGenerator();
            var definition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName("cr123")),
                Components =
                {
                    new GptComponent(id: Guid.NewGuid(), displayName: "Component One", schemaName: ".gpt.default")
                }
            }.Build();

            var ok = sut.TryGenerateCompletionSnippets(Schema.PrimitiveKind.GptComponentSchemaNameReference, definition, out var snippets);

            Assert.True(ok);
            Assert.Equal([".gpt.default"], snippets.ToArray());
        }

        [Fact]
        public void TryGenerateCompletionSnippets_GptComponentSchemaNameReference_WithNoComponents()
        {
            var sut = new BotElementCompletionGenerator();
            var definition = new BotDefinition.Builder
            {
                Entity = new BotEntity().WithSchemaName(new BotEntitySchemaName("cr123")),
            }.Build();

            var ok = sut.TryGenerateCompletionSnippets(Schema.PrimitiveKind.GptComponentSchemaNameReference, definition, out var snippets);

            Assert.False(ok);
            Assert.True(snippets.IsDefault);
        }

        [Fact]
        public void TryGenerateCompletionSnippets_BoolBuiltInValues()
        {
            var sut = new BotElementCompletionGenerator();

            var ok = sut.TryGenerateCompletionSnippets(Schema.PrimitiveKind.@bool, definition: null, out var snippets);

            Assert.True(ok);
            Assert.Equal(["true", "false"], snippets.ToArray());
        }
    }
}
