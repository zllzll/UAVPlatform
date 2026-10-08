using System.Text.Json;
using System.Text.Json.Serialization;

namespace UavPlatform.Core.Models;

/// <summary>
/// 把 <c>NaN</c> / <c>±Infinity</c> 写成 JSON <c>null</c>，读回时还原为 <c>NaN</c>。
/// <para>
/// 设备协议里的「无效值」会自然产生非有限双精度数：UCM221 的 -32768 哨兵值、尚未收到数据时的时长、
/// 未解算出参考点时的坐标等。System.Text.Json 默认遇到这些值直接抛
/// <c>ArgumentException: .NET number values such as positive and negative infinity cannot be written as valid JSON</c>，
/// 会把整条 REST / SignalR 响应打成 500。这里统一兜底为 <c>null</c>。
/// </para>
/// </summary>
public sealed class NonFiniteDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? double.NaN : reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsFinite(value)) writer.WriteNumberValue(value);
        else writer.WriteNullValue();
    }
}

/// <summary><see cref="NonFiniteDoubleConverter"/> 的可空版本。</summary>
public sealed class NonFiniteNullableDoubleConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is { } v && double.IsFinite(v)) writer.WriteNumberValue(v);
        else writer.WriteNullValue();
    }
}

/// <summary>非有限浮点数的 JSON 处理开关。</summary>
public static class JsonNumbers
{
    /// <summary>补上 <c>double</c> / <c>double?</c> 的兜底转换器。可重复调用。</summary>
    public static JsonSerializerOptions AddNonFiniteHandling(this JsonSerializerOptions options)
    {
        options.Converters.Add(new NonFiniteDoubleConverter());
        options.Converters.Add(new NonFiniteNullableDoubleConverter());
        return options;
    }
}
