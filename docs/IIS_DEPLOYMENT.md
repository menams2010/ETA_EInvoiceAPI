# IIS deployment

## URLs

| URL | App | Project |
|---|---|---|
| `http(s)://<host>/EInvoiceAPI` | E-invoice signing & submission API (Swagger at `/EInvoiceAPI/swagger`) | `src/EInvoiceAPI` |
| `http(s)://<host>/EInvoiceAPI/Admin` | Admin web UI | `src/EInvoiceAPIAdmin` |
| `http(s)://<host>/EInvoiceAPI/ETANotifications` | Receiver for ETA notifications | `src/IncomeTaxWebAPI` |

### HTTP and HTTPS

| App | `http://` | `https://` |
|---|---|---|
| EInvoiceAPI | Redirected to HTTPS (307) when the site has an HTTPS binding. Otherwise served over HTTP. | Served |
| Admin | Always redirected to HTTPS. Its login cookie is HTTPS-only. | Served |
| ETANotifications | Served | Served |

So the site needs an **HTTPS binding with a valid certificate**, plus an HTTP binding if you want plain `http://` links to work or redirect.

## Layout on the server

```
IIS: Sites
└── Default Web Site                      (or the site passed as -SiteName)
    └── /EInvoiceAPI                      pool: EInvoiceAPI                  → C:\inetpub\EInvoiceAPI\EInvoiceAPI
        ├── /Admin                        pool: EInvoiceAPI-Admin            → C:\inetpub\EInvoiceAPI\Admin
        └── /ETANotifications             pool: EInvoiceAPI-ETANotifications → C:\inetpub\EInvoiceAPI\ETANotifications

C:\inetpub\EInvoiceAPI\                   web content; the three folders are siblings on disk
├── EInvoiceAPI\                          ← publish\<arch>\EInvoiceAPI
├── Admin\                                ← publish\<arch>\EInvoiceAPIAdmin
└── ETANotifications\                     ← publish\<arch>\IncomeTaxWebAPI

C:\ETA\Data\                              outside the web folders; Modify for all three pools
└── config.db                             shared, encrypted LiteDB (all three apps; SAP endpoints and the ETA Notifications API key included)

C:\EInvoice\                              Modify for the ETANotifications pool (path hard-coded)
├── ProdDB.db
└── DevDB.db
```

- **URLs nest, folders don't.** The URLs are nested, but the folders on disk sit side by side. Publishing clears the target folder first, so if Admin were inside `EInvoiceAPI\`, republishing the API would delete it.
- **One pool per app.** ASP.NET Core in-process hosting needs a separate app pool for each app, set to *No Managed Code*.
- **Nesting is safe.** Each generated `web.config` has `inheritInChildApplications="false"`, so the API's settings don't leak into `/Admin` or `/ETANotifications`.
- **No sub-path setting needed.** IIS passes each app its sub-path automatically, so nothing needs to be configured for it on the server.

## Steps

1. **On the server, once:**
   - Install the **ASP.NET Core 10.0 Hosting Bundle**. It installs both the 32-bit and 64-bit runtimes. Restart IIS afterwards (`iisreset`).
   - Install the USB token driver if you sign with the token.
2. **Publish** on the build machine, from the repository root. Use `x86` if the server signs with the USB token, because its driver is 32-bit.

   ```powershell
   .\publish.ps1 -Arch x64
   ```

3. **Create the IIS layout** on the server, as Administrator. The script prompts for the shared `config.db` password, which must be at least 32 characters. Use the same `-Arch` as in step 2.

   ```powershell
   .\deploy\Setup-IIS.ps1 -Arch x64
   ```

   It creates or updates the three app pools and applications, sets `Database__Path`, `Database__Password` and `ASPNETCORE_ENVIRONMENT` on all three pools, and grants folder permissions. You can safely run it again.

4. **Copy the published folders.** Stop the pools first, because in-process apps lock their DLLs:

   ```powershell
   Stop-WebAppPool EInvoiceAPI; Stop-WebAppPool EInvoiceAPI-Admin; Stop-WebAppPool EInvoiceAPI-ETANotifications
   robocopy publish\x64\EInvoiceAPI       C:\inetpub\EInvoiceAPI\EInvoiceAPI      /MIR
   robocopy publish\x64\EInvoiceAPIAdmin  C:\inetpub\EInvoiceAPI\Admin            /MIR
   robocopy publish\x64\IncomeTaxWebAPI   C:\inetpub\EInvoiceAPI\ETANotifications /MIR
   Start-WebAppPool EInvoiceAPI; Start-WebAppPool EInvoiceAPI-Admin; Start-WebAppPool EInvoiceAPI-ETANotifications
   ```

5. **First install with an empty `config.db` only:**
   - Add `Bootstrap__Username` and `Bootstrap__Password` to the **EInvoiceAPI-Admin** pool's environment variables. The password needs 12 or more characters.
   - Start the pool and open `/EInvoiceAPI/Admin` once.
   - Remove the two variables again.

   For an existing database, follow the migration and key-rotation runbook in [src/EInvoiceAPIAdmin/docs/security-hardening.md](../src/EInvoiceAPIAdmin/docs/security-hardening.md) first.

6. **Set the API key and SAP credentials.** In Admin, open **ETA Notifications**.
   - **API key:** ETA must send it with every notification, as `Authorization: ApiKey <key>`. Set it (or generate one) and enter the same key in the ETA portal. With no key set, ETANotifications rejects every request with 401.
   - **SAP endpoints:** edit **Prod**, and **Dev** if you use it. Enter the SAP username and password, which used to be hard-coded in IncomeTaxWebAPI, and check the URL and timeout.
   - ETANotifications reads these values for every notification, so changes apply immediately.
   - Until a username is set, notifications are forwarded to SAP without an `Authorization` header.

## Addresses that change with these URLs

- **ETA portal (notification receiver).** Register the base URL `https://<host>/EInvoiceAPI/ETANotifications/api/einvoice`. ETA then calls these endpoints under it:
  - `…/ping`
  - `…/notifications/documents`
  - `…/notifications/receipts`

  The Dev variants are under `…/api/einvoice/dev`.
- **Microsoft mail (Admin → SMTP profiles), if used.** Set the redirect URI in the Entra ID app registration to `https://<host>/EInvoiceAPI/Admin/signin-microsoft-mail`.
- **ERP/SAP systems calling the API.** Point them at `https://<host>/EInvoiceAPI/api/Invoice/...`.

## Running locally with the same URLs

Each project's Visual Studio launch profile sets `ASPNETCORE_PATHBASE`, so local URLs match production. For example:
- `http://localhost:5042/EInvoiceAPI/swagger`
- `https://localhost:5001/EInvoiceAPI/Admin`

Don't set `ASPNETCORE_PATHBASE` under IIS, because IIS provides the sub-path itself.
