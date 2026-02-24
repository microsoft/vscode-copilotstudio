namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using Microsoft.PowerPlatformLS.UnitTests.TestUtilities;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using Xunit;

    public class FileWriterTests : IDisposable
    {
        private readonly string _dir;

        // All writers to act on. 
        private readonly IReadOnlyCollection<IFileAccessor> _fileWriters;

        public FileWriterTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "mcs-test", Guid.NewGuid().ToString()).Replace('\\', '/');
            _fileWriters = new List<IFileAccessor> {
                new FileAccessorFactory().Create(new DirectoryPath(_dir)),
                new InMemoryFileWriter()
            };
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        [Fact]
        public async Task WriteReadAsync()
        {
            foreach (var files in _fileWriters)
            {
                var path1 = new AgentFilePath("dir1/abc.txt");
                await files.WriteAsync(path1, "Hello!", default);

                // Verify with file system it was actually written
                if (files is not InMemoryFileWriter)
                {
                    string realPath = Path.Combine(_dir, path1.ToString());
                    string realContents = File.ReadAllText(realPath);
                    Assert.Equal("Hello!", realContents);
                }

                var contents = await files.ReadStringAsync(path1, default);

                Assert.Equal("Hello!", contents);

                var exists = files.Exists(path1);

                Assert.True(exists);
            }
        }

        [Fact]
        public async Task DeleteAsync()
        {
            foreach (var files in _fileWriters)
            {
                // Nop if file doesn't exist 
                var path1 = new AgentFilePath("dir1/abc.txt");
                files.Delete(path1);

                await files.WriteAsync(path1, "Hello", default);

                var exists = files.Exists(path1);
                Assert.True(exists);

                files.Delete(path1);

                exists = files.Exists(path1);
                Assert.False(exists);
            }
        }

        [Theory]
        [InlineData("dir_exists/missing.txt")]
        [InlineData("dir_missing/missing.txt")]
        public async Task Read_NotFound_Async(string path)
        {
            foreach (var files in _fileWriters)
            {
                // write something to ensure a dir exists
                await files.WriteAsync(new AgentFilePath("dir_exists/exists.txt"), "hello", default);

                var path1 = new AgentFilePath(path);

                var exists = files.Exists(path1);
                Assert.False(exists);
                Func<Task> testFunc = () => files.ReadStringAsync(path1, default);
                
                await Assert.ThrowsAsync<FileNotFoundException>(testFunc);                
            }
        }

        // Can create 
        [Fact]
        public void Exclusive_Streams()
        {
            var path = new AgentFilePath("foo.txt");

            foreach (var files in _fileWriters)
            {
                using var writeStream = files.OpenWrite(path);
                Assert.True(writeStream.CanWrite);

                // Attempting to open a 2nd stream will fail.
                // This would be a bug in our code and should never happen.

                Assert.Throws<System.IO.IOException>(() => files.OpenWrite(path));

                Assert.Throws<System.IO.IOException>(() => files.OpenRead(path));
            }
        }

        [Fact]
        public void CreateReadStream_FileNotFound()
        {
            var path = new AgentFilePath("kef.txt");
            
            var files = new FileAccessorFactory().Create(new DirectoryPath("z:/abcdef"));
            var ex = Assert.Throws<System.IO.FileNotFoundException>(() => files.OpenRead(path));

            Assert.Contains("Could not find a part of the path", ex.Message);
        }

        [Fact]
        public async Task ReplaceSourceFileWithTargetFileAsync()
        {
            string sourceContent = "Source content";
            string targetContent = "Target content";

            var fileWriter = new FileAccessorFactory().Create(new DirectoryPath(_dir));

            var sourcePath = new AgentFilePath("source/file.txt");
            var targetPath = new AgentFilePath("target/file.txt");

            await fileWriter.WriteAsync(sourcePath, sourceContent, default);

            await fileWriter.WriteAsync(targetPath, targetContent, default);

            fileWriter.Replace(sourcePath, targetPath);

            Assert.False(fileWriter.Exists(sourcePath));

            var actualTargetContent = await fileWriter.ReadStringAsync(targetPath, default);
            Assert.Equal(sourceContent, actualTargetContent);
        }
    }
}
