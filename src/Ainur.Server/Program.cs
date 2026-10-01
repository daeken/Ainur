using Ainur.Core;
using Ainur.Core.Runtime;
using Ainur.Server;

var options = ServerOptions.FromArgs(args);
// Validation instances run against disposable copies of live state and must not resume agent work on their own.
var runtimeOptions = new RuntimeOptions { Home = options.Home, AutoStartHosts = Environment.GetEnvironmentVariable("AINUR_VALIDATION") != "1" };

// Exactly one scheduler may dispatch work against a home directory at a time.
using var schedulerLock = SchedulerLock.TryAcquire(Path.Combine(options.Home, "runtime", "scheduler.lock"));
if(schedulerLock is null) {
	Console.Error.WriteLine($"Another Ainur runtime holds the scheduler lock for {options.Home}; exiting.");
	return 3;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(o => {
	o.SerializerOptions.PropertyNamingPolicy = JsonUtil.Options.PropertyNamingPolicy;
	o.SerializerOptions.DefaultIgnoreCondition = JsonUtil.Options.DefaultIgnoreCondition;
	foreach(var c in JsonUtil.Options.Converters) o.SerializerOptions.Converters.Add(c);
});

using var runtime = new AinurRuntime(runtimeOptions);
builder.Services.AddSingleton(runtime);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<EventHub>();

var app = builder.Build();
app.UseMiddleware<LocalOnlyMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
Api.Map(app);
app.MapFallbackToFile("index.html");

var hub = app.Services.GetRequiredService<EventHub>();
runtime.Db.Committed += hub.Publish;
runtime.Delta += hub.PublishDelta;
runtime.Start(options.Release);
Console.WriteLine($"Ainur runtime generation {runtime.Generation} ({options.Release ?? "dev"}) serving http://127.0.0.1:{options.Port} with home {options.Home}");

app.Lifetime.ApplicationStopping.Register(() => {
	runtime.DrainAsync(TimeSpan.FromSeconds(options.DrainSeconds)).GetAwaiter().GetResult();
});
await app.RunAsync();
return 0;
