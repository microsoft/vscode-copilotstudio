namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.DependencyInjection
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.Agents.ObjectModel.Abstractions;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.Framework;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Completion;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Validation;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Utilities;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion.Generators;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Completion.Handlers;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Framework;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Handlers;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Models;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Resources;
    using Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio.Validation;

    public class McsLspModule : ILspModule
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddCompletionRulesProcessor<McsLspDocument>();
            services.AddValidationRulesProcessor<McsLspDocument>();
            services.AddSingleton<IAgentFilesAnalyzer, AgentFilesAnalyzer>();
            services.AddSingleton<IValidationRule<McsLspDocument>, BotElementDiagnosticsValidationRule>();
            services.AddSingleton<ILanguageAbstraction, McsLanguage>();
            services.AddSingleton<IFeatureConfiguration, EnabledFeatures>();
            services.AddSingleton<IDiagnosticsProvider<McsLspDocument>, DiagnosticsProvider>();
            services.AddSingleton<IWorkspaceCompiler<DefinitionBase>, McsWorkspaceCompiler>();
            services.AddSingleton<IReferenceResolver, McsReferenceResolver>();
            services.AddSingleton<Contracts.FileLayout.IMcsFileParser, McsFileParser>();
            services.AddSingleton<IComponentPathResolver, LspComponentPathResolver>();
            services.AddSingleton<IStringResources, StringResources>();
            services.AddSingleton<IBotElementCompletionGenerator, BotElementCompletionGenerator>();

            services.AddCompletionHandler<CompletePropertyNameHandler, NewPropertyCompletionEvent>();
            services.AddCompletionHandler<PowerFxCompletionHandler, CompletionEvent>();
            services.AddCompletionHandler<UnknownElementHandler, EditPropertyValueCompletionEvent>();
            services.AddCompletionHandler<EditPropertyValueHandler, EditPropertyValueCompletionEvent>();

            AddMcsHandlers(services);
            services.AddCompletionRule<CopilotStudioCompletionRule>();

            // Signature help, this is for Power Fx expressions 
            services.AddHandler<PowerFxSignatureHandler>();
            services.AddHandler<GetCloudCacheFileHandler>();
        }

        private static void AddMcsHandlers(IServiceCollection services)
        {
            // handlers go here
            services
                .AddHandler<CompletionHandler>()
                .AddHandler<DidChangeHandler>()
                .AddHandler<DidOpenHandler>()
                .AddHandler<GoToDefinitionHandler>()
                .AddHandler<SemanticTokenFullHandler>();
        }
    }

    internal static class ModuleExtensions
    {
        public static IServiceCollection AddCompletionRule<RuleType>(this IServiceCollection services)
            where RuleType : class, ICompletionRule<McsLspDocument>
        {
            services.AddSingleton<ICompletionRule<McsLspDocument>, RuleType>();
            return services;
        }

        public static IServiceCollection AddCompletionHandler<CompletionType, EventType>(this IServiceCollection services)
            where EventType : CompletionEvent
            where CompletionType : class, ICompletionEventHandler<EventType>
        {
            // The real completion handler will request: IEnumerable<ICompletionEventHandler>.
            // So the CompletionAdapter will implement a ICompletionEventHandler. 
            // and then invoke the type-safe CompletionType.
            services.AddSingleton<CompletionType>();            

            services.AddSingleton<ICompletionEventHandler, CompletionAdapter<EventType, CompletionType>>();
            return services;

        }
    }
}