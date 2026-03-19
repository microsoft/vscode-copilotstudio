namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.UnitTests
{
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Transport;
    using Xunit;

    public sealed class NotificationSetMatchingTests
{
    [Fact]
    public async Task Notifications_AreVerifiedAsMultiset()
    {
        await using var server = new FakeStdioServer();
        server.Start();

        var transport = new LspClientTransport(
            server.ClientToServerStream,
            server.ServerToClientStream);
        transport.StartListening();

        await server.SendNotificationAsync("test/alpha", new { seq = 1 });
        await server.SendNotificationAsync("test/beta", new { seq = 2 });
        await server.SendNotificationAsync("test/alpha", new { seq = 3 });

        // WaitForNotificationAsync is a consuming wait — each call removes the
        // first matching notification from the stream and returns its params.
        var p1 = await transport.WaitForNotificationAsync("test/alpha", timeoutMs: 3000);
        var p2 = await transport.WaitForNotificationAsync("test/beta", timeoutMs: 3000);
        var p3 = await transport.WaitForNotificationAsync("test/alpha", timeoutMs: 3000);

        // Verify each notification was actually received (not a timeout) and
        // carries the expected params — proving the 2-alpha / 1-beta multiset.
        Assert.NotNull(p1);
        Assert.NotNull(p2);
        Assert.NotNull(p3);
        Assert.Equal(1, p1.Value.GetProperty("seq").GetInt32());
        Assert.Equal(2, p2.Value.GetProperty("seq").GetInt32());
        Assert.Equal(3, p3.Value.GetProperty("seq").GetInt32());

        // After the three waiters consumed everything, the buffer must be empty.
        var remaining = transport.DrainNotifications();
        Assert.Empty(remaining);

        server.CloseOutputStream();
    }
}
}
