namespace Microsoft.PowerPlatformLS.UnitTests.TestUtilities
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection.Extensions;
    using Microsoft.Extensions.FileProviders;
    using Microsoft.Extensions.Primitives;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common.DependencyInjection;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Models.Lsp;
    using Microsoft.PowerPlatformLS.Impl.Core.IO;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    internal class TestFileModule : ILspModule
    {
        private readonly IDictionary<string, string> _filenameToContent;

        public TestFileModule(bool hasAgentFile = true)
        {
            _filenameToContent = new Dictionary<string, string>();
            if (hasAgentFile)
            {
                _filenameToContent["/agent.mcs.yml"] = "instructions: ";
            }
        }

        /// <summary>
        /// Constructor for TestFileModule.
        /// </summary>
        /// <param name="filenameToContent">Map of file path suffix to file content.<br/>e.g. { "file.ext", "..." } will specify content for any file that ends with "file.ext" ("my_file.ext", "some_file.ext", etc.)<br/>whereas { "/file.ext", "..." } will specify content only for files with exact name "file.ext" </param>
        public TestFileModule(IDictionary<string, string> filenameToContent)
        {
            _filenameToContent = filenameToContent;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.RemoveAll<IFileProviderFactory>();
            services.AddSingleton<IFileProviderFactory>(provider => new TestFileProviderFactory(_filenameToContent, provider.GetRequiredService<IClientInformation>()));
        }
    }

    internal class TestFileProviderFactory : IFileProviderFactory
    {
        private readonly Func<string, TestFileProvider> _fileProvider;

        public TestFileProviderFactory(IDictionary<string, string> filenameToContent, IClientInformation clientInfo)
        {
            _fileProvider = (root) => new TestFileProvider(filenameToContent, root);
        }

        public IFileProvider Create(string root)
        {
            return _fileProvider(root);
        }

        private class TestFileProvider : IFileProvider
        {
            private readonly string _root;
            private readonly IDictionary<string, string> _filenameToContent;

            public TestFileProvider(IDictionary<string, string> filenameToContent, string root)
            {
                _root = root;
                _filenameToContent = filenameToContent;
            }

            public IDirectoryContents GetDirectoryContents(string subpath)
            {
                var files = _filenameToContent.Where(fkv =>
                {
                    var filename = fkv.Key;
                    var lastSeparatorIndex = filename.LastIndexOf('/');

                    if (lastSeparatorIndex > 1)
                    {
                        var trimmedSubPath = subpath.TrimEnd('/');
                        return trimmedSubPath.EndsWith(filename.Substring(0, lastSeparatorIndex));
                    }

                    return false;
                }).Select(fkv => new KeyValuePair<string, string>(
                    subpath.TrimEnd('/') + fkv.Key.Substring(fkv.Key.LastIndexOf('/')),
                    fkv.Value));
                return new TestDirectoryContents(files.ToArray());
            }

            public IFileInfo GetFileInfo(string subpath)
            {
                var fullPath = _root.Length == 0 ? subpath : $"{_root}{subpath}";
                var candidates = _filenameToContent.Where(kvp => fullPath.EndsWith(kvp.Key, StringComparison.OrdinalIgnoreCase));
                if (candidates.Any())
                {
                    var kvp = candidates.First();
                    return new TestFileInfo(fullPath, kvp.Value);
                }

                return new TestFileInfo(fullPath, null);
            }

            public IChangeToken Watch(string filter)
            {
                return NullChangeToken.Singleton;
            }

            private class TestFileInfo : IFileInfo
            {
                private readonly string _path;
                private readonly string? _content;
                private readonly bool _isDirectory;

                public TestFileInfo(string path, string? content, bool isDirectory = false)
                {
                    _path = path;
                    _content = content;
                    _isDirectory = isDirectory;
                }

                public bool Exists => _content != null;

                public long Length => _content?.Length ?? 0;

                public string? PhysicalPath => _path;

                public string Name => _path;

                public DateTimeOffset LastModified => DateTimeOffset.MinValue;

                public bool IsDirectory => _isDirectory;

                public Stream CreateReadStream()
                {
                    return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_content ?? string.Empty), false);
                }
            }

            private class TestDirectoryContents : IDirectoryContents
            {
                private readonly IEnumerable<KeyValuePair<string, string>> _files;

                public TestDirectoryContents(IEnumerable<KeyValuePair<string, string>> files)
                {
                    _files = files;
                }

                public bool Exists => _files.Any();
                public IEnumerator<IFileInfo> GetEnumerator()
                {
                    return InternalGetEnumerator();
                }
                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
                {
                    return InternalGetEnumerator();
                }
                private IEnumerator<IFileInfo> InternalGetEnumerator()
                {
                    return _files.Select(fkv => new TestFileInfo(fkv.Key, fkv.Value)).GetEnumerator();
                }
            }
        }
    }
}
