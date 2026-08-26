// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ProductionFileAccessorFactory = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessorFactory;
using ProductionInMemoryFileAccessor = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessor;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class KnowledgeDownloadStagingNameTests
{
    [Theory]
    [InlineData("Doc.txt.aaaabbbbccccddddeeeeffff00001111.download.tmp")]
    [InlineData("Doc.txt.AAAABBBBCCCCDDDDEEEEFFFF00001111.download.tmp")]
    [InlineData("Doc.txt.00000000000000000000000000000000.download.tmp")]
    public void GeneratedStagingNames_AreRecognised(string fileName)
    {
        Assert.True(WorkspaceSynchronizer.IsKnowledgeDownloadStagingFile(new AgentFilePath("knowledge/files/" + fileName)));
    }

    [Theory]
    [InlineData("report.download.tmp")]
    [InlineData("Doc.txt.download.tmp")]
    [InlineData("Doc.txt.notavalidguidvalue.download.tmp")]
    [InlineData("Doc.txt.aaaabbbbccccddddeeeeffff0000111.download.tmp")]
    [InlineData("Doc.txt.aaaabbbbccccddddeeeeffff000011112.download.tmp")]
    [InlineData("Doc.txt.aaaabbbbccccddddeeeeffff0000zzzz.download.tmp")]
    [InlineData("Doc.txt")]
    public void UserFileNames_AreNotTreatedAsStaging(string fileName)
    {
        Assert.False(WorkspaceSynchronizer.IsKnowledgeDownloadStagingFile(new AgentFilePath("knowledge/files/" + fileName)));
    }

    [Fact]
    public void GeneratedStagingName_MatchesTheDetector()
    {
        var stagingName = $"Doc.txt.{Guid.NewGuid():N}.download.tmp";

        Assert.True(WorkspaceSynchronizer.IsKnowledgeDownloadStagingFile(new AgentFilePath("knowledge/files/" + stagingName)));
    }
}

public class BoundedWriteStreamTests
{
    [Fact]
    public void Write_WithinLimit_PassesThrough()
    {
        using var inner = new MemoryStream();
        using (var bounded = new BoundedWriteStream(inner, 16, "Doc.txt"))
        {
            var payload = Encoding.UTF8.GetBytes("0123456789");
            bounded.Write(payload, 0, payload.Length);
        }

        Assert.Equal("0123456789", Encoding.UTF8.GetString(inner.ToArray()));
    }

    [Fact]
    public void Write_ExceedingLimit_ThrowsKnowledgeFileTooLarge()
    {
        using var inner = new MemoryStream();
        using var bounded = new BoundedWriteStream(inner, 4, "Doc.txt");
        var payload = Encoding.UTF8.GetBytes("0123456789");

        var failure = Assert.Throws<KnowledgeFileTooLargeException>(() => bounded.Write(payload, 0, payload.Length));

        Assert.Equal("Doc.txt", failure.FileName);
        Assert.Equal(4, failure.MaxBytes);
    }

    [Fact]
    public void Write_ExceedingLimit_Throws()
    {
        using var inner = new MemoryStream();
        using var bounded = new BoundedWriteStream(inner, 4, "Doc.txt");
        var payload = Encoding.UTF8.GetBytes("0123456789");

        Assert.Throws<KnowledgeFileTooLargeException>(() => bounded.Write(payload, 0, payload.Length));
    }

    [Fact]
    public void Write_ExceedingLimitAcrossCalls_Throws()
    {
        using var inner = new MemoryStream();
        using var bounded = new BoundedWriteStream(inner, 6, "Doc.txt");
        var payload = Encoding.UTF8.GetBytes("1234");

        bounded.Write(payload, 0, payload.Length);

        Assert.Throws<KnowledgeFileTooLargeException>(() => bounded.Write(payload, 0, payload.Length));
    }

    [Fact]
    public async Task CopyToAsync_ExceedingLimit_Throws()
    {
        using var source = new MemoryStream(new byte[64]);
        using var inner = new MemoryStream();
        using var bounded = new BoundedWriteStream(inner, 16, "Doc.txt");

        await Assert.ThrowsAsync<KnowledgeFileTooLargeException>(() => source.CopyToAsync(bounded, 8, CancellationToken.None));
    }

    [Fact]
    public void ExceptionReportsTheLimitAndKeepsTheFileNameOffTheMessage()
    {
        using var inner = new MemoryStream();
        using var bounded = new BoundedWriteStream(inner, 4, "Report.pdf");

        var failure = Assert.Throws<KnowledgeFileTooLargeException>(() => bounded.Write(new byte[8], 0, 8));

        Assert.DoesNotContain("Report.pdf", failure.Message, StringComparison.Ordinal);
        Assert.Contains("4", failure.Message, StringComparison.Ordinal);
        Assert.Equal("Report.pdf", failure.FileName);
        Assert.Equal(4, failure.MaxBytes);
    }

    [Fact]
    public void Dispose_DisposesInnerStreamSoContentIsCommitted()
    {
        using var factory = new ProductionFileAccessorFactory();
        var accessor = factory.Create(new DirectoryPath("c:/test/bounded-commit/"));
        var path = new AgentFilePath("knowledge/files/Doc.txt");

        using (var bounded = new BoundedWriteStream(accessor.OpenWrite(path), 128, "Doc.txt"))
        {
            var payload = Encoding.UTF8.GetBytes("committed");
            bounded.Write(payload, 0, payload.Length);
        }

        Assert.True(accessor.Exists(path));
    }
}

public class SyncStorageModeRegistrationTests
{
    [Fact]
    public void DefaultMode_ResolvesThePhysicalFactory()
    {
        Assert.False(ResolveFactory(null).IsMemoryBacked);
    }

    [Fact]
    public void PhysicalMode_ResolvesThePhysicalFactory()
    {
        Assert.False(ResolveFactory(SyncStorageMode.Physical).IsMemoryBacked);
    }

    [Fact]
    public void InMemoryMode_ResolvesTheInMemoryFactory()
    {
        Assert.True(ResolveFactory(SyncStorageMode.InMemory).IsMemoryBacked);
    }

    [Fact]
    public void InMemoryMode_FactoryIsDisposedWithTheContainer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFileAccessorFactory, ProductionFileAccessorFactory>();
        var provider = services.BuildServiceProvider();
        var factory = (ProductionFileAccessorFactory)provider.GetRequiredService<IFileAccessorFactory>();
        var accessor = (ProductionInMemoryFileAccessor)factory.Create(new DirectoryPath("c:/test/container-scope/"));

        using (var stream = accessor.OpenWrite(new AgentFilePath("settings.mcs.yml")))
        {
            stream.Write(new byte[64], 0, 64);
        }

        Assert.Equal(1, accessor.Count);

        provider.Dispose();

        Assert.Equal(0, accessor.Count);
    }

    private static IFileAccessorFactory ResolveFactory(SyncStorageMode? storageMode)
    {
        var services = new ServiceCollection();
        if (storageMode == null)
        {
            services.AddSyncServices();
        }
        else
        {
            services.AddSyncServices(storageMode: storageMode.Value);
        }

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IFileAccessorFactory>();
    }
}
