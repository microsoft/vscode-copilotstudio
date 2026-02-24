namespace Microsoft.PowerPlatformLS.Impl.Core.DependencyInjection
{
    using Microsoft.CommonLanguageServerProtocol.Framework;
    using Microsoft.CommonLanguageServerProtocol.Framework.JsonRpc;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.PowerPlatformLS.Impl.Core.Lsp;
    using Microsoft.PowerPlatformLS.Impl.Core.IpcTransport;
    using System;
    using Microsoft.Extensions.Logging;
    using System.Runtime.InteropServices;

    public static class HostApplicationBuilderExtensions
    {
        public static IHostApplicationBuilder AddLsp(this IHostApplicationBuilder builder, string[] args)
        {
            // services registered on the host builder are needed to launch the server
            builder.Configuration.AddLspCommandLine(args);
            builder.Services.AddLspTransport();
            builder.Services.AddSingleton<IJsonRpcStream, JsonRpcStream>();
            builder.Services.AddSingleton<ILspLogger, LspLogger>();
            builder.Services.AddSingleton<ILanguageServer, LanguageServer>();
            builder.Services.AddHostedService<LanguageServerListener>();

            // only services registered in modules will be available within the language server
            builder.Services.AddSingleton<ILspModule, CoreLspModule>();
            return builder;
        }

        private static void AddLspCommandLine(this IConfigurationManager configuration, string[] args)
        {
            var switchMappings = new Dictionary<string, string>
            {
                ["--file"] = FileTransport,
                ["--pipe"] = NamedPipeTransport,
                ["--stdio"] = StdioTransport,
                ["--debugger"] = IsDebuggerRequestedName,
            };

            configuration.AddCommandLine(args, switchMappings);
        }

        private static IServiceCollection AddLspTransport(this IServiceCollection services)
        {
            services.AddSingleton(CreateIpcTransport);
            return services;
        }

        // Section Names in the config. 
        const string NamedPipeTransport = "pipe";
        const string StdioTransport = "stdio";
        const string FileTransport = "lspfile";
        const string IsDebuggerRequestedName = "isDebuggerRequested";

        public static bool IsDebuggerRequested(this IConfiguration config)
        {
            return config.GetValue<bool>(IsDebuggerRequestedName);
        }

        private static ILspTransport CreateIpcTransport(IServiceProvider serviceProvider)
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var logger = serviceProvider.GetRequiredService<ILogger<BaseIpcTransport>>();

            var file = configuration.GetValue<string>(FileTransport);
            
            var namedPipeInfo = configuration.GetValue<string>(NamedPipeTransport);
            // TODO: Need to fix couple of issues with stdio transport.
            // need to fix couple of issues with stdio transport
            var stdIoInfo = configuration.GetSection(StdioTransport) != null;
            ILspTransport? transport = null;

            if (file != null)
            {
                transport = new JsonFileIpc(file);
            }
            else if (!string.IsNullOrWhiteSpace(namedPipeInfo))
            {
                // Pipes are windows only. For non-windows, VSC will generate a unix domain socket. 
                bool useNamedPipes = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

                if (useNamedPipes)
                {
                    logger.LogInformation($"Creating IPC: namedpipe={namedPipeInfo}");
                    transport = new NamedPipeIpc(namedPipeInfo, logger);
                }
                else
                {
                    logger.LogInformation($"Creating IPC: unixdomainsockets={namedPipeInfo}");
                    transport = new UnixDomainSocketsIPC(namedPipeInfo, logger);
                }
            }
            else if (stdIoInfo)
            {
                logger.LogInformation($"Creating IPC: stdio");
                transport = new StdInOutIpc(logger);
            }
            else
            {
                throw new ArgumentException("Transport Choice is not specified");
            }

            return transport;
        }
    }
}