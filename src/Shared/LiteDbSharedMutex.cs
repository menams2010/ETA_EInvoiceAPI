// Shared by EInvoiceAPI, EInvoiceAPIAdmin and IncomeTaxWebAPI (linked into each project; see their .csproj).
#nullable enable
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ETA.Shared;

/// <summary>
/// Lets several IIS app pools open the same LiteDB file in <c>ConnectionType.Shared</c> mode.
/// </summary>
/// <remarks>
/// LiteDB 5 coordinates shared access with the named mutex <c>Global\&lt;SHA1 of the lower-cased full path&gt;.Mutex</c>.
/// On .NET (Core) it creates that mutex with the default security, which only lets the creating identity open it.
/// Each IIS app pool runs as its own identity (IIS AppPool\&lt;name&gt;), so the first app to open config.db locked the
/// others out ("Access to the path 'Global\...Mutex' is denied").
/// Call <see cref="EnsureAccessible"/> before opening the file: it creates the same mutex first, with an ACL that every
/// account can use (as LiteDB itself does on .NET Framework), and keeps it alive for the life of the process.
/// LiteDB then opens the existing mutex instead of creating its own.
/// </remarks>
internal static class LiteDbSharedMutex
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Mutex> Held = new(StringComparer.Ordinal);

    public static void EnsureAccessible(string filename)
    {
        if (!OperatingSystem.IsWindows()) return;
        // Same name as LiteDB.SharedEngine (5.0.x): Path.GetFullPath(filename).ToLower().Sha1(), upper-case hex.
        var name = "Global\\" + Sha1(Path.GetFullPath(filename).ToLower()) + ".Mutex";
        lock (Gate)
        {
            if (Held.ContainsKey(name)) return;
            var mutex = Create(name);
            if (mutex != null) Held[name] = mutex;
        }
    }

    [SupportedOSPlatform("windows")]
    private static Mutex? Create(string name)
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.FullControl, AccessControlType.Allow));
        try
        {
            // Opens the mutex if another of the apps already created it (with this same ACL).
            return MutexAcl.Create(false, name, out _, security);
        }
        catch (UnauthorizedAccessException)
        {
            // Created by a process that did not use this helper; LiteDB will report the access problem itself.
            return null;
        }
    }

    private static string Sha1(string value)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash);
    }
}
