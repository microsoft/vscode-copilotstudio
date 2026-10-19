// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.CopilotStudio.McsCore.Yaml;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Yaml;

public class McsYamlGuardTests
{
    [Fact]
    public void ReaderRejectsNullInput()
    {
        Assert.Throws<ArgumentNullException>(() => McsYamlReader.Parse(null!));
        Assert.Throws<ArgumentNullException>(() => McsYamlReader.ParseDocument(null!));
    }

    [Fact]
    public void WriterRejectsNullInput()
    {
        Assert.Throws<ArgumentNullException>(() => McsYamlWriter.Write(null!));
    }

    [Theory]
    [InlineData("x\0y", "a: \"x\\0y\"")]
    [InlineData("x\ay", "a: \"x\\ay\"")]
    [InlineData("x\by", "a: \"x\\by\"")]
    [InlineData("x\vy", "a: \"x\\vy\"")]
    [InlineData("x\fy", "a: \"x\\fy\"")]
    [InlineData("x\u001by", "a: \"x\\ey\"")]
    [InlineData("x\ry", "a: \"x\\ry\"")]
    [InlineData("x\ny", "a: \"x\\ny\"")]
    [InlineData("x\ty", "a: \"x\\ty\"")]
    [InlineData("x\u0085y", "a: \"x\\Ny\"")]
    [InlineData("x\u2028y", "a: \"x\\Ly\"")]
    [InlineData("x\u2029y", "a: \"x\\Py\"")]
    [InlineData("x\u0001y", "a: \"x\\x01y\"")]
    [InlineData("x\u009fy", "a: \"x\\x9fy\"")]
    public void WriterEscapesControlCharacters(string value, string expected)
    {
        Assert.Equal(LineEndings.ToPlatform(expected + "\r\n"), McsYamlWriter.Write(new Dictionary<string, object?> { ["a"] = value }));
    }

    [Fact]
    public void WriterEscapesNonBreakingSpaceOnlyWhenTheScalarIsAlreadyQuoted()
    {
        Assert.Equal(LineEndings.ToPlatform("a: x\u00a0y\r\n"), McsYamlWriter.Write(new Dictionary<string, object?> { ["a"] = "x\u00a0y" }));
        Assert.Equal(LineEndings.ToPlatform("a: \"x\\_y\\nz\"\r\n"), McsYamlWriter.Write(new Dictionary<string, object?> { ["a"] = "x\u00a0y\nz" }));
    }

    [Theory]
    [InlineData("x\0y")]
    [InlineData("x\ay")]
    [InlineData("x\u001by")]
    [InlineData("x\u00a0y\nz")]
    [InlineData("x\u0001y")]
    [InlineData("x\u2028y")]
    public void EscapedControlCharactersSurviveARoundTrip(string value)
    {
        var document = new Dictionary<string, object?> { ["a"] = value };

        Assert.Equal(value, McsYamlReader.Parse(McsYamlWriter.Write(document))["a"]);
    }

    [Theory]
    [InlineData(typeof(McsYamlFormatException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ArgumentException))]
    public void ProjectionComparisonTreatsSerializationErrorsAsAMismatch(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "failed")!;

        Assert.True(WorkspaceSynchronizer.IsProjectionSerializationFailure(exception));
    }

    [Fact]
    public void ProjectionComparisonTreatsRealObjectModelParseFailuresAsAMismatch()
    {
        var exception = Record.Exception(() => CodeSerializer.Deserialize<BotEntity>(string.Empty, null!));

        Assert.NotNull(exception);
        Assert.True(
            WorkspaceSynchronizer.IsProjectionSerializationFailure(exception!),
            $"a real ObjectModel failure of type {exception!.GetType().Name} is not treated as a projection mismatch");
    }

    [Theory]
    [InlineData(typeof(OutOfMemoryException))]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(IOException))]
    public void ProjectionComparisonDoesNotSwallowUnrelatedFailures(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.False(WorkspaceSynchronizer.IsProjectionSerializationFailure(exception));
    }
}
