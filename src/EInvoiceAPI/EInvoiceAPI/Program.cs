using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EInvoiceAPI
{
    public class Program
    {
        public static void Main(string[] args)
        {
                CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    // Out-of-process under IIS the app runs on Kestrel; same synchronous I/O setting as IIS in-process.
                    webBuilder.ConfigureKestrel(options => options.AllowSynchronousIO = true);
                    webBuilder.UseStartup<Startup>();
                });
    }
}
