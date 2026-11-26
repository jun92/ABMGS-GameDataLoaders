using System.Globalization;
using System.Text.Json;
using JsonLoader.Enums;

namespace JsonLoader;

public static class JsonTableParser
{
    public static Dictionary<string, ColumnData> ParseFile(
        string dataFilePath, 
        string? schemaDirectory = null)
    {
        if(!File.Exists(dataFilePath))
            throw new FileNotFoundException(dataFilePath, schemaDirectory);
        
        var overrides = LoadSchemaOverrides(dataFilePath, schemaDirectory);
        
        using var stream = File.OpenRead(dataFilePath);
        using var document = JsonDocument.Parse(stream);
        
        var columns = new Dictionary<string, ColumnData>(StringComparer.OrdinalIgnoreCase);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            int rowIndex = 0;
            foreach (var element in root.EnumerateArray())
            {
                if(element.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Not Json ojects at row number [{rowIndex}]");
                rowIndex++;
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            ProcessRow(root, columns, overrides, 0);
        }
        else
        {
            throw new InvalidOperationException("Json root should be an object({}) or object array([{}]).");
        }

        NormalizeValues(columns);
        return columns;
    }

    private static Dictionary<string, ColumnType> LoadSchemaOverrides(
        string dataFilePath,
        string? schemaDirectory = null)
    {
        var result = new Dictionary<string, ColumnType>(StringComparer.OrdinalIgnoreCase);
        var baseDir = Path.GetDirectoryName(dataFilePath) ?? string.Empty;
        var targetSchemaDir = schemaDirectory ?? Path.Combine(baseDir, "Schemas");
        var schemaFilePath = Path.Combine(targetSchemaDir, Path.GetFileName(dataFilePath));
        if (!File.Exists(schemaFilePath)) return result;

        var jsonText = File.ReadAllText(schemaFilePath);
        var rawMap = JsonSerializer.Deserialize<Dictionary<string, String>>(jsonText);
        if (rawMap is null) return result;

        foreach (var (colName, typeName) in rawMap)
        {
            if (Enum.TryParse<ColumnType>(typeName, ignoreCase: true, out var parsedType) &&
                parsedType != ColumnType.Unknown)
            {
                result[colName] = parsedType;
            }
            else
            {
                throw new InvalidOperationException($"Wrong schema type");
            }
        }
        return result;
    }

    private static void ProcessRow(
        JsonElement row,
        Dictionary<string, ColumnData> columns,
        Dictionary<string, ColumnType> overrides,
        int rowIndex)
    {
        var seenColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in row.EnumerateObject())
        {
            var colName = property.Name;
            seenColumns.Add(colName);

            if (!columns.TryGetValue(colName, out var colData))
            {
                var hasOverride = overrides.TryGetValue(colName, out var overrideType);
                colData = new ColumnData
                {
                    Name = colName,
                    Type = hasOverride ? overrideType : ColumnType.Unknown,
                    IsOverriden = hasOverride,
                };
                if (rowIndex > 0)
                {
                    colData.IsNullable = true;
                    for (int i = 0; i < rowIndex; i++) colData.Values.Add(null);
                }
                columns[colName] = colData;
            }

            var (inferredType, rawValue) = ExtractValue(property.Value);

            if (!colData.IsOverriden) colData.Type = MergeType(colData.Type, inferredType);
            if (rawValue is null) colData.IsNullable = true;
            colData.Values.Add(rawValue);
        }

        foreach (var (colName, colData) in columns)
        {
            if (!seenColumns.Contains(colName))
            {
                colData.IsNullable = true;
                colData.Values.Add(null);
            }
        }
    }

    private static (ColumnType Type, object? Value) ExtractValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => (ColumnType.Unknown, null),
            JsonValueKind.True => (ColumnType.Boolean, true),
            JsonValueKind.False => (ColumnType.Boolean, false),
            JsonValueKind.Number => element.TryGetInt64(out var longVal)
                ? (ColumnType.Integer, longVal)
                : (ColumnType.Double, element.GetDouble()),
            JsonValueKind.String => (ColumnType.Text, element.GetString()),
            JsonValueKind.Object or JsonValueKind.Array => (ColumnType.Json, element.GetRawText()),
            _ => (ColumnType.Text, element.GetRawText())
        };
    }

    private static ColumnType MergeType(ColumnType current, ColumnType incoming)
    {
        if (incoming == ColumnType.Unknown) return current;
        if (current == ColumnType.Unknown) return incoming;
        if (current == incoming) return current;

        if ((current == ColumnType.Integer && incoming == ColumnType.Double) ||
            (current == ColumnType.Double && incoming == ColumnType.Integer))
        {
            return ColumnType.Double;
        }

        if (current == ColumnType.Json || incoming == ColumnType.Json) return ColumnType.Json;
        return ColumnType.Text;
    }

    private static void NormalizeValues(Dictionary<string, ColumnData> columns)
    {
        foreach (var col in columns.Values)
        {
            if (col.Type == ColumnType.Unknown) col.Type = ColumnType.Text;

            for (int i = 0; i < col.Values.Count; i++)
            {
                var val = col.Values[i];
                if (val is null) continue;
                col.Values[i] = col.Type switch
                {
                    ColumnType.Boolean => Convert.ToBoolean(val, CultureInfo.InvariantCulture),
                    ColumnType.Integer => Convert.ToInt64(val, CultureInfo.InvariantCulture),
                    ColumnType.Float => Convert.ToSingle(val, CultureInfo.InvariantCulture),
                    ColumnType.Double => Convert.ToDouble(val, CultureInfo.InvariantCulture),
                    ColumnType.Text => Convert.ToString(val, CultureInfo.InvariantCulture),
                    ColumnType.Json => val is string s ? s : JsonSerializer.Serialize(val),
                    _ => val
                };
            }
        }
    }
}