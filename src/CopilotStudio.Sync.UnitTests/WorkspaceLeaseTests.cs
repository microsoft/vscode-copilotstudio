// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public void SecondLeaseOnSameRoot_SurvivesTheFirstLeaseBeingDisposed()
    {
        using var factory = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/lease-refcount-survives/");

        using var outer = factory.LeaseWorkspace(root);
        Write(outer.Accessor, "settings.mcs.yml", "content");

        using (var inner = factory.LeaseWorkspace(root))
        {
            Assert.True(inner.Accessor.Exists(new AgentFilePath("settings.mcs.yml")));
        }

        Assert.True(outer.Accessor.Exists(new AgentFilePath("settings.mcs.yml")));
    }

    [Fact]
    public void WorkspaceIsReleasedOnlyWhenTheLastLeaseIsDisposed()
    {
        using var factory = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/lease-refcount-last/");
        ProductionInMemoryFileAccessor accessor;

        var outer = factory.LeaseWorkspace(root);
        accessor = (ProductionInMemoryFileAccessor)outer.Accessor;
        Write(outer.Accessor, "settings.mcs.yml", "content");

        var inner = factory.LeaseWorkspace(root);
        inner.Dispose();
        Assert.Equal(1, accessor.Count);

        outer.Dispose();
        Assert.Equal(0, accessor.Count);
    }

    [Fact]
    public void DisposingTheSameLeaseTwice_DoesNotReleaseAnotherHoldOnThatRoot()
    {
        using var factory = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/lease-refcount-double/");

        using var outer = factory.LeaseWorkspace(root);
        Write(outer.Accessor, "settings.mcs.yml", "content");

        var inner = factory.LeaseWorkspace(root);
        inner.Dispose();
        inner.Dispose();

        Assert.True(outer.Accessor.Exists(new AgentFilePath("settings.mcs.yml")));
    }

    [Fact]
    public void DisposingLease_ReleasesWorkspacesNestedUnderItsRoot()
    {
        using var factory = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/lease-nested/");
        ProductionInMemoryFileAccessor child;

        using (var lease = factory.LeaseWorkspace(root))
        {
            child = (ProductionInMemoryFileAccessor)factory.Create(new DirectoryPath("c:/test/lease-nested/Agent One/"));
            Write(child, "settings.mcs.yml", "content");
            Assert.Equal(1, child.Count);
        }

        Assert.Equal(0, child.Count);
    }

    [Fact]
    public void DisposingParentLease_DoesNotClearAnActivelyLeasedChildWorkspace()
    {
        using var factory = new ProductionFileAccessorFactory();
        var parent = new DirectoryPath("c:/test/lease-child-survives/");
        var child = new DirectoryPath("c:/test/lease-child-survives/Agent One/");

        using var childLease = factory.LeaseWorkspace(child);
        Write(childLease.Accessor, "settings.mcs.yml", "content");

        using (var parentLease = factory.LeaseWorkspace(parent))
        {
            Write(parentLease.Accessor, "root.mcs.yml", "root");
        }

        Assert.True(childLease.Accessor.Exists(new AgentFilePath("settings.mcs.yml")));
    }

    [Fact]
    public void DisposingParentLease_StillClearsUnleasedNestedWorkspaces()
    {
        using var factory = new ProductionFileAccessorFactory();
        var parent = new DirectoryPath("c:/test/lease-child-unleased/");
        ProductionInMemoryFileAccessor child;

        using (var parentLease = factory.LeaseWorkspace(parent))
        {
            child = (ProductionInMemoryFileAccessor)factory.Create(new DirectoryPath("c:/test/lease-child-unleased/Agent One/"));
            Write(child, "settings.mcs.yml", "content");
        }

        Assert.Equal(0, child.Count);
    }

    [Fact]
    public void SecondLeaseOnSameRoot_ReturnsTheSameAccessorInstance()
    {
        using var factory = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/lease-same-instance/");

        using var first = factory.LeaseWorkspace(root);
        using var second = factory.LeaseWorkspace(root);

        Assert.Same(first.Accessor, second.Accessor);
    }

    [Fact]
    public void TemporaryWorkspaceDirectory_IsRemovedByWhicheverLeaseIsDisposedLast()
    {
        var factory = new FileAccessorFactory();

        var temporary = factory.LeaseTemporaryWorkspace("mcs-test-");
        var rootPath = temporary.Root.ToString();
        var second = factory.LeaseWorkspace(temporary.Root);

        temporary.Dispose();
        Assert.True(Directory.Exists(rootPath));

        second.Dispose();
        Assert.False(Directory.Exists(rootPath));
    }

    [Fact]
    public void ConcurrentDisposeOfOneLease_DoesNotReleaseAnotherHold()
    {
        using var factory = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/lease-concurrent-dispose/");

        using var keeper = factory.LeaseWorkspace(root);
        Write(keeper.Accessor, "settings.mcs.yml", "content");

        var inner = factory.LeaseWorkspace(root);
        Parallel.For(0, 16, _ => inner.Dispose());

        Assert.True(keeper.Accessor.Exists(new AgentFilePath("settings.mcs.yml")));
    }

    [Fact]
    public void WhenFactoryCreateThrows_TheFailedLeaseDoesNotRetainAHold()
    {
        var factory = new ThrowOnFirstCreateFactory();
        var root = new DirectoryPath("c:/test/lease-create-throws/");

        Assert.Throws<IOException>(() => factory.LeaseWorkspace(root));

        using (var lease = factory.LeaseWorkspace(root))
        {
            Write(lease.Accessor, "settings.mcs.yml", "content");
        }

        Assert.Equal(1, factory.ReleaseCount);
    }

    private sealed class ThrowOnFirstCreateFactory : IFileAccessorFactory
    {
        private readonly ProductionFileAccessorFactory inner = new ProductionFileAccessorFactory();
        private int creates;

        public bool IsMemoryBacked => true;

        public int ReleaseCount { get; private set; }

        public IFileAccessor Create(DirectoryPath root)
        {
            if (++this.creates == 1)
            {
                throw new IOException("create failed");
            }

            return this.inner.Create(root);
        }

        public void Release(DirectoryPath root)
        {
            this.ReleaseCount++;
            this.inner.Release(root);
        }
    }

    [Fact]
    public void SessionRequiredFactory_RejectsWorkspaceUseOutsideASession()
    {
        var factory = new ProductionFileAccessorFactory(requireSession: true);

        var failure = Assert.Throws<InvalidOperationException>(() => factory.Create(new DirectoryPath("c:/test/no-session/")));

        Assert.Contains("outside a workspace session", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionRequiredFactory_AllowsWorkspaceUseInsideASession()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);

        using var session = factory.LeaseTemporaryWorkspace("mcs-op-");
        var nested = factory.Create(session.Root.GetChildDirectoryPath("Agent One"));

        Assert.NotNull(nested);
    }

    [Fact]
    public void SessionRequiredFactory_RejectsWorkspaceUseAfterTheSessionEnds()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        DirectoryPath root;

        using (var session = factory.LeaseTemporaryWorkspace("mcs-op-"))
        {
            root = session.Root;
        }

        Assert.Throws<InvalidOperationException>(() => factory.Create(root));
    }

    [Fact]
    public void InMemorySyncServices_RequireAWorkspaceSession()
    {
        var services = new ServiceCollection();
        services.AddSyncServices(storageMode: SyncStorageMode.InMemory);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IFileAccessorFactory>();

        Assert.Throws<InvalidOperationException>(() => factory.Create(new DirectoryPath("c:/test/ams-no-session/")));

        using var session = factory.LeaseTemporaryWorkspace("mcs-op-");
        Assert.NotNull(session.Accessor);
    }

    [Fact]
    public void PhysicalSyncServices_DoNotRequireAWorkspaceSession()
    {
        var services = new ServiceCollection();
        services.AddSyncServices();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IFileAccessorFactory>();

        Assert.NotNull(factory.Create(new DirectoryPath("c:/test/pac-no-session/")));
    }

    [Fact]
    public async Task LeasingAWorkspaceHeldByAnotherOperation_IsRejected()
    {
        using var factory = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/lease-other-operation/");

        using var owner = factory.LeaseWorkspace(root);

        Task<InvalidOperationException> independentOperation;
        using (ExecutionContext.SuppressFlow())
        {
            independentOperation = Task.Run(() => Assert.Throws<InvalidOperationException>(() => factory.LeaseWorkspace(root)));
        }

        var failure = await independentOperation;

        Assert.Contains("already in use by another operation", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedLeaseFromTheSameOperation_IsAllowedAcrossTasks()
    {
        using var factory = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/lease-same-operation-task/");

        using var owner = factory.LeaseWorkspace(root);
        Write(owner.Accessor, "settings.mcs.yml", "content");

        await Task.Run(() =>
        {
            using var nested = factory.LeaseWorkspace(root);
            Assert.True(nested.Accessor.Exists(new AgentFilePath("settings.mcs.yml")));
        });

        Assert.True(owner.Accessor.Exists(new AgentFilePath("settings.mcs.yml")));
    }

    [Fact]
    public async Task AfterAnOperationEnds_AnotherOperationCanLeaseTheSameWorkspace()
    {
        using var factory = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/lease-handover/");

        using (var first = factory.LeaseWorkspace(root))
        {
            Write(first.Accessor, "settings.mcs.yml", "content");
        }

        await Task.Run(() =>
        {
            using var second = factory.LeaseWorkspace(root);
            Assert.False(second.Accessor.Exists(new AgentFilePath("settings.mcs.yml")));
        });
    }

    private static void Write(IFileAccessor accessor, string path, string content)
    {
        using var stream = accessor.OpenWrite(new AgentFilePath(path));
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
