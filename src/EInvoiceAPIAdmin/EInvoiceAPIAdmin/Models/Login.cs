namespace InvoiceWebApp.Models
{
    public class Login
    {
        public int Id { get; set; }
        public string u_name { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonIgnore]
        public string PasswordHash { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonIgnore]
        public string SecurityStamp { get; set; } = string.Empty;
        public bool IsInitial { get; set; }
    }
}
