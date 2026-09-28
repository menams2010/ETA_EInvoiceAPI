using InvoiceWebApp.Models;
using LiteDB;
using Microsoft.AspNetCore.Identity;

namespace InvoiceWebApp.Services;

public static class AccountSecurity
{
    public const string StampClaim = "AccountSecurityStamp";
    private static readonly PasswordHasher<Login> Hasher = new();
    private static readonly Login DummyUser = new();
    private static readonly string DummyHash = Hasher.HashPassword(DummyUser, Guid.NewGuid().ToString());

    public static void SetPassword(Login user, string password)
    {
        user.PasswordHash = Hasher.HashPassword(user, password);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
    }

    public static bool Verify(Login? user, string password)
    {
        try
        {
            return Hasher.VerifyHashedPassword(user ?? DummyUser,
                user?.PasswordHash ?? DummyHash, password) != PasswordVerificationResult.Failed && user != null;
        }
        catch (FormatException) { return false; }
    }

    public static void NormalizeDocument(BsonDocument document, bool renewStamp = false)
    {
        var user = new Login();
        if (document.TryGetValue("PasswordHash", out var hash) && hash.IsString)
            user.PasswordHash = hash.AsString;
        if (string.IsNullOrEmpty(user.PasswordHash))
        {
            var password = document.TryGetValue("u_pass", out var legacy) && legacy.IsString ? legacy.AsString : null;
            if (password == null) throw new InvalidOperationException("An imported account has no password. Supply a password for this account.");
            SetPassword(user, password);
            document["PasswordHash"] = user.PasswordHash;
            renewStamp = true;
        }
        document.Remove("u_pass");
        if (renewStamp || !document.TryGetValue("SecurityStamp", out var stamp) || !stamp.IsString || string.IsNullOrEmpty(stamp.AsString))
            document["SecurityStamp"] = Guid.NewGuid().ToString("N");
    }

    public static void Migrate(LiteDatabase db)
    {
        foreach (var name in new[] { "Logins", "tb_Login", "Login" })
        {
            if (!db.CollectionExists(name)) continue;
            var collection = db.GetCollection(name);
            foreach (var document in collection.FindAll().ToList())
            {
                if (!document.ContainsKey("u_pass") && document.ContainsKey("PasswordHash") && document.ContainsKey("SecurityStamp")) continue;
                NormalizeDocument(document);
                collection.Update(document);
            }
        }
    }
}
