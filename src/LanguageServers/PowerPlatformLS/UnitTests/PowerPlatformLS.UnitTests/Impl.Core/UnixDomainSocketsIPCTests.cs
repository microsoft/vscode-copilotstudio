namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using Microsoft.PowerPlatformLS.Impl.Core.IpcTransport;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.IO;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class UnixDomainSocketsIPCTests
    {
        // Test send / receive on UnixDomainSocketsIPC.
        [Fact]
        public async Task BasicAsync()
        {
            // Needs unique path.
            var unixEndpoint = $"{Path.GetTempPath()}\\lsp-{Guid.NewGuid()}.sock";

            // Convert hangs into timeout failures. 
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var cancel = cts.Token;

            // VS code client (acting as server)
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(unixEndpoint));

            socket.Listen(5);

            var task1 = Task.Run(async () =>
            {
                var s = await socket.AcceptAsync();

                var stream = new NetworkStream(s, true);

                // Send a message
                string payload = """
{
"method":"test/msg",
"params":{
  "name": "hello"
 },
 "jsonRpc":"2.0"
}
""";
                var bytes = UTF8Encoding.UTF8.GetBytes(payload);

                string msgStr = $"Content-Length: {bytes.Length}\r\n\r\n";

                stream.Write(UTF8Encoding.UTF8.GetBytes(msgStr));
                stream.Write(bytes);

                // Receive a response.
                var msg2 = await BaseIpcTransport.ReadMessageAsync(stream, NullLogger.Instance, cancel);

                var msg2b = Assert.IsType<LspJsonRpcMessage>(msg2);
                Assert.Equal("test/msg2", msg2b.Method);
                Assert.Equal("2.0", msg2b.JsonRpc);
            });

            // LSP (acting as client) 
            var task2 = Task.Run(async () =>
            {
                var x = new UnixDomainSocketsIPC(unixEndpoint, NullLogger.Instance);
                await x.StartAsync(default);

                var msg1 = await x.GetNextMessageAsync(cancel);

                // Validate message was successfully received. 
                var msg1a = Assert.IsType<LspJsonRpcMessage>(msg1);
                Assert.Equal("test/msg", msg1a.Method);
                Assert.Equal("2.0", msg1a.JsonRpc);

                // Send response
                var msg2 = JsonRpc.CreateMessage("test/msg2", new MsgParam
                {
                    Name = "hello2"
                });

                await x.SendAsync(msg2, cancel);
            });

            await Task.WhenAll(task1, task2);

            File.Delete(unixEndpoint);
        }


        public class MsgParam
        {
            public string? Name { get; set; }
        }
    }
}
