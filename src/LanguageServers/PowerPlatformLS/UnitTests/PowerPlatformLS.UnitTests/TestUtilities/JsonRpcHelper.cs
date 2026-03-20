#nullable enable

namespace Microsoft.PowerPlatformLS.UnitTests.TestUtilities
{

    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Text.Json;
    using System.Threading;
    using Xunit;

    internal static class JsonRpc
    {
        private static int _nextId;

        private static T GetValidMessageData<T>(BaseJsonRpcMessage? message, Func<BaseJsonRpcMessage, JsonElement?> getValue)
        {
            Assert.NotNull(message);

            // ! : Assert
            var value = getValue(message!);
            Assert.NotNull(value);

            // ! : Assert
            var deserialized = value!.Value.Deserialize<T>(Constants.DefaultSerializationOptions);
            Assert.NotNull(deserialized);
            // ! : Assert
            return deserialized!;
        }

        // Specific method for Params
        internal static T GetValidParams<T>(LspJsonRpcMessage? message)
        {
            return GetValidMessageData<T>(message, msg => ((LspJsonRpcMessage)msg).Params);
        }

        // Specific method for Result
        internal static T GetValidResult<T>(JsonRpcResponse? response)
        {
            return GetValidMessageData<T>(response, msg =>
            {
                var jsonRpcResponse = (JsonRpcResponse)msg;
                Assert.Null(jsonRpcResponse.Error);
                return jsonRpcResponse.Result;
            });
        }

        internal static BaseJsonRpcMessage CreateMessage<T>(string method, T? lspParams)
        {
            return new LspJsonRpcMessage
            {
                Method = method,
                Params = lspParams == null ? null : JsonSerializer.SerializeToElement(lspParams, Constants.DefaultSerializationOptions),
            };
        }

        internal static BaseJsonRpcMessage CreateRequestMessage<T>(string method, T? lspParams)
        {
            var id = Interlocked.Increment(ref _nextId);
            return new LspJsonRpcMessage
            {
                Id = id,
                Method = method,
                Params = lspParams == null ? null : JsonSerializer.SerializeToElement(lspParams, Constants.DefaultSerializationOptions),
            };
        }
    }
}
