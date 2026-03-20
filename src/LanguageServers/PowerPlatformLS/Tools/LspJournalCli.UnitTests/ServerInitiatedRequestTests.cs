namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.UnitTests
{
    using System.Text.Json;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Transport;
    using Xunit;

    /// <summary>
/// Tests for server-initiated JSON-RPC request handling in <see cref="LspClientTransport"/>.
/// Before the fix, the transport treated server requests as notifications
/// (never responded), which could deadlock the server.
/// </summary>
public sealed class ServerInitiatedRequestTests
{
    /// <summary>
    /// Proves the pre-fix bug: when the server sends a request (method + id),
    /// the transport must respond. If it doesn't, the server hangs waiting.
    /// This test sends workspace/configuration from the server and verifies
    /// that the client sends back a valid JSON-RPC response.
    /// </summary>
    [Fact]
    public async Task ServerRequest_WorkspaceConfiguration_GetsResponse()
    {
        await using var server = new FakeStdioServer();
        server.Start();

        var transport = new LspClientTransport(
            server.ClientToServerStream,
            server.ServerToClientStream);
        transport.StartListening();

        // Server sends a workspace/configuration request to the client
        await server.SendServerRequestAsync(
            id: 1,
            method: "workspace/configuration",
            @params: new { items = new[] { new { section = "powerplatform" } } });

        // The client should respond. Give it a reasonable timeout.
        var response = await server.WaitForClientMessageAsync(timeoutMs: 3000);

        // Verify it's a valid JSON-RPC response with the correct id
        Assert.True(response.TryGetProperty("id", out var idEl), "Response must have 'id'");
        Assert.Equal(1, idEl.GetInt32());
        Assert.True(
            response.TryGetProperty("result", out _),
            "Response must have 'result' (not an error)");

        // workspace/configuration default should return an array
        var result = response.GetProperty("result");
        Assert.Equal(JsonValueKind.Array, result.ValueKind);

        server.CloseOutputStream();
    }

    /// <summary>
    /// Verifies that client/registerCapability gets a null result (per spec, void response).
    /// </summary>
    [Fact]
    public async Task ServerRequest_ClientRegisterCapability_GetsNullResult()
    {
        await using var server = new FakeStdioServer();
        server.Start();

        var transport = new LspClientTransport(
            server.ClientToServerStream,
            server.ServerToClientStream);
        transport.StartListening();

        await server.SendServerRequestAsync(
            id: 42,
            method: "client/registerCapability",
            @params: new { registrations = new[] { new { id = "1", method = "textDocument/didChange" } } });

        var response = await server.WaitForClientMessageAsync(timeoutMs: 3000);

        Assert.True(response.TryGetProperty("id", out var idEl));
        Assert.Equal(42, idEl.GetInt32());
        Assert.True(response.TryGetProperty("result", out var result));
        Assert.Equal(JsonValueKind.Null, result.ValueKind);

        server.CloseOutputStream();
    }

    /// <summary>
    /// Verifies that the transport continues processing normally after handling
    /// a server request. Send a server request interleaved with a client request
    /// and confirm both complete.
    /// </summary>
    [Fact]
    public async Task ServerRequest_DoesNotDisruptClientRequests()
    {
        await using var server = new FakeStdioServer();
        server.Start();

        var transport = new LspClientTransport(
            server.ClientToServerStream,
            server.ServerToClientStream);
        transport.StartListening();

        // Start a client request (will pend until server responds)
        var clientRequestTask = transport.SendRequestAsync("textDocument/completion", new { });

        // Read the client's request from the fake server
        var clientMsg = await server.WaitForClientMessageAsync(timeoutMs: 3000);
        Assert.Equal("textDocument/completion", clientMsg.GetProperty("method").GetString());
        var clientReqId = clientMsg.GetProperty("id").GetInt32();

        // Before responding, the server sends its own request
        await server.SendServerRequestAsync(
            id: 100,
            method: "workspace/configuration",
            @params: new { items = Array.Empty<object>() });

        // The client should auto-respond to the server request
        var serverReqResponse = await server.WaitForClientMessageAsync(timeoutMs: 3000);
        Assert.Equal(100, serverReqResponse.GetProperty("id").GetInt32());
        Assert.True(serverReqResponse.TryGetProperty("result", out _));

        // Now the server responds to the client's original request
        await server.SendResponseAsync(clientReqId, new { items = Array.Empty<object>() });

        // The client request should complete normally
        var result = await clientRequestTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(result);

        server.CloseOutputStream();
    }

    /// <summary>
    /// Verifies that a custom server-request handler can be registered and overrides
    /// the default response policy.
    /// </summary>
    [Fact]
    public async Task ServerRequest_CustomHandler_OverridesDefaults()
    {
        await using var server = new FakeStdioServer();
        server.Start();

        var transport = new LspClientTransport(
            server.ClientToServerStream,
            server.ServerToClientStream);

        // Register a custom handler for workspace/configuration
        transport.SetServerRequestHandler("workspace/configuration", (method, @params) =>
        {
            // Return a specific config value instead of the default empty array
            return JsonSerializer.SerializeToElement(new[] { new { enabled = true } });
        });

        transport.StartListening();

        await server.SendServerRequestAsync(
            id: 7,
            method: "workspace/configuration",
            @params: new { items = new[] { new { section = "test" } } });

        var response = await server.WaitForClientMessageAsync(timeoutMs: 3000);
        Assert.Equal(7, response.GetProperty("id").GetInt32());

        var result = response.GetProperty("result");
        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.True(result[0].GetProperty("enabled").GetBoolean());

        server.CloseOutputStream();
    }

    /// <summary>
    /// Verifies that if the custom handler throws, the transport sends a
    /// JSON-RPC error response (not silently dropping the request).
    /// </summary>
    [Fact]
    public async Task ServerRequest_HandlerThrows_ReturnsJsonRpcError()
    {
        await using var server = new FakeStdioServer();
        server.Start();

        var transport = new LspClientTransport(
            server.ClientToServerStream,
            server.ServerToClientStream);

        transport.SetServerRequestHandler("workspace/configuration", (_, _) =>
        {
            throw new InvalidOperationException("Handler failed");
        });

        transport.StartListening();

        await server.SendServerRequestAsync(
            id: 9,
            method: "workspace/configuration",
            @params: new { });

        var response = await server.WaitForClientMessageAsync(timeoutMs: 3000);
        Assert.Equal(9, response.GetProperty("id").GetInt32());
        Assert.True(response.TryGetProperty("error", out var error), "Should return an error response");
        Assert.Contains("Handler failed", error.GetProperty("message").GetString());

        server.CloseOutputStream();
    }

    /// <summary>
    /// Verifies that an unknown server request method gets a default null response
    /// rather than being silently dropped.
    /// </summary>
    [Fact]
    public async Task ServerRequest_UnknownMethod_GetsNullResult()
    {
        await using var server = new FakeStdioServer();
        server.Start();

        var transport = new LspClientTransport(
            server.ClientToServerStream,
            server.ServerToClientStream);
        transport.StartListening();

        await server.SendServerRequestAsync(
            id: 55,
            method: "custom/unknownServerRequest",
            @params: null);

        var response = await server.WaitForClientMessageAsync(timeoutMs: 3000);
        Assert.Equal(55, response.GetProperty("id").GetInt32());
        Assert.True(response.TryGetProperty("result", out var result));
        Assert.Equal(JsonValueKind.Null, result.ValueKind);

        server.CloseOutputStream();
    }

    /// <summary>
    /// Notifications from the server (no id) should still be captured as before —
    /// they should NOT receive a response.
    /// </summary>
    [Fact]
    public async Task ServerNotification_StillWorkAsExpected()
    {
        await using var server = new FakeStdioServer();
        server.Start();

        var transport = new LspClientTransport(
            server.ClientToServerStream,
            server.ServerToClientStream);
        transport.StartListening();

        // Send a server notification (no id)
        await server.SendNotificationAsync("textDocument/publishDiagnostics",
            new { uri = "file:///test.yaml", diagnostics = Array.Empty<object>() });

        // Deterministically wait for the notification instead of sleeping.
        // WaitForNotificationAsync consumes the notification from the stream,
        // so we assert on the returned params directly.
        var notifParams = await transport.WaitForNotificationAsync(
            "textDocument/publishDiagnostics",
            timeoutMs: 3000);

        Assert.NotNull(notifParams);

        // The notification was consumed by the waiter — buffer should be empty.
        var remaining = transport.DrainNotifications();
        Assert.Empty(remaining);

        server.CloseOutputStream();
    }
}
}
