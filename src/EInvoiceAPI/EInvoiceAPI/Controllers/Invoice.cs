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
        //public async Task<IActionResult> SignInvoice([FromHeader] string CompanyCode, [FromHeader] string EnvironmentId, [FromBody] object body)
        public async Task<IActionResult> SignInvoice()
        {
            StreamReader sr = new StreamReader(Request.Body);
            string requestbody = await sr.ReadToEndAsync();
            //string requestbody = body.ToString(); //await sr.ReadToEndAsync();
            //var hashed = _hasher.Hash(requestbody);
            object useXmltojson = TokenManager.GetSettings("UseXMLTOJSON");
            object runImpersonated = TokenManager.GetSettings("UseImpersonation");

            if (useXmltojson is null)
            {
                UseXML = false;
            }
            else
            {
                UseXML = Convert.ToBoolean(useXmltojson);
            }

            if (runImpersonated is null)
            {
                UseIMP = false;
            }
            else
            {
                UseIMP = Convert.ToBoolean(runImpersonated);
            }

            if (UseXML == true)
            {
                //- Convert received XML to JSON - Start

                XmlSerializer serializer = new XmlSerializer(typeof(document));
                StringReader reader = new StringReader(requestbody);
                object XmlInvoice = (document)serializer.Deserialize(reader);

                if (((document)XmlInvoice).receiver.id == null)
                {
                    ((document)XmlInvoice).receiver.id = String.Empty;
                }


                //if (((document)XmlInvoice).documentType == "EI")
                //{
                //    serializer = new XmlSerializer(typeof(export_document));
                //    reader = new StringReader(requestbody);
                //    XmlInvoice = (export_document)serializer.Deserialize(reader);
                //    if (((export_document)XmlInvoice).receiver.id == null)
                //    {
                //        ((export_document)XmlInvoice).receiver.id = String.Empty;
                //    }
                //}

                requestbody = Newtonsoft.Json.JsonConvert.SerializeObject(XmlInvoice);
            }
            //- Convert received XML to JSON - END

            //var hashed = _hasher.Hash(requestbody);

            JsonSerializerSettings? settings = new JsonSerializerSettings();
            settings.FloatFormatHandling = FloatFormatHandling.String;
            settings.FloatParseHandling = FloatParseHandling.Decimal;
            settings.DateFormatHandling = DateFormatHandling.IsoDateFormat;
            settings.DateParseHandling = DateParseHandling.None;
            settings.Formatting = Newtonsoft.Json.Formatting.None;

            JObject? invoice = JsonConvert.DeserializeObject<JObject>(requestbody, settings);
            //var issuer_id = invoice.SelectToken("documents[0].issuer.id").Value<string>();

            var serializedJson = _serializer.Serialize(invoice);

            string docVer = invoice["documentTypeVersion"].ToString();

            GetE_Sign(Request.Headers["CompanyCode"]);
            string signValue = (docVer.Equals("0.9") ? "ANY" : SignWithCms(Encoding.UTF8.GetBytes(serializedJson), UseIMP));
            if (signValue.StartsWith("Error:"))
            {
                //return StatusCode(Convert.ToInt32(HttpStatusCode.BadRequest), signValue);
                return StatusCode(SignError, signValue);
            }

            JObject jobject = new JObject(new object[2]
                {
                    (object) new JProperty("signatureType", (object) "I"),
                    (object) new JProperty("value",(object)signValue)
                });
            JArray jarray = new JArray();
            jarray.Add((JToken)jobject);
            invoice.Add("signatures", (JToken)jarray);
            string signedInvoice = "{\"documents\":[" + ((object)invoice).ToString() + "]}";

            return new ContentResult
            {
                StatusCode = (int)HttpStatusCode.OK,
                Content = signedInvoice,
                ContentType = "application/json"
            };
        }

        private string SignWithCms(byte[] data, bool runImpersonated)
        {
            //int tries = 3;
            //bool is_logged = false;
            if (runImpersonated)
            {
                string domainName = TokenManager.GetSettings("ImpDomain").ToString();
                string userName = TokenManager.GetSettings("ImpUsername").ToString();
                string password = TokenManager.GetSettings("ImpPassword").ToString();

                const int logon32ProviderDefault = 0;
                //This parameter causes LogonUser to create a primary token. 
                const int logon32LogonInteractive = 2;

                // Call LogonUser to obtain a handle to an access token. 
                SafeAccessTokenHandle safeAccessTokenHandle;
                bool returnValue = LogonUser(userName, domainName, password, logon32LogonInteractive, logon32ProviderDefault, out safeAccessTokenHandle);

                if (false == returnValue)
                {
                    int ret = Marshal.GetLastWin32Error();
                    throw new System.ComponentModel.Win32Exception(ret);
                }

                Pkcs11InteropFactories factories = new Pkcs11InteropFactories();
                using (IPkcs11Library pkcs11Library = factories.Pkcs11LibraryFactory.LoadPkcs11Library(factories, DllLibPath, AppType.MultiThreaded))
                {
                    var slots = pkcs11Library.GetSlotList(SlotsType.WithTokenPresent);
                    ISlot? slot = pkcs11Library.GetSlotList(SlotsType.WithTokenPresent).AsQueryable().Single(slot => slot.GetTokenInfo().SerialNumber == TokenSerial);
                    if (slot is null)
                    {
                        return "Error: No slots found";
                    }

                    //ITokenInfo tokenInfo = slot.GetTokenInfo();

                    //ISlotInfo slotInfo = slot.GetSlotInfo();

                    //slot.CloseAllSessions();

                    //SignAgain:
                    using (var session = slot.OpenSession(SessionType.ReadWrite))
                    {
                        try
                        {
                            //if (is_logged == false)
                            //{
                            session.Login(CKU.CKU_USER, Encoding.UTF8.GetBytes(TokenPin));
                            //}
                            //is_logged = false;

                            var certificateSearchAttributes = new List<IObjectAttribute>()
                            {
                                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_CERTIFICATE),
                                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, true),
                                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CERTIFICATE_TYPE, CKC.CKC_X_509)
                            };

                            IObjectHandle? certificate = session.FindAllObjects(certificateSearchAttributes).FirstOrDefault();

                            if (certificate is null)
                            {
                                return "Error: Certificate not found";
                            }

                            X509Certificate2 certForSigning = null;
                            WindowsIdentity.RunImpersonated(
                              safeAccessTokenHandle,
                              // User action
                              () =>
                              {
                                  string currentUser = WindowsIdentity.GetCurrent().Name;
                                  X509Store store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                                  store.Open(OpenFlags.MaxAllowed);

                                  // find cert by thumbprint
                                  //var foundCerts = store.Certificates.Find(X509FindType.FindByIssuerName, "Egypt Trust Sealing CA", true);

                                  //if (foundCerts.Count == 0)
                                  //   return "no device detected";

                                  //certForSigning = foundCerts[0];

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
                            ContentInfo content = new ContentInfo(new Oid("1.2.840.113549.1.7.5"), data);


                            SignedCms cms = new SignedCms(content, true);


                            EssCertIDv2 bouncyCertificate = new EssCertIDv2(new Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier(new DerObjectIdentifier("1.2.840.113549.1.9.16.2.47")), _hasher.HashBytes(certForSigning.RawData));

                            SigningCertificateV2 signerCertificateV2 = new SigningCertificateV2(new EssCertIDv2[] { bouncyCertificate });


                            CmsSigner signer = new CmsSigner(certForSigning)
                            {
                                IncludeOption = X509IncludeOption.EndCertOnly,
                                DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1")
                            };


                            signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
                            signer.SignedAttributes.Add(new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.47"), signerCertificateV2.GetEncoded()));

                            cms.ComputeSignature(signer);
                            var output = cms.Encode();
                            //session.Logout();
                            slot.CloseSession(session);

                            if (output == null)
                            {
                                //if (tries < 0)
                                //{
                                //    tries--;
                                //if (session != null)
                                if (session.SessionId > 0)
                                {
                                    slot.CloseSession(session);
                                }
                                //is_logged = true;
                                //goto SignAgain;
                                //}
                            }
                            //if (session != null)
                            if (session.SessionId > 0)
                            {
                                slot.CloseSession(session);
                            }
                            return Convert.ToBase64String(output);

                        }
                        catch (Exception ex)
                        {
                            //if (tries > 0)
                            //{
                            //    tries--;
                            //if (session != null)
                            if (session.SessionId > 0)
                            {
                                slot.CloseSession(session);
                            }
                            //is_logged = true;
                            //goto SignAgain;
                            //}
                            //if (session != null)
                            if (session.SessionId > 0)
                            {
                                slot.CloseSession(session);
                            }
                            return "Error: Signing Error \r\n" + ex.Message + "\r\n" + ex.InnerException;
                        }
                        finally
                        {
                            //tries--;
                            //slot.CloseAllSessions();
                            //if (session != null)
                            if (session.SessionId > 0)
                            {
                                slot.CloseSession(session);
                            }
                        }
                    }
                }
            }
            else
            {
                Pkcs11InteropFactories factories = new Pkcs11InteropFactories();
                using (IPkcs11Library pkcs11Library = factories.Pkcs11LibraryFactory.LoadPkcs11Library(factories, DllLibPath, AppType.MultiThreaded))
                {
                    var slots = pkcs11Library.GetSlotList(SlotsType.WithTokenPresent);
                    ISlot? slot = pkcs11Library.GetSlotList(SlotsType.WithTokenPresent).AsQueryable().Single(slot => slot.GetTokenInfo().SerialNumber == TokenSerial);
                    if (slot is null)
                    {
                        return "Error: No slots found";
                    }

                    //ITokenInfo tokenInfo = slot.GetTokenInfo();
                    //ISlotInfo slotInfo = slot.GetSlotInfo();

                    X509Certificate2? certForSigning = null;

                    //SignAgain2:
                    using (var session = slot.OpenSession(SessionType.ReadWrite))
                    {
                        try
                        {
                            //if (is_logged == false)
                            //{
                            session.Login(CKU.CKU_USER, Encoding.UTF8.GetBytes(TokenPin));
                            //}
                            //is_logged = false;

                            var certificateSearchAttributes = new List<IObjectAttribute>()
                        {
                            session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_CERTIFICATE),
                            session.Factories.ObjectAttributeFactory.Create(CKA.CKA_TOKEN, true),
                            session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CERTIFICATE_TYPE, CKC.CKC_X_509)
                        };

                            IObjectHandle? certificate = session.FindAllObjects(certificateSearchAttributes).FirstOrDefault();

                            if (certificate is null)
                            {
                                return "Error: Certificate not found";
                            }

                            X509Store store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                            store.Open(OpenFlags.MaxAllowed);

                            // find cert by thumbprint
                            //var foundCerts = store.Certificates.Find(X509FindType.FindByIssuerName, "Egypt Trust Sealing CA", true);

                            //if (foundCerts.Count == 0)
                            //   return "no device detected";

                            //certForSigning = foundCerts[0];

                            foreach (X509Certificate2 cert in store.Certificates)
                            {
                                if (cert.SerialNumber == CertSerialNum)
                                {
                                    certForSigning = cert;
                                }
                            }

                            if (certificate is null)
                            {
                                return "Error: Certificate not found";
                            }


                            //New Way Sign Begin/06.09.2023/
                            var attributeValues = session.GetAttributeValue(certificate, new List<CKA>
                            {
                                CKA.CKA_VALUE
                            });


                            certForSigning = new X509Certificate2(attributeValues[0].GetValueAsByteArray());

                            certificateSearchAttributes = new List<IObjectAttribute>()
                            {
                                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_CLASS, CKO.CKO_PRIVATE_KEY),
                                session.Factories.ObjectAttributeFactory.Create(CKA.CKA_KEY_TYPE,CKK.CKK_RSA)
                            };

                            IObjectHandle? privateKeyHandler = session.FindAllObjects(certificateSearchAttributes).LastOrDefault();

                            RSA privateKey = new TokenRSA(certForSigning, session, slot, privateKeyHandler);
                            //New Way Sign End/06.09.2023/

                            store.Close();
                            //session.Logout();
                            slot.CloseSession(session);

                            ContentInfo content = new ContentInfo(new Oid("1.2.840.113549.1.7.5"), data);
                            SignedCms cms = new SignedCms(content, true);
                            EssCertIDv2 bouncyCertificate = new EssCertIDv2(new Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier(new DerObjectIdentifier("1.2.840.113549.1.9.16.2.47")), _hasher.HashBytes(certForSigning.RawData));

                            var x = bouncyCertificate.HashAlgorithm;

                            SigningCertificateV2 signerCertificateV2 = new SigningCertificateV2(new EssCertIDv2[] { bouncyCertificate });
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
                            slot.CloseSession(session);

                            if (output == null)
                            {
                                //if (tries < 0)
                                //{
                                //    tries--;
                                //if (session != null)
                                if (session.SessionId > 0)
                                {
                                    slot.CloseSession(session);
                                }
                                //        is_logged = true;
                                //        goto SignAgain2;
                                //    }
                            }

                            return Convert.ToBase64String(output);
                        }
                        catch (Exception ex)
                        {

                            //if (tries > 0)
                            //{
                            //    tries--;
                            //if (session != null)
                            if (session.SessionId > 0)
                            {
                                slot.CloseSession(session);
                            }
                            //    is_logged = true;
                            //    goto SignAgain2;
                            //}
                            //if (session != null)
                            if (session.SessionId > 0)
                            {
                                slot.CloseSession(session);
                            }
                            return "Error: Signing Error\r\n" + ex.Message + "\r\n" + ex.InnerException;
                        }
                        finally
                        {
                            //tries--;
                            //slot.CloseAllSessions();
                            //if (session != null)
                            if (session.SessionId > 0)
                            {
                                slot.CloseSession(session);
                            }
                        }
                    }
                }
            }
        }
    
    }
}
