namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Language.CopilotStudio
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.PowerFx;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Utilities;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.Linq;
    using Xunit;

    public class ObjectModelExtensionsTests
    {
        private const string TestData = "kind: Unknown";
        private const string DataverseConnectorId = "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps";
        private const string Office365UsersConnectorId = "/providers/Microsoft.PowerApps/apis/shared_office365users";
        private const string ConnectionReferenceName = "cr123_agent.shared_commondataserviceforapps.ref";

        [Theory]
        [InlineData(typeof(UnknownBotElementIssue.Builder), DiagnosticSeverity.Information)]
        [InlineData(typeof(PropertyWarning.Builder), DiagnosticSeverity.Warning)]
        [InlineData(typeof(IncorrectTypeError.Builder), DiagnosticSeverity.Error)]
        [InlineData(typeof(PropertyError.Builder), DiagnosticSeverity.Error)]
        [InlineData(typeof(InvalidReferenceError.Builder), DiagnosticSeverity.Error)]
        [InlineData(typeof(ExpressionError.Builder), DiagnosticSeverity.Error)]
        public void Default_OnDiagnosticConversion(Type builderType, DiagnosticSeverity expectedSeverity)
        {
            var botDiag = ((BotElementDiagnostic.Builder)Activator.CreateInstance(builderType)!).Build();
            var lspDiag = botDiag.ToLspDiagnostics(new UnknownBotElement.Builder().Build(), new MarkResolver(TestData)).Single();
            Assert.Equal(expectedSeverity, lspDiag.Severity);
            Assert.Equal(PowerPlatformLS.Contracts.Lsp.Models.Range.Zero, lspDiag.Range);
        }

        [Theory]
        [InlineData("=1", 1)]
        [InlineData("1", 0)]
        public void Success_OnExpressionErrorWithSyntax(string syntaxData, int errorColumn)
        {
            var botDiag = new ExpressionError.Builder().Build();
            var botElement = CodeSerializer.Deserialize<BotElement>(syntaxData, new Uri("file:///c:/test.yml"));
            Assert.NotNull(botElement);
            var lspDiag = botDiag.ToLspDiagnostics(botElement, new MarkResolver(TestData)).Single();
            Assert.Equal(Utils.CreateRange(0, errorColumn, 0, errorColumn), lspDiag.Range);
        }

        [Fact]
        public void ToLspDiagnostics_ConnectorActionNotSupportedWithPlaceholderConnectorMetadata_ReportsGetChangesWarning()
        {
            var (action, botDiag) = ValidateDataverseListRowsAction(new ConnectorDefinition.Builder()
            {
                ConnectorId = DataverseConnectorId,
            });

            var lspDiag = botDiag.ToLspDiagnostics(action, new MarkResolver(TestData)).Single();

            Assert.Equal(DiagnosticSeverity.Warning, lspDiag.Severity);
            Assert.Equal(nameof(ValidationErrorCode.ConnectorActionNotSupported), lspDiag.Code);
            Assert.Equal("Connector metadata for 'shared_commondataserviceforapps' hasn't been downloaded to this workspace, so the action 'ListRecordsWithOrganization' couldn't be checked. Run 'Copilot Studio: Get changes' to download it.", lspDiag.Message);
        }

        [Fact]
        public void ToLspDiagnostics_ConnectorActionNotSupportedWithDownloadedConnectorMetadata_ReportsError()
        {
            var (action, botDiag) = ValidateDataverseListRowsAction(DownloadedDataverseConnectorDefinition());

            var lspDiag = botDiag.ToLspDiagnostics(action, new MarkResolver(TestData)).Single();

            Assert.Equal(DiagnosticSeverity.Error, lspDiag.Severity);
            Assert.Equal(nameof(ValidationErrorCode.ConnectorActionNotSupported), lspDiag.Code);
            Assert.Equal(botDiag.ErrorMessage, lspDiag.Message);
        }

        [Fact]
        public void ToLspDiagnostics_ConnectorActionNotSupportedWithTriggerOnlyConnectorMetadata_ReportsError()
        {
            var connectorDefinition = DownloadedDataverseConnectorDefinition();
            connectorDefinition.Operations.Clear();
            connectorDefinition.Triggers.Add(new ConnectorTriggerOperation.Builder()
            {
                TriggerId = "SubscribeWebhookTrigger",
                InputType = DataType.EmptyRecord,
                OutputType = DataType.EmptyRecord,
            });
            var (action, botDiag) = ValidateDataverseListRowsAction(connectorDefinition);

            var lspDiag = botDiag.ToLspDiagnostics(action, new MarkResolver(TestData)).Single();

            Assert.Equal(DiagnosticSeverity.Error, lspDiag.Severity);
            Assert.Equal(nameof(ValidationErrorCode.ConnectorActionNotSupported), lspDiag.Code);
            Assert.Equal(botDiag.ErrorMessage, lspDiag.Message);
        }

        [Theory]
        [InlineData("CR123_AGENT.SHARED_COMMONDATASERVICEFORAPPS.REF")]
        [InlineData(ConnectionReferenceName)]
        public void ToLspDiagnostics_ConnectorActionNotSupportedWithPlaceholderMetadataOnEarlierSameNamedReference_ReportsError(string earlierReferenceName)
        {
            var bot = CreateDataverseListRowsBot(DownloadedDataverseConnectorDefinition());
            bot.ConnectionReferences.Insert(0, new ConnectionReference.Builder()
            {
                Id = new ConnectionReferenceId(Guid.Parse("00000000-0000-0000-0000-000000000004")),
                ConnectionReferenceLogicalName = earlierReferenceName,
                ConnectorId = Office365UsersConnectorId,
                ConnectionId = "/providers/Microsoft.PowerApps/connections/office365users",
            });
            bot.ConnectorDefinitions.Add(new ConnectorDefinition.Builder()
            {
                ConnectorId = Office365UsersConnectorId,
            });
            var (action, botDiag) = ValidateConnectorAction(bot);

            var lspDiag = botDiag.ToLspDiagnostics(action, new MarkResolver(TestData)).Single();

            Assert.Equal(DiagnosticSeverity.Error, lspDiag.Severity);
            Assert.Equal(nameof(ValidationErrorCode.ConnectorActionNotSupported), lspDiag.Code);
            Assert.Equal(botDiag.ErrorMessage, lspDiag.Message);
        }

        private static ConnectorDefinition.Builder DownloadedDataverseConnectorDefinition() => new ConnectorDefinition.Builder()
        {
            ConnectorId = DataverseConnectorId,
            Version = 639299305600560213,
            Operations =
            {
                new ConnectorOperation.Builder()
                {
                    OperationId = "ListRecords",
                    InputType = DataType.EmptyRecord,
                    OutputType = DataType.EmptyRecord,
                },
            },
        };

        private static (InvokeConnectorAction Action, BotElementDiagnostic Diagnostic) ValidateDataverseListRowsAction(ConnectorDefinition.Builder connectorDefinition) =>
            ValidateConnectorAction(CreateDataverseListRowsBot(connectorDefinition));

        private static BotDefinition.Builder CreateDataverseListRowsBot(ConnectorDefinition.Builder connectorDefinition)
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
                        ConnectionReferenceLogicalName = ConnectionReferenceName,
                        ConnectorId = DataverseConnectorId,
                        ConnectionId = "/providers/Microsoft.PowerApps/connections/dataverse",
                    },
                },
                ConnectorDefinitions = { connectorDefinition },
                Components =
                {
                    new DialogComponent.Builder()
                    {
                        SchemaName = "cr123_agent.topic.listContacts",
                        Id = new BotComponentId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
                        Dialog = new AdaptiveDialog.Builder()
                        {
                            BeginDialog = new OnRedirect.Builder()
                            {
                                Id = "beginDialog_listContacts",
                                Actions =
                                {
                                    new InvokeConnectorAction.Builder()
                                    {
                                        Id = "invokeConnectorAction_listContacts",
                                        OperationId = "ListRecordsWithOrganization",
                                        ConnectionReference = ConnectionReferenceName,
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
            };
        }

        private static (InvokeConnectorAction Action, BotElementDiagnostic Diagnostic) ValidateConnectorAction(BotDefinition.Builder bot)
        {
            var featureConfiguration = DefaultFeatureConfiguration.Instance;
            var validatedBot = bot.Build().Validate(
                validateAcrossComponents: true,
                expressionChecker: new PowerFxExpressionChecker(featureConfiguration),
                featureConfiguration: featureConfiguration);

            var action = validatedBot.DescendantsAndSelf().OfType<InvokeConnectorAction>().Single();
            var diagnostic = action.Diagnostics
                .OfType<PropertyError>()
                .Single(error => error.ErrorCode?.Value == ValidationErrorCode.ConnectorActionNotSupported);
            return (action, diagnostic);
        }
    }
}
