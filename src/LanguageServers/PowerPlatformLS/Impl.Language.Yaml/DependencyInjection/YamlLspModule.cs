namespace Microsoft.PowerPlatformLS.Impl.Language.Yaml.DependencyInjection
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Completion;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Validation;
    using Microsoft.PowerPlatformLS.Impl.Language.Yaml.Completion;
    using Microsoft.PowerPlatformLS.Impl.Language.Yaml.Framework;
    using Microsoft.PowerPlatformLS.Impl.Language.Yaml.Handlers;
    using Microsoft.PowerPlatformLS.Impl.Language.Yaml.Validation;

    public class YamlLspModule : ILspModule
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddCompletionRulesProcessor<YamlLspDocument>();
            services.AddValidationRulesProcessor<YamlLspDocument>();
            services.AddSingleton<ILanguageAbstraction, YamlLanguage>();
            services.AddSingleton<IDiagnosticsProvider<YamlLspDocument>, DiagnosticsProvider>();
            AddLspMethodHandlers(services);
            AddYamlValidationRules(services);
            AddYamlCompletionRules(services);
        }

        private static void AddLspMethodHandlers(IServiceCollection services)
        {
            services
                .AddHandler<CompletionHandler>()
                .AddHandler<DidChangeHandler>()
                .AddHandler<DidOpenHandler>();
        }

        private static void AddYamlValidationRules(IServiceCollection services)
        {
            services.AddSingleton<IValidationRule<YamlLspDocument>, UniqueIdsErrorRule>();
        }

        private static void AddYamlCompletionRules(IServiceCollection services)
        {
            services.AddSingleton<ICompletionRule<YamlLspDocument>, MissingUniqueIdCompletionRule>();
        }
    }
}