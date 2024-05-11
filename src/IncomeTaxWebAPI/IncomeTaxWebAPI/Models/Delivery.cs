using System;
using System.Collections.Generic;

using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace IncomeTaxWebAPI
{
    public partial class Delivery
    {
        [JsonProperty("DeliveryId")]
        public string DeliveryId { get; set; }

        [JsonProperty("Type")]
        public string Type { get; set; }

        [JsonProperty("Count")]
        public long Count { get; set; }

        [JsonProperty("Message")]
        public List<Message> Message { get; set; }
    }
    public partial class Message
    {
        [JsonProperty("Type")]
        public string Type { get; set; }

        [JsonProperty("UUId")]
        public string UuId { get; set; }

        [JsonProperty("SubmissionUUId")]
        public string SubmissionUuId { get; set; }

        [JsonProperty("LongId")]
        public string LongId { get; set; }

        [JsonProperty("InternalId")]
        public string InternalId { get; set; }

        [JsonProperty("Status")]
        public string Status { get; set; }
    }

    public partial class Delivery
    {
        public static Delivery DeliveryFromJson(string json) => JsonConvert.DeserializeObject<Delivery>(json, IncomeTaxWebAPI.DeliveryConverter.Settings);
    }

    public static class DeliverySerialize
    {
        public static string DeliveryToJson(this Delivery self) => JsonConvert.SerializeObject(self, IncomeTaxWebAPI.DeliveryConverter.Settings);
    }

    internal static class DeliveryConverter
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

    internal class DeliveryParseStringConverter : JsonConverter
    {
        public override bool CanConvert(Type t) => t == typeof(long) || t == typeof(long?);

        public override object ReadJson(JsonReader reader, Type t, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;
            var value = serializer.Deserialize<string>(reader);
            long l;
            if (Int64.TryParse(value, out l))
            {
                return l;
            }
            throw new Exception("Cannot unmarshal type long");
        }

        public override void WriteJson(JsonWriter writer, object untypedValue, JsonSerializer serializer)
        {
            if (untypedValue == null)
            {
                serializer.Serialize(writer, null);
                return;
            }
            var value = (long)untypedValue;
            serializer.Serialize(writer, value.ToString());
            return;
        }

        public static readonly DeliveryParseStringConverter Singleton = new DeliveryParseStringConverter();
    }
}