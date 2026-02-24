namespace Microsoft.PowerPlatformLS.Impl.Language.PowerFx.DependencyInjection
{

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Framework;
    using Microsoft.PowerPlatformLS.Impl.Language.PowerFx.Framework;
    using Microsoft.PowerPlatformLS.Impl.Language.PowerFx.Handlers;

    public class PowerFxLspModule : ILspModule
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<ILanguageAbstraction, PowerFxLanguage>();
            services.AddSingleton<IDiagnosticsProvider<PowerFxLspDocument>, DiagnosticsProvider>();
            AddMethodHandlers(services);
        }

        private static void AddMethodHandlers(IServiceCollection services)
        {
            // method handlers go here
            services
                .AddHandler<DidChangeHandler>()
                .AddHandler<ComputeSignatureHandler>()
                .AddHandler<CompletionHandler>()
                .AddHandler<DidOpenHandler>();
        }
    }
}