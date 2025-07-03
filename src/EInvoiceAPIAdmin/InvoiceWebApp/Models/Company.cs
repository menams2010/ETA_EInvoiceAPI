namespace InvoiceWebApp.Models
{
    public class Company
    {
        public int Id { get; set; }
        public long? CompanyCode { get; set; }
        public string CompanyName { get; set; }
        public string TokenSerial { get; set; }
        public string TokenPin { get; set; }
        public string CertSerialNum { get; set; }
        public string UseXMLTOJSON { get; set; }
        //public string UseXMLTOJSON
        //{
        //    get => useXMLTOJSON == "1" ? "true" : "false";
        //    set => useXMLTOJSON = value == "true" ? "1" : "0";
        //}

    }
}
