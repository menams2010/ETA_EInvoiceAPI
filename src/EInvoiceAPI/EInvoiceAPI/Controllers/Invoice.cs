using EInvoiceAPI.Models;
using EInvoiceAPI.Services;
using EInvoiceAPI.Helpers;
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
using System.Dynamic;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using System.Collections.Concurrent;

namespace EInvoiceAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class Invoice : ControllerBase
    {
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

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool LogonUser(
            string lpszUsername,
            string lpszDomain,
            string lpszPassword,
            int dwLogonType,
            int dwLogonProvider,
            out SafeAccessTokenHandle phToken);

        private (JObject invoice, string signedInvoice, string signValue) ParseAndSignInvoice(string requestBody, string companyCode, bool useXml, bool useImp)
        {
            if (useXml)
            {
                try
                {
                    InvoiceHelper.ConvertXmlToInvoiceObject(requestBody, out requestBody);
                }
                catch (Exception ex)
                {
                    throw new Exception("Error: Invalid XML format - " + ex.Message);
                }
            }

            var invoice = InvoiceHelper.DeserializeInvoice(requestBody);
            var serializedJson = _serializer.Serialize(invoice);
            string docVer = invoice["documentTypeVersion"]?.ToString() ?? string.Empty;

            TokenHelper.GetE_Sign(companyCode, out var tokenSerial, out var tokenPin, out var certSerialNum, out var useXmlFlag);

            string signValue = docVer.Equals("0.9") ? "ANY" : SignWithCms(Encoding.UTF8.GetBytes(serializedJson), useImp, companyCode, tokenSerial, tokenPin, certSerialNum);

            JObject jobject = new JObject
            {
                { "signatureType", "I" },
                { "value", signValue }
            };
            JArray jarray = new JArray { jobject };
            invoice.Add("signatures", jarray);
            string signedInvoice = "{\"documents\":[" + invoice.ToString() + "]}";
            return (invoice, signedInvoice, signValue);
        }

        [HttpPost("[action]")]
        [Produces("application/json")]
        public async Task<IActionResult> SignInvoice()
        {
            var companyCode = Request.Headers["CompanyCode"].ToString();
            string requestBody;
            using (var sr = new StreamReader(Request.Body))
                requestBody = await sr.ReadToEndAsync();

            bool useImp = Convert.ToBoolean(TokenManager.GetSettings("UseImpersonation") ?? false);
            TokenHelper.GetE_Sign(companyCode, out _, out _, out _, out var useXml);

            try
            {
                var (_, signedInvoice, signValue) = ParseAndSignInvoice(requestBody, companyCode, useXml, useImp);
                if (signValue.StartsWith("Error:"))
                    return StatusCode((int)HttpStatusCode.Locked, signValue);

                return new ContentResult
                {
                    StatusCode = (int)HttpStatusCode.OK,
                    Content = signedInvoice,
                    ContentType = "application/json"
                };
            }
            catch (Exception ex)
            {
                return StatusCode((int)HttpStatusCode.Locked, ex.Message);
            }
        }

        [HttpPost("[action]")]
        public async Task<IActionResult> SendInvoice()
        {
            var companyCode = Request.Headers["CompanyCode"].ToString();
            var environmentId = Request.Headers["EnvironmentId"].ToString();
            string requestBody;
            using (var sr = new StreamReader(Request.Body))
                requestBody = await sr.ReadToEndAsync();

            bool useImp = Convert.ToBoolean(TokenManager.GetSettings("UseImpersonation") ?? false);
            TokenHelper.GetE_Sign(companyCode, out _, out _, out _, out var useXml);

            try
            {
                var (_, signedInvoice, signValue) = ParseAndSignInvoice(requestBody, companyCode, useXml, useImp);
                if (signValue.StartsWith("Error:"))
                    return StatusCode((int)HttpStatusCode.Locked, signValue);

                var sysUIFormat = CultureInfo.CurrentUICulture.DateTimeFormat.ShortDatePattern + " " + CultureInfo.CurrentUICulture.DateTimeFormat.LongTimePattern;
                var token = TokenHelper.GetToken(companyCode, environmentId, out var tokenSettings, sysUIFormat);
                if (token == "CANNOT")
                    return Unauthorized("Cannot Get Token");

                var client = InvoiceHelper.SharedClient;
                client.BaseUrl = new Uri(tokenSettings?.EnvInvoicingUrl + "documentsubmissions");
                client.Timeout = 30000;
                var request = new RestRequest(Method.POST);
                request.AddHeader("Content-Type", "application/json");
                request.AddHeader("Authorization", "Bearer " + token);
                request.AddParameter("application/json", signedInvoice, ParameterType.RequestBody);
                var response = client.Execute(request);

                return StatusCode(Convert.ToInt32(response.StatusCode), response.Content);
            }
            catch (Exception ex)
            {
                return StatusCode((int)HttpStatusCode.Locked, ex.Message);
            }
        }

        [HttpGet("[action]")]
        [Produces("application/json")]
        public IActionResult GetDocumentDetails(string documentUUID)
        {
            var companyCode = Request.Headers["CompanyCode"].ToString();
            var environmentId = Request.Headers["EnvironmentId"].ToString();
            var sysUIFormat = CultureInfo.CurrentUICulture.DateTimeFormat.ShortDatePattern + " " + CultureInfo.CurrentUICulture.DateTimeFormat.LongTimePattern;
            var token = TokenHelper.GetToken(companyCode, environmentId, out var tokenSettings, sysUIFormat);
            if (token == "CANNOT")
                return Unauthorized("Cannot Get Token");

            var client = InvoiceHelper.SharedClient;
            client.BaseUrl = new Uri(tokenSettings?.EnvInvoicingUrl + "documents/" + documentUUID + "/details");
            client.Timeout = 30000;
            var request = new RestRequest(Method.GET);
            request.AddHeader("Authorization", "Bearer " + token);
            var response = client.Execute(request);
            var res = JsonConvert.DeserializeObject<ExpandoObject>(response.Content, InvoiceHelper.JsonSettings);
            return StatusCode(Convert.ToInt32(response.StatusCode), res);
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

        private string SignWithCms(byte[] data, bool runImpersonated, string CompanyCode, string? TokenSerial, string? TokenPin, string? CertSerialNum)
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
    }
}