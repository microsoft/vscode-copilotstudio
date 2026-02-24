namespace Microsoft.PowerPlatformLS.Impl.Core.DependencyInjection
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.PowerPlatformLS.Impl.Core.IO;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp.Handlers;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers;

    internal class CoreLspModule : ILspModule
    {
        private readonly ILspTransport _transport;

        public CoreLspModule(ILspTransport transport)
        {
            _transport = transport;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services
                .AddSingleton(_transport)
                .AddSingleton<IFileProviderFactory, PhysicalFileProviderFactory>()
                .AddSingleton<IClientWorkspaceFileProvider, ClientWorkspaceFileProvider>()
                .AddSingleton<ILanguageProvider, LspLanguageProvider>()
                .AddSingleton<IRequestContextResolver, RequestContextResolver>()
                .AddSingleton<ICapabilitiesProvider, CapabilitiesProvider>()
                .AddSingleton<IDiagnosticsPublisher, DiagnosticsPublisher>();
            AddDefaultHandlers(services);
        }

        private static void AddDefaultHandlers(IServiceCollection services)
        {
            services.AddHandler<SignatureHelpHandler>();
            services.AddHandler<DidChangeWatchedFilesHandler>();
            services.AddHandler<DidSaveHandler>();
            services.AddHandler<DidCloseHandler>();
            services.AddHandler<CodeActionHandler>();
            services.AddHandler<DidRenameHandler>();
            services.AddHandler<ListWorkspacesHandler>();
        }
    }
}