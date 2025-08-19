using EInvoiceAPI.Helpers;
using EInvoiceAPI.Models;
using EInvoiceAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Win32.SafeHandles;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Ess;
using Org.BouncyCastle.Asn1.Ocsp;
using RestSharp;
using System.Data;
using System.Dynamic;
using System.Globalization;
using System.Net;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace EInvoiceAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class Invoice : ControllerBase
    {
        //private static RestClient client = InvoiceHelper.SharedClient;
        private int tryCount = 0;
        private DataTable tokenTable;
        private string? IdentityBaseURL;
        private string? SystemAPIBaseURL;
        private string? TokenSerial;
        private string? TokenPin;
        private string? CertSerialNum;
        private string? ClientId;
        private string? ClientSecret;
        private bool UseXML = false;
        private bool UseIMP;
        private readonly string sysUIFormat = TokenManager.GetSettings("DateTimeFormat")?.ToString();

        private const int SignError = (int)HttpStatusCode.Locked;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool LogonUser(String lpszUsername, String lpszDomain, String lpszPassword,
           int dwLogonType, int dwLogonProvider, out SafeAccessTokenHandle phToken);

        private readonly Serializer _serializer;
        private readonly Hasher _hasher;
        private readonly CertificateCache _certificateCache;
        private readonly SlackLogger _slackLogger;

        private readonly string DllLibPath = "eps2003csp11.dll";
        private static readonly Dictionary<string, object> SlotLocks = new();
        private static readonly object SlotLocksDictLock = new();

        public Invoice(Serializer serializer, Hasher hasher, CertificateCache certificateCache, SlackLogger slackLogger)
        {
            _serializer = serializer;
            _hasher = hasher;
            _certificateCache = certificateCache;
            _slackLogger = slackLogger;
        }

        [HttpPost("[action]")]
        [Produces("application/json")]
        public async Task<IActionResult> SignInvoice([FromHeader] string CompanyCode)
        {
            string requestBody;
            using (var sr = new StreamReader(Request.Body))
            {
                requestBody = await sr.ReadToEndAsync();
            }

            object runImpersonated = TokenManager.GetSettings("UseImpersonation");
            UseIMP = runImpersonated != null && Convert.ToBoolean(runImpersonated);

            TokenHelper.GetE_Sign(CompanyCode, out TokenSerial, out TokenPin, out CertSerialNum, out UseXML);

            if (UseXML)
            {
                try
                {
                    InvoiceHelper.ConvertXmlToInvoiceObject(requestBody, out var json);
                    requestBody = json;
                }
                catch (Exception ex)
                {
                    return StatusCode(SignError, "Error: Invalid XML format - " + ex.Message);
                }
            }

            JObject? invoice = InvoiceHelper.DeserializeInvoice(requestBody);
            var serializedJson = _serializer.Serialize(invoice);
            string docVer = invoice["documentTypeVersion"]?.ToString() ?? string.Empty;

            string signValue = docVer.Equals("0.9")
                ? "ANY"
                : SignWithCms(Encoding.UTF8.GetBytes(serializedJson), UseIMP, CompanyCode);

            if (signValue.StartsWith("Error:"))
            {
                return StatusCode(SignError, signValue);
            }

            JObject jobject = new JObject
            {
                { "signatureType", "I" },
                { "value", signValue }
            };
            JArray jarray = new JArray { jobject };
            invoice.Add("signatures", jarray);
            string signedInvoice = "{\"documents\":[" + invoice.ToString() + "]}";
            ////////////////////////////////////////////////////////////
            // Log the API call to Slack
            //await _slackLogger.LogApiCallAsync(CompanyCode, Request, "SignInvoice");
            ////////////////////////////////////////////////////////////

            return new ContentResult
            {
                StatusCode = (int)HttpStatusCode.OK,
                Content = signedInvoice,
                ContentType = "application/json"
            };
        }

        [HttpPost("[action]")]
        public async Task<IActionResult> SendInvoice([FromHeader] string CompanyCode, [FromHeader] string EnvironmentId)
        {
            string requestBody;
            using (var sr = new StreamReader(Request.Body))
            {
                requestBody = await sr.ReadToEndAsync();
            }

            object? runImpersonated = TokenManager.GetSettings("UseImpersonation");
            UseIMP = runImpersonated != null && Convert.ToBoolean(runImpersonated);

            TokenHelper.GetE_Sign(CompanyCode, out TokenSerial, out TokenPin, out CertSerialNum, out UseXML);

            if (UseXML)
            {
                try
                {
                    InvoiceHelper.ConvertXmlToInvoiceObject(requestBody, out var json);
                    requestBody = json;
                }
                catch (Exception ex)
                {
                    return StatusCode(SignError, "Error: Invalid XML format - " + ex.Message);
                }
            }

            JObject? invoice = InvoiceHelper.DeserializeInvoice(requestBody);
            var serializedJson = _serializer.Serialize(invoice);
            string docVer = invoice["documentTypeVersion"]?.ToString() ?? string.Empty;

            string signValue = docVer.Equals("0.9")
                ? "ANY"
                : SignWithCms(Encoding.UTF8.GetBytes(serializedJson), UseIMP, CompanyCode);
            if (signValue.StartsWith("Error:"))
            {
                return StatusCode(SignError, signValue);
            }

            JObject jobject = new JObject
            {
                { "signatureType", "I" },
                { "value", signValue }
            };
            JArray jarray = new JArray { jobject };
            invoice.Add("signatures", jarray);
            string signedInvoice = "{\"documents\":[" + invoice.ToString() + "]}";
        ////////////////////////////////////////////////////////////
        // Log the API call to Slack
        //await _slackLogger.LogApiCallAsync(CompanyCode, Request, "SendInvoice");
        ////////////////////////////////////////////////////////////

        GetToken:
            var token = TokenHelper.GetToken(CompanyCode, EnvironmentId, out var tokenSettings, sysUIFormat);
            if (string.IsNullOrEmpty(token) || token.StartsWith("CANNOT", StringComparison.OrdinalIgnoreCase))
            {
                return Unauthorized("Cannot Get Token \r\n" + token);
            }

            IdentityBaseURL = tokenSettings?.EnvTokenUrl;
            SystemAPIBaseURL = tokenSettings?.EnvInvoicingUrl;

            // Ensure SystemAPIBaseURL is not null or empty
            if (string.IsNullOrWhiteSpace(SystemAPIBaseURL))
            {
                var reason = $"SystemAPIBaseURL is null or empty. " +
                             $"tokenSettings: {(tokenSettings == null ? "null" : JsonConvert.SerializeObject(tokenSettings))}, " +
                             $"EnvironmentId: {EnvironmentId}";
                return StatusCode(500, reason);
            }

            // Enforce modern TLS and optionally relax SSL validation (for non-production troubleshooting)
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch { /* ignore */ }
            var allowInvalidSsl = Convert.ToBoolean(TokenManager.GetSettings("AllowInvalidSsl") ?? "false");

            var clientOptions = new RestClientOptions(SystemAPIBaseURL + "documentsubmissions")
            {
                Timeout = TimeSpan.FromSeconds(30),
                RemoteCertificateValidationCallback = allowInvalidSsl ? (sender, cert, chain, errors) => true : null
            };
            RestClient client = new RestClient(clientOptions);

            int maxRetries = 3;
            int attempt = 0;
            RestResponse? response = null;

            while (attempt < maxRetries)
            {
                attempt++;
                var request = new RestRequest(string.Empty, Method.Post);
                request.AddHeader("Content-Type", "application/json");
                request.AddHeader("Authorization", "Bearer " + token);
                request.AddStringBody(signedInvoice, DataFormat.Json);

                response = await client.ExecuteAsync(request);

                // Log the API call to Slack
                await _slackLogger.LogApiCallAsync(CompanyCode, EnvironmentId, client, request, response, "SendInvoice");

                if ((int)response.StatusCode == 0 || response.StatusCode == (HttpStatusCode)429)
                {
                    await Task.Delay(2000 * attempt);
                    continue;
                }

                break;
            }

            if (response is null || (int)response.StatusCode == 0)
            {
                return StatusCode(500, "Error: Received status code 0 with an empty response body from the ETA API.\r\n" + response?.ErrorMessage);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                goto GetToken;
            }

            return new ContentResult
            {
                StatusCode = (int)response.StatusCode,
                Content = response.Content,
                ContentType = "application/json"
            };

        }

        [HttpGet("[action]")]
        public async Task<ActionResult> GetSubmission([FromHeader] string CompanyCode, [FromHeader] string EnvironmentId, string submitionUUID)
        {
        //companyCode = Request.Headers["CompanyCode"];
        //environmentId = Request.Headers["EnvironmentId"];

        GetToken:
            var token = TokenHelper.GetToken(CompanyCode, EnvironmentId, out var tokenSettings, sysUIFormat);
            if (token.StartsWith("CANNOT"))
            {
                return Unauthorized("Cannot Get Token \r\n" + token);
            }

            IdentityBaseURL = tokenSettings?.EnvTokenUrl;
            SystemAPIBaseURL = tokenSettings?.EnvInvoicingUrl;

            var client = new RestClient(new RestClientOptions(SystemAPIBaseURL + "documentSubmissions/" + submitionUUID)
            {
                Timeout = TimeSpan.FromSeconds(30)
            });

            int maxRetries = 3;
            int attempt = 0;
            RestResponse? response = null;

            while (attempt < maxRetries)
            {
                attempt++;
                var request = new RestRequest(string.Empty, Method.Get);
                request.AddHeader("PageSize", "10");
                request.AddHeader("PageNo", "1");
                request.AddHeader("Authorization", "Bearer " + token);

                response = await client.ExecuteAsync(request);

                if ((int)response.StatusCode == 0 || response.StatusCode == (HttpStatusCode)429)
                {
                    await Task.Delay(2000 * attempt);
                    continue;
                }

                break;
            }

            if (response is null || (int)response.StatusCode == 0)
            {
                return StatusCode(500, "Error: Received status code 0 with an empty response body from the API.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                goto GetToken;
            }
            var res = JsonConvert.DeserializeObject<ExpandoObject>(response.Content, InvoiceHelper.JsonSettings);
            return StatusCode((int)response.StatusCode, res);
        }

        [HttpGet("[action]")]
        [Produces("application/json")]
        public async Task<IActionResult> GetDocumentDetails([FromHeader] string CompanyCode, [FromHeader] string EnvironmentId, string documentUUID)
        {
        //companyCode = Request.Headers["CompanyCode"].ToString();
        //environmentId = Request.Headers["EnvironmentId"].ToString();

        GetToken:
            var token = TokenHelper.GetToken(CompanyCode, EnvironmentId, out var tokenSettings, sysUIFormat);
            if (token.StartsWith("CANNOT"))
            {
                return Unauthorized("Cannot Get Token \r\n" + token);
            }

            IdentityBaseURL = tokenSettings?.EnvTokenUrl;
            SystemAPIBaseURL = tokenSettings?.EnvInvoicingUrl;

            var client = new RestClient(new RestClientOptions(SystemAPIBaseURL + "documents/" + documentUUID + "/details")
            {
                Timeout = TimeSpan.FromSeconds(30)
            });

            int maxRetries = 3;
            int attempt = 0;
            RestResponse? response = null;

            while (attempt < maxRetries)
            {
                attempt++;
                var request = new RestRequest(string.Empty, Method.Get);
                request.AddHeader("Authorization", "Bearer " + token);
                response = await client.ExecuteAsync(request);

                if ((int)response.StatusCode == 0 || response.StatusCode == (HttpStatusCode)429)
                {
                    await Task.Delay(2000 * attempt);
                    continue;
                }

                break;
            }

            if (response.Content is null || (int)response.StatusCode == 0)
            {
                return StatusCode(500, "Error: Received status code 0 with an empty response body from the API.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                goto GetToken;
            }
            var res = JsonConvert.DeserializeObject<ExpandoObject>(response.Content, InvoiceHelper.JsonSettings);
            return StatusCode((int)response.StatusCode, res);
        }

        [HttpGet("[action]")]
        public async Task<ActionResult<string>> GetDocumentErrors([FromHeader] string CompanyCode, [FromHeader] string EnvironmentId, string documentUUID)
        {
        //companyCode = Request.Headers["CompanyCode"].ToString();
        //environmentId = Request.Headers["EnvironmentId"].ToString();

        GetToken:
            var token = TokenHelper.GetToken(CompanyCode, EnvironmentId, out var tokenSettings, sysUIFormat);
            if (token.StartsWith("CANNOT"))
            {
                return Unauthorized("Cannot Get Token \r\n" + token);
            }

            IdentityBaseURL = tokenSettings?.EnvTokenUrl;
            SystemAPIBaseURL = tokenSettings?.EnvInvoicingUrl;

            var client = new RestClient(new RestClientOptions(SystemAPIBaseURL + "documents/" + documentUUID + "/details")
            {
                Timeout = TimeSpan.FromSeconds(30)
            });

            int maxRetries = 3;
            int attempt = 0;
            RestResponse? response = null;

            while (attempt < maxRetries)
            {
                attempt++;
                var request = new RestRequest(string.Empty, Method.Get);
                request.AddHeader("Authorization", "Bearer " + token);

                response = await client.ExecuteAsync(request);

                if ((int)response.StatusCode == 0 || response.StatusCode == (HttpStatusCode)429)
                {
                    await Task.Delay(2000 * attempt);
                    continue;
                }

                break;
            }

            if (response is null || (int)response.StatusCode == 0)
            {
                return StatusCode(500, "Error: Received status code 0 with an empty response body from the API.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                goto GetToken;
            }

            var ValidationResults = InvoiceValidationResult.FromJson(response.Content).ValidationResults;
            StringBuilder stringBuilder = new StringBuilder();
            foreach (var item in ValidationResults.ValidationSteps)
            {
                if (item.Error != null)
                {
                    if (item.Error.ErrorError != null && !stringBuilder.ToString().Contains(item.Error.ErrorError))
                    {
                        stringBuilder.AppendLine(item.Error.ErrorError);
                    }
                    foreach (var err in item.Error.InnerError)
                    {
                        if (err.ErrorError != null && !stringBuilder.ToString().Contains(err.ErrorError))
                        {
                            stringBuilder.AppendLine(err.ErrorError);
                        }
                    }
                }
            }
            return Ok(stringBuilder.ToString());
        }

        [HttpGet("[action]")]
        public async Task<ActionResult<string>> GetDocumentValid([FromHeader] string CompanyCode, [FromHeader] string EnvironmentId, string invoiceDocumentNo, [Optional] string? issueDateTime)
        {
        //companyCode = Request.Headers["CompanyCode"].ToString();
        //environmentId = Request.Headers["EnvironmentId"].ToString();

        GetToken:
            var token = TokenHelper.GetToken(CompanyCode, EnvironmentId, out var tokenSettings, sysUIFormat);
            if (token.StartsWith("CANNOT"))
            {
                return Unauthorized("Cannot Get Token \r\n" + token);
            }

            IdentityBaseURL = tokenSettings?.EnvTokenUrl;
            SystemAPIBaseURL = tokenSettings?.EnvInvoicingUrl;

            RestClient client = new RestClient();

            int maxRetries = 3;
            int attempt = 0;
            RestResponse? response = null;

            while (attempt < maxRetries)
            {
                attempt++;
                var request = new RestRequest("https://api.invoicing.eta.gov.eg/api/v1/documents/" + invoiceDocumentNo + "/details", Method.Get);
                request.AddHeader("Authorization", "Bearer " + token);
                response = await client.ExecuteAsync(request);

                if ((int)response.StatusCode == 0 || response.StatusCode == (HttpStatusCode)429)
                {
                    await Task.Delay(2000 * attempt);
                    continue;
                }

                break;
            }

            if (response is null || (int)response.StatusCode == 0)
            {
                return StatusCode(500, "Error: Received status code 0 with an empty response body from the API.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                goto GetToken;
            }

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var validationResults = InvoiceValidationResult.FromJson(response.Content).ValidationResults;
                foreach (var item in validationResults.ValidationSteps)
                {
                    if (item.StepId == "8" && item.Status == "Invalid")
                    {
                        string vUUID = item.Error.InnerError[0].ErrorError;
                        vUUID = vUUID[(vUUID.IndexOf(":") + 1)..];
                        return StatusCode((int)HttpStatusCode.OK, vUUID.Trim());
                    }
                }
                return StatusCode((int)HttpStatusCode.NotFound, "No Valid Documents");
            }
            var res = JsonConvert.DeserializeObject<ExpandoObject>(response.Content, InvoiceHelper.JsonSettings);
            return StatusCode(Convert.ToInt32(response.StatusCode), res);
        }

        [HttpGet("[action]")]
        public async Task<ActionResult<string>> GetVendorDocumentUUID([FromHeader] string CompanyCode, [FromHeader] string EnvironmentId, string vendorDocumentNo, string vendorVatRegNo)
        {
            RestClient client = new RestClient();
            long pageNo = 1;
        GetToken:
            var token = TokenHelper.GetToken(CompanyCode, EnvironmentId, out var tokenSettings, sysUIFormat);
            if (token.StartsWith("CANNOT"))
            {
                return Unauthorized("Cannot Get Token \r\n" + token);
            }

            IdentityBaseURL = tokenSettings?.EnvTokenUrl;
            SystemAPIBaseURL = tokenSettings?.EnvInvoicingUrl;

        ReadInvoices:
            int maxRetries = 3;
            int attempt = 0;
            RestResponse? response = null;

            while (attempt < maxRetries)
            {
                attempt++;
                var request = new RestRequest(SystemAPIBaseURL + "documents/recent?PageSize=100&PageNo=" + pageNo
                                              + "&direction=received&issuerId=" + vendorVatRegNo, Method.Get);
                request.AddHeader("Authorization", "Bearer " + token);

                response = await client.ExecuteAsync(request);

                if ((int)response.StatusCode == 0 || response.StatusCode == (HttpStatusCode)429)
                {
                    await Task.Delay(2000 * attempt);
                    continue;
                }

                break;
            }

            if (response is null || (int)response.StatusCode == 0)
            {
                return StatusCode(500, "Error: Received status code 0 with an empty response body from the API.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                goto GetToken;
            }

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var recentDocuments = RecentDocument.FromJson(response.Content);
                var pagesCount = recentDocuments.Metadata.TotalPages;

                if (recentDocuments.Result.Length > 0)
                {
                    var matchingDocs = recentDocuments.Result
                        .Where(item => item.InternalId == vendorDocumentNo)
                        .OrderByDescending(item => item.DateTimeReceived)
                        .ToArray();

                    if (matchingDocs.Length > 0)
                    {
                        return Ok(matchingDocs[0].Uuid);
                    }

                    if (pageNo < pagesCount)
                    {
                        pageNo++;
                        goto ReadInvoices;
                    }
                    return BadRequest("No Documents found");
                }
                return BadRequest("No Documents found");
            }
            var res = JsonConvert.DeserializeObject<ExpandoObject>(response.Content, InvoiceHelper.JsonSettings);
            return StatusCode(Convert.ToInt32(response.StatusCode), res);
        }

        [HttpGet("[action]")]
        public async Task<ActionResult<string>> CancelInvoice([FromHeader] string CompanyCode, [FromHeader] string EnvironmentId, string documentUUID)
        {
            RestClient client = new RestClient(new RestClientOptions(SystemAPIBaseURL + "documents/state/" + documentUUID + "/state")
            {
                Timeout = TimeSpan.FromSeconds(30)
            });

        GetToken:
            var token = TokenHelper.GetToken(CompanyCode, EnvironmentId, out var tokenSettings, sysUIFormat);
            if (token.StartsWith("CANNOT"))
            {
                return Unauthorized("Cannot Get Token \r\n" + token);
            }

            IdentityBaseURL = tokenSettings?.EnvTokenUrl;
            SystemAPIBaseURL = tokenSettings?.EnvInvoicingUrl;

            int maxRetries = 3;
            int attempt = 0;
            RestResponse? response = null;

            while (attempt < maxRetries)
            {
                attempt++;
                var request = new RestRequest(string.Empty, Method.Put);
                request.AddHeader("Content-Type", "application/json");
                request.AddHeader("Authorization", "Bearer " + token);
                request.AddStringBody("{\"status\":\"cancelled\",\"reason\":\"Wrong invoice details\"}", DataFormat.Json);

                response = await client.ExecuteAsync(request);

                if ((int)response.StatusCode == 0 || response.StatusCode == (HttpStatusCode)429)
                {
                    await Task.Delay(2000 * attempt);
                    continue;
                }

                break;
            }
            var res = JsonConvert.DeserializeObject<ExpandoObject>(response.Content, InvoiceHelper.JsonSettings);

            if (response is null || response.StatusCode == 0)
            {
                return StatusCode(500, "Error: Received status code 0 with an empty response body from the API.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                goto GetToken;
            }
            return BadRequest(res);
        }

        private object GetSlotLock(string slotSerial)
        {
            lock (SlotLocksDictLock)
            {
                if (!SlotLocks.TryGetValue(slotSerial, out var slotLock))
                {
                    slotLock = new object();
                    SlotLocks[slotSerial] = slotLock;
                }
                return slotLock;
            }
        }

        private string SignWithCms(byte[] data, bool runImpersonated, string CompanyCode)
        {
            string domainName = TokenManager.GetSettings("ImpDomain")?.ToString() ?? string.Empty;
            string userName = TokenManager.GetSettings("ImpUsername")?.ToString() ?? string.Empty;
            string password = TokenManager.GetSettings("ImpPassword")?.ToString() ?? string.Empty;

            const int logon32ProviderDefault = 0;
            const int logon32LogonInteractive = 2;

            SafeAccessTokenHandle safeAccessTokenHandle;
            bool returnValue = LogonUser(userName, domainName, password, logon32LogonInteractive, logon32ProviderDefault, out safeAccessTokenHandle);

            if (!returnValue)
            {
                int ret = Marshal.GetLastWin32Error();
                throw new System.ComponentModel.Win32Exception(ret);
            }

            Pkcs11InteropFactories factories = new Pkcs11InteropFactories();
            using (IPkcs11Library pkcs11Library = factories.Pkcs11LibraryFactory.LoadPkcs11Library(factories, DllLibPath, AppType.MultiThreaded))
            {
                var slots = pkcs11Library.GetSlotList(SlotsType.WithTokenPresent);
                ISlot? slot = slots.AsQueryable().SingleOrDefault(s => s.GetTokenInfo().SerialNumber == TokenSerial);
                if (slot is null)
                {
                    return "Error: No slots found";
                }
                using (var session = slot.OpenSession(SessionType.ReadWrite))
                {
                    var slotLock = GetSlotLock(TokenSerial);
                    lock (slotLock)
                    {
                        try
                        {
                            session.Login(CKU.CKU_USER, Encoding.UTF8.GetBytes(TokenPin));

                            var certificateSearchAttributes = new List<IObjectAttribute>
                            {
                                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_CERTIFICATE),
                                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, true),
                                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CERTIFICATE_TYPE, CKC.CKC_X_509)
                            };

                            IObjectHandle? certificate = session.FindAllObjects(certificateSearchAttributes).FirstOrDefault();

                            if (certificate is null)
                            {
                                return "Error: Certificate not found in token";
                            }

                            X509Certificate2? certForSigning = null;

                            certForSigning = _certificateCache.Get(CertSerialNum);

                            if (certForSigning == null)
                            {
                                if (runImpersonated)
                                {
                                    WindowsIdentity.RunImpersonated(
                                        safeAccessTokenHandle,
                                        () =>
                                        {
                                            X509Store store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                                            store.Open(OpenFlags.MaxAllowed);
                                            foreach (X509Certificate2 cert in store.Certificates)
                                            {
                                                if (cert.SerialNumber == CertSerialNum)
                                                {
                                                    certForSigning = cert;
                                                    _certificateCache.Set(CertSerialNum, certForSigning);
                                                    break;
                                                }
                                            }
                                            store.Close();
                                        }
                                    );
                                }
                                else
                                {
                                    X509Store store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                                    store.Open(OpenFlags.MaxAllowed);
                                    foreach (X509Certificate2 cert in store.Certificates)
                                    {
                                        if (cert.SerialNumber == CertSerialNum)
                                        {
                                            certForSigning = cert;
                                            _certificateCache.Set(CertSerialNum, certForSigning);
                                            break;
                                        }
                                    }
                                    store.Close();
                                }
                            }

                            if (certForSigning == null)
                            {
                                return "Error: Certificate not found in user certificates store";
                            }

                            if (DateTime.UtcNow > certForSigning.NotAfter.ToUniversalTime())
                            {
                                return $"Error: Certificate expired on {certForSigning.NotAfter.ToString("u")}";
                            }

                            var attributes = ParseSubjectAttributes(certForSigning);

                            string companyName = attributes.TryGetValue("CN", out var cn) ? cn : "Unknown";
                            string? vategValue = attributes.TryGetValue("OID.2.5.4.97", out var vategVal) ? vategVal : null;

                            string certCompanyCode = null;
                            if (vategValue != null && vategValue.StartsWith("VATEG-"))
                            {
                                certCompanyCode = vategValue.Substring("VATEG-".Length);
                            }

                            if (certCompanyCode == null)
                            {
                                return $"Error: VAT number not found in certificate";
                            }

                            if (!string.Equals(certCompanyCode, CompanyCode, StringComparison.Ordinal))
                            {
                                return $"Error: Certificate VAT number ({certCompanyCode}) does not match CompanyCode ({CompanyCode})\r\nCertificate related to company {companyName}";
                            }

                            var attributeValues = session.GetAttributeValue(certificate, new List<CKA> { CKA.CKA_VALUE });
                            if (attributeValues.Count == 0)
                            {
                                return "Error: Certificate value not found";
                            }
                            certForSigning = new X509Certificate2(attributeValues[0].GetValueAsByteArray());

                            certificateSearchAttributes = new List<IObjectAttribute>
                            {
                                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_PRIVATE_KEY),
                                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_KEY_TYPE, CKK.CKK_RSA)
                            };

                            IObjectHandle? privateKeyHandler = session.FindAllObjects(certificateSearchAttributes).LastOrDefault();

                            RSA privateKey = new TokenRSA(certForSigning, session, slot, privateKeyHandler);

                            ContentInfo content = new ContentInfo(new Oid("1.2.840.113549.1.7.5"), data);
                            SignedCms cms = new SignedCms(content, true);
                            EssCertIDv2 bouncyCertificate = new EssCertIDv2(
                                new Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier(new DerObjectIdentifier("1.2.840.113549.1.9.16.2.47")),
                                _hasher.HashBytes(certForSigning.RawData));

                            SigningCertificateV2 signerCertificateV2 = new SigningCertificateV2(new[] { bouncyCertificate });
                            CmsSigner signer = new CmsSigner(certForSigning)
                            {
                                PrivateKey = privateKey,
                                IncludeOption = X509IncludeOption.EndCertOnly,
                                DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1")
                            };

                            signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
                            signer.SignedAttributes.Add(new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.47"), signerCertificateV2.GetEncoded()));

                            cms.ComputeSignature(signer);
                            var output = cms.Encode();

                            return Convert.ToBase64String(output);
                        }
                        catch (Exception ex)
                        {
                            return "Error: Signing Error \r\n" + ex.Message + "\r\n" + ex.InnerException?.Message;
                        }
                        finally
                        {
                            try
                            {
                                session.Logout();
                                slot.CloseSession(session);
                            }
                            catch { }
                        }
                    }
                }
            }
        }

        private static Dictionary<string, string> ParseSubjectAttributes(X509Certificate2 cert)
        {
            var attributes = new Dictionary<string, string>();
            var dn = cert.SubjectName;
            string subject = dn.Name;

            foreach (var part in subject.Split(','))
            {
                var trimmed = part.Trim();
                int idx = trimmed.IndexOf('=');
                if (idx > 0 && idx < trimmed.Length - 1)
                {
                    string key = trimmed.Substring(0, idx).Trim();
                    string value = trimmed.Substring(idx + 1).Trim();
                    attributes[key] = value;
                }
            }
            return attributes;
        }

        [HttpGet("[action]")]
        public string ApiToken(string companyCode, string environmentId)
        {
            var token = TokenHelper.GetToken(companyCode, environmentId, out TokenSettings? tokenSettings, sysUIFormat);
            return token ?? string.Empty;
        }
    }
}