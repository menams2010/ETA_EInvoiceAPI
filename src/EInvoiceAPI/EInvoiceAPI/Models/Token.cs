using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;


namespace EInvoiceAPI
{
    public partial class Token
    {
        [JsonProperty("access_token")]
        public string? AccessToken { get; set; }

        [JsonProperty("expires_in")]
        public long ExpiresIn { get; set; }

        [JsonProperty("token_type")]
        public string TokenType { get; set; }

        [JsonProperty("scope")]
        public string Scope { get; set; }
    }

    public partial class Token
    {
        public static Token FromJson(string json) => JsonConvert.DeserializeObject<Token>(json, TokenConverter.Settings);
    }

    public static class TokenSerialize
    {
        public static string ToJson(this Token self) => JsonConvert.SerializeObject(self, TokenConverter.Settings);
    }

    internal static class TokenConverter
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
            Converters =
            {
                new IsoDateTimeConverter { DateTimeStyles = DateTimeStyles.AssumeUniversal }
            },
        };
    }
}