namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent.Methods
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using System;
    using Xunit;

    public class SyncPushHandlerComponentDetectionTests
    {
        private static DialogComponent BuildDialogComponent(string dialogYaml)
        {
            var dialog = CodeSerializer.Deserialize<AdaptiveDialog>(dialogYaml);
            var builder = new DialogComponent.Builder
            {
                SchemaName = new DialogSchemaName("cr123.topic.detect"),
                Id = new BotComponentId(Guid.NewGuid())
            };
            return builder.Build().WithDialog(dialog);
        }

        [Fact]
        public void ComponentReferencesWorkflowOrModel_ModelReference_ReturnsTrue()
        {
            var component = BuildDialogComponent(
                "kind: AdaptiveDialog\n" +
                "beginDialog:\n" +
                "  kind: OnUnknownIntent\n" +
                "  actions:\n" +
                "    - kind: SearchAndSummarizeContent\n" +
                "      aIModelId: 3b5436b4-d7b4-4389-96e8-107446c9094a\n");

            Assert.True(SyncPushHandler.ComponentReferencesWorkflowOrModel(component));
        }

        [Fact]
        public void ComponentReferencesWorkflowOrModel_NoWorkflowOrModel_ReturnsFalse()
        {
            var component = BuildDialogComponent(
                "kind: AdaptiveDialog\n" +
                "beginDialog:\n" +
                "  kind: OnUnknownIntent\n" +
                "  actions:\n" +
                "    - kind: SendActivity\n" +
                "      activity: Hello\n");

            Assert.False(SyncPushHandler.ComponentReferencesWorkflowOrModel(component));
        }

        [Fact]
        public void ComponentReferencesWorkflowOrModel_NullRootElement_ReturnsFalse()
        {
            var component = new DialogComponent.Builder
            {
                SchemaName = new DialogSchemaName("cr123.topic.empty"),
                Id = new BotComponentId(Guid.NewGuid())
            }.Build();

            Assert.False(SyncPushHandler.ComponentReferencesWorkflowOrModel(component));
        }
    }
}
