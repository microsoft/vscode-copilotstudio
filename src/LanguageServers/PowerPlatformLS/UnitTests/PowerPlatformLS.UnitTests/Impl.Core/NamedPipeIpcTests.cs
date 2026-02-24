namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.IpcTransport;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Pipes;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class NamedPipeIpcTests
    {
        private static readonly ILogger Logger = NullLogger.Instance;

        // Basic happy path to test named pipe:
        // VSCode --> LSP --> VSCode.
        [Fact]
        public async Task BasicAsync()
        {
            var pipeName = Guid.NewGuid().ToString(); // Must be machine unique 

            // Simulate VSCode client 
            using var vscode = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1);
            var clientSend = new StreamWriter(vscode);
            var clientReceive = vscode;

            using var lsp = new NamedPipeIpc(pipeName, Logger);

            Assert.False(lsp.IsActive);
            await lsp.StartAsync(default);

            await vscode.WaitForConnectionAsync();

            Assert.True(lsp.IsActive);

            // Send message: VSCode --> LSP
            var messageString = """                
{
 "method": "message/m1",
 "params" : { } 
}
""";
            var messageBuilder = new StringBuilder();
            messageBuilder.Append("Content-Length: " + messageString.Length + "\r\n");
            messageBuilder.Append("\r\n");
            messageBuilder.Append(messageString);

            await clientSend.WriteAsync(messageBuilder.ToString());

            var taskFlush = clientSend.FlushAsync(); // Blocks until read

            // LSP Read the message from the client
            var lspGetMessageTask = lsp.GetNextMessageAsync(default);

            await Task.WhenAll(taskFlush, lspGetMessageTask);

            Assert.True(lspGetMessageTask.IsCompletedSuccessfully);
            var msg = await lspGetMessageTask;

            var msg2 = Assert.IsType<LspJsonRpcMessage>(msg);

            Assert.Equal("message/m1", msg2.Method);

            // LSP sends a response back to VS Code client
            var resp1 = new LspJsonRpcMessage
            {
                Method = "message/m2",
                Params = null
            };

            var lspSendResponseTask = lsp.SendAsync(resp1, default);
            var vscodeGetResponseTask = BaseIpcTransport.ReadMessageAsync(clientReceive, Logger, default);

            await Task.WhenAll(lspSendResponseTask, vscodeGetResponseTask);

            var vscodeGetResponse = await vscodeGetResponseTask;

            var response = Assert.IsType<LspJsonRpcMessage>(vscodeGetResponse);
            Assert.Equal("message/m2", response.Method);
        }

        // Test multiple sends
        [Fact]
        public async Task MultipleSendAsync()
        {
            var pipeName = Guid.NewGuid().ToString(); // Must be machine unique 

            // Simulate VSCode client 
            using var vscode = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1);
            var clientSend = new StreamWriter(vscode);
            var clientReceive = new StreamReader(vscode);

            using var lsp = new NamedPipeIpc(pipeName, Logger);

            await lsp.StartAsync(default);

            await vscode.WaitForConnectionAsync();

            int N = 5;
            string[] responseMsgs = new string[N];

            // Safety - rather than hang the test, fail with timeout on a deadlock.
            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var ct = cts.Token;

            // VSCode client receiving 
            Thread t = new Thread(() =>
            {
                for (int i = 0; i < N; i++)
                {
                    var vscodeGetResponse = BaseIpcTransport.ReadMessageAsync(clientReceive.BaseStream, Logger, ct).Result;

                    var response = Assert.IsType<LspJsonRpcMessage>(vscodeGetResponse);

                    responseMsgs[i] = response.Method;
                }
            });
            t.Start();

            // Send from LSP in parallel.
            // This simulates LSP receiving/handling multiple messages in parallel
            Parallel.For(0, N, i =>
            {
                // LSP sends a response back to VS Code client
                var resp1 = new LspJsonRpcMessage
                {
                    Method = $"{i}",
                    Params = null
                };

                lsp.SendAsync(resp1, ct).Wait();
            });

            t.Join();

            // Ensure we received all 5 unique messages. 
            Array.Sort(responseMsgs); // Can be received in any order.
            var str = string.Join(",", responseMsgs);
            Assert.Equal((object) "0,1,2,3,4", str);
        }


        public class MsgParam
        {
            public string? Name { get; set; }
        }

        // Partial sends - send bytes in multiple chunks to stress test that 
        // reader can handle this. 
        // We need a queue-like stream like NamedPipes, to repro this.
        [Fact]
        public async Task PartialSendAsync()
        {
            var pipeName = Guid.NewGuid().ToString(); // Must be machine unique 

            // Simulate VSCode client 
            using var vscode = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1);

            string payload = """
{
"method":"test/msg",
"params":{
  "name": "Hello"
 },
 "jsonRpc":"2.0"
}
""";
            var bytes = UTF8Encoding.UTF8.GetBytes(payload);

            var message = new StringBuilder();
            message.Append($"Content-Length: {bytes.Length}\r\n");
            message.Append("\r\n");

            using var lsp = new NamedPipeIpc(pipeName, Logger);

            await lsp.StartAsync(default);

            await vscode.WaitForConnectionAsync();

            CancellationTokenSource cts = new CancellationTokenSource();

            Func<Task> clientTask = async () =>
            {
                await vscode.WriteAsync(Encoding.ASCII.GetBytes(message.ToString()));

                int chunk = 10;
                await vscode.WriteAsync(bytes, 0, chunk, cts.Token);

                await vscode.FlushAsync(cts.Token);

                // write rest of message.
                await Task.Delay(100);

                // This will hang until bytes are read. 
                await vscode.WriteAsync(bytes, chunk, bytes.Length - chunk, cts.Token);
            };

            var t1 = clientTask(); // start, but don't await since we need to run getMessage.

            // LSP Read the message from the client
            var getMessage = await lsp.GetNextMessageAsync(default);
            await cts.CancelAsync();

            if (getMessage is JsonRpcResponse error)
            {
                // This is failure case.
                // getMessage should have succeeded and returned LspJsonRpcMessage. 
                Assert.Null(error.Error);
            }

            var msg2 = Assert.IsType<LspJsonRpcMessage>(getMessage); // not an error 
            Assert.Equal("test/msg", msg2.Method);
            var payload2 = JsonRpc.GetValidParams<MsgParam>(msg2);
            Assert.Equal("Hello", payload2.Name); // successful read message.
        }

        
    }
}
