// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Microsoft.CopilotStudio.McsCore;

#region PathHelper

/// <summary>
/// Shared code for path types.
/// </summary>
internal static class PathHelper
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool AreEqual<T>(T path, object? obj, Func<T, string> getValue)
    {
        if (obj is T other)
        {
            return getValue(other).Equals(getValue(path), StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetHashCode<T>(T path, Func<T, string> getValue)
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(getValue(path));
    }

    internal static string GetRelativePath(string relativeTo, string path)
    {
#if NETSTANDARD2_0
        return GetRelativePathPolyfill(relativeTo, path);
#else
        return Path.GetRelativePath(relativeTo, path);
#endif
    }

#if NETSTANDARD2_0
    private static string GetRelativePathPolyfill(string relativeTo, string path)
    {
        if (string.IsNullOrEmpty(relativeTo)) throw new ArgumentNullException(nameof(relativeTo));
        if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));

        var rt = Path.GetFullPath(relativeTo);
        var p = Path.GetFullPath(path);

        if (rt.Length == 0 || (rt[rt.Length - 1] != Path.DirectorySeparatorChar
                              && rt[rt.Length - 1] != Path.AltDirectorySeparatorChar))
        {
            rt += Path.DirectorySeparatorChar;
        }

        var rtUri = new Uri(rt);
        var pUri = new Uri(p);

        if (rtUri.Scheme != pUri.Scheme) return p;

        var relativeUri = rtUri.MakeRelativeUri(pUri);
        var rel = Uri.UnescapeDataString(relativeUri.ToString());

        if (string.Equals(pUri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
        {
            rel = rel.Replace('/', Path.DirectorySeparatorChar);
        }

        return rel;
    }
#endif

    internal static T GetRelativeTo<T>(T path, DirectoryPath parent, Func<T, string> getValue, Func<string, T> create)
    {
        return create(getValue(path).Substring(parent.Length));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string GetString<T>(T path, Func<T, string> getValue)
    {
        return getValue(path);
    }

    /// <summary>
    /// For usage before initialization.
    /// </summary>
    /// <param name="path">The path string. Must use forward slashes.</param>
    /// <exception cref="ArgumentException">Thrown if the path contains backslashes.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ValidatePath(string path)
    {
        if (path.IndexOf('\\') >= 0)
        {
            throw new ArgumentException($"Path should use forward slash: {path}", nameof(path));
        }
    }
}

#endregion

#region DirectoryPath

/// <summary>
/// Agent directory path.
/// Can be either an absolute path or a relative path to a parent agent directory.
/// Use / convention and always ends with a slash, except for the root directory which is represented by an empty string.
/// </summary>
[DebuggerDisplay("{_value}")]
public readonly struct DirectoryPath : IEquatable<DirectoryPath>
{
    private readonly string _value;

    public DirectoryPath(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        _value = path.Length == 0 || (path[path.Length - 1] == '/') ? path : path + "/";
        PathHelper.ValidatePath(_value);
    }

    public int Length => _value.Length;

    public DirectoryPath GetRelativeTo(DirectoryPath parent)
    {
        return PathHelper.GetRelativeTo(this, parent, GetValue, x => new DirectoryPath(x));
    }

    public bool Contains(DirectoryPath childPath)
    {
        return Contains<DirectoryPath>(childPath);
    }

    public bool Contains(FilePath childPath)
    {
        return Contains<FilePath>(childPath);
    }

    private bool Contains<T>(T childPath) where T : struct
    {
        if (_value.Length == 0)
        {
            return true;
        }

        var childPathValue = childPath.ToString();
        if (childPathValue == null || childPathValue.Length == 0)
        {
            return false;
        }

        return childPathValue.StartsWith(_value, StringComparison.OrdinalIgnoreCase);
    }

    public FilePath GetChildFilePath(string child)
    {
        return new FilePath(_value + child);
    }

    public DirectoryPath GetChildDirectoryPath(string child)
    {
        return new DirectoryPath(_value + child);
    }

    public DirectoryPath GetParentDirectoryPath()
    {
        if (_value.Length < 2)
        {
            return this;
        }

        var secondLastSlashIndex = _value.LastIndexOf('/', _value.Length - 2);
        if (secondLastSlashIndex < 0)
        {
            return new DirectoryPath(string.Empty);
        }

        return new DirectoryPath(_value.Substring(0, secondLastSlashIndex + 1));
    }

    public override string ToString()
    {
        return PathHelper.GetString(this, GetValue);
    }

    public bool Equals(DirectoryPath other)
    {
        return _value.Equals(other._value, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        return obj is DirectoryPath other && Equals(other);
    }

    public override int GetHashCode()
    {
        return PathHelper.GetHashCode(this, GetValue);
    }

    public static bool operator ==(DirectoryPath left, DirectoryPath right) => left.Equals(right);
    public static bool operator !=(DirectoryPath left, DirectoryPath right) => !left.Equals(right);

    private static string GetValue(DirectoryPath path) => path._value;

    public void EnsureIsRooted()
    {
        if (!Path.IsPathRooted(_value))
        {
            throw new InvalidOperationException($"Paths musted be rooted: {_value}");
        }
    }

    public RelativeDirectoryPath GetRelativeFrom(DirectoryPath parent)
    {
        EnsureIsRooted();
        parent.EnsureIsRooted();

        var relative = PathHelper.GetRelativePath(parent._value, _value);
        relative = relative.Replace('\\', '/');

        return new RelativeDirectoryPath(relative);
    }

    public DirectoryPath ResolveRelativeRef(RelativeDirectoryPath relative)
    {
        var path = relative.ToString();

        var final = this;

        while (path.StartsWith("../", StringComparison.Ordinal))
        {
            final = final.GetParentDirectoryPath();
            path = path.Substring(3);
        }

        final = final.GetChildDirectoryPath(path);
        return final;
    }
}

#endregion

#region FilePath

/// <summary>
/// Arbitrary file path.
/// Can be either an absolute path or a relative path to a parent directory.
/// Use / convention.
/// </summary>
[DebuggerDisplay("{_value}")]
public readonly struct FilePath : IEquatable<FilePath>
{
    private readonly string _value;

    public FilePath(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (path.Length == 0 || path == "/")
        {
            throw new ArgumentException($"File path cannot be empty or root. Use '{nameof(DirectoryPath)}'.", nameof(path));
        }
        PathHelper.ValidatePath(path);
        _value = path;
        ParentDirectoryPath = GetParentDirectoryPath();
    }

    public DirectoryPath ParentDirectoryPath { get; }

    public string FileNameWithoutExtension => WorkspacePath.RemoveExtension(this).FileName;

    public string FileName => Path.GetFileName(ToString());

    public FilePath GetRelativeTo(DirectoryPath parent)
    {
        return PathHelper.GetRelativeTo(this, parent, GetValue, x => new FilePath(x));
    }

    public override string ToString()
    {
        return PathHelper.GetString(this, GetValue);
    }

    public bool Equals(FilePath other)
    {
        return _value.Equals(other._value, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        return obj is FilePath other && Equals(other);
    }

    public override int GetHashCode()
    {
        return PathHelper.GetHashCode(this, GetValue);
    }

    public static bool operator ==(FilePath left, FilePath right) => left.Equals(right);
    public static bool operator !=(FilePath left, FilePath right) => !left.Equals(right);

    private DirectoryPath GetParentDirectoryPath()
    {
        var lastSlashIndex = _value.LastIndexOf('/');
        if (lastSlashIndex < 0)
        {
            return new DirectoryPath(string.Empty);
        }

        return new DirectoryPath(_value.Substring(0, lastSlashIndex + 1));
    }

    private static string GetValue(FilePath path) => path._value;
}

#endregion

#region RelativeDirectoryPath

/// <summary>
/// Represent a relative directory reference that could start with "../" and refer to peer directories.
/// </summary>
[DebuggerDisplay("{_value}")]
public readonly struct RelativeDirectoryPath : IEquatable<RelativeDirectoryPath>
{
    private readonly string _value;

    public RelativeDirectoryPath(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        _value = value;
        PathHelper.ValidatePath(_value);
    }

    public override string ToString()
    {
        return _value;
    }

    public bool Equals(RelativeDirectoryPath other)
    {
        return string.Equals(_value, other._value, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        return obj is RelativeDirectoryPath other && Equals(other);
    }

    public override int GetHashCode()
    {
        return _value is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(_value);
    }

    public static bool operator ==(RelativeDirectoryPath left, RelativeDirectoryPath right) => left.Equals(right);
    public static bool operator !=(RelativeDirectoryPath left, RelativeDirectoryPath right) => !left.Equals(right);
}

#endregion

#region WorkspacePath

public enum LanguageType
{
    Default = 0,
    CopilotStudio,
    PowerFx,
    Yaml,
}

internal static class WorkspacePath
{
    private static readonly ImmutableArray<(string, LanguageType)> ExtensionToLanguageTypes = [
        (".mcs.yaml", LanguageType.CopilotStudio),
        (".mcs.yml", LanguageType.CopilotStudio),
        ("botdefinition.json", LanguageType.CopilotStudio),
        (".fx1", LanguageType.PowerFx),
        (".yml", LanguageType.Yaml),
        (".yaml", LanguageType.Yaml),
        ("icon.png", LanguageType.CopilotStudio),
    ];

    public static bool TryGetLanguageType(FilePath path, out LanguageType languageType)
    {
        var extension = GetExtension(path);
        if (extension != null)
        {
            foreach (var (ext, type) in ExtensionToLanguageTypes)
            {
                if (extension.Equals(ext, StringComparison.OrdinalIgnoreCase))
                {
                    languageType = type;
                    return true;
                }
            }
        }

        languageType = default;
        return false;
    }

    public static FilePath RemoveExtension(FilePath path)
    {
        var extension = GetExtension(path);
        if (extension != null)
        {
            var pathValue = path.ToString();
            var newLength = pathValue.Length - extension.Length;
            if (newLength > 0)
            {
                return new FilePath(pathValue.Substring(0, newLength));
            }
        }

        return path;
    }

    public static string? GetExtension(FilePath path)
    {
        var pathValue = path.ToString();
        foreach (var (extension, _) in ExtensionToLanguageTypes)
        {
            if (pathValue.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return extension;
            }
        }

        return Path.GetExtension(pathValue);
    }
}

#endregion

#region AgentFilePath

/// <summary>
/// Path of a file inherently relative to an agent directory.
/// </summary>
public readonly struct AgentFilePath : IEquatable<AgentFilePath>
{
    private static readonly DirectoryPath AgentsDirectory = new DirectoryPath("agents");
    private readonly FilePath _value;

    public AgentFilePath(string value) : this(new FilePath(value))
    {
    }

    public AgentFilePath(FilePath path)
    {
        var pathValue = path.ToString();
        if (Path.IsPathRooted(pathValue))
        {
            throw new ArgumentException("AgentFilePath must be relative to an agent directory.", nameof(path));
        }

        if (pathValue.StartsWith("../", StringComparison.Ordinal) ||
            pathValue.StartsWith("./", StringComparison.Ordinal) ||
            pathValue.Contains("/../", StringComparison.Ordinal) ||
            pathValue.Contains("/./", StringComparison.Ordinal) ||
            pathValue.EndsWith("/.", StringComparison.Ordinal) ||
            pathValue.EndsWith("/..", StringComparison.Ordinal))
        {
            throw new ArgumentException("AgentFilePath must be fully resolved. It must be a canonical relative path. It cannot refer to current or parent directory.", nameof(path));
        }

        _value = path;
    }

    public string FileNameWithoutExtension => _value.FileNameWithoutExtension;

#pragma warning disable CA1021 // Standard TryGet pattern
    public bool TryGetSubAgentName(
        [NotNullWhen(true)] out string? agentName,
        [NotNullWhen(true)] out AgentFilePath? subPath)
    {
#pragma warning restore CA1021
        if (AgentsDirectory.Contains(_value))
        {
            var start = AgentsDirectory.Length;
            var pathValue = _value.ToString();
            var end = pathValue.IndexOf('/', start);

            if (end < 0)
            {
                agentName = null;
                subPath = null;
                return false;
            }

            var len = end - start;

            agentName = pathValue.Substring(start, len);

            var x = pathValue.Substring(end + 1);
            subPath = new AgentFilePath(x);
            return true;
        }

        agentName = null;
        subPath = null;
        return false;
    }

    public bool IsDefinition() => IsDefinition(_value);

    public static bool IsDefinition(FilePath filePath)
    {
        return filePath.ToString().EndsWith("botdefinition.json", StringComparison.OrdinalIgnoreCase);
    }

    public AgentFilePath RemoveExtension() => new AgentFilePath(WorkspacePath.RemoveExtension(_value));

    public bool IsInSubDir => ToString().IndexOf('/') >= 0;

    public string ParentDirectoryName => _value.ParentDirectoryPath.ToString();

    public string FileName => _value.FileName;

    public bool IsIcon() => IsIcon(_value);

    public static bool IsIcon(FilePath fileName)
    {
        return string.Equals(fileName.FileName, "icon.png", StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString()
    {
        return _value.ToString();
    }

    public bool Equals(AgentFilePath other)
    {
        return _value.Equals(other._value);
    }

    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        return obj is AgentFilePath other && Equals(other);
    }

    public override int GetHashCode()
    {
        return _value.GetHashCode();
    }

    public static bool operator ==(AgentFilePath left, AgentFilePath right) => left.Equals(right);
    public static bool operator !=(AgentFilePath left, AgentFilePath right) => !left.Equals(right);
}

#endregion
