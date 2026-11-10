// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AgentPublishServiceTests
{
    private const string Provisioning = "Provisioning";

    private static readonly Guid AgentId = new("6f0c2a1e-4b7d-4e9a-8c3f-1d2e3f4a5b6c");

    private static readonly AgentPublishOperation Earlier = Operation("Succeeded", 1);

    private readonly SteppingTimeProvider _clock = new();

    public static TheoryData<Exception> TransientFailures => new()
    {
        new DataverseRequestException((HttpStatusCode)429, "Slow down."),
        new DataverseRequestException(HttpStatusCode.InternalServerError, "{}"),
        new DataverseRequestException(HttpStatusCode.BadGateway, "{}"),
        new DataverseRequestException(HttpStatusCode.ServiceUnavailable, "{}"),
        new DataverseRequestException(HttpStatusCode.GatewayTimeout, "{}"),
        new HttpRequestException("The connection was reset."),
        new IOException("The response ended early."),
        new TaskCanceledException("The request timed out."),
    };

    public static TheoryData<Exception, string> PermanentFailures => new()
    {
        { new DataverseRequestException(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"0x80040220\",\"message\":\"Principal lacks the privilege.\"},\"trace\":\"secret detail\"}"), "The request for the publish's progress failed with HTTP 403 Forbidden. Principal lacks the privilege. (0x80040220)" },
        { new DataverseRequestException(HttpStatusCode.NotFound, "secret detail"), "The request for the publish's progress failed with HTTP 404 Not Found." },
        { new FormatException("secret detail"), "The bot's synchronization status is not in a form Copilot Studio is known to write." },
        { new InvalidOperationException("secret detail"), "The request for the publish's progress failed with InvalidOperationException." },
    };

    public static TheoryData<object?> ProvisioningReadsThatDoNotWait => new()
    {
        null,
        "Provisioned",
        "Failed",
        string.Empty,
        new FormatException("secret detail"),
        new DataverseRequestException(HttpStatusCode.Forbidden, "secret detail"),
        new DataverseRequestException(HttpStatusCode.ServiceUnavailable, "{}"),
        new HttpRequestException("The connection was reset."),
    };

    [Fact]
    public void AddSyncServices_ResolvesThePublishService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IDataverseHttpClientAccessor>());
        services.AddSyncServices();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<AgentPublishService>(provider.GetRequiredService<IAgentPublishService>());
    }

    [Fact]
    public async Task PublishAsync_OfAnAgentNeverPublished_WaitsForTheFirstFinishedPublish()
    {
        var publisher = new ScriptedPublisher(null, null, null, Operation("Succeeded", 2));

        Assert.Equal(new AgentPublishOutcome(AgentPublishOutcomeKind.Succeeded, "Succeeded", null) { FinishedOn = Operation("Succeeded", 2).OperationEnd }, await PublishAsync(publisher, TimeSpan.FromMinutes(5)));
        Assert.Equal(new[] { "read", "publish", "read", "read", "read" }, publisher.Calls);
        Assert.Equal(Seconds(1, 1, 1), _clock.Delays);
    }

    [Fact]
    public async Task PublishAsync_WhileTheLastFinishedPublishIsStillTheEarlierOne_KeepsPollingEverySecondUntilANewOneAppears()
    {
        Assert.Equal(AgentPublishOutcomeKind.Succeeded, (await PublishAsync(new ScriptedPublisher(Earlier, Earlier, Earlier, Earlier, Earlier, Earlier, Operation("Succeeded", 2)), TimeSpan.FromMinutes(5))).Kind);
        Assert.Equal(Seconds(1, 1, 1, 1, 1, 1), _clock.Delays);
    }

    [Theory]
    [InlineData("Succeeded", AgentPublishOutcomeKind.Succeeded)]
    [InlineData("succeeded", AgentPublishOutcomeKind.Succeeded)]
    [InlineData("SUCCEEDED", AgentPublishOutcomeKind.Succeeded)]
    [InlineData("Failed", AgentPublishOutcomeKind.Failed)]
    [InlineData("Canceled", AgentPublishOutcomeKind.Failed)]
    public async Task PublishAsync_WhenCopilotStudioFinishesTheNewPublish_ReportsItsStatus(string status, AgentPublishOutcomeKind expected)
    {
        Assert.Equal(new AgentPublishOutcome(expected, status, null) { FinishedOn = Operation(status, 2).OperationEnd }, await PublishAsync(new ScriptedPublisher(Earlier, Operation(status, 2)), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task PublishAsync_WhenTheNewPublishNamesNoEndTime_ReportsItWithoutAFinishTime()
    {
        var outcome = await PublishAsync(new ScriptedPublisher(Earlier, new AgentPublishOperation("Succeeded", Operation("Succeeded", 2).OperationStart, null)), TimeSpan.FromMinutes(5));

        Assert.Equal(new AgentPublishOutcome(AgentPublishOutcomeKind.Succeeded, "Succeeded", null), outcome);
    }

    [Theory]
    [InlineData(3000, new[] { 1000, 1000, 1000 })]
    [InlineData(2500, new[] { 1000, 1000, 500 })]
    [InlineData(400, new[] { 400 })]
    public async Task PublishAsync_WhenCopilotStudioDoesNotFinishInTime_TimesOutWithoutAskingForAnotherPublish(int timeoutMilliseconds, int[] expectedDelayMilliseconds)
    {
        var publisher = new ScriptedPublisher(Earlier);

        Assert.Equal(new AgentPublishOutcome(AgentPublishOutcomeKind.TimedOut, null, null), await PublishAsync(publisher, TimeSpan.FromMilliseconds(timeoutMilliseconds)));
        Assert.Equal(expectedDelayMilliseconds.Select(milliseconds => TimeSpan.FromMilliseconds(milliseconds)), _clock.Delays);
        Assert.Equal(new[] { "read", "publish" }.Concat(expectedDelayMilliseconds.Select(_ => "read")), publisher.Calls);
    }

    [Theory]
    [MemberData(nameof(TransientFailures))]
    public async Task PublishAsync_WhenAPollFailsTransiently_KeepsPolling(Exception failure)
    {
        Assert.Equal(AgentPublishOutcomeKind.Succeeded, (await PublishAsync(new ScriptedPublisher(null, failure, failure, Operation("Succeeded", 2)), TimeSpan.FromMinutes(5))).Kind);
        Assert.Equal(Seconds(1, 1, 1), _clock.Delays);
    }

    [Theory]
    [MemberData(nameof(PermanentFailures))]
    public async Task PublishAsync_WhenAPollFailsOtherwise_FailsAtOnceWithAReasonThatRepeatsNoServiceResponse(Exception failure, string expectedReason)
    {
        Assert.Equal(new AgentPublishOutcome(AgentPublishOutcomeKind.Failed, null, expectedReason), await PublishAsync(new ScriptedPublisher(null, failure), TimeSpan.FromMinutes(5)));
        Assert.Equal(Seconds(1), _clock.Delays);
    }

    [Fact]
    public async Task PublishAsync_WhenTheLastPublishCannotBeRead_ThrowsWithoutAskingForAPublish()
    {
        var unreadable = new FormatException("The bot's synchronization status cannot be read.");
        var publisher = new ScriptedPublisher(unreadable);

        Assert.Same(unreadable, await Assert.ThrowsAsync<FormatException>(() => PublishAsync(publisher, TimeSpan.FromMinutes(5))));
        Assert.Equal(new[] { "read" }, publisher.Calls);
        Assert.Empty(_clock.Delays);
    }

    [Fact]
    public async Task PublishAsync_WhenTheFirstPublishRequestIsRefused_AsksOnceMoreAfterTheRetryDelayAndWaitsForThePublish()
    {
        var publisher = new ScriptedPublisher(null, null, Operation("Succeeded", 2)) { PublishFailures = new Exception?[] { new DataverseRequestException(HttpStatusCode.BadRequest, "{\"statuscode\":409,\"statusdescription\":\"Conflict\"}") } };

        Assert.Equal(AgentPublishOutcomeKind.Succeeded, (await PublishAsync(publisher, TimeSpan.FromMinutes(5))).Kind);
        Assert.Equal(new[] { "read", "publish", "read", "publish", "read" }, publisher.Calls);
        Assert.Equal(new[] { AgentPublishService.PublishRetryDelay, TimeSpan.FromSeconds(1) }, _clock.Delays);
    }

    [Fact]
    public async Task PublishAsync_WhenAnotherPublishFinishesWhileWaitingToAskAgain_ReadsTheLastPublishAgainJustBeforeAskingAndReportsTheRetriedPublish()
    {
        var retried = Operation("Succeeded", 3);
        var startedAt = _clock.GetUtcNow();
        Func<object?> lastFinished = () => (_clock.GetUtcNow() - startedAt).TotalSeconds switch { < 3 => Earlier, < 7 => Operation("Failed", 2), _ => retried };
        var publisher = new ScriptedPublisher(lastFinished, lastFinished) { PublishFailures = new Exception?[] { new DataverseRequestException(HttpStatusCode.BadRequest, "{}") } };

        Assert.Equal(new AgentPublishOutcome(AgentPublishOutcomeKind.Succeeded, "Succeeded", null) { FinishedOn = retried.OperationEnd }, await PublishAsync(publisher, TimeSpan.FromMinutes(5)));
        Assert.Equal(new[] { "read", "publish", "read", "publish", "read", "read" }, publisher.Calls);
        Assert.Equal(new[] { AgentPublishService.PublishRetryDelay }.Concat(Seconds(1, 1)), _clock.Delays);
    }

    [Fact]
    public async Task PublishAsync_WhenTheLastPublishCannotBeReadAgainBeforeAskingAgain_ThrowsWithoutAskingAgain()
    {
        var unreadable = new DataverseRequestException(HttpStatusCode.Forbidden, "{}");
        var publisher = new ScriptedPublisher(null, unreadable) { PublishFailures = new Exception?[] { new DataverseRequestException(HttpStatusCode.BadRequest, "{}") } };

        Assert.Same(unreadable, await Assert.ThrowsAsync<DataverseRequestException>(() => PublishAsync(publisher, TimeSpan.FromMinutes(5))));
        Assert.Equal(new[] { "read", "publish", "read" }, publisher.Calls);
        Assert.Equal(new[] { AgentPublishService.PublishRetryDelay }, _clock.Delays);
    }

    [Fact]
    public async Task PublishAsync_AfterARetriedPublishRequest_StartsTheTimeoutWhenTheRequestSucceeds()
    {
        var publisher = new ScriptedPublisher(null) { PublishFailures = new Exception?[] { new DataverseRequestException(HttpStatusCode.BadRequest, "{}") } };

        Assert.Equal(AgentPublishOutcomeKind.TimedOut, (await PublishAsync(publisher, TimeSpan.FromSeconds(3))).Kind);
        Assert.Equal(new[] { "read", "publish", "read", "publish", "read", "read", "read" }, publisher.Calls);
        Assert.Equal(new[] { AgentPublishService.PublishRetryDelay }.Concat(Seconds(1, 1, 1)), _clock.Delays);
    }

    [Fact]
    public async Task PublishAsync_WhenBothPublishRequestsAreRefused_ThrowsTheSecondRefusal()
    {
        var second = new DataverseRequestException(HttpStatusCode.BadRequest, "The bot cannot be published.");
        var publisher = new ScriptedPublisher(null) { PublishFailures = new Exception?[] { new DataverseRequestException(HttpStatusCode.BadRequest, "{}"), second } };

        Assert.Same(second, await Assert.ThrowsAsync<DataverseRequestException>(() => PublishAsync(publisher, TimeSpan.FromMinutes(5))));
        Assert.Equal(new[] { "read", "publish", "read", "publish" }, publisher.Calls);
        Assert.Equal(new[] { AgentPublishService.PublishRetryDelay }, _clock.Delays);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishAsync_WhenThePublishRequestFailsWithoutARefusal_ThrowsWithoutAskingAgain(bool canceled)
    {
        Exception failure = canceled ? new OperationCanceledException() : new HttpRequestException("The connection was reset.");
        var publisher = new ScriptedPublisher(null) { PublishFailures = new Exception?[] { failure } };

        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => PublishAsync(publisher, TimeSpan.FromMinutes(5))));
        Assert.Equal(new[] { "read", "publish" }, publisher.Calls);
        Assert.Empty(_clock.Delays);
    }

    [Fact]
    public async Task PublishAsync_WhenCanceledWhileWaitingToAskAgain_StopsWithoutAskingAgain()
    {
        using var cancellation = new CancellationTokenSource();
        var publisher = new ScriptedPublisher(null) { PublishFailures = new Exception?[] { new DataverseRequestException(HttpStatusCode.BadRequest, "{}") }, OnPublish = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AgentPublishService(publisher, _clock).PublishAsync(AgentId, TimeSpan.FromMinutes(5), cancellation.Token));

        Assert.Equal(new[] { "read", "publish" }, publisher.Calls);
        Assert.Empty(_clock.Delays);
    }

    [Fact]
    public async Task PublishAsync_WhenCanceledOnceThePublishWasAskedFor_StopsWaitingWithoutAskingAgain()
    {
        using var cancellation = new CancellationTokenSource();
        var publisher = new ScriptedPublisher(null) { OnPublish = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AgentPublishService(publisher, _clock).PublishAsync(AgentId, TimeSpan.FromMinutes(5), cancellation.Token));

        Assert.Equal(new[] { "read", "publish" }, publisher.Calls);
        Assert.Empty(_clock.Delays);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishAsync_WhenCanceledDuringAPoll_StopsWithTheCancellationRatherThanAnOutcome(bool pollThrowsCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        Func<object?> cancelDuringPoll = () =>
        {
            cancellation.Cancel();

            return pollThrowsCancellation ? new OperationCanceledException(cancellation.Token) : new HttpRequestException("The request was aborted.");
        };
        var publisher = new ScriptedPublisher(null, cancelDuringPoll);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AgentPublishService(publisher, _clock).PublishAsync(AgentId, TimeSpan.FromMinutes(5), cancellation.Token));

        Assert.Equal(new[] { "read", "publish", "read" }, publisher.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PublishAsync_WithATimeoutThatIsNotPositive_IsRefusedBeforeAnyRequest(int timeoutSeconds)
    {
        var publisher = new ScriptedPublisher(null);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PublishAsync(publisher, TimeSpan.FromSeconds(timeoutSeconds)));

        Assert.Empty(publisher.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("empty-1.0.0")]
    [InlineData("default-2.1.0")]
    public async Task PublishAsync_WhileCopilotStudioStillProvisionsABotItProvisionsOnItsOwn_WaitsOnTheProvisioningBackoffAndThenPollsThePublishEverySecond(string? template)
    {
        var publisher = new ProvisioningPublisher(new object?[] { Provisioning, Provisioning, Provisioning, "Provisioned" }, null, null, Operation("Succeeded", 2)) { Template = template };

        Assert.Equal(new AgentPublishOutcome(AgentPublishOutcomeKind.Succeeded, "Succeeded", null) { FinishedOn = Operation("Succeeded", 2).OperationEnd }, await PublishAsync(publisher, TimeSpan.FromMinutes(5)));
        Assert.Equal(new[] { "provisioning", "provisioning", "provisioning", "provisioning", "read", "publish", "read", "read" }, publisher.Calls);
        Assert.Equal(Seconds(2, 3, 4, 1, 1), _clock.Delays);
    }

    [Theory]
    [InlineData("cliagent-1.0.0")]
    [InlineData("CLIAGENT-2.0.0")]
    public async Task PublishAsync_ForABotCreatedFromACliAgentTemplateThatReportsProvisioning_AsksForThePublishAtOnceSinceThePublishProvisionsIt(string template)
    {
        var publisher = new ProvisioningPublisher(new object?[] { Provisioning }, null, Operation("Succeeded", 2)) { Template = template };

        Assert.Equal(new AgentPublishOutcome(AgentPublishOutcomeKind.Succeeded, "Succeeded", null) { FinishedOn = Operation("Succeeded", 2).OperationEnd }, await PublishAsync(publisher, TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { "provisioning", "read", "publish", "read" }, publisher.Calls);
        Assert.Equal(Seconds(1), _clock.Delays);
    }

    [Theory]
    [InlineData(10, new[] { 2, 3, 4, 1 })]
    [InlineData(1, new[] { 1 })]
    public async Task PublishAsync_WhenTheAgentIsStillProvisioningAtTheTimeout_TimesOutWithoutAskingForAPublish(int timeoutSeconds, int[] expectedDelays)
    {
        var publisher = new ProvisioningPublisher(new object?[] { Provisioning }, null);

        Assert.Equal(new AgentPublishOutcome(AgentPublishOutcomeKind.TimedOut, null, $"Copilot Studio was still provisioning the agent after {TimeSpan.FromSeconds(timeoutSeconds)}, so no publish was asked for."), await PublishAsync(publisher, TimeSpan.FromSeconds(timeoutSeconds)));
        Assert.Equal(Seconds(expectedDelays), _clock.Delays);
        Assert.Equal(Enumerable.Repeat("provisioning", expectedDelays.Length + 1), publisher.Calls);
    }

    [Theory]
    [MemberData(nameof(ProvisioningReadsThatDoNotWait))]
    public async Task PublishAsync_WhenTheAgentReportsAnotherProvisioningStatusOrItCannotBeRead_AsksForThePublishWithoutWaiting(object? status)
    {
        var publisher = new ProvisioningPublisher(new[] { status }, null, Operation("Succeeded", 2));

        Assert.Equal(AgentPublishOutcomeKind.Succeeded, (await PublishAsync(publisher, TimeSpan.FromMinutes(5))).Kind);
        Assert.Equal(new[] { "provisioning", "read", "publish", "read" }, publisher.Calls);
        Assert.Equal(Seconds(1), _clock.Delays);
    }

    [Fact]
    public async Task PublishAsync_WhenCanceledWhileWaitingForTheAgentToProvision_StopsWithoutAskingForAPublish()
    {
        using var cancellation = new CancellationTokenSource();
        Func<object?> cancelDuringRead = () =>
        {
            cancellation.Cancel();

            return new OperationCanceledException(cancellation.Token);
        };
        var publisher = new ProvisioningPublisher(new object?[] { Provisioning, cancelDuringRead }, null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AgentPublishService(publisher, _clock).PublishAsync(AgentId, TimeSpan.FromMinutes(5), cancellation.Token));

        Assert.Equal(new[] { "provisioning", "provisioning" }, publisher.Calls);
        Assert.Equal(Seconds(2), _clock.Delays);
    }

    private static AgentPublishOperation Operation(string status, int minute) => new(status, new DateTimeOffset(2026, 10, 25, 19, minute, 2, TimeSpan.Zero), new DateTimeOffset(2026, 10, 25, 19, minute, 12, TimeSpan.Zero));

    internal static TimeSpan[] Seconds(params int[] seconds) => seconds.Select(second => TimeSpan.FromSeconds(second)).ToArray();

    private static Task<TResult?> Play<TResult>(object?[] script, ref int played) where TResult : class
    {
        var scripted = script[Math.Min(played++, script.Length - 1)];
        var result = scripted is Func<object?> produce ? produce() : scripted;

        return result is Exception failure ? Task.FromException<TResult?>(failure) : Task.FromResult((TResult?)result);
    }

    private Task<AgentPublishOutcome> PublishAsync(ScriptedPublisher publisher, TimeSpan timeout) => new AgentPublishService(publisher, _clock).PublishAsync(AgentId, timeout, CancellationToken.None);

    private class ScriptedPublisher : IAgentPublishDataverseClient
    {
        private readonly object?[] _reads;

        private int _readCount;

        private int _publishCount;

        public ScriptedPublisher(object? baseline, params object?[] polls) => _reads = polls.Prepend(baseline).ToArray();

        public List<string> Calls { get; } = new();

        public Exception?[] PublishFailures { get; init; } = Array.Empty<Exception?>();

        public Action? OnPublish { get; init; }

        public void SetDataverseUrl(string dataverseUrl) => throw new NotSupportedException();

        public Task PublishAgentAsync(Guid agentId, CancellationToken cancellationToken)
        {
            var failure = _publishCount < PublishFailures.Length ? PublishFailures[_publishCount] : null;
            _publishCount++;
            Calls.Add("publish");
            OnPublish?.Invoke();

            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }

        public Task<AgentPublishOperation?> GetLastPublishOperationAsync(Guid agentId, CancellationToken cancellationToken)
        {
            Calls.Add("read");

            return Play<AgentPublishOperation>(_reads, ref _readCount);
        }
    }

    private sealed class ProvisioningPublisher : ScriptedPublisher, IAgentProvisioningDataverseClient
    {
        private readonly object?[] _statuses;

        private int _statusCount;

        public ProvisioningPublisher(object?[] statuses, object? baseline, params object?[] polls) : base(baseline, polls) => _statuses = statuses;

        public string? Template { get; init; }

        public async Task<AgentProvisioningStatus> GetProvisioningStatusAsync(Guid agentId, CancellationToken cancellationToken)
        {
            Calls.Add("provisioning");

            return new AgentProvisioningStatus(await Play<string>(_statuses, ref _statusCount), Template);
        }
    }
}
