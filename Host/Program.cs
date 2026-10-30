using System.Text.Json.Serialization;
using AgenticSdr.Host;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Extensions.Tasks;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5088");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.Configure<SdrOptions>(builder.Configuration.GetSection("Sdr"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SdrOptions>>().Value);
builder.Services.AddSingleton<ClipStore>();
builder.Services.AddSingleton(sp =>
{
    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("AgenticSdr/0.1 (local amateur-radio receiver)");
    return new RepeaterLookup(http, sp.GetRequiredService<ILogger<RepeaterLookup>>());
});
builder.Services.AddSingleton(sp =>
{
    var env = sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>();
    return new LlmStore(Path.Combine(env.ContentRootPath, "llm.json"));
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
app.UseStaticFiles();
app.MapMcp("/mcp");
WebApi.Map(app);
app.Run();

/// <summary>
/// Opens the first visible radio when the process starts, and closes it on shutdown.
/// If nothing is plugged in, the page stays up and the status line says how to attach one.
/// </summary>
public sealed class RadioStartup(RadioSession session, ILogger<RadioStartup> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = RadioSession.ListDevices();
            if (devices.Count == 0)
            {
                session.Note("No SDR is visible. An RTL2832U dongle needs librtlsdr and, on Windows, the WinUSB driver for VID_0BDA PID_2838. Other radios need SoapySDR and that radio's module.");
                return;
            }

            await session.OpenAsync(0, cancellationToken);
            log.LogInformation("Opened {Name}.", devices[0].Name);
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
