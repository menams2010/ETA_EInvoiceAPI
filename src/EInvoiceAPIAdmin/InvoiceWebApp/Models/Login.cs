namespace InvoiceWebApp.Models
{
    public class Login
    {
        public int Id { get; set; }
        public string u_name { get; set; }
        public string u_pass { get; set; }
        public bool IsInitial { get; set; }
    }
}
