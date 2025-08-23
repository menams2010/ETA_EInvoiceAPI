using EInvoiceAPI.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace EInvoiceAPI.Services
{
    /// <summary>
    /// Service for logging API requests and responses to Slack
    /// </summary>
    public class SlackLogger
    {
        private readonly HttpClient _httpClient;
        private readonly string _webhookUrl;
        private readonly bool _isEnabled;

        public SlackLogger(IConfiguration configuration)
        {
            _httpClient = new HttpClient();
            _webhookUrl = configuration["Logging:Slack:WebhookUrl"];
            _isEnabled = !string.IsNullOrEmpty(_webhookUrl) &&
                          bool.TryParse(configuration["Logging:Slack:IsEnabled"], out bool enabled) &&
                          enabled;
        }

        /// <summary>
        /// Logs a REST API request and response to Slack
        /// </summary>
        /// <param name="companyCode">Company code from request header</param>
        /// <param name="environmentId">Environment ID from request header</param>
        /// <param name="request">The RestSharp request object</param>
        /// <param name="response">The RestSharp response object</param>
        /// <param name="methodName">Name of the API method being called</param>
        /// <returns>Task representing the async operation</returns>
        public async Task LogApiCallAsync(string companyCode, string environmentId, RestClient client, RestRequest request, RestResponse response, string methodName)
        {
            if (!_isEnabled)
                return;

            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                // Extract invoice number or document ID from request body if possible
                string requestBody = request.Parameters
                    .FirstOrDefault(p => p.Type == ParameterType.RequestBody)
                    ?.Value?.ToString() ?? string.Empty;

                JObject? invoice = InvoiceHelper.DeserializeInvoice(requestBody);
                string invoiceNumber = invoice?["internalID"]?.ToString() ?? string.Empty;
                string localUrl = request.Resource;
                string remoteUrl = response.ResponseUri?.ToString() ?? string.Empty;
                string baseUrl = client?.Options?.BaseUrl?.ToString() ?? string.Empty;

                // Build response headers
                var responseHeaders = new StringBuilder();
                if (response.Headers != null)
                {
                    foreach (var header in response.Headers)
                    {
                        responseHeaders.AppendLine($"{header.Name}: {header.Value}");
                    }
                }

                // Format response content (limit size)
                string responseContent = string.Empty;
                if (!string.IsNullOrEmpty(response.Content))
                {
                    responseContent = response.Content.Length > 1000
                        ? $"{response.Content.Substring(0, 1000)}... [truncated]"
                        : response.Content;
                }

                // Truncate request body if needed
                if (!string.IsNullOrEmpty(requestBody) && requestBody.Length > 1000)
                {
                    requestBody = $"{requestBody.Substring(0, 1000)}... [truncated]";
                }

                var blocks = new List<object>
                {
                    new
                    {
                        type = "header",
                        text = new { type = "plain_text", text = $"API Call: {methodName}" }
                    },
                    new
                    {
                        type = "section",
                        fields = new object[]
                        {
                            new { type = "mrkdwn", text = $"*Timestamp:*\n{timestamp}" },
                            new { type = "mrkdwn", text = $"*Status:*\n{(int)response.StatusCode} - {response.StatusCode}" },
                            new { type = "mrkdwn", text = $"*Company Code:*\n{companyCode}" },
                            new { type = "mrkdwn", text = $"*Environment ID:*\n{environmentId}" }
                        }
                    },
                    new
                    {
                        type = "section",
                        fields = new object[]
                        {
                            new { type = "mrkdwn", text = $"*Invoice Number:*\n{invoiceNumber}" },
                            new { type = "mrkdwn", text = $"*Local URL:*\n{localUrl}" }
                        }
                    },
                    new
                    {
                        type = "section",
                        fields = new object[]
                        {
                            new { type = "mrkdwn", text = $"*Base URL:*\n{baseUrl}" },
                            new { type = "mrkdwn", text = $"*Remote URL:*\n{remoteUrl}" }
                        }
                    }
                };

                if (!string.IsNullOrEmpty(requestBody))
                {
                    blocks.Add(new
                    {
                        type = "section",
                        text = new { type = "mrkdwn", text = $"*Request Body:*\n```{requestBody}```" }
                    });
                }

                blocks.Add(new
                {
                    type = "section",
                    text = new { type = "mrkdwn", text = $"*Response Headers:*\n```{responseHeaders}```" }
                });

                if (!string.IsNullOrEmpty(responseContent))
                {
                    blocks.Add(new
                    {
                        type = "section",
                        text = new { type = "mrkdwn", text = $"*Response Body:*\n```{responseContent}```" }
                    });
                }

                blocks.Add(new { type = "divider" });

                var message = new { blocks = blocks.ToArray() };

                // Send to Slack (disabled by default/commented)
                var json = JsonSerializer.Serialize(message);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                //if (!response.IsSuccessful && response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Accepted)
                //{
                //    await _httpClient.PostAsync(_webhookUrl, content);
                //}
            }
            catch (Exception ex)
            {
                // Silently fail for logging errors
                Console.WriteLine($"Error logging to Slack: {ex.Message}");
            }
        }

        /// <summary>
        /// Logs an API request from HttpRequest to Slack
        /// </summary>
        /// <param name="companyCode">Company code from request header</param>
        /// <param name="request">The HttpRequest object</param>
        /// <param name="methodName">Name of the API method being called</param>
        /// <returns>Task representing the async operation</returns>
        public async Task LogApiCallAsync(string companyCode, HttpRequest request, string methodName)
        {
            if (!_isEnabled)
                return;

            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                // Safely read the request body
                string requestBody = string.Empty;
                if (request.Body != null && request.Body.CanSeek)
                {
                    var position = request.Body.Position;
                    request.Body.Position = 0;

                    using (var reader = new System.IO.StreamReader(request.Body, Encoding.UTF8, true, 1024, true))
                    {
                        requestBody = await reader.ReadToEndAsync();
                    }

                    request.Body.Position = position;
                }

                // Extract invoice number from request body
                JObject? invoice = InvoiceHelper.DeserializeInvoice(requestBody);
                string invoiceNumber = invoice?["internalID"]?.ToString() ?? string.Empty;

                var blocks = new List<object>
                {
                    new
                    {
                        type = "header",
                        text = new { type = "plain_text", text = $"API Call: {methodName}" }
                    },
                    new
                    {
                        type = "section",
                        fields = new object[]
                        {
                            new { type = "mrkdwn", text = $"*Timestamp:*\n{timestamp}" },
                            new { type = "mrkdwn", text = $"*Company Code:*\n{companyCode}" }
                        }
                    },
                    new
                    {
                        type = "section",
                        fields = new object[]
                        {
                            new { type = "mrkdwn", text = $"*Invoice Number:*\n{invoiceNumber}" },
                            new { type = "mrkdwn", text = $"*Local URL:*\n{request.Path}" }
                        }
                    }
                };

                if (!string.IsNullOrEmpty(requestBody))
                {
                    if (requestBody.Length > 1000)
                    {
                        requestBody = $"{requestBody.Substring(0, 1000)}... [truncated]";
                    }

                    blocks.Add(new
                    {
                        type = "section",
                        text = new { type = "mrkdwn", text = $"*Request Body:*\n```{requestBody}```" }
                    });
                }

                blocks.Add(new { type = "divider" });

                var message = new { blocks = blocks.ToArray() };

                var json = JsonSerializer.Serialize(message);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                //await _httpClient.PostAsync(_webhookUrl, content);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error logging to Slack: {ex.Message}");
            }
        }

        /// <summary>
        /// Custom log method for TokenHelper and other static helpers.
        /// </summary>
        /// <param name="companyCode">Company code (optional)</param>
        /// <param name="message">Message to log</param>
        /// <param name="methodName">Method or context name</param>
        /// <returns>Task</returns>
        public async Task LogHelperAsync(string? companyCode, string message, string methodName)
        {
            if (!_isEnabled)
                return;

            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var blocks = new List<object>
                {
                    new
                    {
                        type = "header",
                        text = new { type = "plain_text", text = $"TokenHelper Log: {methodName}" }
                    },
                    new
                    {
                        type = "section",
                        fields = new object[]
                        {
                            new { type = "mrkdwn", text = $"*Timestamp:*\n{timestamp}" },
                            new { type = "mrkdwn", text = $"*Company Code:*\n{companyCode ?? "N/A"}" }
                        }
                    },
                    new
                    {
                        type = "section",
                        text = new { type = "mrkdwn", text = $"*Message:*\n```{message}```" }
                    },
                    new { type = "divider" }
                };

                var slackMessage = new { blocks = blocks.ToArray() };
                var json = JsonSerializer.Serialize(slackMessage);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                //await _httpClient.PostAsync(_webhookUrl, content);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error logging to Slack: {ex.Message}");
            }
        }
    }
}