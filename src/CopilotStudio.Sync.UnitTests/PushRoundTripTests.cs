// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class PushRoundTripTests
{
    private const string MarkerPath = "NB1/agent.sync.yaml";
    private const string GreetingPath = "NB1/topics/Greeting.mcs.yml";
    private const string FlowPath = "NB1/workflows/NBWorkflow1-588a3ca3-076c-1b1e-7bee-5e4a397a639d/metadata.yml";
    private const string SkillPath = "NB1/behaviors/my-skill-1/skill.mcs.yml";
    private const string SettingsPath = "ContosoHR/settings.mcs.yml";
    private const string Marker = "# Workspace layout marker (Sync overlay; generic YAML, never MCS-parsed).\nlayoutVersion: 1\n";
    private const string ActivatedFlow = "name: NB Workflow1\nsyncWorkflowLogOnFailure: false\nstateCode: 1\nstatusCode: 2\nrunAs: 1\n";
    private const string DraftFlow = "name: NB Workflow1\nsyncWorkflowLogOnFailure: false\nstateCode: 0\nstatusCode: 1\nrunAs: 1\n";
    private const string Skill = "mcs.metadata:\n  componentName: my-skill-1\n  description: description of skill 1 cloud 2\nkind: InlineAgentSkill\nskillFolderName: my-skill-1";
    private const string BlankSkill = PushRoundTripTests.Skill + "\nauthoringSource: Blank";

    private static readonly byte[] Icon = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x0D, 0x0A };

    public static IEnumerable<object[]> AgentFilesThatDifferOnlyInWhatThePushDoesNotCarry => new[]
    {
        new object[] { PushRoundTripTests.FlowPath, "\uFEFF" + PushRoundTripTests.DraftFlow, PushRoundTripTests.ActivatedFlow },
        new object[] { PushRoundTripTests.FlowPath, PushRoundTripTests.DraftFlow.Replace("\n", "\r\n", StringComparison.Ordinal), PushRoundTripTests.ActivatedFlow },
        new object[] { PushRoundTripTests.SkillPath, PushRoundTripTests.Skill, PushRoundTripTests.BlankSkill },
        new object[] { PushRoundTripTests.SkillPath, "kind: InlineAgentSkill\nskillFolderName: my-skill-1\n", "authoringSource: Blank\nkind: InlineAgentSkill\nskillFolderName: my-skill-1\n" },
    };

    public static IEnumerable<object[]> AgentFilesWithAnEditBesideWhatThePushDoesNotCarry => new[]
    {
        new object[] { PushRoundTripTests.FlowPath, PushRoundTripTests.DraftFlow.Replace("NB Workflow1", "NB Workflow2", StringComparison.Ordinal), PushRoundTripTests.ActivatedFlow },
        new object[] { PushRoundTripTests.SkillPath, PushRoundTripTests.Skill.Replace("cloud 2", "edited in Copilot Studio", StringComparison.Ordinal), PushRoundTripTests.BlankSkill },
    };

    public static IEnumerable<object[]> AgentFilesOtherThanAFlowsMetadataOrASkill => new[]
    {
        new object[] { "NB1/settings.mcs.yml", PushRoundTripTests.DraftFlow, PushRoundTripTests.ActivatedFlow },
        new object[] { PushRoundTripTests.GreetingPath, PushRoundTripTests.Skill, PushRoundTripTests.BlankSkill },
        new object[] { "NB1/workflows/NBWorkflow1-588a3ca3-076c-1b1e-7bee-5e4a397a639d/workflow.json", PushRoundTripTests.DraftFlow, PushRoundTripTests.ActivatedFlow },
        new object[] { "NB1/behaviors/my-skill-1/SKILL.md", PushRoundTripTests.Skill, PushRoundTripTests.BlankSkill },
    };

    public static IEnumerable<object[]> FlowsThatAreNotUtf8 => new[]
    {
        new object[] { PushRoundTripTests.ToBytes("stateCode: 0\n", 0xFF), Encoding.UTF8.GetBytes("stateCode: 1\n\uFFFD") },
        new object[] { Encoding.UTF8.GetBytes("stateCode: 0\n\uFFFD"), PushRoundTripTests.ToBytes("stateCode: 1\n", 0xFF) },
    };

    public static IEnumerable<object[]> TextThatDiffersOnlyInByteOrderMarksOrLineEndings => new[]
    {
        new object[] { "\uFEFFkind: AgentDialog\nname: HR\n", "kind: AgentDialog\nname: HR\n" },
        new object[] { "kind: AgentDialog\r\nname: HR\r\n", "\uFEFFkind: AgentDialog\nname: HR\n" },
        new object[] { "\uFEFFa\r\nb", "\uFEFFa\nb" },
    };

    public static IEnumerable<object[]> FilesThatHoldDifferentText => new[]
    {
        new object[] { Encoding.UTF8.GetBytes("\uFEFFkind: AgentDialog\nname: Payroll\n"), Encoding.UTF8.GetBytes("kind: AgentDialog\nname: HR\n") },
        new object[] { PushRoundTripTests.Icon, PushRoundTripTests.Icon },
        new object[] { Encoding.UTF8.GetBytes("kind: AgentDialog\n"), PushRoundTripTests.Icon },
    };

    [Fact]
    public void ToComparedForm_FilesEqualToTheFilesTheyAreComparedWithOrComparedWithNone_ReturnsTheFilesThemselves()
    {
        var files = PushRoundTripTests.Files((PushRoundTripTests.MarkerPath, PushRoundTripTests.Marker), (PushRoundTripTests.SkillPath, PushRoundTripTests.Skill));

        Assert.Same(files, PushRoundTrip.ToComparedForm(files, PushRoundTripTests.Files((PushRoundTripTests.MarkerPath, PushRoundTripTests.Marker)), isAgent: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToComparedForm_TextThatDiffersOnlyInAByteOrderMarkOrLineEndings_TakesTheComparedBytes(bool isAgent)
    {
        var compared = PushRoundTripTests.Files((PushRoundTripTests.MarkerPath, "\uFEFF" + PushRoundTripTests.Marker), (PushRoundTripTests.GreetingPath, "kind: AdaptiveDialog\n"));

        var inComparedForm = PushRoundTrip.ToComparedForm(PushRoundTripTests.Files((PushRoundTripTests.MarkerPath, PushRoundTripTests.Marker), (PushRoundTripTests.GreetingPath, "kind: AdaptiveDialog\r\n")), compared, isAgent);

        Assert.True(inComparedForm[PushRoundTripTests.MarkerPath].Equals(compared[PushRoundTripTests.MarkerPath]));
        Assert.True(inComparedForm[PushRoundTripTests.GreetingPath].Equals(compared[PushRoundTripTests.GreetingPath]));
    }

    [Theory]
    [MemberData(nameof(AgentFilesThatDifferOnlyInWhatThePushDoesNotCarry))]
    public void ToComparedForm_AgentFlowOrSkillThatDiffersOnlyInWhatThePushDoesNotCarry_TakesTheComparedBytes(string path, string content, string comparedContent)
    {
        var compared = PushRoundTripTests.Files((path, comparedContent));

        Assert.True(PushRoundTrip.ToComparedForm(PushRoundTripTests.Files((path, content)), compared, isAgent: true)[path].Equals(compared[path]));
    }

    [Theory]
    [MemberData(nameof(AgentFilesWithAnEditBesideWhatThePushDoesNotCarry))]
    public void ToComparedForm_AgentFlowOrSkillWithAnEditBesideWhatThePushDoesNotCarry_KeepsDevsBytes(string path, string content, string comparedContent)
    {
        var files = PushRoundTripTests.Files((path, content));

        Assert.Same(files, PushRoundTrip.ToComparedForm(files, PushRoundTripTests.Files((path, comparedContent)), isAgent: true));
    }

    [Fact]
    public void ToComparedForm_StandaloneWorkflowWhoseStateDiffers_KeepsDevsBytes()
    {
        var files = PushRoundTripTests.Files((PushRoundTripTests.FlowPath, PushRoundTripTests.DraftFlow));

        Assert.Same(files, PushRoundTrip.ToComparedForm(files, PushRoundTripTests.Files((PushRoundTripTests.FlowPath, PushRoundTripTests.ActivatedFlow)), isAgent: false));
    }

    [Theory]
    [MemberData(nameof(AgentFilesOtherThanAFlowsMetadataOrASkill))]
    public void ToComparedForm_AgentFileOtherThanAFlowsMetadataOrASkill_KeepsDevsStateAndAuthoringSourceEdits(string path, string content, string comparedContent)
    {
        var files = PushRoundTripTests.Files((path, content));

        Assert.Same(files, PushRoundTrip.ToComparedForm(files, PushRoundTripTests.Files((path, comparedContent)), isAgent: true));
    }

    [Theory]
    [MemberData(nameof(FlowsThatAreNotUtf8))]
    public void ToComparedForm_AgentFlowThatIsNotUtf8_KeepsDevsBytes(byte[] content, byte[] comparedContent)
    {
        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [PushRoundTripTests.FlowPath] = content };

        Assert.Same(files, PushRoundTrip.ToComparedForm(files, new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [PushRoundTripTests.FlowPath] = comparedContent }, isAgent: true));
    }

    [Theory]
    [MemberData(nameof(TextThatDiffersOnlyInByteOrderMarksOrLineEndings))]
    public void ToComparedForm_TextThatDiffersOnlyInByteOrderMarksOrLineEndings_TakesTheComparedBytes(string content, string comparedContent)
    {
        var compared = PushRoundTripTests.Files((PushRoundTripTests.SettingsPath, comparedContent));

        Assert.True(PushRoundTrip.ToComparedForm(PushRoundTripTests.Files((PushRoundTripTests.SettingsPath, content)), compared, isAgent: true)[PushRoundTripTests.SettingsPath].Equals(compared[PushRoundTripTests.SettingsPath]));
    }

    [Theory]
    [MemberData(nameof(FilesThatHoldDifferentText))]
    public void ToComparedForm_DifferentTextOrABinaryFile_ReturnsTheFilesThemselves(byte[] content, byte[] comparedContent)
    {
        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [PushRoundTripTests.SettingsPath] = content };

        Assert.Same(files, PushRoundTrip.ToComparedForm(files, new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) { [PushRoundTripTests.SettingsPath] = comparedContent }, isAgent: true));
    }

    private static Dictionary<string, ReadOnlyMemory<byte>> Files(params (string Path, string Content)[] files) => files.ToDictionary(file => file.Path, file => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(file.Content), StringComparer.Ordinal);

    private static byte[] ToBytes(string text, byte trailing) => Encoding.UTF8.GetBytes(text).Append(trailing).ToArray();
}
