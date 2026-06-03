namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.PowerFx;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio;
    using System.Linq;
    using Xunit;

    public class EnabledFeaturesTests
    {
        [Fact]
        public void IsEnvironmentFeatureEnabled_UsesDefaultValue()
        {
            var featureConfiguration = new EnabledFeatures();

            Assert.False(featureConfiguration.IsEnvironmentFeatureEnabled(
                FeatureFlags.SecurityHealthCheckEnabled.FeatureName,
                FeatureFlags.SecurityHealthCheckEnabled.DefaultValue));
            Assert.True(featureConfiguration.IsEnvironmentFeatureEnabled("EnabledByDefault", true));
        }

        [Fact]
        public void IsTenantFeatureEnabled_UsesDefaultValue()
        {
            var featureConfiguration = new EnabledFeatures();

            Assert.False(featureConfiguration.IsTenantFeatureEnabled("DisabledByDefault", false));
            Assert.True(featureConfiguration.IsTenantFeatureEnabled("EnabledByDefault", true));
        }

        [Fact]
        public void ValidateMakerConnectorAction_WithConnectionId_DoesNotReportConnectionErrorsOrWarnings()
        {
            var featureConfiguration = new EnabledFeatures();
            var bot = CreateMakerConnectorActionBot(connectionId: "/providers/Microsoft.PowerApps/connections/conRef");

            var validatedBot = bot.Validate(
                validateAcrossComponents: true,
                expressionChecker: new PowerFxExpressionChecker(featureConfiguration),
                featureConfiguration: featureConfiguration);

            var diagnostics = validatedBot
                .DescendantsAndSelf()
                .SelectMany(element => element.Diagnostics)
                .ToArray();

            var connectionNotSetErrors = diagnostics
                .OfType<PropertyError>()
                .Where(error => error.ErrorCode?.Value == ValidationErrorCode.ConnectionNotSet);
            var missingRequiredPropertyErrors = diagnostics
                .OfType<PropertyError>()
                .Where(error => error.ErrorCode?.Value == ValidationErrorCode.MissingRequiredProperty);
            var makerConnectionWarnings = diagnostics
                .OfType<PropertyWarning>()
                .Where(warning => warning.ErrorCode?.Value == ValidationErrorCode.MakerConnectionIsUsed);

            Assert.Empty(connectionNotSetErrors);
            Assert.Empty(missingRequiredPropertyErrors);
            Assert.Empty(makerConnectionWarnings);
        }

        [Fact]
        public void ValidateMakerConnectorAction_WithoutConnectionId_ReportsConnectionNotSet()
        {
            var featureConfiguration = new EnabledFeatures();
            var bot = CreateMakerConnectorActionBot(connectionId: null);

            var validatedBot = bot.Validate(
                validateAcrossComponents: true,
                expressionChecker: new PowerFxExpressionChecker(featureConfiguration),
                featureConfiguration: featureConfiguration);

            var connectionNotSetErrors = validatedBot
                .DescendantsAndSelf()
                .SelectMany(element => element.Diagnostics)
                .OfType<PropertyError>()
                .Where(error => error.ErrorCode?.Value == ValidationErrorCode.ConnectionNotSet)
                .ToArray();

            var error = Assert.Single(connectionNotSetErrors);
            Assert.Equal("ConnectionReference", error.PropertyName);
        }

        private static BotDefinition CreateMakerConnectorActionBot(string? connectionId)
        {
            return new BotDefinition.Builder()
            {
                Entity = new BotEntity.Builder()
                {
                    SchemaName = "cr123_agent",
                    CdsBotId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                    AuthenticationMode = BotAuthenticationMode.Integrated,
                },
                ConnectionReferences =
                {
                    new ConnectionReference.Builder()
                    {
                        Id = new ConnectionReferenceId(Guid.Parse("00000000-0000-0000-0000-000000000003")),
                        ConnectionReferenceLogicalName = "conRef",
                        ConnectorId = "/providers/Microsoft.PowerApps/apis/conRef",
                        ConnectionId = connectionId,
                    },
                },
                ConnectorDefinitions =
                {
                    new ConnectorDefinition.Builder()
                    {
                        ConnectorId = "/providers/Microsoft.PowerApps/apis/conRef",
                        HasPublicData = false,
                        Operations =
                        {
                            new ConnectorOperation.Builder()
                            {
                                OperationId = "opId",
                                InputType = DataType.EmptyRecord,
                                OutputType = DataType.EmptyRecord,
                            },
                        },
                    },
                },
                Components =
                {
                    new DialogComponent.Builder()
                    {
                        SchemaName = "cr123_agent.topic.testMakerConnectorAction",
                        Id = new BotComponentId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
                        Dialog = new AdaptiveDialog.Builder()
                        {
                            BeginDialog = new OnRedirect.Builder()
                            {
                                Id = "beginDialog_test",
                                Actions =
                                {
                                    new InvokeConnectorAction.Builder()
                                    {
                                        OperationId = "opId",
                                        Id = "invokeConnectorAction_test",
                                        ConnectionReference = "conRef",
                                        DynamicInputSchema = DataType.EmptyRecord,
                                        DynamicOutputSchema = DataType.EmptyRecord,
                                        ConnectionProperties = new ConnectionProperties.Builder()
                                        {
                                            Mode = ConnectionMode.Maker,
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            }.Build();
        }
    }
}