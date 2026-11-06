using EInvoiceAPI.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.IO;
using System.Xml.Serialization;
using System.Collections.Concurrent;
using RestSharp;

namespace EInvoiceAPI.Helpers
{
    public static class InvoiceHelper
    {
        public static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            FloatFormatHandling = FloatFormatHandling.String,
            FloatParseHandling = FloatParseHandling.Decimal,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DateParseHandling = DateParseHandling.None,
            Formatting = Formatting.None
        };

        //// Shared RestClient for reuse
        //public static readonly RestClient SharedClient = new RestClient();

        // Cache XmlSerializer instances for performance
        private static readonly ConcurrentDictionary<Type, XmlSerializer> XmlSerializers = new();

        public static XmlSerializer GetXmlSerializer(Type type) =>
            XmlSerializers.GetOrAdd(type, t => new XmlSerializer(t));

        public static JObject DeserializeInvoice(string requestBody) =>
            JsonConvert.DeserializeObject<JObject>(requestBody, JsonSettings);

        public static object ConvertXmlToInvoiceObject(string requestBody, out string json)
        {
            try
            {
                var serializer = GetXmlSerializer(typeof(document));
                StringReader reader = new StringReader(requestBody);
                object XmlInvoice = (document)serializer.Deserialize(reader);

                if (((document)XmlInvoice).receiver.id == null)
                {
                    ((document)XmlInvoice).receiver.id = string.Empty;
                }

                if (((document)XmlInvoice).documentType == "EI")
                {
                    serializer = GetXmlSerializer(typeof(export_document));
                    reader = new StringReader(requestBody);
                    XmlInvoice = (export_document)serializer.Deserialize(reader);
                    if (((export_document)XmlInvoice).receiver.id == null)
                    {
                        ((export_document)XmlInvoice).receiver.id = string.Empty;
                    }
                }
                json = JsonConvert.SerializeObject(XmlInvoice);
                return XmlInvoice;
            }
            catch
            {
                json = null;
                throw;
            }
        }
    }
}