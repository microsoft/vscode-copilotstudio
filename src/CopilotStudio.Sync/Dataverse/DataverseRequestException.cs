// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

public class DataverseRequestException : InvalidOperationException
{
    public DataverseRequestException(HttpStatusCode statusCode, string responseBody)
        : base($"Dataverse request failed ({(int)statusCode}): {responseBody}")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }

    public string ResponseBody { get; }
}
