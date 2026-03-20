// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CommonLanguageServerProtocol.Framework
{
    using Microsoft.CommonLanguageServerProtocol.Framework.JsonRpc;
    using System;
    using System.Reflection;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public abstract class SystemTextJsonLanguageServer<TRequestContext>(
        IJsonRpcStream jsonRpc, JsonSerializerOptions options, ILspLogger logger, AbstractTypeRefResolver? typeRefResolver = null)
        : AbstractLanguageServer<TRequestContext>(jsonRpc, logger, typeRefResolver)
    {
        /// <summary>
        /// JsonSerializer options used by streamjsonrpc (and for serializing / deserializing the requests to streamjsonrpc).
        /// </summary>
        private readonly JsonSerializerOptions _jsonSerializerOptions = options;

        public override TRequest DeserializeRequest<TRequest>(object? serializedRequest, RequestHandlerMetadata metadata)
        {
            if (serializedRequest is null)
            {
                if (metadata.RequestTypeRef is not null)
                {
                    throw new InvalidOperationException($"Handler {metadata.HandlerDescription} requires request parameters but received none");
                }
                else
                {
                    // We checked that TRequest is typeof(NoValue).
                    return (TRequest)(object)NoValue.Instance;
                }
            }

            if (metadata.RequestTypeRef is null)
            {
                throw new InvalidOperationException($"Handler {metadata.HandlerDescription} does not accept parameters, but received some.");
            }

            var request = (JsonElement)serializedRequest;

            return JsonSerializer.Deserialize<TRequest>(request, _jsonSerializerOptions)
                ?? throw new InvalidOperationException($"Unable to deserialize {request} into {typeof(TRequest)} for {metadata.HandlerDescription}");
        }

        protected override DelegatingEntryPoint<TRequestContext> CreateDelegatingEntryPoint(string method)
        {
            return new SystemTextJsonDelegatingEntryPoint(method, this);
        }

        private sealed class SystemTextJsonDelegatingEntryPoint(
            string method,
            SystemTextJsonLanguageServer<TRequestContext> target) : DelegatingEntryPoint<TRequestContext>(method)
        {
            private static readonly MethodInfo ParameterlessEntryPointMethodInfo = typeof(SystemTextJsonDelegatingEntryPoint).GetMethod(nameof(SystemTextJsonDelegatingEntryPoint.ExecuteRequest0Async), BindingFlags.NonPublic | BindingFlags.Instance);
            private static readonly MethodInfo EntryPointMethodInfo = typeof(SystemTextJsonDelegatingEntryPoint).GetMethod(nameof(SystemTextJsonDelegatingEntryPoint.ExecuteRequestAsync), BindingFlags.NonPublic | BindingFlags.Instance);

            public override MethodInfo GetEntryPoint(bool hasParameter)
            {
                return hasParameter ? EntryPointMethodInfo : ParameterlessEntryPointMethodInfo;
            }

            /// <summary>
            /// StreamJsonRpc entry point for handlers with no parameters.
            /// Unlike Newtonsoft, we have to differentiate instead of using default parameters.
            /// </summary>
            private Task<JsonElement?> ExecuteRequest0Async(CancellationToken cancellationToken = default)
            {
                return ExecuteRequestAsync(null, cancellationToken);
            }

            /// <summary>
            /// StreamJsonRpc entry point for handlers with parameters (and any response) type.
            /// </summary>
            private async Task<JsonElement?> ExecuteRequestAsync(JsonElement? request, CancellationToken cancellationToken = default)
            {
                var queue = target.GetRequestExecutionQueue();
                var lspServices = target.GetLspServices();

                var result = await InvokeAsync(queue, request, lspServices, cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    return null;
                }

                var serializedResult = JsonSerializer.SerializeToElement(result, target._jsonSerializerOptions);
                return serializedResult;
            }
        }
    }
}
