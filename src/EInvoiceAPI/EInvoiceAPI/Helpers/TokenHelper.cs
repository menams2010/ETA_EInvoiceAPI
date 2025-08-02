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
        public static TokenSettings? MapTokenTable(DataTable table, string sysUIFormat)
        {
            if (table.Rows.Count != 1) return null;
            var row = table.Rows[0];

            return new TokenSettings
            {
                EnvTokenUrl = row["EnvTokenUrl"].ToString(),
                EnvInvoicingUrl = row["EnvInvoicingUrl"].ToString(),
                ClientId = row["ClientId"].ToString(),
                ClientSecret = row["ClientSecret"].ToString(),
                Token = row["str_token"].ToString(),
                TokenTime = row["time_get"] == DBNull.Value
                    ? DateTime.Now.AddDays(-1).ToString(sysUIFormat)
                    : DateTime.Parse(row["time_get"].ToString() ?? DateTime.Now.AddDays(-1).ToString()).ToString(sysUIFormat)
            };
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

        public static string? GetToken(string companyCode, string environmentId, out TokenSettings? tokenSettings, string sysUIFormat)
        {
            int maxTries = 3;
            tokenSettings = null;
            for (int attempt = 0; attempt < maxTries; attempt++)
            {
                var tokenTable = TokenManager.GetToken(companyCode, environmentId);
                tokenSettings = MapTokenTable(tokenTable, sysUIFormat);
                if (tokenSettings == null)
                    return "CANNOT: MAP_TOKEN_TABLE_FAILED";

                var dateTimeFormat = TokenManager.GetSettings("DateTimeFormat")?.ToString() ?? sysUIFormat;
                if (tokenSettings.TokenTime == DateTime.MinValue.ToString(sysUIFormat))
                {
                    tokenSettings.TokenTime = DateTime.Now.AddDays(-1).ToString(sysUIFormat);
                    dateTimeFormat = sysUIFormat;
                    TokenManager.SaveSettings("DateTimeFormat", sysUIFormat);
                }
                var tokenDate = tokenSettings.TokenTime;
                TimeSpan timeDiff = DateTime.Now - Convert.ToDateTime(tokenDate);
                if (timeDiff.TotalSeconds > 3000)
                {
                    var client = InvoiceHelper.SharedClient;
                    client.BaseUrl = new Uri(tokenSettings.EnvTokenUrl!);
                    client.Timeout = 30000;
                    var tokenRequest = new RestSharp.RestRequest(RestSharp.Method.POST);
                    tokenRequest.AddHeader("Content-Type", "application/x-www-form-urlencoded");
                    tokenRequest.AddParameter("grant_type", "client_credentials");
                    tokenRequest.AddParameter("client_id", tokenSettings.ClientId);
                    tokenRequest.AddParameter("client_secret", tokenSettings.ClientSecret);
                    tokenRequest.AddParameter("scope", "InvoicingAPI");
                    var tokenResponse = client.Execute(tokenRequest);
                    if (tokenResponse.StatusCode != System.Net.HttpStatusCode.OK && tokenResponse.Content == null)
                    {
                        continue;
                    }
                    else if (tokenResponse.StatusCode != System.Net.HttpStatusCode.OK && tokenResponse.Content != null)
                    {
                        return $"CANNOT: {tokenResponse.Content}";
                    }
                    TokenManager.SaveToken(Token.FromJson(tokenResponse.Content).AccessToken, DateTime.Now.ToString(), companyCode, environmentId);
                    TokenManager.SaveSettings("DateTimeFormat", sysUIFormat);
                    return Token.FromJson(tokenResponse.Content).AccessToken;
                }
                return tokenSettings.Token;
            }
            return "CANNOT: UNKNOWN_ERROR";
        }
    }
}