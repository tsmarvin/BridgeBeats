using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BridgeBeats.Contracts.Enums;

namespace BridgeBeats.Core.Infrastructure.Settings;

/// <summary>Creates the stable JSON contract used only for database-backed application settings.</summary>
internal static class ApplicationSettingsJson {
    internal static JsonSerializerOptions CreateOptions( ) {
        JsonSerializerOptions options = new( JsonSerializerDefaults.Web );
        options.Converters.Add( new SupportedProviderDictionaryConverter( ) );
        return options;
    }

    /// <summary>
    /// Persists provider dictionary keys by their explicit numeric value so renaming an enum member
    /// does not invalidate an existing settings row.
    /// </summary>
    private sealed class SupportedProviderDictionaryConverter
        : JsonConverter<Dictionary<SupportedProviders, int>> {
        public override Dictionary<SupportedProviders, int> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        ) {
            if (reader.TokenType != JsonTokenType.StartObject) {
                throw new JsonException( "A provider settings dictionary must be a JSON object." );
            }

            Dictionary<SupportedProviders, int> result = [];
            while (reader.Read( )) {
                if (reader.TokenType == JsonTokenType.EndObject) {
                    return result;
                }

                if (reader.TokenType != JsonTokenType.PropertyName) {
                    throw new JsonException( "A provider settings dictionary contains an invalid key." );
                }

                string? keyText = reader.GetString( );
                if (!int.TryParse( keyText, NumberStyles.None, CultureInfo.InvariantCulture, out int numericKey ) ||
                    !Enum.IsDefined( typeof( SupportedProviders ), numericKey )) {
                    throw new JsonException( "A provider settings dictionary contains an unsupported provider key." );
                }

                if (!reader.Read( ) || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32( out int value )) {
                    throw new JsonException( "A provider settings dictionary contains an invalid value." );
                }

                SupportedProviders key = (SupportedProviders)numericKey;
                if (!result.TryAdd( key, value )) {
                    throw new JsonException( "A provider settings dictionary contains a duplicate provider key." );
                }
            }

            throw new JsonException( "A provider settings dictionary was incomplete." );
        }

        public override void Write(
            Utf8JsonWriter writer,
            Dictionary<SupportedProviders, int> value,
            JsonSerializerOptions options
        ) {
            writer.WriteStartObject( );
            foreach ((SupportedProviders provider, int configuredValue) in value.OrderBy( pair => (int)pair.Key )) {
                writer.WriteNumber(
                    ((int)provider).ToString( CultureInfo.InvariantCulture ),
                    configuredValue
                );
            }

            writer.WriteEndObject( );
        }
    }
}
