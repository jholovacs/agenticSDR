using System.Text.Json.Serialization;
using AgenticSdr.Host;
using AgenticSdr.Radio;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Extensions.Tasks;

if (SetupCommands.TryHandle(args, out int setupCode))
    return setupCode;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5088");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.Configure<SdrOptions>(builder.Configuration.GetSection("Sdr"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SdrOptions>>().Value);
builder.Services.AddSingleton<ClipStore>();
builder.Services.AddSingleton<RecordingFiles>();
builder.Services.AddSingleton(sp =>
{
    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("AgenticSdr/0.1 (local amateur-radio receiver)");
    return new RepeaterLookup(http, sp.GetRequiredService<ILogger<RepeaterLookup>>());
});
builder.Services.AddSingleton(sp =>
{
    var env = sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>();
    return new LlmStore(AppPaths.SelectLlm(env.ContentRootPath));
});
builder.Services.AddSingleton(sp =>
{
    var http = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("AgenticSdr/0.1 (local amateur-radio receiver)");
    return new InstructAgent(sp.GetRequiredService<LlmStore>(), sp.GetRequiredService<RadioSession>(), http);
});
builder.Services.AddSingleton(sp => new SpeechDecoder(sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>().ContentRootPath));
builder.Services.AddSingleton<RadioSession>();
builder.Services.AddHostedService<RadioStartup>();
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<SdrTools>()
    .WithResources<SdrResources>()
    .WithPrompts<SdrPrompts>()
    .WithTasks(new InMemoryMcpTaskStore());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});
app.MapMcp("/mcp");
WebApi.Map(app);
app.Run();
return 0;

/// <summary>
/// Opens the first visible radio when the process starts, and closes it on shutdown.
/// If nothing is plugged in, the page stays up and the status line says how to attach one.
/// </summary>
public sealed class RadioStartup(RadioSession session, ILogger<RadioStartup> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => OpenFirst(cancellationToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task OpenFirst(CancellationToken cancellationToken)
    {
        try
        {
            var devices = RadioSession.ListDevices();
            if (devices.Count == 0)
            {
                session.Note("No SDR is visible. An RTL2832U dongle needs librtlsdr and, on Windows, the WinUSB driver for VID_0BDA PID_2838. A LimeSDR-USB needs the Cypress driver for VID_1D50 PID_6108 and LimeSuiteNG. Other radios need SoapySDR and that radio's module.");
                return;
            }

            RadioDeviceInfo? rtl = devices.FirstOrDefault(device => device.Driver.Equals("rtlsdr", StringComparison.OrdinalIgnoreCase));
            int index = rtl?.Index ?? 0;
            await session.OpenAsync(index, cancellationToken);
            log.LogInformation("Opened {Name}.", devices[index].Name);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "The radio was not opened at startup.");
            session.Note(ex.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        session.Close();
        return Task.CompletedTask;
    }
}
