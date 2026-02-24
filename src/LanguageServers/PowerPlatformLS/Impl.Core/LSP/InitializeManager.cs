namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Globalization;

    internal class InitializeManager : IInitializeManager<InitializeParams, InitializeResult>
    {
        private readonly ICapabilitiesProvider _capabilities;
        private readonly IClientInformation _clientInfo;
        private readonly IClientInformationInitializer _clientInfoInitializer;

        public InitializeManager(ICapabilitiesProvider capabilities, ILspLogger logger, IClientInformation clientInformation, IClientInformationInitializer clientInfoInitializer)
        {
            _capabilities = capabilities;
            _clientInfo = clientInformation;
            _clientInfoInitializer = clientInfoInitializer;
        }

        public void SetInitializeParams(InitializeParams request)
        {
            _clientInfoInitializer.Initialize(request);
        }

        public InitializeResult GetInitializeResult()
        {
            var serverCapabilities = new ServerCapabilities()
            {
                TextDocumentSync = new TextDocumentSyncOptions
                {
                    OpenClose = true,
                    Change = TextDocumentSyncKind.Incremental,
                    Save = new SaveOptions { IncludeText = false },
                },
                CompletionProvider = new CompletionOptions
                {
                    ResolveProvider = false,
                    TriggerCharacters = _capabilities.TriggerCharacters.ToArray(),
                },
                SignatureHelpProvider = new SignatureHelpOptions
                {
                    TriggerCharacters = ["(", GetListSeparator(_clientInfo.CultureInfo)]
                },
                DefinitionProvider = true,
                SemanticTokensProvider = new SemanticTokensOptions
                {
                    DocumentSelector = [
                        new DocumentFilter
                        {
                            Language = Constants.LanguageIds.CopilotStudio
                        }
                    ],
                    Legend = new SemanticTokensLegend
                    {
                        TokenTypes = Enum.GetNames(typeof(SemanticTokenType))
                                   .Select(name => name.ToLower())
                                   .ToArray(),
                        TokenModifiers = Enum.GetNames(typeof(SemanticTokenModifier))
                                   .Select(name => name.ToLower())
                                   .ToArray()
                    }
                },
                CodeActionProvider = new CodeActionOptions
                {
                    CodeActionKinds = [
                        CodeActionKind.QuickFix,
                    ],
                },
                Workspace = new ServerWorkspaceCapabilities
                {
                    FileOperations = new FileOperationCapabilities
                    {
                        DidRename = new FileOperationRegistrationOptions
                        {
                            Filters = [
                                new FileOperationFilter
                                {
                                    Pattern = new FileOperationPattern
                                    {
                                        Glob = "**/*",
                                    },
                                }
                            ]
                        },
                    },
                }
            };

            var initializeResult = new InitializeResult
            {
                ServerInfo = new LspServerInfo
                {
                    Name = "Power Platform Pro-Code Language Server",
                    Version = "1.0",
                },
                Capabilities = serverCapabilities,
            };

            return initializeResult;
        }

        public InitializeParams GetInitializeParams()
        {
            return _clientInfo.InitializeParams;
        }

        private static string GetListSeparator(CultureInfo culture)
        {
            var decimalSeparator = culture.NumberFormat.NumberDecimalSeparator;
            return decimalSeparator == "." ? "," : ";";
        }
    }
}