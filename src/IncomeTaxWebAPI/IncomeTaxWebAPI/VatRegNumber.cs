using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace IncomeTaxWebAPI
{
    public partial class VatRegNumber
    {

        [JsonProperty("Rin")]
        public string Rin { get; set; }
    }
    public partial class VatRegNumber
    {
        public static VatRegNumber FromJson(string json) => JsonConvert.DeserializeObject<VatRegNumber>(json);
    }

    public static class Serialize
    {
        public static string ToJson(this VatRegNumber self) => JsonConvert.SerializeObject(self);
    }
}