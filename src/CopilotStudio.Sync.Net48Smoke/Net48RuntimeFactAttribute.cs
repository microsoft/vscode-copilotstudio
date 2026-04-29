// Copyright (C) Microsoft Corporation. All rights reserved.

using Xunit;

namespace Microsoft.CopilotStudio.Sync.Net48Smoke;

/// <summary>
/// xUnit Fact that auto-skips on hosts where strong-name verification is still
/// enforced for delay-signed assemblies (the default on a fresh net48 dev box).
///
/// This project's primary value is its compile success: the project builds against
/// the netstandard2.0 binary of Microsoft.CopilotStudio.Sync from a net48 consumer,
/// proving that public init-only setters are usable cross-assembly without a public
/// IsExternalInit polyfill. The runtime test bodies are bonus -- they exercise the
/// loaded assembly to catch type-load issues -- but they require the dev-built
/// delay-signed Sync.dll to be loadable, which means strong-name verification must
/// be disabled for the assembly's public key token.
///
/// CI does this once at pipeline start via:
///   reg ADD "HKLM\Software\Microsoft\StrongName\Verification\*,*" /f
///
/// To enable the runtime tests on a local dev box:
///   1. Either run the same registry add (admin shell), or use sn.exe:
///        sn -Vr *,31bf3856ad364e35
///      (where 31bf3856ad364e35 is the public key token reported in the load
///       failure message; same value as Microsoft's public key).
///   2. Set the env var ENABLE_NET48_SMOKE_RUNTIME=1 and re-run tests.
///
/// Published builds of the package are fully signed, so consumers of the released
/// nupkg do not need any of this -- they just reference and use Sync normally.
/// </summary>
internal sealed class Net48RuntimeFactAttribute : FactAttribute
{
    public Net48RuntimeFactAttribute()
    {
        if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("ENABLE_NET48_SMOKE_RUNTIME")))
        {
            Skip = "Disabled by default. Requires strong-name verification skip for the delay-signed dev-build assembly. " +
                   "Set ENABLE_NET48_SMOKE_RUNTIME=1 to enable; see Net48RuntimeFactAttribute.cs for setup.";
        }
    }
}
