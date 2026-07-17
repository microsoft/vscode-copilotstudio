// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.ObjectModel;

namespace Microsoft.CopilotStudio.McsCore;

internal static class AiPromptProjection
{
    private const string AIPromptOutputBindingName = "predictionOutput";
    private const string PromptInputPlaceholderPattern = @"\{\{([A-Za-z0-9_\-]+)\}\}";

    internal static AIModelDefinition Build(Guid aiModelId, string? name, string? customConfiguration)
    {
        var (inputType, outputType) = ExtractAIPromptIO(customConfiguration);
        return new AIModelDefinition(id: new AIModelId(aiModelId), name: name, inputType: inputType, outputType: outputType);
    }

    internal static Guid? ExtractTrailingGuidFromFileName(string fileName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(fileName, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$");
        if (match.Success && Guid.TryParse(match.Value, out var parsedGuid))
        {
            return parsedGuid;
        }
        return null;
    }

    internal static string? TryReadPromptName(string promptJsonText)
    {
        try
        {
            using var document = JsonDocument.Parse(promptJsonText);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                return nameElement.GetString();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    internal static string BuildCustomConfigurationFromPromptJson(string promptJsonText)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(promptJsonText);
        }
        catch (JsonException)
        {
            return promptJsonText;
        }

        using (document)
        {
            var rootElement = document.RootElement;
            var looksPortalShape = rootElement.ValueKind == JsonValueKind.Object && (rootElement.TryGetProperty("instruction", out _) || rootElement.TryGetProperty("inputs", out _) || rootElement.TryGetProperty("output", out _) || rootElement.TryGetProperty("model", out _));
            var looksRaw = rootElement.ValueKind == JsonValueKind.Object && rootElement.TryGetProperty("prompt", out _) && rootElement.TryGetProperty("definitions", out _);

            if (!looksPortalShape && looksRaw)
            {
                return promptJsonText;
            }

            var rawObject = new JsonObject
            {
                ["version"] = rootElement.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == JsonValueKind.String ? versionElement.GetString() : "GptDynamicPrompt-2"
            };

            var promptArray = new JsonArray();
            if (rootElement.TryGetProperty("instruction", out var instructionElement) && instructionElement.ValueKind == JsonValueKind.String)
            {
                var instruction = instructionElement.GetString() ?? string.Empty;
                var placeholderRegex = new System.Text.RegularExpressions.Regex(PromptInputPlaceholderPattern);
                var lastIndex = 0;
                foreach (System.Text.RegularExpressions.Match placeholderMatch in placeholderRegex.Matches(instruction))
                {
                    if (placeholderMatch.Index > lastIndex)
                    {
                        promptArray.Add(new JsonObject
                        {
                            ["type"] = "literal",
                            ["text"] = instruction.Substring(lastIndex, placeholderMatch.Index - lastIndex)
                        });
                    }
                    promptArray.Add(new JsonObject
                    {
                        ["type"] = "inputVariable",
                        ["id"] = placeholderMatch.Groups[1].Value
                    });
                    lastIndex = placeholderMatch.Index + placeholderMatch.Length;
                }
                if (lastIndex < instruction.Length)
                {
                    promptArray.Add(new JsonObject
                    {
                        ["type"] = "literal",
                        ["text"] = instruction.Substring(lastIndex)
                    });
                }
            }
            rawObject["prompt"] = promptArray;

            var definitions = new JsonObject
            {
                ["inputs"] = rootElement.TryGetProperty("inputs", out var inputsElement) ? JsonNode.Parse(inputsElement.GetRawText()) : new JsonArray(),
                ["formulas"] = rootElement.TryGetProperty("formulas", out var formulasElement) ? JsonNode.Parse(formulasElement.GetRawText()) : new JsonArray(),
                ["data"] = rootElement.TryGetProperty("data", out var dataElement) ? JsonNode.Parse(dataElement.GetRawText()) : new JsonArray()
            };

            if (rootElement.TryGetProperty("output", out var outputElement))
            {
                definitions["output"] = JsonNode.Parse(outputElement.GetRawText());
            }

            rawObject["definitions"] = definitions;

            var modelParameters = rootElement.TryGetProperty("modelParameters", out var modelParametersElement) && modelParametersElement.ValueKind == JsonValueKind.Object ? (JsonObject)JsonNode.Parse(modelParametersElement.GetRawText())! : new JsonObject();
            if (rootElement.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String)
            {
                modelParameters["modelType"] = modelElement.GetString();
            }
            rawObject["modelParameters"] = modelParameters;

            if (rootElement.TryGetProperty("settings", out var settingsElement))
            {
                rawObject["settings"] = JsonNode.Parse(settingsElement.GetRawText());
            }

            rawObject["code"] = rootElement.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String ? codeElement.GetString() ?? string.Empty : string.Empty;
            rawObject["signature"] = rootElement.TryGetProperty("signature", out var signatureElement) && signatureElement.ValueKind == JsonValueKind.String ? signatureElement.GetString() ?? string.Empty : string.Empty;

            return rawObject.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
    }

    internal static (RecordDataType? inputType, RecordDataType? outputType) ExtractAIPromptIO(string? customConfiguration)
    {
        if (string.IsNullOrWhiteSpace(customConfiguration))
        {
            return (null, null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(customConfiguration!);
        }
        catch (JsonException)
        {
            return (null, null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("definitions", out var definitionsElement) || definitionsElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            RecordDataType? inputType = null;
            if (definitionsElement.TryGetProperty("inputs", out var inputsElement))
            {
                inputType = BuildRecordDataTypeFromAIPromptInputs(inputsElement);
            }

            RecordDataType? outputType = null;
            if (definitionsElement.TryGetProperty("output", out var outputElement))
            {
                outputType = BuildRecordDataTypeFromAIPromptOutput(outputElement);
            }

            return (inputType, outputType);
        }
    }

    private static RecordDataType? BuildRecordDataTypeFromAIPromptInputs(JsonElement inputsElement)
    {
        var properties = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);

        if (inputsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var input in inputsElement.EnumerateArray())
            {
                if (input.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var name = (input.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : null) ?? (input.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                var promptInputType = input.TryGetProperty("type", out var promptInputTypeElement) && promptInputTypeElement.ValueKind == JsonValueKind.String ? promptInputTypeElement.GetString() : null;
                properties[name!] = new PropertyInfo(displayName: name!, description: null, isRequired: false, type: MapAiPromptInputType(promptInputType));
            }
        }
        else if (inputsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var input in inputsElement.EnumerateObject())
            {
                var promptInputType = input.Value.ValueKind == JsonValueKind.Object && input.Value.TryGetProperty("type", out var promptInputTypeElement) && promptInputTypeElement.ValueKind == JsonValueKind.String ? promptInputTypeElement.GetString() : null;
                properties[input.Name] = new PropertyInfo(displayName: input.Name, description: null, isRequired: false, type: MapAiPromptInputType(promptInputType));
            }
        }

        return properties.Count == 0 ? null : new RecordDataType(properties.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private static RecordDataType BuildRecordDataTypeFromAIPromptOutput(JsonElement outputElement)
    {
        var predictionOutputFields = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = new PropertyInfo(displayName: "text", description: null, isRequired: false, type: DataType.String),
            ["finishReason"] = new PropertyInfo(displayName: "finishReason", description: null, isRequired: false, type: DataType.String),
            ["dataUsed"] = new PropertyInfo(displayName: "dataUsed", description: null, isRequired: false, type: DataType.String),
        };

        if (outputElement.ValueKind == JsonValueKind.Object && outputElement.TryGetProperty("jsonSchema", out var jsonSchemaElement) && jsonSchemaElement.ValueKind == JsonValueKind.Object && jsonSchemaElement.TryGetProperty("properties", out var schemaPropertiesElement) && schemaPropertiesElement.ValueKind == JsonValueKind.Object)
        {
            var structuredRecord = BuildRecordDataTypeFromJsonSchemaProperties(schemaPropertiesElement);
            if (structuredRecord != null)
            {
                predictionOutputFields["structuredOutput"] = new PropertyInfo(displayName: "structuredOutput", description: null, isRequired: false, type: structuredRecord);
            }
        }

        var predictionOutputRecord = new RecordDataType(predictionOutputFields.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
        var rootFields = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase)
        {
            [AIPromptOutputBindingName] = new PropertyInfo(displayName: AIPromptOutputBindingName, description: null, isRequired: false, type: predictionOutputRecord),
        };

        return new RecordDataType(rootFields.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private static RecordDataType? BuildRecordDataTypeFromJsonSchemaProperties(JsonElement propertiesElement)
    {
        if (propertiesElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var properties = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in propertiesElement.EnumerateObject())
        {
            properties[ProcessAIPromptOutputName(prop.Name)] = BuildPropertyInfoFromJsonSchema(prop.Value, prop.Name);
        }

        return properties.Count == 0 ? null : new RecordDataType(properties.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
    }

    // Process AI prompt output property names that are not valid (ex: contain spaces or other punctuation). Topics author against
    // Rule:
    //   - If the property name is already a valid identifier ([A-Za-z_][A-Za-z0-9_]*), keep it unchanged.
    //   - Otherwise, XML-style name-encode the original: each non-alphanumeric char becomes "_XXXX" (4 hex digits, uppercase, of the char code).
    //     Take the first 8 chars of that encoded string as the prefix, then append SHA256(UTF-8(originalName))[:32] hex.
    // Examples:
    //   "Due-Date"                        -> "Due_002Dda5f9e59e67c82f09c296caa2bfca354"
    //   "date description"                -> "date_002da27979fcb9686b5b8261c3d2a79ec84"
    //   "shiping method"                  -> "shiping_588f41922a4dc30d2d1c4654fd7b6fd7"
    //   "Container 1 Registration Number" -> "Containe2e5985a0512b7b685a45b21ef6350c0d"
    internal static string ProcessAIPromptOutputName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            return name;
        }

        var encoded = new StringBuilder(name.Length * 2);
        foreach (var c in name)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                encoded.Append(c);
            }
            else
            {
                encoded.Append('_').Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var prefix = encoded.Length >= 8 ? encoded.ToString(0, 8) : encoded.ToString().PadRight(8, '_');

        using var sha = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(name));
        var hashHex = new StringBuilder(64);
        foreach (var b in hashBytes)
        {
            hashHex.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return prefix + hashHex.ToString(0, 32);
    }

    private static PropertyInfo BuildPropertyInfoFromJsonSchema(JsonElement schemaElement, string propName)
    {
        var displayName = schemaElement.ValueKind == JsonValueKind.Object && schemaElement.TryGetProperty("title", out var titleNode) && titleNode.ValueKind == JsonValueKind.String ? titleNode.GetString() : propName;
        var description = schemaElement.ValueKind == JsonValueKind.Object && schemaElement.TryGetProperty("description", out var descNode) && descNode.ValueKind == JsonValueKind.String ? descNode.GetString() : null;
        DataType type = MapJsonSchemaType(schemaElement);
        return new PropertyInfo(displayName: displayName, description: description, isRequired: false, type: type);
    }

    private static DataType MapJsonSchemaType(JsonElement schemaElement)
    {
        if (schemaElement.ValueKind != JsonValueKind.Object)
        {
            return DataType.String;
        }

        var schemaType = schemaElement.TryGetProperty("type", out var typeNode) && typeNode.ValueKind == JsonValueKind.String ? typeNode.GetString() : null;

        if (string.Equals(schemaType, "object", StringComparison.OrdinalIgnoreCase) && schemaElement.TryGetProperty("properties", out var nestedProps))
        {
            return BuildRecordDataTypeFromJsonSchemaProperties(nestedProps) ?? DataType.EmptyRecord;
        }

        if (string.Equals(schemaType, "array", StringComparison.OrdinalIgnoreCase))
        {
            if (schemaElement.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Object)
            {
                var itemSchemaType = itemsElement.TryGetProperty("type", out var itemTypeNode) && itemTypeNode.ValueKind == JsonValueKind.String ? itemTypeNode.GetString() : null;

                if (string.Equals(itemSchemaType, "object", StringComparison.OrdinalIgnoreCase) && itemsElement.TryGetProperty("properties", out var itemProps) && itemProps.ValueKind == JsonValueKind.Object)
                {
                    var rowProps = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in itemProps.EnumerateObject())
                    {
                        rowProps[ProcessAIPromptOutputName(p.Name)] = BuildPropertyInfoFromJsonSchema(p.Value, p.Name);
                    }

                    return new TableDataType(rowProps.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
                }

                var scalarColumn = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Value"] = new PropertyInfo(displayName: "Value", description: null, isRequired: false, type: MapJsonSchemaType(itemsElement))
                };
                return new TableDataType(scalarColumn.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
            }

            return DataType.EmptyTable;
        }

        return MapFlowType(schemaType ?? "string");
    }

    internal static PropertyInfo CreatePropertyInfoFromJson(JsonElement propValue, string propName, bool isFileRecordField = false)
    {
        DataType type = DataType.String;

        var schemaType = propValue.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
        var hasFileContentHint = propValue.TryGetProperty("x-ms-content-hint", out var hintNode) && string.Equals(hintNode.GetString(), "FILE", StringComparison.OrdinalIgnoreCase);

        if (schemaType != null)
        {
            type = MapFlowType(schemaType);
        }

        if (isFileRecordField && IsContentBytesField(propName, schemaType, propValue))
        {
            type = DataType.File;
        }

        if (string.Equals(schemaType, "object", StringComparison.OrdinalIgnoreCase) && propValue.TryGetProperty("properties", out var nestedProps) && nestedProps.ValueKind == JsonValueKind.Object)
        {
            var fields = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var nested in nestedProps.EnumerateObject())
            {
                fields[nested.Name] = CreatePropertyInfoFromJson(nested.Value, nested.Name, hasFileContentHint);
            }

            type = fields.Count == 0 ? DataType.EmptyRecord : new RecordDataType(fields.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
        }
        else if (hasFileContentHint)
        {
            type = DataType.File;
        }

        var property = new PropertyInfo(
            displayName: propValue.TryGetProperty("title", out var titleNode) ? titleNode.GetString() : propName,
            description: propValue.TryGetProperty("description", out var descNode) ? descNode.GetString() : null,
            isRequired: false,
            type: type
        );

        return property;
    }

    private static bool IsContentBytesField(string propName, string? schemaType, JsonElement propValue)
    {
        return string.Equals(propName, "contentBytes", StringComparison.OrdinalIgnoreCase)
            && string.Equals(schemaType, "string", StringComparison.OrdinalIgnoreCase)
            && propValue.TryGetProperty("format", out var formatNode)
            && string.Equals(formatNode.GetString(), "byte", StringComparison.OrdinalIgnoreCase);
    }

    private static DataType MapFlowType(string jsonType)
    {
        return jsonType.ToUpperInvariant() switch
        {
            "STRING" => DataType.String,
            "BOOLEAN" => DataType.Boolean,
            "NUMBER" => DataType.Number,
            "INTEGER" => DataType.Number,
            "DATE" => DataType.DateTime,
            _ => DataType.String
        };
    }

    private static DataType MapAiPromptInputType(string? promptInputType)
    {
        return promptInputType?.ToUpperInvariant() switch
        {
            "TEXT" => DataType.String,
            "NUMBER" => DataType.Number,
            "BOOLEAN" => DataType.Boolean,
            _ => DataType.Any
        };
    }
}
