
namespace Microsoft.PowerPlatformLS.Impl.Core.Lsp
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System.Text.Json;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.CommonLanguageServerProtocol.Framework.JsonRpc;
    using Microsoft.CommonLanguageServerProtocol.Framework.Handlers;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using System.Diagnostics.CodeAnalysis;
    using Microsoft.PowerPlatformLS.Contracts.Internal.CodeAnalysis;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp.Uris;

    internal class LanguageServer : SystemTextJsonLanguageServer<RequestContext>, ILanguageServer
    {
        private readonly IEnumerable<ILspModule> _extraModules;

        public LanguageServer(IJsonRpcStream jsonRpc, ILspLogger logger, IEnumerable<ILspModule> extraModules) : base(jsonRpc, Constants.DefaultSerializationOptions, logger)
        {
            _extraModules = extraModules;
            // This spins up the queue and ensure the LSP is ready to start receiving requests
            Initialize();
        }

        /// <inheritdoc/>
        /// <remarks>Largely inspired from https://github.com/dotnet/roslyn/blob/main/src/LanguageServer/Protocol/RoslynLanguageServer.cs</remarks>
        public override bool TryGetLanguageForRequest(string methodName, object? serializedParameters, [NotNullWhen(true)] out string? language)
        {
            if (serializedParameters == null)
            {
                Logger.LogInformation("No request parameters given, using default language handler");
                language = LanguageServerConstants.DefaultLanguageName;
                return true;
            }

            // We implement the STJ language server so this must be a JsonElement.
            var parameters = (JsonElement)serializedParameters;

            // For certain requests like text syncing we'll always use the default language handler
            // as we do not want languages to be able to override them.
            if (ShouldUseDefaultLanguage(methodName))
            {
                language = LanguageServerConstants.DefaultLanguageName;
                return true;
            }

            var lspWorkspaceManager = GetLspServices().GetRequiredService<ILanguageProvider>();

            // Phase 1b: Use LspUriFactory for typed URI handling
            var typedLspUri = LspUriFactory.FromJsonElement(parameters, Logger);
            
            // Log with scheme preserved
            Logger.LogInformation($"Processing request for URI: {typedLspUri.Raw}");

            // Handle unsupported URIs with default language
            if (!typedLspUri.IsSupported)
            {
                Logger.LogInformation($"Using default language handler for unsupported URI: {typedLspUri.Raw}");
                language = LanguageServerConstants.DefaultLanguageName;
                return true;
            }

            if (!lspWorkspaceManager.TryGetLanguageForDocument(typedLspUri, out var languageObject))
            {
                Logger.LogError($"Failed to get language for {typedLspUri.Raw}");
                language = null;
                return false;
            }

            language = languageObject.LanguageType.ToString();
            return true;

            static bool ShouldUseDefaultLanguage(string methodName)
            {
                // Roslyn use default language to process file changes event.
                // We define those handlers algo in a base class to use by language that supports those events.
                return methodName switch
                {
                    LspMethods.Initialize => true,
                    LspMethods.Initialized => true,
                    LspMethods.Shutdown => true,
                    LspMethods.Exit => true,
                    _ => false,
                };
            }
        }

        protected override ILspServices ConstructLspServices(IMethodHandler shutdownHandler, IMethodHandler exitHandler)
        {
            var serviceCollection = new ServiceCollection();

            var _ = AddHandlers(serviceCollection)
                .AddSingleton(shutdownHandler)
                .AddSingleton(exitHandler)
                .AddSingleton<ILspLogger>(Logger)
                .AddSingleton<AbstractRequestContextFactory<RequestContext>, RequestContextFactory>()
                .AddSingleton<AbstractHandlerProvider>(s => HandlerProvider)
                .AddSingleton<IInitializeManager<InitializeParams, InitializeResult>, InitializeManager>()
                .AddSingleton<ClientInformation>()
                .AddSingleton<IClientInformation>(p => p.GetRequiredService<ClientInformation>())
                .AddSingleton<IClientInformationInitializer>(p => p.GetRequiredService<ClientInformation>())
                .AddSingleton<ILifeCycleManager, LspLifeCycleManager>()
                .AddSingleton(this);

            var lspServices = new LspServices(serviceCollection);

            return lspServices;
        }

        protected virtual IServiceCollection AddHandlers(IServiceCollection serviceCollection)
        {
            _ = serviceCollection
                .AddSingleton<IMethodHandler, InitializeHandler<InitializeParams, InitializeResult, RequestContext>>()
                .AddSingleton<IMethodHandler, InitializedHandler<InitializedParams, RequestContext>>();

            foreach (var module in _extraModules)
            {
                serviceCollection.Install(module);
            }

            return serviceCollection;
        }
    }
}
