using System.Text.Json.Serialization;
using SmartX.Api.Endpoints;
using SmartX.Api.Models;
using SmartX.Api.Services;
using SmartX.Api.Services.Topology;

var builder = WebApplication.CreateBuilder(args);

// Part 1 services
builder.Services.AddSingleton<TelemetryStore>();
builder.Services.AddSingleton<SensorRegistry>();
builder.Services.AddSingleton<DeploymentValidator>();

// Part 2 - real-time command stream
builder.Services.AddSingleton<SmartXDataStructures>();   // every Part 2 data structure, named by purpose
builder.Services.AddSingleton<DeviceStateStore>();
builder.Services.AddSingleton<AlertTracker>();
builder.Services.AddSingleton<ActionEngine>();
builder.Services.AddSingleton<LiveBroadcaster>();
builder.Services.AddSingleton<GatewayPipeline>();
builder.Services.AddSingleton<CommandService>();
builder.Services.AddSingleton<SimulationControl>();
builder.Services.AddSingleton<DeviceListImporter>();

// Final PoE - Network Topology & Mesh Routing
builder.Services.AddSingleton<TelemetryHistoryIndex>();
builder.Services.AddSingleton<TopologyService>();

// Authentication - Firebase ID tokens checked on every /api request.
builder.Services.AddSingleton<FirebaseTokenValidator>();

builder.Services.AddHostedService<TelemetryProcessor>();
builder.Services.AddHostedService<HeartbeatMonitor>();
builder.Services.AddHostedService<AutomationLoop>();
builder.Services.AddHostedService<DeviceFleetSimulator>();

// Enums go over the wire as names ("Actuator", "Critical") - easier to read
// in the dashboard and in curl output. Numbers are still accepted on input,
// so the Part 1 registration payload keeps working.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddProblemDetails();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

// Any unhandled exception becomes a JSON 500 instead of tearing down the
// request pipeline - the dashboard shows the error and keeps running.
app.UseExceptionHandler();
app.UseCors();                                   // before auth so even 401s carry CORS headers
app.UseMiddleware<FirebaseAuthMiddleware>();     // see Services/FirebaseAuth.cs

app.Services.GetRequiredService<ActionEngine>().SeedHistoricalSessions();

// Load 48 hours of readings (one every 15 s) into each fleet sensor's AVL tree,
// so historical range queries work from the very first run.
app.Services.GetRequiredService<TelemetryHistoryIndex>().SeedHistory(
[
    (DeviceFleetSimulator.SensorA, "moisture"),
    (DeviceFleetSimulator.SensorB, "moisture"),
    (DeviceFleetSimulator.UtilityMeter, "wattage"),
    (DeviceFleetSimulator.TempC, "temperature"),
    (DeviceFleetSimulator.GridMeter, "wattage"),
], TimeSpan.FromHours(48), TimeSpan.FromSeconds(15));

app.MapGet("/", () => Results.Ok(new { service = "Smart-X Gateway API", status = "running", part = "Final PoE" }));
app.MapGet("/api/health", (GatewayPipeline p) => Results.Ok(new { status = "ok", time = DateTime.UtcNow, queue = p.Queue.StandardCount + p.Queue.PriorityCount }));

// Public: lets the login page tell whether the API side of Firebase is set up.
app.MapGet("/api/auth/status", (FirebaseTokenValidator v) => new { configured = v.IsConfigured, projectId = v.ProjectId });

// Protected: echoes who the API thinks you are (proves the token was accepted).
app.MapGet("/api/auth/me", (HttpContext ctx) => ctx.Items["user"] is AuthenticatedUser u ? Results.Ok(u) : Results.Unauthorized());

var uploadsRoot = Path.Combine(AppContext.BaseDirectory, "uploads");
Directory.CreateDirectory(uploadsRoot);

// Sensor registration

app.MapPost("/api/sensors", (SensorDevice device, SensorRegistry registry) =>
{
    var saved = registry.Register(device);
    return Results.Created($"/api/sensors/{saved.MacAddress}", saved);
});

app.MapGet("/api/sensors", (SensorRegistry registry) => registry.All());

app.MapGet("/api/sensors/{mac}", (string mac, SensorRegistry registry) =>
    registry.TryGet(mac, out var device) ? Results.Ok(device) : Results.NotFound());

// Media / log attachments 

app.MapPost("/api/sensors/{mac}/attachments", async (string mac, HttpRequest request, SensorRegistry registry) =>
{
    if (!request.HasFormContentType) return Results.BadRequest("Expected multipart/form-data.");
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.BadRequest("No file provided.");

    var deviceDir = Path.Combine(uploadsRoot, mac.Replace(":", "-"));
    Directory.CreateDirectory(deviceDir);
    var storedName = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Path.GetFileName(file.FileName)}";
    var fullPath = Path.Combine(deviceDir, storedName);

    await using (var stream = File.Create(fullPath))
    {
        await file.CopyToAsync(stream);
    }

    var added = registry.AddAttachment(mac, storedName);
    return added ? Results.Ok(new { storedName }) : Results.NotFound("Sensor not registered.");
});

// Bulk import: upload a .txt device list. Every valid line is registered and
// goes live straight away (sensor list, Command Stream grid, category index,
// simulator). Returns what was imported and why any lines were skipped.
app.MapPost("/api/devices/import", async (HttpRequest request, DeviceListImporter importer, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart/form-data with a 'file' field." });
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "No file provided." });
    if (file.Length > DeviceListImporter.MaxFileBytes) return Results.BadRequest(new { error = "File is larger than 1 MB." });

    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext is not (".txt" or ".csv")) return Results.BadRequest(new { error = "Upload a .txt (or .csv) device list." });

    // Keep a copy of what was imported, alongside the other uploads.
    var importDir = Path.Combine(uploadsRoot, "imports");
    Directory.CreateDirectory(importDir);
    var storedName = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Path.GetFileName(file.FileName)}";
    await using (var copy = File.Create(Path.Combine(importDir, storedName)))
    {
        await file.CopyToAsync(copy, ct);
    }

    await using var stream = file.OpenReadStream();
    var result = await importer.ImportAsync(stream, ct);
    return Results.Ok(new { storedName, result.Imported, result.Updated, result.LinesRead, result.Skipped, result.Devices });
}).DisableAntiforgery();

app.MapGet("/api/sensors/{mac}/attachments", (string mac, SensorRegistry registry) =>
    registry.TryGet(mac, out var device) ? Results.Ok(device.AttachmentFiles) : Results.NotFound());

//Telemetry ingestion

// Part 2: telemetry no longer goes straight into the store. It is parsed,
// classified and dropped into the DualLaneQueue - normal readings into the
// FIFO Queue<T>, out-of-band readings into the PriorityQueue. The
// TelemetryProcessor background worker drains it (priority lane first).
app.MapPost("/api/telemetry", (TelemetryIngestRequest req, GatewayPipeline pipeline) =>
{
    var result = pipeline.Accept(req);
    if (!result.Ok)
    {
        return result.Error!.Contains("full")
            ? Results.Json(new { accepted = false, error = result.Error }, statusCode: 503)
            : Results.BadRequest(new { accepted = false, error = result.Error });
    }

    return Results.Accepted(value: new
    {
        accepted = true,
        sequence = result.Packet!.Sequence,
        severity = result.Packet.Severity.ToString(),
        lane = result.Lane,
        queuePosition = result.Position,
    });
});

app.MapPost("/api/telemetry/batch", (string deviceId, string metric, double[][] batches, TelemetryStore store) =>
{
    store.IngestRawBatches(deviceId, metric, batches);
    return Results.Ok(new { rowsIngested = batches.Length });
});

app.MapGet("/api/telemetry/snapshot", (TelemetryStore store) => store.Snapshot());

app.MapGet("/api/anomalies", (TelemetryStore store) => store.RecentAnomalies());

// Deployment tree recursion 

app.MapPost("/api/deployment/validate", (DeploymentNode tree, DeploymentValidator validator) =>
    validator.Validate(tree));

app.MapGet("/api/deployment/seed", () => new DeploymentNode
{
    Name = "Facility A",
    Children =
    [
        new DeploymentNode
        {
            Name = "Zone 1",
            Children =
            [
                new DeploymentNode
                {
                    Name = "Sub-Zone B",
                    Children =
                    [
                        new DeploymentNode { Name = "Node A", DeviceMac = "AA:BB:CC:00:01" },
                        new DeploymentNode { Name = "Node B", DeviceMac = "AA:BB:CC:00:02" },
                    ],
                },
            ],
        },
        new DeploymentNode
        {
            Name = "Zone 2",
            Children = [new DeploymentNode { Name = "Utility Room", DeviceMac = "AA:BB:CC:00:03" }],
        },
    ],
});



app.MapGet("/api/demo/power-aggregate", () =>
{
    var meter1 = new PowerReading("Meter1", 640.5);
    var meter2 = new PowerReading("Meter2", 812.25);
    var combined = meter1 + meter2;
    var delta = meter2 - meter1;
    return Results.Ok(new
    {
        meter1 = meter1.ToString(),
        meter2 = meter2.ToString(),
        combined = combined.ToString(),
        delta = delta.ToString(),
        meter2ExceedsMeter1 = meter2 > meter1,
    });
});

app.MapCommandStream();
app.MapTopology();

app.Run();
