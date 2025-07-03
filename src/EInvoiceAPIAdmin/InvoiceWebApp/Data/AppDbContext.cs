using InvoiceWebApp.Models;
using Microsoft.AspNetCore.Connections;
using Microsoft.EntityFrameworkCore;

namespace InvoiceWebApp.Data
{
    public class AppDbContext : DbContext
    {
        public DbSet<Company> Companies { get; set; }
        public DbSet<CompEnv> CompEnvs { get; set; }
        public DbSet<EInvoiceEnv> EInvoiceEnvs { get; set; }
        public DbSet<Settings> Settings { get; set; }
        public DbSet<Login> Logins { get; set; }


        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
            // Ensure database and tables are created
            var dbCreated = Database.EnsureCreated();

            if (dbCreated)
            {
                Database.ExecuteSqlRaw(@"
                    INSERT INTO [tb_Login] ([Id],[u_name],[u_pass],[IsInitial]) VALUES (1,'mina.mouris','Mariam1995',1);
                    INSERT INTO [tb_eInvoiceEnv] ([Id],[EnvName],[EnvTokenUrl],[EnvInvoicingUrl]) VALUES (1,'PreProd','https://id.preprod.eta.gov.eg/connect/token','https://api.preprod.invoicing.eta.gov.eg/api/v1/');
                    INSERT INTO [tb_eInvoiceEnv] ([Id],[EnvName],[EnvTokenUrl],[EnvInvoicingUrl]) VALUES (2,'Prod','https://id.eta.gov.eg/connect/token','https://api.invoicing.eta.gov.eg/api/v1/');
                    INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (1,'DBVersion','2025-06-28 19:40:00');
                    INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (2,'UseImpersonation','false');
                    INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (3,'ImpDomain',NULL);
                    INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (4,'ImpUsername',NULL);
                    INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (5,'ImpPassword',NULL);
                    INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (6,'DateTimeFormat','dd/MM/yyyy hh:mm:ss tt');
                    INSERT INTO [tb_Settings] ([Id],[SettingName],[SettingValue]) VALUES (7,'CertCacheLastRefresh',NULL);
                    CREATE VIEW V_Data as  
                        Select CompanyCode,CompanyName,TokenSerial,TokenPin,CertSerialNum,EnvId,EnvName,EnvTokenUrl,EnvInvoicingUrl,ClientId,ClientSecret,str_token,time_get,UseXMLTOJSON
                        From tb_company 
                        Left Join tb_CompEnv ON tb_CompEnv.CompanyId = tb_company.Id 
                        Left Join tb_eInvoiceEnv ON tb_eInvoiceEnv.Id = tb_CompEnv.EnvId;
                ");
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Company>(entity =>
            {
                entity.ToTable("tb_Company");
                //entity.Property(e => e.UseXMLTOJSON).IsRequired(false);
                // All string properties are required by default (non-nullable in model)
            });

            modelBuilder.Entity<CompEnv>(entity =>
            {
                entity.ToTable("tb_CompEnv");
                entity.Property(e => e.ClientId).IsRequired(false);
                entity.Property(e => e.ClientSecret).IsRequired(false);
                entity.Property(e => e.str_token).IsRequired(false);
                entity.Property(e => e.time_get).IsRequired(false);
            });

            modelBuilder.Entity<EInvoiceEnv>(entity =>
            {
                entity.ToTable("tb_eInvoiceEnv");
                // All string properties are required by default (non-nullable in model)
            });

            modelBuilder.Entity<Settings>(entity =>
            {
                entity.ToTable("tb_Settings");
                entity.Property(e => e.SettingValue).IsRequired(false);
                // All string properties are required by default (non-nullable in model)
            });

            modelBuilder.Entity<Login>(entity =>
            {
                entity.ToTable("tb_Login");
                // All string properties are required by default (non-nullable in model)
            });
        }
    }

}