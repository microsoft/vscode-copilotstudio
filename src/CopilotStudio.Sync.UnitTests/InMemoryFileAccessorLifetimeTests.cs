// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Xunit;
using ProductionFileAccessorFactory = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessorFactory;
using ProductionInMemoryFileAccessor = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessor;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

[CollectionDefinition(MemoryLifetimeCollection.Name, DisableParallelization = true)]
public sealed class MemoryLifetimeCollection
{
    public const string Name = "MemoryLifetime";
}

[Collection(MemoryLifetimeCollection.Name)]
public class InMemoryFileAccessorLifetimeTests
{
    private const int PayloadSize = 512 * 1024;

    [Fact]
    public void Factory_ReportsMemoryBacked()
    {
        using var factory = new ProductionFileAccessorFactory();

        Assert.True(factory.IsMemoryBacked);
    }

    [Fact]
    public void Release_DiscardsContentEvenWhenCallerStillHoldsTheAccessor()
    {
        using var factory = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/release-holds-accessor/");
        var accessor = (ProductionInMemoryFileAccessor)factory.Create(root);
        WritePayload(accessor, "knowledge/files/Doc.txt");

        Assert.Equal(1, accessor.Count);

        factory.Release(root);

        Assert.Equal(0, accessor.Count);
    }

    [Fact]
    public void Release_ForUnknownRoot_DoesNothing()
    {
        using var factory = new ProductionFileAccessorFactory();

        factory.Release(new DirectoryPath("c:/test/never-created/"));
    }

    [Fact]
    public void Release_CalledTwice_DoesNothingTheSecondTime()
    {
        using var factory = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/release-twice/");
        WritePayload(factory.Create(root), "settings.mcs.yml");

        factory.Release(root);
        factory.Release(root);
    }

    [Fact]
    public void Create_AfterRelease_ReturnsAnEmptyStore()
    {
        using var factory = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/reused-root/");
        WritePayload(factory.Create(root), "settings.mcs.yml");
        factory.Release(root);

        Assert.Equal(0, ((ProductionInMemoryFileAccessor)factory.Create(root)).Count);
    }

    [Fact]
    public void Dispose_DiscardsEveryWorkspaceStore()
    {
        var accessors = new List<ProductionInMemoryFileAccessor>();
        using (var factory = new ProductionFileAccessorFactory())
        {
            for (var index = 0; index < 5; index++)
            {
                var accessor = (ProductionInMemoryFileAccessor)factory.Create(new DirectoryPath($"c:/test/dispose-{index}/"));
                WritePayload(accessor, "knowledge/files/Doc.txt");
                accessors.Add(accessor);
            }

            Assert.All(accessors, accessor => Assert.Equal(1, accessor.Count));
        }

        Assert.All(accessors, accessor => Assert.Equal(0, accessor.Count));
    }

    [Fact]
    public void ReleasedAccessor_IsEligibleForCollection()
    {
        using var factory = new ProductionFileAccessorFactory();

        var weakAccessor = CreateAndRelease(factory, new DirectoryPath("c:/test/collectable/"));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weakAccessor.IsAlive);
    }

    [Fact]
    public void RepeatedWorkspaceCycles_DoNotRetainWorkspaceBytes()
    {
        const int Cycles = 40;
        using var factory = new ProductionFileAccessorFactory();
        var retained = new List<IFileAccessor>();

        RunWorkspaceCycle(factory, 0, retained);
        var baseline = GetSettledMemory();

        for (var index = 1; index <= Cycles; index++)
        {
            RunWorkspaceCycle(factory, index, retained);
        }

        var growth = GetSettledMemory() - baseline;
        GC.KeepAlive(retained);

        Assert.True(growth < PayloadSize * 4, $"Retained {growth} bytes after {Cycles} released workspaces holding {PayloadSize} bytes each.");
    }

    [Fact]
    public void WrittenContent_RoundTripsExactly()
    {
        using var factory = new ProductionFileAccessorFactory();
        var accessor = factory.Create(new DirectoryPath("c:/test/round-trip/"));
        var path = new AgentFilePath("knowledge/files/Doc.txt");
        var payload = Encoding.UTF8.GetBytes(new string('a', 100_000) + "-tail");

        using (var stream = accessor.OpenWrite(path))
        {
            stream.Write(payload, 0, payload.Length);
        }

        using var read = new MemoryStream();
        using (var source = accessor.OpenRead(path))
        {
            source.CopyTo(read);
        }

        Assert.Equal(payload, read.ToArray());
    }

    [Fact]
    public void OverwrittenContent_DoesNotRetainTheEarlierTail()
    {
        using var factory = new ProductionFileAccessorFactory();
        var accessor = factory.Create(new DirectoryPath("c:/test/overwrite/"));
        var path = new AgentFilePath("settings.mcs.yml");

        using (var stream = accessor.OpenWrite(path))
        {
            var large = Encoding.UTF8.GetBytes(new string('x', 50_000));
            stream.Write(large, 0, large.Length);
        }

        using (var stream = accessor.OpenWrite(path))
        {
            var small = Encoding.UTF8.GetBytes("short");
            stream.Write(small, 0, small.Length);
        }

        using var read = new MemoryStream();
        using (var source = accessor.OpenRead(path))
        {
            source.CopyTo(read);
        }

        Assert.Equal("short", Encoding.UTF8.GetString(read.ToArray()));
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        using var factory = new ProductionFileAccessorFactory();
        var accessor = factory.Create(new DirectoryPath("c:/test/double-dispose/"));
        var stream = accessor.OpenWrite(new AgentFilePath("settings.mcs.yml"));
        stream.Write(new byte[32], 0, 32);

        stream.Dispose();
        stream.Dispose();

        Assert.True(accessor.Exists(new AgentFilePath("settings.mcs.yml")));
    }

    [Fact]
    public void Replace_MovesContentAndRemovesTheSource()
    {
        using var factory = new ProductionFileAccessorFactory();
        var accessor = factory.Create(new DirectoryPath("c:/test/replace/"));
        var source = new AgentFilePath("knowledge/files/Doc.txt.aaaabbbbccccddddeeeeffff00001111.download.tmp");
        var target = new AgentFilePath("knowledge/files/Doc.txt");
        WritePayload(accessor, source.ToString());

        accessor.Replace(source, target);

        Assert.False(accessor.Exists(source));
        Assert.True(accessor.Exists(target));
    }

    [Fact]
    public void Replace_MissingSource_Throws()
    {
        using var factory = new ProductionFileAccessorFactory();
        var accessor = factory.Create(new DirectoryPath("c:/test/replace-missing/"));

        Assert.Throws<FileNotFoundException>(() => accessor.Replace(new AgentFilePath("absent.txt"), new AgentFilePath("target.txt")));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndRelease(ProductionFileAccessorFactory factory, DirectoryPath root)
    {
        var accessor = (ProductionInMemoryFileAccessor)factory.Create(root);
        WritePayload(accessor, "knowledge/files/Doc.txt");
        var weakAccessor = new WeakReference(accessor);
        factory.Release(root);
        return weakAccessor;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunWorkspaceCycle(ProductionFileAccessorFactory factory, int index, List<IFileAccessor> retained)
    {
        var root = new DirectoryPath($"c:/test/cycle-{index}/");
        var accessor = factory.Create(root);
        WritePayload(accessor, "knowledge/files/Doc.txt");
        WritePayload(accessor, "settings.mcs.yml");
        retained.Add(accessor);
        factory.Release(root);
    }

    private static long GetSettledMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static void WritePayload(IFileAccessor accessor, string relativePath)
    {
        using var stream = accessor.OpenWrite(new AgentFilePath(relativePath));
        stream.Write(new byte[PayloadSize], 0, PayloadSize);
    }
}
