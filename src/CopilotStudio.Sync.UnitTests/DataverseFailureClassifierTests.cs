using Microsoft.Agents.Platform.Content.Exceptions;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class DataverseFailureClassifierTests
{
    private const string ComponentConflictText = "Cannot complete the creation of botcomponent because it violates a database constraint. The violation happens on the key schemaname: crf9a_ahe2emf66agentd1969_WWrnhE.file.abcmain2.txt_0Sh. Please delete the existing record or use update.";

    [Fact]
    public void FindKeyConflict_TheServiceTextOfADuplicateComponent_NamesTheEntityKeyAndValue()
    {
        var failure = new DataverseBadRequestException("ExportKeyInvalidCreate", "0x80072013", "b463c422-2dae-4621-b6bd-f58d18a80411", ComponentConflictText, null);

        Assert.Equal(new DataverseKeyConflict("botcomponent", "schemaname", "crf9a_ahe2emf66agentd1969_WWrnhE.file.abcmain2.txt_0Sh"), DataverseFailureClassifier.FindKeyConflict(failure));
    }

    [Fact]
    public void FindKeyConflict_TheTextAnEndToEndRunReported_NamesTheEntityKeyAndValue()
    {
        var failure = new InvalidOperationException("DataverseBadRequestException: HTTP Status:BadRequest, ServiceRequestId: b463c422-2dae-4621-b6bd-f58d18a80411 [0x80072013:ExportKeyInvalidCreate] " + ComponentConflictText);

        Assert.Equal(new DataverseKeyConflict("botcomponent", "schemaname", "crf9a_ahe2emf66agentd1969_WWrnhE.file.abcmain2.txt_0Sh"), DataverseFailureClassifier.FindKeyConflict(failure));
    }

    [Fact]
    public void FindKeyConflict_ADuplicateConnectionReference_NamesItsLogicalName()
    {
        var failure = new InvalidOperationException("[0x80072013:ExportKeyInvalidCreate] Cannot complete the creation of connectionreference because it violates a database constraint. The violation happens on the key connectionreferencelogicalname: cre98_AgentC1.cr.WX3p-EQ4");

        Assert.Equal(new DataverseKeyConflict("connectionreference", "connectionreferencelogicalname", "cre98_AgentC1.cr.WX3p-EQ4"), DataverseFailureClassifier.FindKeyConflict(failure));
    }

    [Theory]
    [InlineData("ExportKeyInvalidCreate at https://contoso.crm.dynamics.com/api/data/v9.2/connectionreferences", "connectionreference")]
    [InlineData("0x80072013 POST https://contoso.crm.dynamics.com/api/data/v9.2/activityparties(00000000-0000-0000-0000-000000000001)", "activityparty")]
    public void FindKeyConflict_OnlyTheMarkerAndAnEntitySetUrl_NamesTheSingularEntity(string message, string expectedEntity)
    {
        Assert.Equal(new DataverseKeyConflict(expectedEntity, null, null), DataverseFailureClassifier.FindKeyConflict(new InvalidOperationException(message)));
    }

    [Fact]
    public void FindKeyConflict_AConflictInAnInnerException_IsFound()
    {
        var failure = new InvalidOperationException("The push failed.", new AggregateException("One or more errors occurred.", new DataverseBadRequestException("ExportKeyInvalidCreate", "0x80072013", "request-1", ComponentConflictText, null)));

        Assert.Equal("botcomponent", DataverseFailureClassifier.FindKeyConflict(failure)?.EntityLogicalName);
    }

    [Theory]
    [InlineData("ExportKeyInvalidCreate without any entity")]
    [InlineData("Something else failed at https://contoso.crm.dynamics.com/api/data/v9.2/connectionreferences")]
    [InlineData("The service is unavailable.")]
    public void FindKeyConflict_TextThatNamesNoDuplicateOfAKnownEntity_ReturnsNull(string message)
    {
        Assert.Null(DataverseFailureClassifier.FindKeyConflict(new InvalidOperationException(message)));
    }

    [Fact]
    public void IsMissingRow_ANotFoundFailureNamingTheEntity_IsMissing()
    {
        var failure = new InvalidOperationException("The push failed.", new DataverseNotFoundException("ObjectDoesNotExist", "0x80040217", "request-1", "Entity 'connectionreference' With Id = 2a5f0c1e-0000-0000-0000-000000000001 Does Not Exist", null));

        Assert.True(DataverseFailureClassifier.IsMissingRow(failure, "connectionreference"));
        Assert.False(DataverseFailureClassifier.IsMissingRow(failure, "workflow"));
    }

    [Fact]
    public void IsMissingRow_AnObjectDoesNotExistCodeOnAnotherDataverseFailure_IsMissing()
    {
        var failure = new DataverseBadRequestException("ObjectDoesNotExist", "0x80040217", "request-1", "connectionreference With Id = 2a5f0c1e-0000-0000-0000-000000000001 Does Not Exist", null);

        Assert.True(DataverseFailureClassifier.IsMissingRow(failure, "connectionreference"));
    }

    [Fact]
    public void IsMissingRow_TextOutsideADataverseNotFoundFailure_IsNotMissing()
    {
        Assert.False(DataverseFailureClassifier.IsMissingRow(new InvalidOperationException("Entity 'connectionreference' With Id = 2a5f0c1e-0000-0000-0000-000000000001 Does Not Exist"), "connectionreference"));
        Assert.False(DataverseFailureClassifier.IsMissingRow(new DataverseBadRequestException("InvalidArgument", "0x80040203", "request-1", "connectionreference is invalid", null), "connectionreference"));
    }

    [Theory]
    [InlineData("Entity 'Workflow' With Id = 2a5f0c1e-0000-0000-0000-000000000001 Does Not Exist", true)]
    [InlineData("The request timed out.", false)]
    public void ReportsMissingRow_ReadsEveryMessageInTheChain(string innerMessage, bool expected)
    {
        Assert.Equal(expected, DataverseFailureClassifier.ReportsMissingRow(new InvalidOperationException("The push failed.", new InvalidOperationException(innerMessage))));
    }
}
