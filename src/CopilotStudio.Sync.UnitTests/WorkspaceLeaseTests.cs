// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Xunit;
using ProductionFileAccessorFactory = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessorFactory;
using ProductionInMemoryFileAccessor = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessor;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class WorkspaceLeaseTests
{
    [Fact]
    public void LeaseWorkspace_ExposesTheRequestedRootAndAccessor()
    {
        using var factory = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/lease-root/");

        using var lease = factory.LeaseWorkspace(root);

        Assert.Equal(root.ToString(), lease.Root.ToString());
        Assert.NotNull(lease.Accessor);
    }

    [Fact]
    public void LeaseWorkspace_AccessorMatchesTheFactoryWorkspace()
    {
        using var factory = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/lease-same-accessor/");

        using var lease = factory.LeaseWorkspace(root);
        Write(lease.Accessor, "settings.mcs.yml", "content");

        Assert.True(factory.Create(root).Exists(new AgentFilePath("settings.mcs.yml")));
    }

    [Fact]
    public void DisposingLease_ReleasesTheWorkspaceContent()
    {
        using var factory = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/lease-dispose/");
        ProductionInMemoryFileAccessor accessor;

        using (var lease = factory.LeaseWorkspace(root))
        {
            accessor = (ProductionInMemoryFileAccessor)lease.Accessor;
            Write(lease.Accessor, "settings.mcs.yml", "content");
            Assert.Equal(1, accessor.Count);
        }

        Assert.Equal(0, accessor.Count);
    }

    [Fact]
    public void DisposingLeaseTwice_IsSafe()
    {
        using var factory = new ProductionFileAccessorFactory();
        var lease = factory.LeaseWorkspace(new DirectoryPath("c:/test/lease-double-dispose/"));
        Write(lease.Accessor, "settings.mcs.yml", "content");

        lease.Dispose();
        lease.Dispose();
    }

    [Fact]
    public void LeaseTemporaryWorkspace_MemoryBacked_DoesNotTouchTheFileSystem()
    {
        using var factory = new ProductionFileAccessorFactory();

        using var lease = factory.LeaseTemporaryWorkspace("mcs-test-");

        Assert.StartsWith("/mcs-test-", lease.Root.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(lease.Root.ToString()));
    }

    [Fact]
    public void LeaseTemporaryWorkspace_Physical_CreatesAndRemovesTheDirectory()
    {
        var factory = new FileAccessorFactory();
        string rootPath;

        using (var lease = factory.LeaseTemporaryWorkspace("mcs-test-"))
        {
            rootPath = lease.Root.ToString();
            Assert.True(Directory.Exists(rootPath));
            Write(lease.Accessor, "settings.mcs.yml", "content");
        }

        Assert.False(Directory.Exists(rootPath));
    }

    [Fact]
    public void LeaseTemporaryWorkspace_IssuesDistinctRoots()
    {
        using var factory = new ProductionFileAccessorFactory();

        using var first = factory.LeaseTemporaryWorkspace("mcs-test-");
        using var second = factory.LeaseTemporaryWorkspace("mcs-test-");

        Assert.NotEqual(first.Root.ToString(), second.Root.ToString());
    }

    [Fact]
    public void LeaseWorkspace_OnPhysicalFactory_DoesNotDeleteCallerContent()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "mcs-lease-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(rootPath);
        try
        {
            var factory = new FileAccessorFactory();
            var root = new DirectoryPath(rootPath.Replace('\\', '/') + "/");

            using (var lease = factory.LeaseWorkspace(root))
            {
                Write(lease.Accessor, "settings.mcs.yml", "content");
            }

            Assert.True(File.Exists(Path.Combine(rootPath, "settings.mcs.yml")));
        }
        finally
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void LeaseWorkspace_NullFactory_Throws()
    {
        IFileAccessorFactory? factory = null;

        Assert.Throws<ArgumentNullException>(() => factory!.LeaseWorkspace(new DirectoryPath("c:/test/null/")));
    }

    private static void Write(IFileAccessor accessor, string path, string content)
    {
        using var stream = accessor.OpenWrite(new AgentFilePath(path));
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
