// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.PowerPlatformLS.Impl.Language.CopilotStudio
{
    using Microsoft.CopilotStudio.McsCore;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;

    internal static class ConnectionsCacheReader
    {
        private static readonly AgentFilePath ConnectionsCachePath = new(".mcs/.connections-cache.json");
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

        public static IReadOnlyList<ConnectionCacheEntry>? ReadConnections(IFileAccessorFactory fileAccessorFactory, DirectoryPath folderPath)
        {
            try
            {
                var accessor = fileAccessorFactory.Create(folderPath);
                if (!accessor.Exists(ConnectionsCachePath))
                {
                    return null;
                }

                string json;
                using (var stream = accessor.OpenRead(ConnectionsCachePath))
                using (var reader = new StreamReader(stream))
                {
                    json = reader.ReadToEnd();
                }

                return JsonSerializer.Deserialize<CacheFileDto>(json, SerializerOptions)?.Connections;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private sealed class CacheFileDto
        {
            public List<ConnectionCacheEntry>? Connections { get; set; }
        }
    }

    internal sealed class ConnectionCacheEntry
    {
        public string? ConnectionReferenceLogicalName { get; set; }

        public string? ConnectorName { get; set; }

        public bool BoundConnectionExists { get; set; }

        public bool IsDeclared { get; set; } = true;
    }
}
