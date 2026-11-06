using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Serialization;

namespace EInvoiceAPI
{
    // using System.Xml.Serialization;
    // XmlSerializer serializer = new XmlSerializer(typeof(document));
    // using (StringReader reader = new StringReader(xml))
    // {
    //    var test = (document)serializer.Deserialize(reader);
    // }

    [XmlRoot(ElementName = "address")]
    public class address
    {
        [XmlElement(ElementName = "branchID")]
        public string branchID { get; set; }

        [XmlElement(ElementName = "country")]
        public string country { get; set; }

        [XmlElement(ElementName = "governate")]
        public string governate { get; set; }

        [XmlElement(ElementName = "regionCity")]
        public string regionCity { get; set; }

        [XmlElement(ElementName = "street")]
        public string street { get; set; }

        [XmlElement(ElementName = "buildingNumber")]
        public string buildingNumber { get; set; }

        [XmlElement(ElementName = "postalCode")]
        public string postalCode { get; set; }
    }

    [XmlRoot(ElementName = "address")]
    public class reciverAddress
    {
        [XmlElement(ElementName = "country")]
        public string country { get; set; }

        [XmlElement(ElementName = "governate")]
        public string governate { get; set; }

        [XmlElement(ElementName = "regionCity")]
        public string regionCity { get; set; }

        [XmlElement(ElementName = "street")]
        public string street { get; set; }

        [XmlElement(ElementName = "buildingNumber")]
        public string buildingNumber { get; set; }

        [XmlElement(ElementName = "postalCode")]
        public string postalCode { get; set; }
    }

    [XmlRoot(ElementName = "issuer")]
    public class issuer
    {

        [XmlElement(ElementName = "type")]
        public string type { get; set; }

        [XmlElement(ElementName = "id")]
        public string id { get; set; }

        [XmlElement(ElementName = "name")]
        public string name { get; set; }

        [XmlElement(ElementName = "address")]
        public address address { get; set; }
    }

    [XmlRoot(ElementName = "receiver")]
    public class receiver
    {

        [XmlElement(ElementName = "type")]
        public string type { get; set; }

        [XmlElement(ElementName = "id")]
        public string id { get; set; }

        [XmlElement(ElementName = "name")]
        public string name { get; set; }

        [XmlElement(ElementName = "address")]
        public reciverAddress address { get; set; }
    }

    [XmlRoot(ElementName = "unitValue")]
    public class unitValue
    {

        [XmlElement(ElementName = "currencySold")]
        public string currencySold { get; set; }

        [XmlElement(ElementName = "amountSold")]
        public double amountSold { get; set; }

        [XmlElement(ElementName = "amountEGP")]
        public double amountEGP { get; set; }

        [XmlElement(ElementName = "currencyExchangeRate")]
        public double currencyExchangeRate { get; set; }
    }

    [XmlRoot(ElementName = "discount")]
    public class discount
    {

        [XmlElement(ElementName = "rate")]
        public double rate { get; set; }

        [XmlElement(ElementName = "amount")]
        public double amount { get; set; }
    }

    [XmlRoot(ElementName = "taxableItem")]
    [JsonObject("taxableItem")]
    public class taxableItem
    {

        [XmlElement(ElementName = "taxType")]
        public string taxType { get; set; }

        [XmlElement(ElementName = "amount")]
        public double amount { get; set; }

        [XmlElement(ElementName = "subType")]
        public string subType { get; set; }

        [XmlElement(ElementName = "rate")]
        public double rate { get; set; }
    }

    //[XmlRoot(ElementName = "taxableItems")]
    //public class taxableItems
    //{

    //    [XmlElement(ElementName = "taxableItem")]
    //    public taxableItem taxableItem { get; set; }
    //}

    [XmlRoot(ElementName = "invoiceLine")]
    [JsonObject("invoiceLine")]
    public class invoiceLine
    {

        [XmlElement(ElementName = "description")]
        public string description { get; set; }

        [XmlElement(ElementName = "itemType")]
        public string itemType { get; set; }

        [XmlElement(ElementName = "itemCode")]
        public string itemCode { get; set; }

        [XmlElement(ElementName = "unitType")]
        public string unitType { get; set; }

        [XmlElement(ElementName = "quantity")]
        public double quantity { get; set; }

        [JsonProperty("unitValue")]
        [XmlElement(ElementName = "unitValue")]
        public unitValue unitValue { get; set; }

        [XmlElement(ElementName = "salesTotal")]
        public double salesTotal { get; set; }

        [XmlElement(ElementName = "total")]
        public double total { get; set; }

        [XmlElement(ElementName = "valueDifference")]
        public double valueDifference { get; set; }

        [XmlElement(ElementName = "totalTaxableFees")]
        public double totalTaxableFees { get; set; }

        [XmlElement(ElementName = "netTotal")]
        public double netTotal { get; set; }

        [XmlElement(ElementName = "itemsDiscount")]
        public double itemsDiscount { get; set; }

        [XmlElement(ElementName = "discount")]
        public discount discount { get; set; }

        [XmlArray(ElementName = "taxableItems")]
        public List<taxableItem> taxableItems { get; set; }

        [XmlElement(ElementName = "internalCode")]
        public string internalCode { get; set; }
    }

    //[JsonArray("invoiceLines")]
    //[XmlRoot(ElementName = "invoiceLines")]
    //public class invoiceLines
    //{

    //    [JsonProperty("invoiceLine")]
    //    [XmlElement(ElementName = "invoiceLine")]
    //    public List<invoiceLine> invoiceLine { get; set; }
    //}

    [XmlRoot(ElementName = "taxTotal")]
    public class taxTotal
    {

        [XmlElement(ElementName = "taxType")]
        public string taxType { get; set; }

        [XmlElement(ElementName = "amount")]
        public double amount { get; set; }
    }

    //[JsonObject("taxTotals")]
    //[XmlRoot(ElementName = "taxTotals")]
    //public class taxTotals
    //{

    //    [XmlElement(ElementName = "taxTotal")]
    //    public taxTotal taxTotal { get; set; }
    //}

    [XmlRoot(ElementName = "document")]
    public class document
    {

        [XmlElement(ElementName = "issuer")]
        public issuer issuer { get; set; }

        [XmlElement(ElementName = "receiver")]
        public receiver receiver { get; set; }

        [XmlElement(ElementName = "documentType")]
        public string documentType { get; set; }

        [XmlElement(ElementName = "documentTypeVersion")]
        public string documentTypeVersion { get; set; }

        [XmlElement(ElementName = "dateTimeIssued")]
        public string dateTimeIssued { get; set; }

        [XmlElement(ElementName = "taxpayerActivityCode")]
        public string taxpayerActivityCode { get; set; }

        [XmlElement(ElementName = "internalID")]
        public string internalID { get; set; }

        [XmlArray(ElementName = "invoiceLines")]
        //public invoiceLines invoiceLines { get; set; }
        public List<invoiceLine> invoiceLines { get; set; }

        [XmlElement(ElementName = "totalSalesAmount")]
        public double totalSalesAmount { get; set; }

        [XmlElement(ElementName = "totalDiscountAmount")]
        public double totalDiscountAmount { get; set; }

        [XmlElement(ElementName = "netAmount")]
        public double netAmount { get; set; }

        [XmlArray(ElementName = "taxTotals")]
        public List<taxTotal> taxTotals { get; set; }

        [XmlElement(ElementName = "totalAmount")]
        public double totalAmount { get; set; }

        [XmlElement(ElementName = "totalItemsDiscountAmount")]
        public double totalItemsDiscountAmount { get; set; }

        [XmlElement(ElementName = "extraDiscountAmount")]
        public double extraDiscountAmount { get; set; }
    }
}