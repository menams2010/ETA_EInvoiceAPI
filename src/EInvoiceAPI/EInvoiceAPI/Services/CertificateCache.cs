using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;

namespace EInvoiceAPI.Services
{
    public class CertificateCache
    {
        private readonly Dictionary<string, X509Certificate2> _certCache = new();
        private readonly object _lock = new();

        public X509Certificate2? Get(string certSerialNum)
        {
            lock (_lock)
            {
                _certCache.TryGetValue(certSerialNum, out var cert);
                return cert;
            }
        }

        public void Set(string certSerialNum, X509Certificate2 cert)
        {
            lock (_lock)
            {
                if (!_certCache.ContainsKey(certSerialNum))
                    _certCache[certSerialNum] = cert;
            }
        }
    }
}