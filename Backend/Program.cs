using System.Threading.Channels;
using JarvisBackend.Configuration;
using JarvisBackend.Data;
using JarvisBackend.Services;
using JarvisBackend.Services.Interfaces;
using JarvisBackend.Workers;
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


// ================= MQTT → RabbitMQ → Worker (ESP32 flow) =================

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection(MqttOptions.SectionName));
builder.Services.Configure<RedisOptions>(builder.Configuration.GetSection(RedisOptions.SectionName));

builder.Services.AddSingleton<RabbitMqService>();
builder.Services.AddSingleton<RedisService>();
builder.Services.AddSingleton<IMqttResponsePublisher, MqttResponsePublisher>();
builder.Services.AddHostedService<MqttListenerService>();
builder.Services.AddHostedService<AudioWorker>();

builder.Services.AddSingleton<IWhisperService, WhisperService>();

builder.Services.AddHttpClient<IOllamaService, OllamaService>((sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(config["Ollama:BaseUrl"] ?? "http://localhost:11434");
});

builder.Services.AddSingleton<ITtsService, PiperTtsService>();
builder.Services.AddSingleton<IVoicePipelineLogger, VoicePipelineLogger>();


// ================= 🔥 NEW: MEMORY + VECTOR =================

// MongoDB
builder.Services.AddSingleton<MongoService>();

// Memory (vector search)
builder.Services.AddSingleton<IMemoryService, MemoryService>();

// Reminder: store conversation turns (ReminderWorker consumes and stores)
builder.Services.AddSingleton(Channel.CreateUnbounded<ReminderItem>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }));
builder.Services.AddSingleton<ChannelReader<ReminderItem>>(sp => sp.GetRequiredService<Channel<ReminderItem>>().Reader);
builder.Services.AddSingleton<ChannelWriter<ReminderItem>>(sp => sp.GetRequiredService<Channel<ReminderItem>>().Writer);
builder.Services.AddSingleton<IReminderService, ReminderService>();
builder.Services.AddHostedService<ReminderWorker>();

// Profile: user facts (birthday, name) in Redis - better than vector for concrete facts
builder.Services.AddSingleton<IProfileService, ProfileService>();

// Roles: character roles in MongoDB (name, style, maxLength) for prompt building
builder.Services.AddSingleton<IRoleService, RoleService>();

// Knowledge (RAG: store docs, search by embedding, feed to Ollama)
builder.Services.AddSingleton<IKnowledgeService, KnowledgeService>();

// Embedding (Ollama embeddings API)
builder.Services.AddHttpClient<IEmbeddingService, EmbeddingService>((sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(config["Ollama:BaseUrl"] ?? "http://localhost:11434");
});


// ================= BUILD =================

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

// ================= INIT =================

var audioInput = Path.Combine(Directory.GetCurrentDirectory(), "audio", "input");
var audioTts = Path.Combine(Directory.GetCurrentDirectory(), "audio", "tts");

Directory.CreateDirectory(audioInput);
Directory.CreateDirectory(audioTts);


// ================= RUN =================

try
{
    var serverHost = app.Configuration["Server:Host"] ?? "0.0.0.0";
    var serverPort = app.Configuration["Server:Port"] ?? "5000";
    Log.Information("Starting JarvisBackend (MQTT + Redis + MongoDB)");
    Log.Information("ESP32: publish audio to jarvis/{{deviceId}}/audio/in → responses on jarvis/{{deviceId}}/audio/out and .../wav");
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