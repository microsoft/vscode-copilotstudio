namespace Microsoft.PowerPlatformLS.Impl.Core.IpcTransport
{
    using Microsoft.Extensions.Logging;
    using Microsoft.PowerPlatformLS.Contracts.Internal;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    // For testing - read LSP messages from a json file.
    // This should just be a Json array of messages. 
    internal class JsonFileIpc : ILspTransport
    {
        public bool IsActive => true;

        private readonly IReadOnlyList<LspJsonRpcMessage> _messages;

        private int _countGet = 0;
        private int _countSend = 1;

        private readonly IReadOnlyList<TaskCompletionSource<BaseJsonRpcMessage>> _tasks;
                
        public JsonFileIpc(string filename)
        {
            var json = File.ReadAllText(filename);

            // ! deserialize is non-null
            var messages = JsonSerializer.Deserialize<LspJsonRpcMessage[]>(json, Constants.DefaultSerializationOptions)!;            

            _tasks = Array.ConvertAll(messages, _ => new TaskCompletionSource<BaseJsonRpcMessage>());

            _messages = messages;
            _tasks[0].SetResult(_messages[0]);
        }

        public void Dispose()
        {
        }

        // Block until next message is available.
        // This can be called immediately many times in a row. 
        public Task<BaseJsonRpcMessage> GetNextMessageAsync(CancellationToken cancellationToken)
        {
            var tsc = (_countGet < _tasks.Count) ?
                _tasks[_countGet] :
                new TaskCompletionSource<BaseJsonRpcMessage>();

            _countGet++;

#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
            return tsc.Task;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
        }


        // This will not be called until after GetNextMessageAsync has returned. 
        public Task SendAsync<T>(T response, CancellationToken cancellationToken) where T : BaseJsonRpcMessage
        {
            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            Console.WriteLine(json);

            // Move to next task
            if (_countSend < _tasks.Count)
            {
                // unblock sending the next event. 
                _tasks[_countSend].SetResult(_messages[_countSend]);
            }
            _countSend++;

            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
