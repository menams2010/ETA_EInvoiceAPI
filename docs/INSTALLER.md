# EInvoiceAPI-Setup.exe

A single setup program that installs EInvoiceAPI, the Admin site and ETANotifications on an IIS server, configures IIS, and prepares the shared `config.db`. It's built with [Inno Setup](https://jrsoftware.org/isinfo.php) from [`installer/EInvoiceAPI-Setup.iss`](../installer/EInvoiceAPI-Setup.iss).

## Getting the EXE

- **From CI:** every push to `master` runs the **Installer** workflow. It builds the EXE on Windows, then installs, upgrades and uninstalls it on a real IIS. Download `EInvoiceAPI-Setup` from the run's **Artifacts**.
- **Build it yourself** on Windows, with the .NET 10 SDK and Inno Setup 6 installed (`winget install JRSoftware.InnoSetup`):

  ```powershell
  .\installer\build-installer.ps1 -Version 1.0.0
  ```

  The result is `artifacts\EInvoiceAPI-Setup-1.0.0.exe`.

## Wizard pages

| Page | Options |
|---|---|
| Select components | EInvoiceAPI, Admin, ETANotifications (any combination) |
| Platform | **64-bit** (default) or **32-bit**. Choose 32-bit on servers that sign with the USB token, because its driver is 32-bit. |
| IIS | Site, picked from the server's IIS sites, and the URL path (default `EInvoiceAPI`). The apps go to `/<path>`, `/<path>/Admin` and `/<path>/ETANotifications`. |
| Folders | Web apps folder (default `C:\inetpub\EInvoiceAPI`), database folder (`C:\ETA\Data`), notification logs folder (`C:\EInvoice`) |
| Database | **New database:** generate a strong password, or **choose your own** (32 or more characters, typed twice). **Existing database:** it's kept and upgraded. Its current password is read from the existing IIS app pools when it can be, otherwise you enter it, and it's checked before installing. Tick **Change the database password** to replace it with a password of your choice (see below). |
| Admin account | First account for the Admin site. Only shown when the database is new. |
| SAP endpoint: Prod / Dev | Endpoint URL, SAP service user name and password, and timeout. On a new database the Prod URL, user and password are required; Dev is optional. On an upgrade, blank fields keep their current values. Only shown when ETANotifications is selected. You can change these later in Admin > ETA Notifications. |
| ETA Notifications API key | The key ETA must send with every notification (`Authorization: ApiKey <key>`). A new database suggests a random key; replace it with the key already set in the ETA portal if you have one. On an upgrade, blank keeps the current key. Only shown when ETANotifications is selected. You can view or change it later in Admin > ETA Notifications. |
| HTTPS | Optionally add an HTTPS binding using a certificate from *Local Computer > Personal*, with a port and an optional host name (SNI). |

## What setup does

1. **IIS.** If IIS is missing, setup offers to install it.
2. **Hosting Bundle.** If the ASP.NET Core 10 Hosting Bundle is missing, setup downloads it from Microsoft, installs it and restarts IIS. The server needs internet access for this.
3. **Database.** Creates or upgrades `config.db`, using the Admin app's own code, creates the first Admin account, and saves the SAP endpoints and the ETA Notifications API key.
4. **Files.** Stops the app pools and copies the apps for the chosen platform.
5. **IIS configuration.** Runs `deploy\Setup-IIS.ps1`. This creates or updates one app pool per app (No Managed Code, 32-bit when x86) and the nested applications. It also sets `Database__Path`, `Database__Password` and the notification folder as app pool environment variables, grants folder permissions, adds the optional HTTPS binding, and starts the pools.

**Running setup again** upgrades in place and remembers the previous choices.

**Changing the database password** (on an upgrade, **Change the database password**):
1. Setup stops **all** the app pools, including apps you didn't select this time.
2. It upgrades the database, then re-encrypts it with the new password.
3. It sets the new password on every app pool, and starts them.

The previous file is kept next to `config.db` as `config.before-password-change-<date-time>.db`. It still opens with the **old** password, so delete it once everything works. If re-encrypting fails, nothing changes: the database keeps its current password and the pools are restarted.

**Uninstalling** (Windows *Apps & features*, or `unins000.exe` in the apps folder) removes the IIS applications, the app pools and the app files. It **keeps** `config.db`, the notification logs and any HTTPS binding.

## Unattended install

Every page can be filled from the command line:

```powershell
EInvoiceAPI-Setup-1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="C:\Temp\eta-setup.log" `
  /COMPONENTS="api,admin,notifications" /ARCH=x86 /SITE="Default Web Site" /BASEPATH=EInvoiceAPI `
  /DIR="C:\inetpub\EInvoiceAPI" /DATADIR="C:\ETA\Data" /NOTIFDIR="C:\EInvoice" `
  /ADMINUSER=admin /ADMINPASSWORD=... /SAPPRODURL=https://... /SAPPRODUSER=... /SAPPRODPASSWORD=... /NOTIFAPIKEY=... `
  /CERT=<thumbprint> /HTTPSPORT=443 /INSTALLIIS=yes
```

How the database password is chosen:
- **New database:** `/DBPASSWORD=<your password>`, or setup generates one if `/DBPASSWORD` is omitted.
- **Change it on an upgrade:** `/NEWDBPASSWORD=<new password>`.
- **Upgrade:** setup reuses the password stored in IIS.

A new database with ETANotifications selected also needs `/SAPPRODURL`, `/SAPPRODUSER` and `/SAPPRODPASSWORD`; otherwise setup stops before installing. Without `/NOTIFAPIKEY`, a new database gets a random API key: read it in Admin > ETA Notifications and enter it in the ETA portal.

Passwords given on the command line can be seen by other administrators and in process logs. Prefer the wizard, or change them afterwards.

The setup log (`/LOG=...`, or `%TEMP%\Setup Log *.txt`) includes the output of the database and IIS steps. It never includes passwords.
