
namespace Microsoft.CommonLanguageServerProtocol.Framework.JsonRpc
{
    using System;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;

    public interface IJsonRpcStream
    {
        Task RunAsync(CancellationToken stoppingToken);

        Func<object?, Task>? DisconnectServerAction { get; set; }

        void AddLocalRpcMethod(MethodInfo handler, object? target, string methodName);
    }
}
