using System.Net.WebSockets;
using System.Text;
using JarvisBackend.Configuration;
using JarvisBackend.Services;
using JarvisBackend.Services.Interfaces;
using JarvisBackend.WebSockets;
using JarvisBackend.Workers;
using Microsoft.Extensions.Options;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://0.0.0.0:5000");

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

builder.Services.AddControllers();

// WebSocket → RabbitMQ → Worker (ws://localhost:5000/ws)
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.AddSingleton<RabbitMqService>();
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<AudioWebSocketHandler>();
builder.Services.AddHostedService<AudioWorker>();

builder.Services.AddSingleton<IWhisperService, WhisperService>();
builder.Services.AddHttpClient<IOllamaService, OllamaService>((sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(config["Ollama:BaseUrl"] ?? "http://localhost:11434");
});
builder.Services.AddSingleton<ITtsService, PiperTtsService>();
builder.Services.AddSingleton<IVoicePipelineLogger, VoicePipelineLogger>();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.MapControllers();

// Removed: /ws/stream no longer exists. Tell clients to use /ws.
app.Map("/ws/stream", async context =>
{
    context.Response.StatusCode = 400;
    context.Response.ContentType = "text/plain";
    await context.Response.WriteAsync("Use /ws instead. Connect to ws://host:5000/ws (not /ws/stream).");
});

// WebSocket → RabbitMQ → Worker: ws://localhost:5000/ws
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    var options = context.RequestServices.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
    if (!options.Enabled)
    {
        context.Response.StatusCode = 503;
        await context.Response.WriteAsync("RabbitMQ pipeline is disabled. Set RabbitMQ:Enabled to true.");
        return;
    }
    var socket = await context.WebSockets.AcceptWebSocketAsync();
    var handler = context.RequestServices.GetRequiredService<AudioWebSocketHandler>();
    await handler.HandleAsync(socket, context.RequestAborted);
});

// Ensure audio directories exist
var audioInput = Path.Combine(Directory.GetCurrentDirectory(), "audio", "input");
var audioTts = Path.Combine(Directory.GetCurrentDirectory(), "audio", "tts");
Directory.CreateDirectory(audioInput);
Directory.CreateDirectory(audioTts);

try
{
    Log.Information("Starting JarvisBackend (WebSocket only)");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
