namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.UnitTests
{
    using System.Linq;
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

        await server.SendNotificationAsync("test/alpha");
        await server.SendNotificationAsync("test/beta");
        await server.SendNotificationAsync("test/alpha");

        await Task.Delay(200);

        var notifications = transport.DrainNotifications();
        var counts = notifications
            .GroupBy(n => n.Method)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(3, notifications.Count);
        Assert.Equal(2, counts["test/alpha"]);
        Assert.Equal(1, counts["test/beta"]);

        server.CloseOutputStream();
    }
}
}
