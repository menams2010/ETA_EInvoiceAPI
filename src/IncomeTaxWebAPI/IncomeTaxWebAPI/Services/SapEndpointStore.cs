using System.Text;
using LiteDB;

namespace IncomeTaxWebAPI.Services
{
    /// <summary>
    /// ETANotifications settings managed in EInvoiceAPIAdmin (Admin > ETA Notifications) and stored in the shared
    /// config.db: the SAP endpoints and the API key. Read on every use, so changes in Admin apply without a restart.
    /// </summary>
    public sealed class SapEndpointStore
    {
        public const string Prod = "Prod";
        public const string Dev = "Dev";

        // Collection and field names must match EInvoiceAPIAdmin (Models/SapEndpoint.cs).
        private const string Collection = "SapEndpoints";
        private const string NotificationSettingsCollection = "NotificationSettings";
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

        /// <summary>Must match EInvoiceAPIAdmin (Models/NotificationSettings.cs): one record, Id 1.</summary>
        public sealed class NotificationSettings
        {
            public int Id { get; set; }
            public string ApiKey { get; set; }
        }

        /// <summary>Returns the endpoint, or null if it is missing or has no URL.</summary>
        public SapEndpoint Get(string name)
        {
            using var db = Open();
            var endpoint = db.GetCollection<SapEndpoint>(Collection).FindOne(x => x.Name == name);
            return string.IsNullOrWhiteSpace(endpoint?.Url) ? null : endpoint;
        }

        /// <summary>
        /// The API key ETA must send (Admin > ETA Notifications), or null when none is set.
        /// Read on every request, so a key changed in Admin applies immediately.
        /// </summary>
        public string GetApiKey()
        {
            using var db = Open();
            var settings = db.GetCollection<NotificationSettings>(NotificationSettingsCollection).FindById(1);
            return string.IsNullOrEmpty(settings?.ApiKey) ? null : settings.ApiKey;
        }

        private LiteDatabase Open()
        {
            if (!File.Exists(_dbPath))
                throw new FileNotFoundException("Config database not found. Check Database:Path.", _dbPath);

            // Shared mode: EInvoiceAPIAdmin and EInvoiceAPI use the same file at the same time.
            ETA.Shared.LiteDbSharedMutex.EnsureAccessible(_dbPath);
            return new LiteDatabase(new ConnectionString
            {
                Filename = _dbPath,
                Password = _password,
                Connection = ConnectionType.Shared
            });
        }
    }
}
