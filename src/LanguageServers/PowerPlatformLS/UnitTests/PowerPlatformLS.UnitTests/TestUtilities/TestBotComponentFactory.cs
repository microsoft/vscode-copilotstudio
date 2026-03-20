namespace Microsoft.PowerPlatformLS.UnitTests.TestUtilities
{
    using Microsoft.Agents.ObjectModel;
    using System;

    internal class TestBotComponentFactory
    {
        private readonly DialogSchemaName _schemaName;
        private readonly BotComponentId _id;
        private readonly string _displayName = "Thank you";
        private readonly string _description = "This topic triggers when the user says thank you.";

        public TestBotComponentFactory(string schemaName)
        {
            _schemaName = new DialogSchemaName(schemaName);
            _id = new BotComponentId(Guid.NewGuid());
        }

        public DialogComponent CreateDialogComponent(string dynamicText, bool includeMetadata = false)
        {
            // Read the base YAML template from test data
            var yamlTemplate = TestDataReader.GetTestData("thankYouTopic.mcs.yml");

            // Inject the dynamic text into the template
            var yamlContent = yamlTemplate.Replace("__TEXT__", dynamicText);
            if (includeMetadata)
            {
                var metadata = $"mcs.metadata:{Environment.NewLine}" +
                               $"  componentName: {_displayName}{Environment.NewLine}" +
                               $"  description: {_description}{Environment.NewLine}";
                yamlContent = metadata + yamlContent;
            }

            // Deserialize the final YAML into an AdaptiveDialog object
            var dialog = CodeSerializer.Deserialize<AdaptiveDialog>(yamlContent);

            var dialogComponentBuilder = new DialogComponent.Builder
            {
                SchemaName = _schemaName,
                Id = _id,
                DisplayName = _displayName,
                Description = _description
            };

            return dialogComponentBuilder.Build().WithDialog(dialog);
        }
    }
}