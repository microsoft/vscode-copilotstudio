// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.Hosting;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class DataverseFailureTextTests
{
    internal const string Marker = "RAW-BODY-MARKER-4b1e";

    internal const string ReportedDescription = "HTTP 400 Bad Request. The referenced row does not exist. (0x80040217)";

    internal static readonly string ReportedBody = $"{{\"error\":{{\"code\":\"0x80040217\",\"message\":\"The referenced row does not exist.\"}},\"trace\":\"{Marker}\"}}";

    public static TheoryData<HttpStatusCode, string, string> Descriptions => new()
    {
        { HttpStatusCode.Forbidden, $"{{\"error\":{{\"code\":\"0x80040220\",\"message\":\"The caller may not read bots.\"}},\"stack\":\"{Marker}\"}}", "HTTP 403 Forbidden. The caller may not read bots. (0x80040220)" },
        { HttpStatusCode.BadRequest, $"{{\"error\":{{\"message\":\"Only a message.\",\"detail\":\"{Marker}\"}}}}", "HTTP 400 Bad Request. Only a message." },
        { HttpStatusCode.BadRequest, $"{{\"error\":{{\"code\":\"0x80040216\",\"message\":\"  \"}},\"trace\":\"{Marker}\"}}", "HTTP 400 Bad Request." },
        { HttpStatusCode.BadRequest, $"{{\"error\":{{\"code\":\"0x80040216\",\"message\":7}},\"trace\":\"{Marker}\"}}", "HTTP 400 Bad Request." },
        { HttpStatusCode.BadRequest, $"{{\"error\":\"{Marker}\"}}", "HTTP 400 Bad Request." },
        { HttpStatusCode.BadRequest, $"[\"{Marker}\"]", "HTTP 400 Bad Request." },
        { HttpStatusCode.InternalServerError, $"<html>{Marker}</html>", "HTTP 500 Internal Server Error." },
        { HttpStatusCode.NotFound, string.Empty, "HTTP 404 Not Found." },
        { HttpStatusCode.NotFound, " \n ", "HTTP 404 Not Found." },
        { (HttpStatusCode)599, "{\"error\":{\"code\":\"0x1\",\"message\":\"Unknown status.\"}}", "HTTP 599 599. Unknown status. (0x1)" },
    };

    [Theory]
    [MemberData(nameof(Descriptions))]
    public void Describe_NamesTheStatusAndTheReportedErrorButNeverTheRestOfTheBody(HttpStatusCode statusCode, string responseBody, string description)
    {
        var failure = new DataverseRequestException(statusCode, responseBody);

        Assert.Equal(description, failure.Describe());
        Assert.Equal(description, DataverseFailureText.Of(failure));
        Assert.Equal($"Refused: {description}", DataverseFailureText.Redact($"Refused: {failure.Message}"));
        Assert.Equal($"Dataverse request failed ({(int)statusCode}): {responseBody}", failure.Message);
    }

    [Fact]
    public void Of_ADataverseFailure_DescribesIt() => Assert.Equal(ReportedDescription, DataverseFailureText.Of(Failure()));

    [Fact]
    public void Of_NoFailure_Throws() => Assert.Throws<ArgumentNullException>(() => DataverseFailureText.Of(null!));

    [Fact]
    public void Of_AFailureWrappingADataverseFailure_DescribesTheWrappedFailureWhereverItsMessageIsRepeated()
    {
        var inner = Failure();
        var middle = new InvalidOperationException($"The flow could not be stored: {inner.Message}", inner);

        Assert.Equal($"The push failed. The flow could not be stored: {ReportedDescription}", DataverseFailureText.Of(new InvalidOperationException($"The push failed. {middle.Message}", middle)));
    }

    [Fact]
    public void Of_AFailureWrappingAnAggregateFailure_DescribesEveryDataverseFailureTheAggregateHolds()
    {
        var aggregate = new AggregateException(new InvalidOperationException("Unrelated."), new DataverseRequestException(HttpStatusCode.Forbidden, $"{{\"error\":{{\"code\":\"0x80040220\",\"message\":\"The caller may not delete bots.\"}},\"stack\":\"{Marker}\"}}"), Failure());

        Assert.Equal($"Deleting the components failed. One or more errors occurred. (Unrelated.) (HTTP 403 Forbidden. The caller may not delete bots. (0x80040220)) ({ReportedDescription})", DataverseFailureText.Of(new InvalidOperationException($"Deleting the components failed. {aggregate.Message}", aggregate)));
    }

    [Fact]
    public void Of_AFailureNamingARawDataverseFailureItDoesNotWrap_DescribesIt() => Assert.Equal($"The workflow was refused: {ReportedDescription}", DataverseFailureText.Of(new InvalidOperationException($"The workflow was refused: {Failure().Message}")));

    [Fact]
    public void Of_AFailureWithoutADataverseFailure_KeepsItsMessage() => Assert.Equal("The island refused the change.", DataverseFailureText.Of(new InvalidOperationException("The island refused the change.", new TimeoutException("Slow."))));

    [Fact]
    public void RequestFailuresOf_ListsTheDataverseFailuresDepthFirstThroughInnerAndAggregatedFailuresAsOfDescribesThem()
    {
        var first = new DataverseRequestException(HttpStatusCode.Forbidden, $"{{\"error\":{{\"code\":\"0x80040220\",\"message\":\"The caller may not delete bots.\"}},\"stack\":\"{Marker}\"}}");
        var second = Failure();
        var third = new DataverseRequestException(HttpStatusCode.NotFound, string.Empty);
        var aggregate = new AggregateException(new InvalidOperationException("Wrapped.", first), new AggregateException(second, third));
        var outer = new InvalidOperationException($"The push failed. {aggregate.Message}", new InvalidOperationException("Retrying.", aggregate));

        Assert.Equal(new[] { first, second, third }, DataverseFailureText.RequestFailuresOf(outer));
        Assert.Equal($"The push failed. One or more errors occurred. (Wrapped.) (One or more errors occurred. ({ReportedDescription}) (HTTP 404 Not Found.))", DataverseFailureText.Of(outer));
    }

    [Fact]
    public void RequestFailuresOf_ADataverseFailure_ListsItself()
    {
        var failure = Failure();

        Assert.Same(failure, Assert.Single(DataverseFailureText.RequestFailuresOf(failure)));
    }

    [Fact]
    public void RequestFailuresOf_AnAggregateFailure_FindsTheDataverseFailureBehindAnyOfItsInnerFailures()
    {
        var refusal = Failure();

        Assert.Same(refusal, Assert.Single(DataverseFailureText.RequestFailuresOf(new AggregateException(new IOException("Disk full."), new InvalidOperationException("Wrapped.", refusal)))));
    }

    [Fact]
    public void RequestFailuresOf_AFailureWithoutADataverseFailure_FindsNone() => Assert.Empty(DataverseFailureText.RequestFailuresOf(new AggregateException(new InvalidOperationException("The island refused the change.", new TimeoutException("Slow.")))));

    [Fact]
    public void RequestFailuresOf_NoFailure_Throws() => Assert.Throws<ArgumentNullException>(() => DataverseFailureText.RequestFailuresOf(null!));

    [Theory]
    [InlineData(null, "")]
    [InlineData("Nothing to redact.", "Nothing to redact.")]
    [InlineData("Dataverse request failed (40): not a status", "Dataverse request failed (40): not a status")]
    public void Redact_OfTextWithoutARawDataverseFailure_KeepsIt(string? text, string expected) => Assert.Equal(expected, DataverseFailureText.Redact(text));

    [Fact]
    public void Redact_OfARawFailureWithAMultilineBody_DescribesItThroughTheEndOfTheText() => Assert.Equal($"Refused: {ReportedDescription}", DataverseFailureText.Redact($"Refused: Dataverse request failed (400): {ReportedBody.Replace(",", ",\n")}"));

    [Fact]
    public void AgentSyncDiagnostic_DescribesARawDataverseFailureHoweverItsMessageIsSet()
    {
        var created = new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.PublishFailed, $"Refused: {Failure().Message}", "Agent/settings.mcs.yml");
        var changed = new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.PublishFailed, "Refused.", "Agent/settings.mcs.yml") with { Message = $"Refused: {Failure().Message}" };

        Assert.Equal($"Refused: {ReportedDescription}", created.Message);
        Assert.Equal(created, changed);
    }

    [Fact]
    public void AgentSyncDiagnostic_DescribesARawDataverseFailureInItsDetailAndKeepsAMissingDetailMissing()
    {
        var diagnostic = new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.WorkflowRefused, "The environment refused the workflow 'Weather'.") { Subject = "Weather", Detail = Failure().Message };

        Assert.Equal(("Weather", ReportedDescription), (diagnostic.Subject, diagnostic.Detail));
        Assert.Null((diagnostic with { Detail = null }).Detail);
        Assert.Null(new AgentSyncDiagnostic(AgentSyncDiagnosticCodes.WorkflowRefused, "Refused.").Detail);
    }

    [Fact]
    public void AgentWorkspaceHostException_DescribesARawDataverseFailureThroughEveryConstructor()
    {
        var raw = $"The push failed: {Failure().Message}";
        var expected = $"The push failed: {ReportedDescription}";

        Assert.Equal(new[] { expected, expected, expected }, new[] { new AgentWorkspaceHostException(raw), new AgentWorkspaceHostException(raw, Failure()), new AgentWorkspaceHostException(AgentWorkspaceHostErrorCodes.PushFailed, raw, null, Failure()) }.Select(failure => failure.Message));
    }

    internal static DataverseRequestException Failure() => new(HttpStatusCode.BadRequest, ReportedBody);
}
