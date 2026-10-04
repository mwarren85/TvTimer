using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Serilog;
using TvTimer;
using TvTimer.Configuration;

var builder = Host.CreateDefaultBuilder(args)
    .UseSystemd()
    .UseSerilog((context, _, configuration) =>
        configuration.ReadFrom.Configuration(context.Configuration))
    .ConfigureServices((context, services) =>
    {
        services.Configure<SonyOptions>(context.Configuration.GetSection("Sony"));
        services.Configure<ScheduleOptions>(context.Configuration.GetSection("Schedule"));

        services.AddHttpClient<SonyBraviaClient>()
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var sonyOptions = sp.GetRequiredService<IOptions<SonyOptions>>().Value;
                var handler = new HttpClientHandler();

                if (sonyOptions.UseHttps)
                {
                    if (string.IsNullOrWhiteSpace(sonyOptions.CaCertPath))
                    {
                        throw new InvalidOperationException(
                            "Sony:UseHttps is true but Sony:CaCertPath is not set. " +
                            "Export the TV's root certificate and point CaCertPath at it.");
                    }

                    var trustedRoot = X509CertificateLoader.LoadCertificateFromFile(sonyOptions.CaCertPath);

                    // TVs' embedded TLS stacks are typically TLS 1.2-only and can reset the
                    // connection outright rather than gracefully downgrading when offered a
                    // modern TLS 1.3 ClientHello, so pin to 1.2 for this connection.
                    handler.SslProtocols = SslProtocols.Tls12;

                    // The TV's cert is self-signed and issued for its bare IP, not a DNS
                    // name — so we pin trust to this specific exported root instead of
                    // relying on the OS trust store or hostname/SAN matching.
                    handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                    {
                        if (cert is null)
                        {
                            return false;
                        }

                        using var chain = new X509Chain();
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
                        return chain.Build(new X509Certificate2(cert));
                    };
                }

                return handler;
            });

        if (!args.Contains("--pair"))
        {
            services.AddHostedService<Worker>();
        }
    });

try
{
    if (args.Contains("--pair"))
    {
        using var host = builder.Build();
        var tvClient = host.Services.GetRequiredService<SonyBraviaClient>();
        await tvClient.PairInteractivelyAsync();
        return;
    }

    await builder.Build().RunAsync();
}
finally
{
    Log.CloseAndFlush();
}
