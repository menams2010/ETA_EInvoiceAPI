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
using RestSharp;
using System.Data;
using System.Diagnostics.Eventing.Reader;
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
        RestClient client = new RestClient();
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
        string? companyCode;
        string? environmentId;
        string sysUIFormat = CultureInfo.CurrentUICulture.DateTimeFormat.ShortDatePattern + " " + CultureInfo.CurrentUICulture.DateTimeFormat.LongTimePattern;
        CultureInfo enUS = new CultureInfo("en-US");
        //const int SignError = 197458;
        const int SignError = (int)HttpStatusCode.Locked;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool LogonUser(String lpszUsername, String lpszDomain, String lpszPassword,
           int dwLogonType, int dwLogonProvider, out SafeAccessTokenHandle phToken);

        private readonly Serializer _serializer;
        private readonly Hasher _hasher;
        private readonly CertificateCache _certificateCache;

        private readonly string DllLibPath = "eps2003csp11.dll";
        private static readonly Dictionary<string, object> SlotLocks = new();
        private static readonly object SlotLocksDictLock = new();

        public Invoice(Serializer serializer, Hasher hasher, CertificateCache certificateCache)
        {
            _serializer = serializer;
            _hasher = hasher;
            _certificateCache = certificateCache;
        }

        private void GetE_Sign(string CompanyCode)
        {
            DataTable dtSign = TokenManager.getE_Signing(CompanyCode);
            if (dtSign.Rows.Count > 0)
            {
                TokenSerial = dtSign.Rows[0]["TokenSerial"].ToString();
                TokenPin = dtSign.Rows[0]["TokenPin"].ToString();
                CertSerialNum = dtSign.Rows[0]["CertSerialNum"].ToString();
                object useXmltojson = dtSign.Rows[0]["UseXMLTOJSON"];
                UseXML = useXmltojson != null && Convert.ToBoolean(useXmltojson);
            }
        }

        [HttpPost("[action]")]
        [Produces("application/json")]
        public async Task<IActionResult> SignInvoice()
        {
            companyCode = Request.Headers["CompanyCode"].ToString();
            string requestBody;
            using (var sr = new StreamReader(Request.Body))
            {
                requestBody = await sr.ReadToEndAsync();
            }

            object runImpersonated = TokenManager.GetSettings("UseImpersonation");
            UseIMP = runImpersonated != null && Convert.ToBoolean(runImpersonated);

            if (UseXML)
            {
                // Convert XML to JSON if the request body is in XML format

                try
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(document));
                    StringReader reader = new StringReader(requestBody);
                    object XmlInvoice = (document)serializer.Deserialize(reader);

                    if (((document)XmlInvoice).receiver.id == null)
                    {
                        ((document)XmlInvoice).receiver.id = string.Empty;
                    }

                    if (((document)XmlInvoice).documentType == "EI")
                    {
                        serializer = new XmlSerializer(typeof(export_document));
                        reader = new StringReader(requestBody);
                        XmlInvoice = (export_document)serializer.Deserialize(reader);
                        if (((export_document)XmlInvoice).receiver.id == null)
                        {
                            ((export_document)XmlInvoice).receiver.id = String.Empty;
                        }
                    }
                    requestBody = JsonConvert.SerializeObject(XmlInvoice);
                }
                catch (Exception ex)
                {
                    return StatusCode(SignError, "Error: Invalid XML format - " + ex.Message);
                }

                //try
                //{
                //    XmlDocument xmlDoc = new XmlDocument();
                //    xmlDoc.LoadXml(requestbody);

                //    XmlNode? receiverNode = xmlDoc.SelectSingleNode("//receiver");
                //    if (receiverNode != null)
                //    {
                //        XmlNode? typeNode = receiverNode.SelectSingleNode("type");
                //        if (typeNode != null)
                //        {
                //            XmlElement newTag = xmlDoc.CreateElement("id");
                //            newTag.InnerText = String.Empty;

                //            if (typeNode.NextSibling != null)
                //            {
                //                receiverNode.InsertAfter(newTag, typeNode);
                //            }
                //            else
                //            {
                //                receiverNode.AppendChild(newTag);
                //            }
                //        }
                //    }

                //    requestbody = JsonConvert.SerializeXmlNode(xmlDoc, Newtonsoft.Json.Formatting.None, true);
                //}
                //catch (Exception ex)
                //{
                //    return StatusCode(SignError, "Error: Invalid XML format - " + ex.Message);
                //}
            }

            var settings = new JsonSerializerSettings
            {
                FloatFormatHandling = FloatFormatHandling.String,
                FloatParseHandling = FloatParseHandling.Decimal,
                DateFormatHandling = DateFormatHandling.IsoDateFormat,
                DateParseHandling = DateParseHandling.None,
                Formatting = Newtonsoft.Json.Formatting.None
            };

            JObject? invoice = JsonConvert.DeserializeObject<JObject>(requestBody, settings);
            var serializedJson = _serializer.Serialize(invoice);

            string docVer = invoice["documentTypeVersion"]?.ToString() ?? string.Empty;

            GetE_Sign(companyCode);
            string signValue = docVer.Equals("0.9") ? "ANY" : SignWithCms(Encoding.UTF8.GetBytes(serializedJson), UseIMP, companyCode);
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

            return new ContentResult
            {
                StatusCode = (int)HttpStatusCode.OK,
                Content = signedInvoice,
                ContentType = "application/json"
            };
        }

        [HttpPost("[action]")]
        //public async Task<IActionResult> SendInvoice([FromBody] object body)
        public async Task<IActionResult> SendInvoice()
        {
            companyCode = Request.Headers["CompanyCode"].ToString();
            environmentId = Request.Headers["EnvironmentId"].ToString();
            string requestBody;
            using (var sr = new StreamReader(Request.Body))
            {
                requestBody = await sr.ReadToEndAsync();
            }

            object runImpersonated = TokenManager.GetSettings("UseImpersonation");
            UseIMP = runImpersonated != null && Convert.ToBoolean(runImpersonated);

            if (UseXML)
            {
                // Convert XML to JSON if the request body is in XML format

                try
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(document));
                    StringReader reader = new StringReader(requestBody);
                    object XmlInvoice = (document)serializer.Deserialize(reader);

                    if (((document)XmlInvoice).receiver.id == null)
                    {
                        ((document)XmlInvoice).receiver.id = string.Empty;
                    }

                    if (((document)XmlInvoice).documentType == "EI")
                    {
                        serializer = new XmlSerializer(typeof(export_document));
                        reader = new StringReader(requestBody);
                        XmlInvoice = (export_document)serializer.Deserialize(reader);
                        if (((export_document)XmlInvoice).receiver.id == null)
                        {
                            ((export_document)XmlInvoice).receiver.id = String.Empty;
                        }
                    }
                    requestBody = JsonConvert.SerializeObject(XmlInvoice);
                }
                catch (Exception ex)
                {
                    return StatusCode(SignError, "Error: Invalid XML format - " + ex.Message);
                }
            }

            var settings = new JsonSerializerSettings
            {
                FloatFormatHandling = FloatFormatHandling.String,
                FloatParseHandling = FloatParseHandling.Decimal,
                DateFormatHandling = DateFormatHandling.IsoDateFormat,
                DateParseHandling = DateParseHandling.None,
                Formatting = Newtonsoft.Json.Formatting.None
            };

            JObject? invoice = JsonConvert.DeserializeObject<JObject>(requestBody, settings);
            var serializedJson = _serializer.Serialize(invoice);

            string docVer = invoice["documentTypeVersion"]?.ToString() ?? string.Empty;

            GetE_Sign(companyCode);
            string signValue = docVer.Equals("0.9") ? "ANY" : SignWithCms(Encoding.UTF8.GetBytes(serializedJson), UseIMP, companyCode);
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

        GetToken:
            string? token = GetToken(companyCode, environmentId);

            client.BaseUrl = new Uri(SystemAPIBaseURL + "documentsubmissions");
            client.Timeout = 30000;
            var request = new RestRequest(Method.POST);
            request.AddHeader("Content-Type", "application/json");
            request.AddHeader("Authorization", "Bearer " + token);
            request.AddParameter("application/json", signedInvoice, ParameterType.RequestBody);
            IRestResponse response = client.Execute(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (token == "CANNOT")
                {
                    return Unauthorized("Cannot Get Token");
                }
                else
                {
                    goto GetToken;
                }
            }

            return StatusCode(Convert.ToInt32(response.StatusCode), response.Content);
        }


        [HttpGet("[action]")]
        public ActionResult<string> GetSubmission(string submitionUUID)
        {
            companyCode = Request.Headers["CompanyCode"];
            environmentId = Request.Headers["EnvironmentId"];
        GetToken:
            string? token = GetToken(companyCode, environmentId);
            client.BaseUrl = new Uri(SystemAPIBaseURL + "documentSubmissions/" + submitionUUID);
            client.Timeout = 30000;
            var request = new RestRequest(Method.GET);
            request.AddHeader("PageSize", "10");
            request.AddHeader("PageNo", "1");
            request.AddHeader("Authorization", "Bearer " + token);
            IRestResponse response = client.Execute(request);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return Ok(response.Content);
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (token == "CANNOT")
                {
                    return Unauthorized("Cannot Get Token");
                }
                else
                {
                    goto GetToken;
                }
            }
            else
            {
                return BadRequest(response.Content);
            }
        }


        [HttpGet("[action]")]
        [Produces("application/json")]
        public IActionResult GetDocumentDetails(string documentUUID)
        {
            companyCode = Request.Headers["CompanyCode"].ToString();
            environmentId = Request.Headers["EnvironmentId"].ToString();
        GetToken:
            string? token = GetToken(companyCode, environmentId);
            client.BaseUrl = new Uri(SystemAPIBaseURL + "documents/" + documentUUID + "/details");
            client.Timeout = 30000;
            var request = new RestRequest(Method.GET);
            request.AddHeader("Authorization", "Bearer " + token);
            IRestResponse response = client.Execute(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (token == "CANNOT")
                {
                    return Unauthorized("Cannot Get Token");
                }
                else
                {
                    goto GetToken;
                }
            }
            else
            {
                JsonSerializerSettings? settings = new JsonSerializerSettings();
                settings.FloatFormatHandling = FloatFormatHandling.String;
                settings.FloatParseHandling = FloatParseHandling.Decimal;
                settings.DateFormatHandling = DateFormatHandling.IsoDateFormat;
                settings.DateParseHandling = DateParseHandling.None;
                settings.Formatting = Newtonsoft.Json.Formatting.None;

                var res = JsonConvert.DeserializeObject<ExpandoObject>(response.Content, settings);
                return StatusCode(Convert.ToInt32(response.StatusCode), res);
            }
        }

        [HttpGet("[action]")]
        public ActionResult<string> GetDocumentErrors(string documentUUID)
        {
            companyCode = Request.Headers["CompanyCode"].ToString();
            environmentId = Request.Headers["EnvironmentId"].ToString();
        GetToken:
            string? token = GetToken(companyCode, environmentId);
            client.BaseUrl = new Uri(SystemAPIBaseURL + "documents/" + documentUUID + "/details");
            client.Timeout = 30000;
            var request = new RestRequest(Method.GET);
            request.AddHeader("Authorization", "Bearer " + token);
            IRestResponse response = client.Execute(request);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var ValidationResults = InvoiceValidationResult.FromJson(response.Content).ValidationResults;
                StringBuilder stringBuilder = new StringBuilder();
                foreach (var item in ValidationResults.ValidationSteps)
                {
                    if (item.Error != null)
                    {
                        if (item.Error.ErrorError != null)
                        {
                            if (!stringBuilder.ToString().Contains(item.Error.ErrorError))
                            {
                                stringBuilder.AppendLine(item.Error.ErrorError);
                            }
                        }
                        foreach (var err in item.Error.InnerError)
                        {
                            if (item.Error.ErrorError != null)
                            {
                                if (!stringBuilder.ToString().Contains(err.ErrorError))
                                {
                                    stringBuilder.AppendLine(err.ErrorError);
                                }
                            }
                        }
                    }
                }
                return Ok(stringBuilder.ToString());
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (token == "CANNOT")
                {
                    return Unauthorized("Cannot Get Token");
                }
                else
                {
                    goto GetToken;
                }
            }
            else
            {
                return BadRequest(response.Content);
            }
        }

        [HttpGet("[action]")]
        public ActionResult<string> GetDocumentValid([FromHeader] string CompanyCode, [FromHeader] string EnvironmentId, string invoiceDocumentNo, [Optional] string? issueDateTime)
        {
            companyCode = Request.Headers["CompanyCode"].ToString();
            environmentId = Request.Headers["EnvironmentId"].ToString();
        GetToken:
            string? token = GetToken(companyCode, environmentId);
            client = new RestClient();
            var request = new RestRequest("https://api.invoicing.eta.gov.eg/api/v1/documents/" + invoiceDocumentNo + "/details");
            request.Method = Method.GET;
            request.AddHeader("Authorization", "Bearer " + token);
            IRestResponse response = client.Execute(request);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var validationResults = InvoiceValidationResult.FromJson(response.Content).ValidationResults;
                foreach (var item in validationResults.ValidationSteps)
                {
                    if (item.StepId == "8" && item.Status == "Invalid")
                    {
                        string vUUID = item.Error.InnerError[0].ErrorError;
                        vUUID = vUUID.Substring(vUUID.IndexOf(":") + 1);
                        return StatusCode((int)HttpStatusCode.OK, vUUID.Trim());
                    }
                }
                return StatusCode((int)HttpStatusCode.NotFound, "No Valid Documents");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (token == "CANNOT")
                {
                    return Unauthorized("Cannot Get Token");
                }

                goto GetToken;
            }

            return StatusCode(Convert.ToInt32(response.StatusCode), response.Content);
        }

        [HttpGet("[action]")]
        public ActionResult<string> GetVendorDocumentUUID(string vendorDocumentNo, string vendorVatRegNo)
        {
            companyCode = Request.Headers["CompanyCode"].ToString();
            environmentId = Request.Headers["EnvironmentId"].ToString();
            long pageNo = 1;
        GetToken:
            string? token = GetToken(companyCode, environmentId);
        ReadInvoices:
            var request = new RestRequest(SystemAPIBaseURL + "documents/recent?PageSize=100&PageNo=" + pageNo + "&direction=received&issuerId=" + vendorVatRegNo);
            request.Method = Method.GET;
            request.AddHeader("Authorization", "Bearer " + token);
            IRestResponse response = client.Execute(request);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var recentDocuments = RecentDocument.FromJson(response.Content);
                var pagesCount = recentDocuments.Metadata.TotalPages;
                if (recentDocuments.Result.Length > 0)
                {
                    if (recentDocuments.Result.Where(item => item.InternalId == vendorDocumentNo).ToArray().Length > 0)
                    {
                        return Ok(recentDocuments.Result.Where(item => item.InternalId == vendorDocumentNo).OrderByDescending(item => item.DateTimeReceived).ToArray()[0].Uuid);
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
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (token == "CANNOT")
                {
                    return Unauthorized("Cannot Get Token");
                }

                goto GetToken;
            }
            return StatusCode(Convert.ToInt32(response.StatusCode), response.Content);
        }

        [HttpGet("[action]")]
        public ActionResult<string> CancelInvoice(string documentUUID)
        {
            companyCode = Request.Headers["CompanyCode"].ToString();
            environmentId = Request.Headers["EnvironmentId"].ToString();
        GetToken:
            string? token = GetToken(companyCode, environmentId);
            client.BaseUrl = new Uri(SystemAPIBaseURL + "documents/state/" + documentUUID + "/state");
            client.Timeout = 30000;
            var request = new RestRequest(Method.PUT);
            request.AddHeader("Content-Type", "application/json");
            request.AddHeader("Authorization", "Bearer " + token);
            request.AddParameter("application/json", "{\"status\":\"cancelled\",\"reason\":\"Wrong invoice details\"}", ParameterType.RequestBody);
            IRestResponse response = client.Execute(request);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return Ok(response.Content);
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (token == "CANNOT")
                {
                    return Unauthorized("Cannot Get Token");
                }
                else
                {
                    goto GetToken;
                }
            }
            else if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                return BadRequest(response.Content);
            }
            else
            {
                return Forbid(response.Content);
            }

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

                            // Try to get from cache
                            certForSigning = _certificateCache.Get(CertSerialNum);

                            if (certForSigning == null)
                            {
                                // Not in cache, use original code to get from store (with or without impersonation)
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
                                                    // Add to cache
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
                                            // Add to cache
                                            _certificateCache.Set(CertSerialNum, certForSigning);
                                            break;
                                        }
                                    }
                                    store.Close();
                                }
                            }

                            // If still not found, return error as before
                            if (certForSigning == null)
                            {
                                return "Error: Certificate not found in user certificates store";
                            }

                            // Check if the certificate is expired
                            if (DateTime.UtcNow > certForSigning.NotAfter.ToUniversalTime())
                            {
                                return $"Error: Certificate expired on {certForSigning.NotAfter.ToString("u")}";
                            }

                            // Validate VATEG number in certificate subject against CompanyCode
                            var attributes = ParseSubjectAttributes(certForSigning);

                            string companyName = attributes.TryGetValue("CN", out var cn) ? cn : "Unknown";
                            string? vategValue = attributes.TryGetValue("OID.2.5.4.97", out var vategVal) ? vategVal : null;

                            // Remove "VATEG-" prefix to get pure company code
                            string certCompanyCode = null;
                            if (vategValue != null && vategValue.StartsWith("VATEG-"))
                            {
                                certCompanyCode = vategValue.Substring("VATEG-".Length);
                            }

                            // Error if OID.2.5.4.97 is missing or doesn't start with VATEG-
                            if (certCompanyCode == null)
                            {
                                return $"Error: VAT number not found in certificate";
                            }

                            // Compare to CompanyCode
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
                            catch { /* Ignore logout errors */ }
                        }
                    }
                }
            }
        }

        private static Dictionary<string, string> ParseSubjectAttributes(X509Certificate2 cert)
        {
            var attributes = new Dictionary<string, string>();
            var dn = cert.SubjectName; // X500DistinguishedName
            string subject = dn.Name; // Canonical, comma-separated

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
        GetToken:
            tryCount++;
            tokenTable = TokenManager.GetToken(companyCode, environmentId);
            if (tokenTable.Rows.Count != 1)
            {
                return "CANNOT";
            }

            IdentityBaseURL = tokenTable.Rows[0]["EnvTokenUrl"].ToString();
            SystemAPIBaseURL = tokenTable.Rows[0]["EnvInvoicingUrl"].ToString();
            ClientId = tokenTable.Rows[0]["ClientId"].ToString();
            ClientSecret = tokenTable.Rows[0]["ClientSecret"].ToString();
            var dateTimeFormat = TokenManager.GetSettings("DateTimeFormat").ToString();
            if (tokenTable.Rows[0]["time_get"].Equals(DBNull.Value))
            {
                tokenTable.Rows[0]["time_get"] = DateTime.Now.AddDays(-1);
                dateTimeFormat = sysUIFormat;
                TokenManager.SaveSettings("DateTimeFormat", sysUIFormat);
            }
            var tokenDate = DateTime.ParseExact(tokenTable.Rows[0]["time_get"].ToString(), dateTimeFormat, null);
            TimeSpan timeDiff = Convert.ToDateTime(DateTime.Now, enUS) - Convert.ToDateTime(tokenDate, enUS);
            if (timeDiff.TotalSeconds > 3000)
            {
                client = new RestClient(IdentityBaseURL);
                client.Timeout = 30000;
                var tokenRequest = new RestRequest(Method.POST);
                tokenRequest.AddHeader("Content-Type", "application/x-www-form-urlencoded");
                tokenRequest.AddParameter("grant_type", "client_credentials");
                tokenRequest.AddParameter("client_id", ClientId);
                tokenRequest.AddParameter("client_secret", ClientSecret);
                tokenRequest.AddParameter("scope", "InvoicingAPI");
                IRestResponse tokenResponse = client.Execute(tokenRequest);
                if (tokenResponse.StatusCode != HttpStatusCode.OK)
                {
                    if (tryCount < 3)
                    {
                        goto GetToken;
                    }
                    else
                    {
                        return "CANNOT";
                    }
                }
                tryCount = 0;
                TokenManager.SaveToken(Token.FromJson(tokenResponse.Content).AccessToken, DateTime.Now.ToString(), companyCode, environmentId);
                TokenManager.SaveSettings("DateTimeFormat", sysUIFormat);
                return Token.FromJson(tokenResponse.Content).AccessToken;
            }
            return tokenTable.Rows[0]["str_token"].ToString();
        }


        private string? GetToken(string companyCode, string environmentId)
        {
        GetToken:
            tryCount++;
            tokenTable = TokenManager.GetToken(companyCode, environmentId);
            if (tokenTable.Rows.Count != 1)
            {
                return "CANNOT";
            }

            IdentityBaseURL = tokenTable.Rows[0]["EnvTokenUrl"].ToString();
            SystemAPIBaseURL = tokenTable.Rows[0]["EnvInvoicingUrl"].ToString();
            ClientId = tokenTable.Rows[0]["ClientId"].ToString();
            ClientSecret = tokenTable.Rows[0]["ClientSecret"].ToString();
            var dateTimeFormat = TokenManager.GetSettings("DateTimeFormat").ToString();
            if (tokenTable.Rows[0]["time_get"].Equals(DBNull.Value))
            {
                tokenTable.Rows[0]["time_get"] = DateTime.Now.AddDays(-1);
                dateTimeFormat = sysUIFormat;
                TokenManager.SaveSettings("DateTimeFormat", sysUIFormat);
            }
            var tokenDate = DateTime.ParseExact(tokenTable.Rows[0]["time_get"].ToString(), dateTimeFormat, null);
            TimeSpan timeDiff = Convert.ToDateTime(DateTime.Now, enUS) - Convert.ToDateTime(tokenDate, enUS);
            if (timeDiff.TotalSeconds > 3000)
            {
                client = new RestClient(IdentityBaseURL);
                client.Timeout = 30000;
                var tokenRequest = new RestRequest(Method.POST);
                tokenRequest.AddHeader("Content-Type", "application/x-www-form-urlencoded");
                tokenRequest.AddParameter("grant_type", "client_credentials");
                tokenRequest.AddParameter("client_id", ClientId);
                tokenRequest.AddParameter("client_secret", ClientSecret);
                tokenRequest.AddParameter("scope", "InvoicingAPI");
                IRestResponse tokenResponse = client.Execute(tokenRequest);
                if (tokenResponse.StatusCode != HttpStatusCode.OK)
                {
                    if (tryCount < 3)
                    {
                        goto GetToken;
                    }
                    else
                    {
                        return "CANNOT";
                    }
                }
                tryCount = 0;
                TokenManager.SaveToken(Token.FromJson(tokenResponse.Content).AccessToken, DateTime.Now.ToString(), companyCode, environmentId);
                TokenManager.SaveSettings("DateTimeFormat", sysUIFormat);
                return Token.FromJson(tokenResponse.Content).AccessToken;
            }
            return tokenTable.Rows[0]["str_token"].ToString();
        }

    }
}