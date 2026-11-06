# ETA_EInvoiceAPI

Combined solution for the ETA e-invoicing and income tax projects. Open `ETA_EInvoiceAPI.sln` in Visual Studio 2022 or later.

| Project | Folder | Framework | Originally from |
|---|---|---|---|
| IncomeTaxWebAPI | `src/IncomeTaxWebAPI` | .NET Framework 4.5.2 | menams2010/IncomeTaxWebAPI (`master`) |
| EInvoiceAPI | `src/EInvoiceAPI` | .NET 7.0 | menams2010/EInvoiceAPI (`master`) |
| EInvoiceAPIAdmin | `src/EInvoiceAPIAdmin` | .NET 9.0 | menams2010/EInvoiceAPIAdmin (`Development`) |
| Admin regression tests | `src/EInvoiceAPIAdmin/tests` | .NET 9.0 | menams2010/EInvoiceAPIAdmin (`Development`) |

The full commit history of each original repository is preserved. Each project's original `.sln` is still in its folder if you want to open it on its own.

`IncomeTaxWebAPI` targets .NET Framework, so the full solution builds only on Windows with Visual Studio or MSBuild. The .NET projects also build with `dotnet build <project>` on any OS.

EInvoiceAPI and EInvoiceAPIAdmin share one LiteDB configuration database (`config.db`). See [docs/MERGE_PLAN.md](docs/MERGE_PLAN.md) and the Admin app's [security-hardening runbook](src/EInvoiceAPIAdmin/docs/security-hardening.md).
