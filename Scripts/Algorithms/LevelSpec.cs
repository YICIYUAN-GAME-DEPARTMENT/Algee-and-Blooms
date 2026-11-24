using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Growth.Algorithms;

/// <summary>
/// 关卡静态数据契约（schema_version = 1）。
/// 生成器输出、运行时 LevelBuilder 输入、测试 fixture 共用同一结构。
/// JSON 字段使用 snake_case，便于外部工具与后续版本化。
/// </summary>
public sealed class LevelSpec
{
    public int SchemaVersion { get; set; } = 1;
    public int GeneratorVersion { get; set; } = 1;
    public long Seed { get; set; }
    public int LevelId { get; set; }
    public string Name { get; set; } = "";
    public Cell Origin { get; set; } = Cell.Zero;
    public int Width { get; set; } = 32;
    public int Height { get; set; } = 32;
    public List<Cell> Floor { get; set; } = new();
    public List<Cell> Obstacles { get; set; } = new();
    public Cell Spawn { get; set; }
    public Cell Goal { get; set; }
    public List<Cell> Foods { get; set; } = new();
    public List<Cell> MechCenters { get; set; } = new();
    public List<int> MechInitialLevels { get; set; } = new();
    public int InitialMaxLen { get; set; } = 3;
    public int FoodLenGain { get; set; } = 2;
    public int GrowthInterval { get; set; } = 6;
    public int MechanismCap { get; set; } = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    static LevelSpec()
    {
        JsonOptions.Converters.Add(new CellJsonConverter());
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public static LevelSpec FromJson(string json)
    {
        return JsonSerializer.Deserialize<LevelSpec>(json, JsonOptions)
            ?? throw new InvalidOperationException("LevelSpec JSON 解析失败");
    }
}

/// <summary>Cell 与 JSON 数组 [x, y] 互转。</summary>
public sealed class CellJsonConverter : JsonConverter<Cell>
{
    public override Cell Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Cell 必须是 [x, y] 数组");
        }
        reader.Read();
        int x = reader.GetInt32();
        reader.Read();
        int y = reader.GetInt32();
        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("Cell 必须是 [x, y] 数组");
        }
        return new Cell(x, y);
    }

    public override void Write(Utf8JsonWriter writer, Cell value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteEndArray();
    }
}
