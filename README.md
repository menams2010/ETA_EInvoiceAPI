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

## Hosting on IIS

All three apps are now ASP.NET Core apps. Each server needs the **ASP.NET Core 10.0 Hosting Bundle**, and each app pool should use **No Managed Code**. `dotnet publish` generates the `web.config` for the ASP.NET Core Module.

## Shared configuration database

EInvoiceAPI and EInvoiceAPIAdmin share one LiteDB configuration database (`config.db`). Give both apps the same `Database__Path` and `Database__Password`. See [docs/MERGE_PLAN.md](docs/MERGE_PLAN.md) and the Admin app's [security-hardening runbook](src/EInvoiceAPIAdmin/docs/security-hardening.md).
