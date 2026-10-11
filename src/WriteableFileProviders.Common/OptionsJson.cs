using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WriteableFileProviders;

/// <summary>
/// The JSON settings that keep the configuration shape the 2026 (Newtonsoft) serializer wrote and read: PascalCase
/// names read case-insensitively, nulls written, and the lenient conversions stored configuration relies on.
/// </summary>
[RequiresUnreferencedCode(WriteableFileProviderSerializer.AotMessage)]
[RequiresDynamicCode(WriteableFileProviderSerializer.AotMessage)]
internal static class OptionsJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            // The 2026 serializer did not escape '+', '<' or non-ASCII characters in keys and PEMs; neither does this one.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new LenientStringConverter());
        options.Converters.Add(new LenientBooleanConverter());
        options.Converters.Add(new StringConvertibleConverterFactory());
        options.MakeReadOnly();
        return options;
    }

    /// <summary>Reads a string from a string, a number or a boolean: stored ids may be numbers.</summary>
    private sealed class LenientStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => Encoding(reader),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => throw new JsonException($"A {reader.TokenType} cannot be read as a string."),
        };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);

        private static string Encoding(Utf8JsonReader reader) =>
            reader.HasValueSequence ? System.Text.Encoding.UTF8.GetString(System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence)) : System.Text.Encoding.UTF8.GetString(reader.ValueSpan);
    }

    /// <summary>Reads a boolean from true/false, the strings "true"/"false", or the numbers 1/0.</summary>
    private sealed class LenientBooleanConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.String when bool.TryParse(reader.GetString(), out var value) => value,
            JsonTokenType.Number when reader.TryGetInt64(out var number) && number is 0 or 1 => number == 1,
            _ => throw new JsonException($"The value cannot be read as a boolean."),
        };

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }

    /// <summary>
    /// For a class that converts from a string (an implicit conversion or a public constructor taking one string), such as
    /// an SDK's constant class: reads the string, or an object holding it under "Value" (what the 2026 serializer wrote
    /// for such types), and writes the string.
    /// </summary>
    [RequiresUnreferencedCode(WriteableFileProviderSerializer.AotMessage)]
    [RequiresDynamicCode(WriteableFileProviderSerializer.AotMessage)]
    private sealed class StringConvertibleConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsClass && typeToConvert != typeof(string) && !typeToConvert.IsArray && !typeof(System.Collections.IEnumerable).IsAssignableFrom(typeToConvert)
            && typeToConvert != typeof(Uri) && !typeof(FileProviderOptions).IsAssignableFrom(typeToConvert) && FromString(typeToConvert) is not null;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(StringConvertibleConverter<>).MakeGenericType(typeToConvert), FromString(typeToConvert)!)!;

        public static Func<string, object?>? FromString(Type type)
        {
            var implicitOperator = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == type && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(string));
            if (implicitOperator is not null)
                return value => implicitOperator.Invoke(null, [value]);
            var constructor = type.GetConstructor([typeof(string)]);
            return constructor is null ? null : value => constructor.Invoke([value]);
        }
    }

    [RequiresUnreferencedCode(WriteableFileProviderSerializer.AotMessage)]
    [RequiresDynamicCode(WriteableFileProviderSerializer.AotMessage)]
    private sealed class StringConvertibleConverter<T>(Func<string, object?> fromString) : JsonConverter<T>
        where T : class
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.String:
                    return (T?)fromString(reader.GetString()!);
                case JsonTokenType.StartObject:
                    string? value = null;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        var name = reader.GetString();
                        reader.Read();
                        if (string.Equals(name, "Value", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.String)
                            value = reader.GetString();
                        else
                            reader.Skip();
                    }
                    return value is null ? null : (T?)fromString(value);
                default:
                    throw new JsonException($"A {reader.TokenType} cannot be read as {typeof(T).Name}.");
            }
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(StringOf(value));
    }

    /// <summary>The string a string-convertible value stands for: its public string "Value" property, or its ToString().</summary>
    public static string? StringOf(object value) =>
        value.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance) is { PropertyType: var t } property && t == typeof(string)
            ? (string?)property.GetValue(value)
            : Convert.ToString(value, CultureInfo.InvariantCulture);

    /// <summary>Converts a configuration string to a property's type, or throws <see cref="FormatException"/>.</summary>
    public static object? FromConfigurationString(string value, Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target == typeof(string))
            return value;
        if (target == typeof(bool))
            return bool.TryParse(value, out var b) ? b : value is "1" or "0" ? value == "1" : throw new FormatException($"'{value}' is not a boolean.");
        if (target.IsEnum)
            return Enum.Parse(target, value, ignoreCase: true);
        if (target == typeof(Uri))
            return new Uri(value, UriKind.RelativeOrAbsolute);
        if (target == typeof(TimeSpan))
            return TimeSpan.Parse(value, CultureInfo.InvariantCulture);
        if (target == typeof(Guid))
            return Guid.Parse(value);
        if (target == typeof(DateTimeOffset))
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
        if (target.IsPrimitive || target == typeof(decimal))
            return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        if (StringConvertibleConverterFactory.FromString(target) is { } fromString)
            return fromString(value);
        throw new FormatException($"A configuration string cannot be converted to {target.Name}.");
    }
}
