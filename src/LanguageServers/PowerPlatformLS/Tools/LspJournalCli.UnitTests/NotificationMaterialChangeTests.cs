namespace Microsoft.PowerPlatformLS.Tools.LspJournalCli.UnitTests
{
    using System.Collections.Generic;
    using System.Text.Json;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Commands;
    using Microsoft.PowerPlatformLS.Tools.LspJournalCli.Models;
    using Xunit;

    public sealed class NotificationMaterialChangeTests
{
    [Fact]
    public void ExtraNotifications_AreNotMaterialChange()
    {
        // Policy: extra unexpected notifications beyond the expected set are
        // warnings only — they do NOT trigger a pending file write.
        var expectedCounts = new Dictionary<string, int>
        {
            ["textDocument/publishDiagnostics:{}"] = 1,
        };

        var actual = new List<JournalNotification>
        {
            new() { Method = "textDocument/publishDiagnostics", Params = JsonSerializer.SerializeToElement(new { }) },
            new() { Method = "textDocument/publishDiagnostics", Params = JsonSerializer.SerializeToElement(new { }) },
        };

        Assert.False(RunCommand.HasNotificationMaterialChange(expectedCounts, actual));
    }

    [Fact]
    public void MatchingNotifications_AreNotMaterialChange()
    {
        var expectedCounts = new Dictionary<string, int>
        {
            ["textDocument/publishDiagnostics:{}"] = 1,
        };

        var actual = new List<JournalNotification>
        {
            new() { Method = "textDocument/publishDiagnostics", Params = JsonSerializer.SerializeToElement(new { }) },
        };

        Assert.False(RunCommand.HasNotificationMaterialChange(expectedCounts, actual));
    }

    [Fact]
    public void MissingNotifications_AreMaterialChange()
    {
        // Missing expected notifications ARE material — the server stopped
        // producing something it used to produce.
        var expectedCounts = new Dictionary<string, int>
        {
            ["textDocument/publishDiagnostics:{}"] = 2,
        };

        var actual = new List<JournalNotification>
        {
            new() { Method = "textDocument/publishDiagnostics", Params = JsonSerializer.SerializeToElement(new { }) },
        };

        Assert.True(RunCommand.HasNotificationMaterialChange(expectedCounts, actual));
    }

    [Fact]
    public void ExtraDifferentMethod_IsNotMaterialChange()
    {
        // An entirely unexpected notification method is also just a warning.
        var expectedCounts = new Dictionary<string, int>
        {
            ["textDocument/publishDiagnostics:{}"] = 1,
        };

        var actual = new List<JournalNotification>
        {
            new() { Method = "textDocument/publishDiagnostics", Params = JsonSerializer.SerializeToElement(new { }) },
            new() { Method = "window/logMessage", Params = JsonSerializer.SerializeToElement(new { type = 3, message = "info" }) },
        };

        Assert.False(RunCommand.HasNotificationMaterialChange(expectedCounts, actual));
    }
}
}
