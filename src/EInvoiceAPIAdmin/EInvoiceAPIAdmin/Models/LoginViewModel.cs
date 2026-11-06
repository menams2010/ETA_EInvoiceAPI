namespace InvoiceWebApp.Models
{
    public class LoginViewModel
    {
        [System.ComponentModel.DataAnnotations.Required]
        [System.ComponentModel.DataAnnotations.StringLength(100)]
        public string u_name { get; set; } = string.Empty;
        [System.ComponentModel.DataAnnotations.Required]
        [System.ComponentModel.DataAnnotations.StringLength(256)]
        public string u_pass { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }
    }
}
