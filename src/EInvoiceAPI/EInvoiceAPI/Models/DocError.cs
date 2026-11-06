using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace EInvoiceAPI
{

    public partial class InvoiceValidationResult
    {
        [JsonProperty("validationResults")]
        public ValidationResults ValidationResults { get; set; }
    }

    public partial class ValidationResults
    {
        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("validationSteps")]
        public ValidationStep[] ValidationSteps { get; set; }
    }

    public partial class ValidationStep
    {
        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("error")]
        public Error Error { get; set; }

        [JsonProperty("stepName")]
        public string StepName { get; set; }

        [JsonProperty("stepId")]
        public string StepId { get; set; }
    }

    public partial class Error
    {
        [JsonProperty("propertyName")]
        public string PropertyName { get; set; }

        [JsonProperty("propertyPath")]
        public string PropertyPath { get; set; }

        [JsonProperty("errorCode")]
        public string ErrorCode { get; set; }

        [JsonProperty("error")]
        public string ErrorError { get; set; }

        [JsonProperty("innerError")]
        public Error[] InnerError { get; set; }
    }

    public partial class InvoiceValidationResult
    {
        public static InvoiceValidationResult FromJson(string json) => JsonConvert.DeserializeObject<InvoiceValidationResult>(json, DocErrorsConverter.Settings);
    }

    public static class Serialize
    {
        public static string ToJson(this InvoiceValidationResult self) => JsonConvert.SerializeObject(self, DocErrorsConverter.Settings);
    }

    internal static class DocErrorsConverter
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