using Newtonsoft.Json;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using IncomeTaxWebAPI.Services;

namespace IncomeTaxWebAPI.Controllers
{
    public class EInvoiceDevController : ControllerBase
    {
        // SAP URL, credentials and timeout come from EInvoiceAPIAdmin (Admin > ETA Notifications, "Dev").
        private readonly SapEndpointStore _sapEndpoints;

        public EInvoiceDevController(SapEndpointStore sapEndpoints) => _sapEndpoints = sapEndpoints;

        [HttpPut]
        //[Route("api/dev")]
        //[Route("api/dev/ping")]
        [Route("api/einvoice/dev/ping")]
        public IActionResult ping([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] object value)
        {
            //IEnumerable<string> hdrContentType;
            string acceptLanguage = Request.Headers.AcceptLanguage.FirstOrDefault();

            //Request.Headers.TryGetValues("Content-Type", out hdrContentType);
            //string contentType = hdrContentType.FirstOrDefault();

            var vat = VatRegNumber.FromJson(value.ToString());
            return Content(vat.ToJson(), "application/json; charset=utf-8");
        }

        [HttpPut]
        [Route("api/einvoice/dev/notifications/documents")]
        public IActionResult documents([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] object value)
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

            var sap = _sapEndpoints.Get(SapEndpointStore.Dev);
            if (sap == null)
            {
                // Not configured in Admin: same response as SAP being unreachable.
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
            var client = new RestClient(sap.Url);
            client.Timeout = sap.TimeoutMs;
            //var request = new RestRequest(Method.GET);
            //request.AddHeader("x-csrf-Token", "Fetch");
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
            if (sap.BasicAuthorizationHeader != null)
                request.AddHeader("Authorization", sap.BasicAuthorizationHeader);
            //request.AddHeader("x-csrf-Token", csrf_Token);
            request.AddHeader("Content-Type", "application/json");
            request.AddParameter("application/json", delivery.DeliveryToJson(), ParameterType.RequestBody);
            IRestResponse uresponse = client.Execute(request);
            //}
            if (uresponse.StatusCode == HttpStatusCode.OK)
            {
            return Content("Received", "application/json; charset=utf-8");
            }
            return StatusCode(StatusCodes.Status500InternalServerError); // SAP did not return 200 OK
        }
        
        [HttpPut]
        [Route("api/einvoice/dev/notifications/receipts")]
        public IActionResult receipts([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] object value)
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

            var sap = _sapEndpoints.Get(SapEndpointStore.Dev);
            if (sap == null)
            {
                // Not configured in Admin: same response as SAP being unreachable.
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
            var client = new RestClient(sap.Url);
            client.Timeout = sap.TimeoutMs;
            //var request = new RestRequest(Method.GET);
            //request.AddHeader("x-csrf-Token", "Fetch");
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
            if (sap.BasicAuthorizationHeader != null)
                request.AddHeader("Authorization", sap.BasicAuthorizationHeader);
            //request.AddHeader("x-csrf-Token", csrf_Token);
            request.AddHeader("Content-Type", "application/json");
            request.AddParameter("application/json", delivery.DeliveryToJson(), ParameterType.RequestBody);
            IRestResponse uresponse = client.Execute(request);
            //}
            if (uresponse.StatusCode == HttpStatusCode.OK)
            {
                return Content("Received", "application/json; charset=utf-8");
            }
            return StatusCode(StatusCodes.Status500InternalServerError); // SAP did not return 200 OK
        }
    }
}
