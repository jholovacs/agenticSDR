using System.Text.Json.Serialization;
using AgenticSdr.Cw;
using AgenticSdr.Privilege;
using AgenticSdr.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5091");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var chat = new CwChat();
var ids = new CwIdTracker();
var preference = new RadioPreferenceStore(SharedPaths.RadioJson());
var corrections = new RadioCorrectionStore(SharedPaths.RadioCorrectionsJson());
CwRadioHolder? holder = null;
var listener = new CwListener(chat, () => holder?.Radio);
holder = new CwRadioHolder(preference, corrections, listener);

builder.Services.AddSingleton(new OperatorSession(SharedPaths.OperatorJson()));
builder.Services.AddSingleton(new AntennaStore(SharedPaths.AntennaJson()));
builder.Services.AddSingleton(new UiPrefsStore(SharedPaths.UiPrefsJson()));
builder.Services.AddSingleton(preference);
builder.Services.AddSingleton(corrections);
builder.Services.AddSingleton(chat);
builder.Services.AddSingleton(ids);
builder.Services.AddSingleton(listener);
builder.Services.AddSingleton(holder!);
builder.Services.AddSingleton(_ => new LlmStore(SharedPaths.LlmJson(), SharedPaths.LlmJson()));
builder.Services.AddSingleton(sp =>
{
    var http = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("AgenticSdr.Cw/0.1 (CW mode desk)");
    return new CwAgent(
        sp.GetRequiredService<LlmStore>(),
        sp.GetRequiredService<CwChat>(),
        sp.GetRequiredService<OperatorSession>(),
        sp.GetRequiredService<CwRadioHolder>(),
        http);
});

var app = builder.Build();
app.Services.GetRequiredService<OperatorSession>().Restore();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});
CwApi.Map(app);
app.Run();
