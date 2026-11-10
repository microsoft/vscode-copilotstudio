// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.CopilotStudio.Sync;

internal static class SyncHttpClientRegistrations
{
    public static IHttpClientBuilder AddUnredirectedHttpClient(this IServiceCollection services, string name) => services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
}
