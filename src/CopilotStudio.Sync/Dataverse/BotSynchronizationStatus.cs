// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text.Json;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

internal static class BotSynchronizationStatus
{
    public const string Column = "synchronizationstatus";

    public const string ProvisioningColumns = Column + "," + TemplateColumn;

    public const string Provisioning = "Provisioning";

    private const string TemplateColumn = "template";

    private const string PublishStatus = "publish status";

    private const string ProvisioningStatus = "provisioning status";

    public static AgentPublishOperation? ReadLastPublishOperation(JsonElement bot) => Read(bot, PublishStatus, ReadFinishedPublish);

    public static AgentProvisioningStatus ReadProvisioningStatus(JsonElement bot) => new(Read(bot, ProvisioningStatus, status => TryGetPresent(status, "currentSynchronizationState", out var state) && state.ValueKind == JsonValueKind.Object ? JsonElementReader.ReadString(state, "provisioningStatus") : null), JsonElementReader.ReadString(bot, TemplateColumn));

    private static TStatus? Read<TStatus>(JsonElement bot, string statusName, Func<JsonElement, TStatus?> read) where TStatus : class
    {
        if (bot.ValueKind != JsonValueKind.Object)
        {
            throw Unreadable(statusName, "Dataverse returned no bot row.");
        }

        if (!TryGetPresent(bot, Column, out _))
        {
            return null;
        }

        using var status = Parse(statusName, JsonElementReader.ReadString(bot, Column) ?? throw Unreadable(statusName, "The column does not hold text."));

        return status.RootElement.ValueKind == JsonValueKind.Object ? read(status.RootElement) : throw Unreadable(statusName, "The status is not a JSON object.");
    }

    private static AgentPublishOperation? ReadFinishedPublish(JsonElement status)
    {
        if (!TryGetPresent(status, "lastFinishedPublishOperation", out var operation))
        {
            return null;
        }

        if (operation.ValueKind != JsonValueKind.Object)
        {
            throw Unreadable(PublishStatus, "The last finished publish is not a JSON object.");
        }

        return new AgentPublishOperation(JsonElementReader.ReadString(operation, "status") is { Length: > 0 } outcome ? outcome : throw Unreadable(PublishStatus, "The last finished publish names no status."), ReadTime(operation, "operationStart"), ReadTime(operation, "operationEnd"));
    }

    private static JsonDocument Parse(string statusName, string synchronizationStatus)
    {
        try
        {
            return JsonDocument.Parse(synchronizationStatus);
        }
        catch (JsonException failure)
        {
            throw new FormatException(UnreadableMessage(statusName, "The status is not JSON."), failure);
        }
    }

    private static DateTimeOffset? ReadTime(JsonElement operation, string name) => JsonElementReader.ReadTimestamp(operation, name) ?? (TryGetPresent(operation, name, out _) ? throw Unreadable(PublishStatus, $"The last finished publish's {name} is not a time.") : default(DateTimeOffset?));

    private static bool TryGetPresent(JsonElement element, string name, out JsonElement value) => element.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;

    private static FormatException Unreadable(string statusName, string detail) => new(UnreadableMessage(statusName, detail));

    private static string UnreadableMessage(string statusName, string detail) => $"The bot's synchronization status cannot be read as Copilot Studio's {statusName}. {detail}";
}
