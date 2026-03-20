namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Completion;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Validation;

    /// <summary>
    /// Installation extensions for the service collection.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Add the bindings in the module to the service collection.
        /// </summary>
        /// <param name="services">The current service collection to the the module to.</param>
        /// <param name="module">A re-usable module of bindings to add to the current service collection.</param>
        /// <returns>The IServiceCollection instance</returns>
        public static IServiceCollection Install(this IServiceCollection services, IServiceCollectionModule module)
        {
            module.ConfigureServices(services);
            return services;
        }

        public static IServiceCollection AddValidationRulesProcessor<DocType>(this IServiceCollection services)
            where DocType : LspDocument
        {
            services.AddSingleton<IValidationRulesProcessor<DocType>, ValidationRulesProcessor<DocType>>();
            return services;
        }

        public static IServiceCollection AddCompletionRulesProcessor<DocType>(this IServiceCollection services)
            where DocType : LspDocument
        {
            // non-generic registration allow to list all processors. e.g. CapabilityProvider
            services.AddSingleton<ICompletionRulesProcessor, CompletionRulesProcessor<DocType>>();

            // specific processor is used for processing completion requests for a specific model type
            services.AddSingleton<ICompletionRulesProcessor<DocType>, CompletionRulesProcessor<DocType>>();
            return services;
        }

        public static IServiceCollection AddHandler<HandlerType>(this IServiceCollection services)
            where HandlerType : class, IMethodHandler
        {
            services.AddSingleton<IMethodHandler, HandlerType>();
            return services;
        }
    }
}
