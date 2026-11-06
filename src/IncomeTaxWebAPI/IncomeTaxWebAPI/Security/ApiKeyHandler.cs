using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace IncomeTaxWebAPI.Security
{
    public class ApiKeyHandler:DelegatingHandler
    {
        //private Entities asce = new Entities();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bool isValidAPIKey = false;
            IEnumerable<string> lsHeaders;
            
            var checkApiExists = request.Headers.TryGetValues("Authorization", out lsHeaders);

            if (checkApiExists)
            {
                string apiKeyClient = lsHeaders.FirstOrDefault();
                //isValidAPIKey = asce.Keys.Count(s => s.Status.value == true && s.APIKey.Equals(apiKeyClient)) > 0;

                isValidAPIKey = (apiKeyClient == "ApiKey FreshTax2022");
            }

            if (!isValidAPIKey)
            {
                return request.CreateResponse(HttpStatusCode.Unauthorized, "Bad API Key");
            }

            var response = await base.SendAsync(request, cancellationToken);
            return response;
        }
    }
}