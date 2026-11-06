using System.Text;
using LiteDB;

namespace IncomeTaxWebAPI.Services
{
    /// <summary>
    /// SAP endpoint settings, managed in EInvoiceAPIAdmin (Admin > SAP Endpoints) and stored in the shared
    /// config.db. Read on every notification, so changes in Admin apply without a restart.
    /// </summary>
    public sealed class SapEndpointStore
    {
        public const string Prod = "Prod";
        public const string Dev = "Dev";

        // Collection and field names must match EInvoiceAPIAdmin (Models/SapEndpoint.cs).
        private const string Collection = "SapEndpoints";
        private const int MinimumPasswordLength = 32;

        private readonly string _dbPath;
        private readonly string _password;

        public SapEndpointStore(IConfiguration configuration)
        {
            _password = configuration["Database:Password"] ?? string.Empty;
            if (_password.Length < MinimumPasswordLength)
                throw new InvalidOperationException(
                    $"Configure Database:Password (at least {MinimumPasswordLength} characters) with the same value EInvoiceAPIAdmin uses. " +
                    "SAP endpoints are read from the shared config database.");

            var configuredPath = configuration["Database:Path"];
            if (string.IsNullOrWhiteSpace(configuredPath))
                throw new InvalidOperationException("Configure Database:Path with the same config.db path EInvoiceAPIAdmin uses.");
            _dbPath = Path.GetFullPath(configuredPath);
        }

        public sealed class SapEndpoint
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Url { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
            public int TimeoutMs { get; set; }

            /// <summary>"Basic base64(username:password)", or null when no username is configured.</summary>
            public string BasicAuthorizationHeader =>
                string.IsNullOrEmpty(Username)
                    ? null
                    : "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
        }

        /// <summary>Returns the endpoint, or null if it is missing or has no URL.</summary>
        public SapEndpoint Get(string name)
        {
            if (!File.Exists(_dbPath))
                throw new FileNotFoundException("Config database not found. Check Database:Path.", _dbPath);

            // Shared mode: EInvoiceAPIAdmin and EInvoiceAPI use the same file at the same time.
            ETA.Shared.LiteDbSharedMutex.EnsureAccessible(_dbPath);
            using var db = new LiteDatabase(new ConnectionString
            {
                Filename = _dbPath,
                Password = _password,
                Connection = ConnectionType.Shared
            });
            var endpoint = db.GetCollection<SapEndpoint>(Collection).FindOne(x => x.Name == name);
            return string.IsNullOrWhiteSpace(endpoint?.Url) ? null : endpoint;
        }
    }
}
