namespace Microsoft.PowerPlatformLS.UnitTests.Impl.Core
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Bot.Schema.Teams;
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

    public class BaseIpcTransportTests
    {
        public class MsgParam
        {
            public string? Name { get; set; }
        }

        // form the send message directly.
        [Theory]
        [InlineData("Hello")] // easy ascii encoding 
        [InlineData("안녕하세요")] // UTF8 encoding, 
        public async Task NonAsciiCharsDirectAsync(string name)
        {
            // Directly form bytes of message to mimic VSCode's json serailzier
            // and avoid STJ serializer. This will write UTF8 directly. 
            string payload = """
{
"method":"test/msg",
"params":{
  "name": "%name%"
 },
 "jsonRpc":"2.0"
}
""";
            payload = payload.Replace("%name%", name);
            var bytes = UTF8Encoding.UTF8.GetBytes(payload);

            string msgStr = $"Content-Length: {bytes.Length}\r\n\r\n";

            var buffer = new MemoryStream();
            buffer.Write(UTF8Encoding.UTF8.GetBytes(msgStr));
            buffer.Write(bytes);

            var finalPosition = buffer.Position;
                       
            buffer.Position = 0;
            var receiver = new TestIpcTransport(buffer, null);
            var msg2 = await receiver.GetNextMessageAsync(default);
            var msg2b = (LspJsonRpcMessage)msg2;
            Assert.Equal(finalPosition, buffer.Position);

            var payload2 = JsonRpc.GetValidParams<MsgParam>(msg2b);

            Assert.Equal(name, payload2.Name);
        }

        [Fact]
        public async Task NonAsciiCharsAsync()
        {
            var payload = new MsgParam
            {
                Name = "Hello"
            };
            payload.Name = "안녕하세요"; // len=5 chars
            var msg = JsonRpc.CreateMessage("test/msg", payload);

            var buffer1 = new MemoryStream();

            // Use STJ serializer. This will write ascii, and encode chars as  \u####.

            var sender = new TestIpcTransport(null, buffer1);
            await sender.SendAsync(msg, default);
            var finalPosition = buffer1.Position;

            buffer1.Position = 0;
            var receiver = new TestIpcTransport(buffer1, null);
            var msg2 = await receiver.GetNextMessageAsync(default);
            var msg2b = (LspJsonRpcMessage)msg2;
            Assert.Equal(finalPosition, buffer1.Position);

            var payload2 = JsonRpc.GetValidParams<MsgParam>(msg2b);

            Assert.Equal(payload.Name, payload2.Name);
        }

        // Send a message with multiple headers.
        // Unrecognized headers still parse, but get ignored. 
        [Fact]
        public async Task MultipleHeadersAsync()
        {
            //string name = "hello";
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
            message.Append($"Header1: value\r\n");
            message.Append($"Content-Length: {bytes.Length}\r\n");
            message.Append($"Other2: value\r\n");
            message.Append("\r\n");
            
            var buffer = new MemoryStream();
            buffer.Write(UTF8Encoding.ASCII.GetBytes(message.ToString()));
            buffer.Write(bytes);

            var finalPosition = buffer.Position;

            buffer.Position = 0;
            var receiver = new TestIpcTransport(buffer, null);
            var msg2 = await receiver.GetNextMessageAsync(default);
            var msg2b = (LspJsonRpcMessage)msg2;
            Assert.Equal(finalPosition, buffer.Position);

            var payload2 = JsonRpc.GetValidParams<MsgParam>(msg2b);

            Assert.Equal("Hello", payload2.Name);
        }

        // Test if client sends a malformed message.
        [Theory]
        [InlineData("""

{
"method":"test/msg",
"params":{
    "name": "Hello"
    },
    "jsonRpc":"2.0"
}
""")] // no headers, but at least has \r\n separator to start body
        [InlineData("""
{
"method":"test/msg",
"params":{
    "name": "Hello"
    },
    "jsonRpc":"2.0"
}
""")] // no headers  - first { will be interpreted as a malformed header.
        [InlineData("""
BadHeader: abc

{
"method":"test/msg",
"params":{
    "name": "Hello"
    },
    "jsonRpc":"2.0"
}
""")] // Extra header.
        [InlineData("""
BadHeader

{
"method":"test/msg",
"params":{
    "name": "Hello"
    },
    "jsonRpc":"2.0"
}
""")] // Missing Value
        [InlineData("""
Content-Length: 3

{ parse error object  
""")]
        [InlineData("""
Content-Length: 2
{} 
""")] // missing newline between header and body
        [InlineData("""
Header1: 2
Header1: 3
{} 
""")] // duplicate header
        [InlineData("Content-Length: 2\n\r\n{}", false)]
        [InlineData("Content-Length: 2\r\n\n{}", false)]

        [InlineData("""
Content-Length: 100000

{} 
""")] // content-length too large

        [InlineData("""
Content-Length: -1

{} 
""")] // illegal content length

        [InlineData("""
Content-Length: abc

{} 
""")] // illegal content length

        [InlineData("""
LongHeader-1234567890-1234567890-1234567890-1234567890: 123

{} 
""")] // header length is too long.
        [InlineData("""
Header: 123456789-123456789-123456789-123456789-123456789-123456789-123456789-123456789-

{} 
""")] // header value is too long. 
        public async Task BadMessageAsync(string messageStr, bool normalizeNewLine = true)
        {
            if (normalizeNewLine)
            {
                messageStr = messageStr.Replace("\r", "").Replace("\n", "\r\n"); // normalize to \r\n
            }
            var bytes = UTF8Encoding.UTF8.GetBytes(messageStr);

            var buffer = new MemoryStream();
            buffer.Write(bytes);
            await buffer.FlushAsync();
            buffer.Position = 0;

            var receiver = new TestIpcTransport(buffer, null);
            
            var msg2 = await receiver.GetNextMessageAsync(default);
            var msg2b = (JsonRpcResponse)msg2;
            Assert.Equal("2.0", msg2b.JsonRpc);
            Assert.Null(msg2b.Result);
            Assert.Null(msg2b.Id);
            Assert.NotNull(msg2b.Error);
        }

        private class TestIpcTransport : BaseIpcTransport
        {
            private readonly Stream? _reader;
            private readonly Stream? _writer;

            public TestIpcTransport(Stream? reader, Stream? writer)
                : base(NullLogger.Instance)
            {
                _reader = reader;
                _writer = writer;
            }

            protected override Stream Reader => _reader ?? throw new InvalidOperationException("reader not set");
            protected override Stream Writer => _writer ?? throw new InvalidOperationException("writer not set");

            public override Task StartAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            protected override void ChildTransportDispose()
            {
            }
        }
    }
}
