namespace InvoiceWebApp.Models
{
    public class CompEnv
    {
        public int Id { get; set; }
        public long CompanyId { get; set; }
        public long EnvId { get; set; }
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
        public string? str_token { get; set; }
        public string? time_get { get; set; }
    }

}
