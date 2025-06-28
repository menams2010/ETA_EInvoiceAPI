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
                if (_certCache.TryGetValue(certSerialNum, out var cert))
                {
                    if (cert != null && DateTime.UtcNow > cert.NotAfter.ToUniversalTime())
                    {
                        _certCache.Remove(certSerialNum);
                        return null;
                    }
                    return cert;
                }
                return null;
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