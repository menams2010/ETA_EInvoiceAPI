using Newtonsoft.Json;
using System;

namespace EInvoiceAPI.Models
{
    public class SearchDocumentsFilter
    {
        [JsonProperty("submissionDateFrom")]
        public DateTime? SubmissionDateFrom { get; set; }

        [JsonProperty("submissionDateTo")]
        public DateTime? SubmissionDateTo { get; set; }

        [JsonProperty("issueDateFrom")]
        public DateTime? IssueDateFrom { get; set; }

        [JsonProperty("issueDateTo")]
        public DateTime? IssueDateTo { get; set; }

        [JsonProperty("continuationToken")]
        public string? ContinuationToken { get; set; }

        [JsonProperty("pageSize")]
        public int? PageSize { get; set; }

        [JsonProperty("uuid")]
        public string? Uuid { get; set; }

        [JsonProperty("internalID")]
        public string? InternalID { get; set; }

        [JsonProperty("direction")]
        public string? Direction { get; set; }

        [JsonProperty("status")]
        public string? Status { get; set; }

        [JsonProperty("documentType")]
        public string? DocumentType { get; set; }

        [JsonProperty("receiverType")]
        public string? ReceiverType { get; set; }

        [JsonProperty("receiverId")]
        public string? ReceiverId { get; set; }

        [JsonProperty("senderName")]
        public string? SenderName { get; set; }

        [JsonProperty("receiverName")]
        public string? ReceiverName { get; set; }
    }
}
