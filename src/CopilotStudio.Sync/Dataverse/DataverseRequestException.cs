// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using System.Text.Json;

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

    /// <summary>Describes the failure by its HTTP status and the error code and message Dataverse reported, never echoing the raw response body.</summary>
    public string Describe() => Describe(StatusCode, ResponseBody);

    internal static string Describe(HttpStatusCode statusCode, string? responseBody)
    {
        using var response = new HttpResponseMessage(statusCode);
        var status = $"HTTP {(int)statusCode} {response.ReasonPhrase ?? statusCode.ToString()}.";

        if (responseBody is null || string.IsNullOrWhiteSpace(responseBody))
        {
            return status;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);

            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return status;
            }

            var message = JsonElementReader.ReadString(error, "message");
            var code = JsonElementReader.ReadString(error, "code");

            return string.IsNullOrWhiteSpace(message) ? status : string.IsNullOrWhiteSpace(code) ? $"{status} {message}" : $"{status} {message} ({code})";
        }
        catch (JsonException)
        {
            return status;
        }
    }
}