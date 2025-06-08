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
using System.Globalization;
using System.Net;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
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
        private bool UseXML;
        private bool UseIMP;
        string sysUIFormat = CultureInfo.CurrentUICulture.DateTimeFormat.ShortDatePattern + " " + CultureInfo.CurrentUICulture.DateTimeFormat.LongTimePattern;
        CultureInfo enUS = new CultureInfo("en-US");
        //const int SignError = 197458;
        const int SignError = (int)HttpStatusCode.Locked;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool LogonUser(String lpszUsername, String lpszDomain, String lpszPassword,
           int dwLogonType, int dwLogonProvider, out SafeAccessTokenHandle phToken);

        private readonly Serializer _serializer;
        private readonly Hasher _hasher;

        private readonly string DllLibPath = "eps2003csp11.dll";

        public Invoice(Serializer serializer, Hasher hasher)
        {
            _serializer = serializer;
            _hasher = hasher;
        }

        private void GetE_Sign(string CompanyCode)
        {
            DataTable dtSign = TokenManager.getE_Signing(Request.Headers["CompanyCode"]);
            if (dtSign.Rows.Count > 0)
            {
                TokenSerial = dtSign.Rows[0]["TokenSerial"].ToString();
                TokenPin = dtSign.Rows[0]["TokenPin"].ToString();
                CertSerialNum = dtSign.Rows[0]["CertSerialNum"].ToString();
            }
        }

        [HttpPost("[action]")]
        [Produces("application/json")]
        public async Task<IActionResult> SignInvoice()
        {
            string requestbody;
            using (var sr = new StreamReader(Request.Body))
            {
                requestbody = await sr.ReadToEndAsync();
            }

            object useXmltojson = TokenManager.GetSettings("UseXMLTOJSON");
            object runImpersonated = TokenManager.GetSettings("UseImpersonation");

            UseXML = useXmltojson != null && Convert.ToBoolean(useXmltojson);
            UseIMP = runImpersonated != null && Convert.ToBoolean(runImpersonated);

            if (UseXML)
            {
                XmlSerializer serializer = new XmlSerializer(typeof(document));
                StringReader reader = new StringReader(requestbody);
                object XmlInvoice = (document)serializer.Deserialize(reader);

                if (((document)XmlInvoice).receiver.id == null)
                {
                    ((document)XmlInvoice).receiver.id = string.Empty;
                }

                requestbody = JsonConvert.SerializeObject(XmlInvoice);
            }

            var settings = new JsonSerializerSettings
            {
                FloatFormatHandling = FloatFormatHandling.String,
                FloatParseHandling = FloatParseHandling.Decimal,
                DateFormatHandling = DateFormatHandling.IsoDateFormat,
                DateParseHandling = DateParseHandling.None,
                Formatting = Formatting.None
            };

            JObject? invoice = JsonConvert.DeserializeObject<JObject>(requestbody, settings);
            var serializedJson = _serializer.Serialize(invoice);

            string docVer = invoice["documentTypeVersion"]?.ToString() ?? string.Empty;

            GetE_Sign(Request.Headers["CompanyCode"]);
            string signValue = (docVer.Equals("0.9") ? "ANY" : SignWithCms(Encoding.UTF8.GetBytes(serializedJson), UseIMP));
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

        private string SignWithCms(byte[] data, bool runImpersonated)
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

                        X509Certificate2 certForSigning = null;
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
                                }
                            }
                            store.Close();
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

                        if (certForSigning is null)
                        {
                            return "Error: Certificate not found in user certificates store";
                        }

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
                        session.Logout();
                        slot.CloseSession(session);
                    }
                }
            }
        }
    }
}