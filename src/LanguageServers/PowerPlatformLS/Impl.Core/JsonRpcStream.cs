

namespace Microsoft.PowerPlatformLS.Impl.Core
{
    using Microsoft.CommonLanguageServerProtocol.Framework.JsonRpc;
    using Microsoft.Extensions.Logging;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Exceptions;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Reflection;
    using System.Text.Json;

    internal sealed class JsonRpcStream : IJsonRpcStream
    {
        private readonly ILspTransport _transport;
        private readonly ILogger<JsonRpcStream> _logger;
        private readonly Dictionary<string, (MethodInfo Method, object? Target)> _rpcMethods = new();

        public JsonRpcStream(ILspTransport transport, ILogger<JsonRpcStream> logger)
        {
            _transport = transport;
            _logger = logger;
        }

        public Func<object?, Task>? DisconnectServerAction { get; set; }

        
        public void AddLocalRpcMethod(MethodInfo handler, object? target, string methodName)
        {
            _rpcMethods[methodName] = (handler, target);
        }

        public async Task RunAsync(CancellationToken stoppingToken)
        {
            await _transport.StartAsync(stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested && _transport.IsActive)
            {
                var message = await _transport.GetNextMessageAsync(stoppingToken).ConfigureAwait(false);
                if (message is JsonRpcResponse errorMessage)
                {
                    _logger.LogWarning($"Error message. Sending message back: '{errorMessage.GetType().Name}' with Error ({errorMessage.Error?.Code}): {errorMessage.Error?.Message}");
                    await _transport.SendAsync(errorMessage, stoppingToken).ConfigureAwait(false);
                }
                else if (message is LspJsonRpcMessage lspMessage)
                {
                    _logger.LogInformation($"Received Message: method={lspMessage.Method}, id={lspMessage.Id}");

                    // Don't await processing task and let them run in parallel.
                    // Scheduling is done in the server queue.
                    _ = ProcessJsonRpcMessageAsync(lspMessage, stoppingToken);
                }
                else
                {
                    _logger.LogError($"Unhandled message : {message.GetType().Name}.");
                }
            }

            if (DisconnectServerAction != null)
            {
                await DisconnectServerAction(null);
            }
        }

        private async Task ProcessJsonRpcMessageAsync(LspJsonRpcMessage request, CancellationToken stoppingToken)
        {
            // Lookup the method by its RPC method name.
            if (!_rpcMethods.TryGetValue(request.Method, out var entry))
            {
                _logger.LogWarning($"Method '{request.Method}' is not registered. Wont't process event with id '{request.Id}'");
                return;
            }

            MethodInfo method = entry.Method;
            object? target = entry.Target;

            BaseJsonRpcMessage? response;
            try
            {
                // We always pass the request Params, if it's null, it's going to act as a parameterless function
                var resultTask = method.Invoke(target, [request.Params, stoppingToken]);

                if (resultTask is Task task)
                {
                    await task;

                    Type returnType = method.ReturnType;
                    if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
                    {
                        // Extract the result dynamically
                        object? result = returnType.GetProperty("Result")?.GetValue(task);
                        _logger.LogTrace($"LSP Method Handler Task completed for {request.Method}({request.Id}) with result: {result ?? Constants.Null}");

                        if (request.Id.HasValue)
                        {
                            // Requests (with an id) MUST always get a response per JSON-RPC 2.0 / LSP spec,
                            // even when the result is null (e.g. shutdown returns {"result": null}).
                            // Use JsonValueKind.Null element (not C# null) so serialization emits "result": null
                            // instead of omitting the field (DefaultIgnoreCondition = WhenWritingNull).
                            var jsonResult = result != null
                                ? (JsonElement)result
                                : JsonSerializer.SerializeToElement<object?>(null);
                            response = new JsonRpcResponse { Id = request.Id, Result = jsonResult };
                        }
                        else
                        {
                            // Notifications (no id) never get a response.
                            response = null;
                        }
                    }
                    else
                    {
                        // Non-generic Task — handler returned no value.
                        if (request.Id.HasValue)
                        {
                            _logger.LogTrace($"LSP Method Handler Task completed for {request.Method}({request.Id}) with no result. Sending null response.");
                            response = new JsonRpcResponse { Id = request.Id, Result = JsonSerializer.SerializeToElement<object?>(null) };
                        }
                        else
                        {
                            _logger.LogTrace($"LSP Notification Handler Task completed for {request.Method}.");
                            response = null;
                        }
                    }
                }
                else
                {
                    throw new InvalidCastException($"Local LSP method handler returned unhandled type: {resultTask?.GetType()}. Expected Task.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error invoking method '{request.Method}': {ex}");
                response = ex.CreateErrorResponseFromException(request.Id);
            }

            try
            {
                if (response != null)
                {
                    await _transport.SendAsync(response, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error sending response to client for '{request.Method}' ({request.Id}): {ex}");
            }
        }
    }
}