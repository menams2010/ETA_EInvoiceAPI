using System.ComponentModel.DataAnnotations;

namespace InvoiceWebApp.Models;

public class AccountInput
{
    public int Id { get; set; }
    [Required, StringLength(100)]
    public string u_name { get; set; } = string.Empty;
    [StringLength(256, MinimumLength = 12)]
    public string? Password { get; set; }
    public bool IsInitial { get; set; }
}
