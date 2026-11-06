using EInvoiceAPI.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using System;
using System.Collections.Generic;

namespace EInvoiceAPI
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            // Shared config.db with EInvoiceAPIAdmin: same Database:Path and Database:Password settings.
            TokenManager.Configure(Configuration);

            services.AddDistributedMemoryCache();

            // IIS only exists on Windows; non-Windows runtimes may not include the IIS types at all.
            if (OperatingSystem.IsWindows())
            {
                ConfigureIis(services);
            }

            services.AddSession();

            //services.ConfigureApplicationCookie(o =>
            //{
            //    o.ExpireTimeSpan = TimeSpan.FromSeconds(10);
            //    o.SlidingExpiration = true;
            //});

            services.AddMvc();

            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Invoice API", Version = "v1" });
            });
            services.AddScoped<Serializer>();
            services.AddScoped<Hasher>();
            services.AddControllers();
            services.AddSingleton<CertificateCache>();

        }

        // Kept in its own non-inlined method so IISServerOptions is only loaded when this runs.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ConfigureIis(IServiceCollection services)
        {
            services.Configure<IISServerOptions>(options =>
            {
                options.AllowSynchronousIO = true;
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            // Under IIS the application path (/EInvoiceAPI) is applied automatically. ASPNETCORE_PATHBASE
            // reproduces it when running outside IIS (Kestrel / Visual Studio) so URLs match production.
            var pathBase = Configuration["PathBase"];
            if (!string.IsNullOrEmpty(pathBase)) app.UsePathBase(pathBase);

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseHsts();
            }
            
            app.UseHttpsRedirection();

            app.UseRouting();

            //app.UseSession();

            app.UseSwagger();
            //app.UseSwagger(c =>
            //{
            //    c.RouteTemplate = "InvoiceAPICore/swagger/v1/swagger.json";
            //}
            //);
            //app.UseSwaggerUI(c =>
            //{
            //    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Invoice API");
            //});
            app.UseSwaggerUI(c =>
            {
                //string swaggerJsonBasePath = string.IsNullOrWhiteSpace(c.RoutePrefix) ? "." : "..";
                //c.SwaggerEndpoint($"{swaggerJsonBasePath}/swagger/v1/swagger.json", "Invoice API");
                c.SwaggerEndpoint($"v1/swagger.json", "Invoice API");
            });
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }
    }
}