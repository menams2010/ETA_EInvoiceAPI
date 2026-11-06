# ETA_EInvoiceAPI

Combined solution for the ETA e-invoicing and income tax projects. Open `ETA_EInvoiceAPI.sln` in Visual Studio 2026 (or 2022 17.14+ with the .NET 10 SDK), or build from the command line:

```bash
dotnet build ETA_EInvoiceAPI.sln
```

All projects target **.NET 10** (LTS, supported until November 2028). The target framework is set once in [`Directory.Build.props`](Directory.Build.props), and [`global.json`](global.json) selects the .NET 10 SDK. To move to a newer .NET later, change `Directory.Build.props`.

| Project | Folder | Originally from |
|---|---|---|
| IncomeTaxWebAPI | `src/IncomeTaxWebAPI` | menams2010/IncomeTaxWebAPI (`master`); ported from ASP.NET Web API 2 on .NET Framework 4.5.2 |
| EInvoiceAPI | `src/EInvoiceAPI` | menams2010/EInvoiceAPI (`master`); was .NET 7 |
| EInvoiceAPIAdmin | `src/EInvoiceAPIAdmin` | menams2010/EInvoiceAPIAdmin (`Development`); was .NET 9 |
| Admin regression tests | `src/EInvoiceAPIAdmin/tests` | menams2010/EInvoiceAPIAdmin (`Development`) |

The full commit history of each original repository is preserved. Each project's original `.sln` is still in its folder if you want to open it on its own.

## Publishing

Each web app has two Folder publish profiles in `Properties/PublishProfiles/`:

| Profile | Runtime | Use for |
|---|---|---|
| `FolderProfile-x64` (default) | `win-x64` | Normal 64-bit IIS app pools |
| `FolderProfile-x86` | `win-x86` | Servers that sign with the USB token. The token driver `eps2003csp11.dll` is 32-bit and can't be loaded by a 64-bit process. |

Both are Release, framework-dependent builds. They write to `publish\<x64|x86>\<App>\` at the repository root; that folder isn't tracked by git. Copy each app's folder to its IIS site.

- **Visual Studio:** right-click a project, choose **Publish**, then pick `FolderProfile-x64` or `FolderProfile-x86`.
- **PowerShell**, from the repository root. Publish all three apps as x64:

  ```powershell
  .\publish.ps1
  ```

  Publish all three as x86:

  ```powershell
  .\publish.ps1 -Arch x86
  ```

  Publish one app:

  ```powershell
  .\publish.ps1 -Project EInvoiceAPI -Arch x86
  ```

- **dotnet CLI**, for one project:

  ```powershell
  dotnet publish src\EInvoiceAPI\EInvoiceAPI\EInvoiceAPI.csproj -c Release -p:PublishProfile=FolderProfile-x64
  ```

**Using the USB token.** EInvoiceAPI signs with the token, and EInvoiceAPIAdmin's Company screen tests it. On a server that uses the token, publish those apps with `FolderProfile-x86` and set the IIS app pool's **Enable 32-Bit Applications** to **True**. EInvoiceAPI ships `eps2003csp11.dll` in its publish folder. EInvoiceAPIAdmin loads it by name, so the driver must be installed on the server.

## Installer

**`EInvoiceAPI-Setup.exe`** installs and configures everything on an IIS server through a wizard. The wizard covers which apps to install, 64- or 32-bit, the IIS site and URL path, folders, the database password, the first Admin account, the SAP endpoints and an HTTPS certificate. It also handles prerequisites. Download it from the **Installer** workflow's artifacts, or build it with `installer\build-installer.ps1`. See [docs/INSTALLER.md](docs/INSTALLER.md).

## Hosting on IIS

| URL | App |
|---|---|
| `http(s)://<host>/EInvoiceAPI` | EInvoiceAPI |
| `http(s)://<host>/EInvoiceAPI/Admin` | EInvoiceAPIAdmin |
| `http(s)://<host>/EInvoiceAPI/ETANotifications` | IncomeTaxWebAPI |

[`deploy/Setup-IIS.ps1`](deploy/Setup-IIS.ps1) creates this layout: app pools, nested applications, environment variables and folder permissions. See [docs/IIS_DEPLOYMENT.md](docs/IIS_DEPLOYMENT.md) for the server map, the deployment steps, and the ETA portal and Microsoft mail addresses that change with these URLs. Each server needs the **ASP.NET Core 10.0 Hosting Bundle**.

## Shared configuration database

EInvoiceAPI and EInvoiceAPIAdmin share one LiteDB configuration database (`config.db`). Give both apps the same `Database__Path` and `Database__Password`. See [docs/MERGE_PLAN.md](docs/MERGE_PLAN.md) and the Admin app's [security-hardening runbook](src/EInvoiceAPIAdmin/docs/security-hardening.md).
