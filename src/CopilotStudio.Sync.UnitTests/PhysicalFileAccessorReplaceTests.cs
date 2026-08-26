// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class PhysicalFileAccessorReplaceTests : IDisposable
{
    private readonly string _rootPath;
    private readonly IFileAccessorFactory _factory;
    private readonly IFileAccessor _accessor;

    public PhysicalFileAccessorReplaceTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "mcs-replace-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_rootPath);
        _factory = new FileAccessorFactory();
        _accessor = _factory.Create(new DirectoryPath(_rootPath.Replace('\\', '/') + "/"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_rootPath, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Factory_ReportsNotMemoryBacked()
    {
        Assert.False(_factory.IsMemoryBacked);
    }

    [Fact]
    public void Release_DoesNotDeleteWorkspaceContent()
    {
        var path = new AgentFilePath("settings.mcs.yml");
        Write(path, "kept");

        _factory.Release(new DirectoryPath(_rootPath.Replace('\\', '/') + "/"));

        Assert.True(_accessor.Exists(path));
    }

    [Fact]
    public void Replace_WhenDestinationMissing_MovesSource()
    {
        var source = new AgentFilePath("staged.tmp");
        var target = new AgentFilePath("knowledge/files/Doc.txt");
        Write(source, "new-content");

        _accessor.Replace(source, target);

        Assert.False(_accessor.Exists(source));
        Assert.Equal("new-content", Read(target));
    }

    [Fact]
    public void Replace_WhenDestinationExists_SwapsContent()
    {
        var source = new AgentFilePath("staged.tmp");
        var target = new AgentFilePath("knowledge/files/Doc.txt");
        Write(target, "old-content");
        Write(source, "new-content");

        _accessor.Replace(source, target);

        Assert.False(_accessor.Exists(source));
        Assert.Equal("new-content", Read(target));
    }

    [Fact]
    public void Replace_LeavesNoBackupFileBehind()
    {
        var source = new AgentFilePath("staged.tmp");
        var target = new AgentFilePath("Doc.txt");
        Write(target, "old-content");
        Write(source, "new-content");

        _accessor.Replace(source, target);

        Assert.Empty(Directory.GetFiles(_rootPath, "*.replace.bak", SearchOption.AllDirectories));
    }

    [Fact]
    public void Replace_WhenDestinationIsLocked_PreservesDestinationContent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = new AgentFilePath("staged.tmp");
        var target = new AgentFilePath("Doc.txt");
        Write(target, "must-survive");
        Write(source, "new-content");

        var targetFullPath = Path.Combine(_rootPath, "Doc.txt");
        using (new FileStream(targetFullPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<Exception>(() => _accessor.Replace(source, target));
        }

        Assert.Equal("must-survive", Read(target));
    }

    [Fact]
    public void Replace_WhenRestoreAlsoFails_RetainsTheRecoveryCopy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = new AgentFilePath("staged.tmp");
        var target = new AgentFilePath("Doc.txt");
        Write(target, "must-be-recoverable");
        Write(source, "new-content");

        var targetFullPath = Path.Combine(_rootPath, "Doc.txt");
        using (new FileStream(targetFullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => _accessor.Replace(source, target));

            var backups = Directory.GetFiles(_rootPath, "*.replace.bak", SearchOption.AllDirectories);
            Assert.Single(backups);
            Assert.Equal("must-be-recoverable", File.ReadAllText(backups[0]));
        }
    }

    [Fact]
    public void Replace_WhenSourceIsMissing_PreservesDestinationContent()
    {
        var source = new AgentFilePath("missing-staged.tmp");
        var target = new AgentFilePath("Doc.txt");
        Write(target, "must-survive");

        Assert.ThrowsAny<Exception>(() => _accessor.Replace(source, target));

        Assert.Equal("must-survive", Read(target));
        Assert.Empty(Directory.GetFiles(_rootPath, "*.replace.bak", SearchOption.AllDirectories));
    }

    [Fact]
    public void Replace_RetainedRecoveryCopies_AreNotOverwrittenByALaterAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = new AgentFilePath("staged.tmp");
        var target = new AgentFilePath("Doc.txt");
        Write(target, "first-original");

        var targetFullPath = Path.Combine(_rootPath, "Doc.txt");
        using (new FileStream(targetFullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Write(source, "attempt-one");
            Assert.ThrowsAny<Exception>(() => _accessor.Replace(source, target));

            Write(source, "attempt-two");
            Assert.ThrowsAny<Exception>(() => _accessor.Replace(source, target));
        }

        var backups = Directory.GetFiles(_rootPath, "*.replace.bak", SearchOption.AllDirectories);

        Assert.Equal(2, backups.Length);
        Assert.All(backups, path => Assert.Equal("first-original", File.ReadAllText(path)));
    }

    private void Write(AgentFilePath path, string content)
    {
        using var stream = _accessor.OpenWrite(path);
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private string Read(AgentFilePath path)
    {
        using var stream = _accessor.OpenRead(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
