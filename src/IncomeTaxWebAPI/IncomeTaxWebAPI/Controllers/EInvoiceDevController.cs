using Newtonsoft.Json;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Web.Http;

namespace IncomeTaxWebAPI.Controllers
{
    public class EInvoiceDevController : ApiController
    {
        [HttpPut]
        //[Route("api/dev")]
        //[Route("api/dev/ping")]
        [Route("api/einvoice/dev/ping")]
        public HttpResponseMessage ping([FromBody] object value)
        {
            IEnumerable<string> hdrAcceptLanguage;
            //IEnumerable<string> hdrContentType;
            Request.Headers.TryGetValues("Accept-Language", out hdrAcceptLanguage);
            string acceptLanguage = hdrAcceptLanguage.FirstOrDefault();

            //Request.Headers.TryGetValues("Content-Type", out hdrContentType);
            //string contentType = hdrContentType.FirstOrDefault();

            var vat = VatRegNumber.FromJson(value.ToString());
            var response = this.Request.CreateResponse(HttpStatusCode.OK);
            response.Content = new StringContent(vat.ToJson(), Encoding.UTF8, "application/json");
            return response;
        }

        [HttpPut]
        [Route("api/einvoice/dev/notifications/documents")]
        public HttpResponseMessage documents([FromBody] object value)
        {
            Delivery delivery = null;
            if (value != null)
            {
                try
                {
                    delivery = Delivery.DeliveryFromJson(value.ToString());
                    DBManager.AddDelivery(delivery, "DevDB");
                }
                catch (Exception)
                {
                }
            }

            var client = new RestClient("https://vhfrgdevci.sap.i.fresh.com.eg:44300/zeinvoice?sap-client=300");
            //var client = new RestClient("http://192.168.22.73:50100/zeinvoice?sap-client=300");
            //var client = new RestClient("https://192.168.22.73:50101/zeinvoice?sap-client=300");
            client.Timeout = 3000;
            //var request = new RestRequest(Method.GET);
            //request.AddHeader("x-csrf-Token", "Fetch");
            //request.AddHeader("Authorization", "Basic RnJlc2g6RnJlc2gyMDIy");
            //request.AddHeader("Content-Type", "application/json");
            //IRestResponse fresponse = client.Execute(request);
            //string csrf_Token = null;
            //foreach (var item in fresponse.Headers)
            //{
            //    if (item.Name == "x-csrf-token")
            //    {
            //        csrf_Token = item.Value.ToString();
            //    }
            //}
            //if (fresponse.StatusCode == HttpStatusCode.OK)
            //{
            var request = new RestRequest(Method.PUT);
            //request.AddHeader("Authorization", "Basic RnJlc2g6RnJlc2gyMDIy");
            request.AddHeader("Authorization", "Basic RVRBLlNlcnZpY2U6bGptcns+V3ApLVZDK3JySD1OLUc+YW1uZlwjI2V7I0VOJWRoY1EzVQ==");
            //request.AddHeader("x-csrf-Token", csrf_Token);
            request.AddHeader("Content-Type", "application/json");
            request.AddParameter("application/json", delivery.DeliveryToJson(), ParameterType.RequestBody);
            IRestResponse uresponse = client.Execute(request);
            //}
            if (uresponse.StatusCode == HttpStatusCode.OK)
            {
            var response = this.Request.CreateResponse(HttpStatusCode.OK);
            response.Content = new StringContent("Received", Encoding.UTF8, "application/json");
            return response;
            }
            return null;
        }
        
        [HttpPut]
        [Route("api/einvoice/dev/notifications/receipts")]
        public HttpResponseMessage receipts([FromBody] object value)
        {
            Delivery delivery = null;
            if (value != null)
            {
                try
                {
                    delivery = Delivery.DeliveryFromJson(value.ToString());
                    DBManager.AddDelivery(delivery, "DevDB");
                }
                catch (Exception)
                {
                }
            }

            var client = new RestClient("https://vhfrgdevci.sap.i.fresh.com.eg:44300/zeinvoice?sap-client=300");
            //var client = new RestClient("http://192.168.22.73:50100/zeinvoice?sap-client=300");
            //var client = new RestClient("https://192.168.22.73:50101/zeinvoice?sap-client=300");
            client.Timeout = 3000;
            //var request = new RestRequest(Method.GET);
            //request.AddHeader("x-csrf-Token", "Fetch");
            //request.AddHeader("Authorization", "Basic RnJlc2g6RnJlc2gyMDIy");
            //request.AddHeader("Content-Type", "application/json");
            //IRestResponse fresponse = client.Execute(request);
            //string csrf_Token = null;
            //foreach (var item in fresponse.Headers)
            //{
            //    if (item.Name == "x-csrf-token")
            //    {
            //        csrf_Token = item.Value.ToString();
            //    }
            //}
            //if (fresponse.StatusCode == HttpStatusCode.OK)
            //{
            var request = new RestRequest(Method.PUT);
            //request.AddHeader("Authorization", "Basic RnJlc2g6RnJlc2gyMDIy");
            request.AddHeader("Authorization", "Basic RVRBLlNlcnZpY2U6bGptcns+V3ApLVZDK3JySD1OLUc+YW1uZlwjI2V7I0VOJWRoY1EzVQ==");
            //request.AddHeader("x-csrf-Token", csrf_Token);
            request.AddHeader("Content-Type", "application/json");
            request.AddParameter("application/json", delivery.DeliveryToJson(), ParameterType.RequestBody);
            IRestResponse uresponse = client.Execute(request);
            //}
            if (uresponse.StatusCode == HttpStatusCode.OK)
            {
                var response = this.Request.CreateResponse(HttpStatusCode.OK);
                response.Content = new StringContent("Received", Encoding.UTF8, "application/json");
                return response;
            }
            return null;
        }
    }
}
