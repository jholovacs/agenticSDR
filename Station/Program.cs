using System.Text.Json.Serialization;
using AgenticSdr.Host;
using AgenticSdr.Station;
using ModelContextProtocol.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5090");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSingleton<StationDesk>();
builder.Services.AddSingleton<StationReceiver>();
builder.Services.AddSingleton(services =>
{
    string root = services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
    return new StationAntenna(AppPaths.SelectAntenna(root));
});
builder.Services.AddSingleton(services =>
{
    string root = services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
    string antenna = AppPaths.SelectAntenna(root);
    string? directory = Path.GetDirectoryName(antenna);
    string path = Path.Combine(string.IsNullOrEmpty(directory) ? root : directory, "station-operator.json");
    return new StationMemory(path);
});
builder.Services.AddSingleton(services =>
{
    string root = services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
    string antenna = AppPaths.SelectAntenna(root);
    string? directory = Path.GetDirectoryName(antenna);
    string path = Path.Combine(string.IsNullOrEmpty(directory) ? root : directory, "station-repeater.json");
    return new RepeaterMemory(path);
});
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<StationTools>();

var app = builder.Build();
app.Services.GetRequiredService<StationMemory>().Restore(app.Services.GetRequiredService<StationDesk>());
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});
app.MapMcp("/mcp");
StationApi.Map(app);
app.Run();
