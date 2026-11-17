// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Thrown when Copilot Studio could not check the user's license because a Conditional Access policy blocked the sign-in it makes on the user's behalf.</summary>
public sealed class LicenseCheckSignInException : Exception
{
    /// <summary>Creates the exception from what Copilot Studio reported about the blocked sign-in.</summary>
    /// <param name="statusCode">The status Copilot Studio responded with.</param>
    /// <param name="errorCode">The Microsoft Entra error code, such as AADSTS53000, when the response named one.</param>
    /// <param name="errorDescription">The Microsoft Entra error description, when the response carried one.</param>
    public LicenseCheckSignInException(HttpStatusCode statusCode, string? errorCode, string? errorDescription)
        : base($"Copilot Studio could not check the license because a Conditional Access policy blocked the sign-in on the user's behalf ({(int)statusCode}{(errorCode is null ? string.Empty : ", " + errorCode)}).")
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        ErrorDescription = errorDescription;
    }

    /// <summary>The status Copilot Studio responded with.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>The Microsoft Entra error code, such as AADSTS53000, or <see langword="null"/> when the response named none.</summary>
    public string? ErrorCode { get; }

    /// <summary>The Microsoft Entra error description, which can name the user or their device, or <see langword="null"/> when the response carried none.</summary>
    public string? ErrorDescription { get; }
}
