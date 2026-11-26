using JsonLoader.Enums;

namespace JsonLoader;

public sealed class ColumnData
{
    public required string Name { get; init; }
    public ColumnType Type { get; set; } = ColumnType.Unknown;
    public bool IsNullable { get; set; }
    public bool IsOverriden { get; init; }
    public List<object?> Values { get; } = [];
}