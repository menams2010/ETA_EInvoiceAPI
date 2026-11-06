using EInvoiceAPI.Services;
using RestSharp;
using System;
using System.Data;
using System.Globalization;

namespace EInvoiceAPI.Helpers
{
    public class TokenSettings
    {
        public string? EnvTokenUrl { get; set; }
        public string? EnvInvoicingUrl { get; set; }
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
        public string? Token { get; set; }
        public string? TokenTime { get; set; }
    }

    public static class TokenHelper
    {
        private static readonly CultureInfo enUS = new CultureInfo("en-US");
        public static TokenSettings? MapTokenTable(DataTable table, string sysUIFormat)
        {
            if (table.Rows.Count != 1) return null;
            var row = table.Rows[0];
            TokenSettings tokenSettings = new TokenSettings();
            tokenSettings.EnvTokenUrl = row["EnvTokenUrl"].ToString();
            tokenSettings.EnvInvoicingUrl = row["EnvInvoicingUrl"].ToString();
            tokenSettings.ClientId = row["ClientId"].ToString();
            tokenSettings.ClientSecret = row["ClientSecret"].ToString();
            tokenSettings.Token = row["str_token"].ToString();
            var timeGetText = row["time_get"]?.ToString();

            if (string.IsNullOrWhiteSpace(timeGetText))
            {
                tokenSettings.TokenTime = DateTime.Now.AddDays(-1).ToString(sysUIFormat, enUS);
            }
            else if (DateTime.TryParseExact(timeGetText, sysUIFormat, enUS, DateTimeStyles.None, out var dt) ||
                     DateTime.TryParse(timeGetText, enUS, DateTimeStyles.AssumeLocal, out dt))
            {
                tokenSettings.TokenTime = dt.ToString(sysUIFormat, enUS);
            }
            else
            {
                tokenSettings.TokenTime = DateTime.Now.AddDays(-1).ToString(sysUIFormat, enUS);
            }

            return tokenSettings;
        }

        public static TokenSettings? GetE_Sign(string companyCode, out string? tokenSerial, out string? tokenPin, out string? certSerialNum, out bool useXml)
        {
            var dtSign = TokenManager.getE_Signing(companyCode);
            tokenSerial = tokenPin = certSerialNum = null;
            useXml = false;
            if (dtSign.Rows.Count > 0)
            {
                tokenSerial = dtSign.Rows[0]["TokenSerial"].ToString();
                tokenPin = dtSign.Rows[0]["TokenPin"].ToString();
                certSerialNum = dtSign.Rows[0]["CertSerialNum"].ToString();
                object useXmltojson = dtSign.Rows[0]["UseXMLTOJSON"];
                useXml = useXmltojson != null && Convert.ToBoolean(useXmltojson);
            }
            return null;
        }

        public static string? GetToken(
            string companyCode,
            string environmentId,
            out TokenSettings? tokenSettings,
            string sysUIFormat)
        {
            int maxTries = 3;
            tokenSettings = null;
            for (int attempt = 0; attempt < maxTries; attempt++)
            {
                var tokenTable = TokenManager.GetToken(companyCode, environmentId);
                if (tokenTable.Rows.Count.Equals(0))
                {
                    return $"CANNOT: COMPANY_NOT_FOUND_WITH_ENV_{environmentId.ToUpper()}";
                }
                if (tokenTable.Rows.Count.Equals(1))
                {
                    var clientSecretExpirationDate = tokenTable.Rows[0]["ClientSecretExpirationDate"];
                    if (clientSecretExpirationDate != DBNull.Value && DateOnly.TryParse(clientSecretExpirationDate.ToString(), out DateOnly expirationDate))
                    {
                        if (expirationDate < DateOnly.FromDateTime(DateTime.Now))
                        {
                            return "CANNOT: CLIENT_SECRET_EXPIRED";
                        }
                    }
                }

                tokenSettings = MapTokenTable(tokenTable, sysUIFormat);
                if (tokenSettings == null)
                {
                    return "CANNOT: MAP_TOKEN_TABLE_FAILED";
                }

                var dateTimeFormat = TokenManager.GetSettings("DateTimeFormat")?.ToString() ?? sysUIFormat;
                if (tokenSettings.TokenTime == DateTime.MinValue.ToString(sysUIFormat, enUS))
                {
                    tokenSettings.TokenTime = DateTime.Now.AddDays(-1).ToString(sysUIFormat, enUS);
                }

                var parsedTokenTime = DateTime.ParseExact(tokenSettings.TokenTime, sysUIFormat, enUS, DateTimeStyles.None);
                TimeSpan timeDiff = DateTime.Now - Convert.ToDateTime(parsedTokenTime, enUS);
                if (timeDiff.TotalSeconds > 3000)
                {
                    var client = new RestClient(new RestClientOptions(tokenSettings.EnvTokenUrl!)
                    {
                        Timeout = TimeSpan.FromSeconds(30),
                    });
                    var tokenRequest = new RestRequest(string.Empty, Method.Post);
                    tokenRequest.AddHeader("Content-Type", "application/x-www-form-urlencoded");
                    tokenRequest.AddParameter("grant_type", "client_credentials");
                    tokenRequest.AddParameter("client_id", tokenSettings.ClientId);
                    tokenRequest.AddParameter("client_secret", tokenSettings.ClientSecret);
                    tokenRequest.AddParameter("scope", "InvoicingAPI");
                    var tokenResponse = client.ExecuteAsync(tokenRequest).GetAwaiter().GetResult();

                    if (tokenResponse.StatusCode != System.Net.HttpStatusCode.OK && tokenResponse.Content == null)
                    {
                        continue;
                    }
                    else if (tokenResponse.StatusCode != System.Net.HttpStatusCode.OK && tokenResponse.Content != null)
                    {
                        return $"CANNOT: {tokenResponse.Content}";
                    }
                    TokenManager.SaveToken(Token.FromJson(tokenResponse.Content).AccessToken, DateTime.Now.ToString(sysUIFormat, enUS), companyCode, environmentId);

                    return Token.FromJson(tokenResponse.Content).AccessToken;
                }
                return tokenSettings.Token;
            }
            return "CANNOT: UNKNOWN_ERROR";
        }
    }
}