using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:3001");

var app = builder.Build();

var mqttHost = Env("MQTT_HOST", "core-mosquitto");
var mqttPort = int.TryParse(Env("MQTT_PORT", "1883"), out var parsedPort) ? parsedPort : 1883;
var mqttUser = Env("MQTT_USERNAME", "");
var mqttPass = Env("MQTT_PASSWORD", "");
var baseTopic = Env("MQTT_BASE_TOPIC", "cs2_bridge").Trim('/');
var discoveryPrefix = Env("DISCOVERY_PREFIX", "homeassistant").Trim('/');
var publishDiscovery = bool.TryParse(Env("PUBLISH_DISCOVERY", "true"), out var pd) && pd;

var mqttFactory = new MqttFactory();
var mqttClient = mqttFactory.CreateMqttClient();
var mqttOptionsBuilder = new MqttClientOptionsBuilder().WithTcpServer(mqttHost, mqttPort);
if (!string.IsNullOrWhiteSpace(mqttUser))
    mqttOptionsBuilder = mqttOptionsBuilder.WithCredentials(mqttUser, mqttPass);
var mqttOptions = mqttOptionsBuilder.Build();

await EnsureConnectedAsync(mqttClient, mqttOptions);
if (publishDiscovery)
    await PublishDiscoveryAsync(mqttClient, discoveryPrefix, baseTopic);

app.MapPost("/", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var raw = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(raw))
        return Results.BadRequest("Empty body");

    Console.WriteLine($"Received GSI payload: {raw}");
    await EnsureConnectedAsync(mqttClient, mqttOptions);

    await PublishStringAsync(mqttClient, $"{baseTopic}/state/raw", raw);

    try
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var roundPhase = GetString(root, "round", "phase");
        var bombState = GetString(root, "bomb", "state");
        var bombCountdown = GetDouble(root, "bomb", "countdown");
        var roundBomb = GetString(root, "round", "bomb");
        var phaseName = GetString(root, "phase_countdowns", "phase");
        var phaseTimeLeft = GetDouble(root, "phase_countdowns", "phase_ends_in");
        var activity = GetString(root, "player", "activity");
        var team = GetString(root, "player", "team");
        var health = GetInt(root, "player", "state", "health");
        var mapPhase = GetString(root, "map", "phase");

        await PublishIfHasValueAsync(mqttClient, $"{baseTopic}/round/phase", roundPhase);
        await PublishIfHasValueAsync(mqttClient, $"{baseTopic}/bomb/state", bombState);
        await PublishIfHasValueAsync(mqttClient, $"{baseTopic}/round/bomb", roundBomb);
        await PublishIfHasValueAsync(mqttClient, $"{baseTopic}/phase/name", phaseName);
        await PublishIfHasValueAsync(mqttClient, $"{baseTopic}/player/activity", activity);
        await PublishIfHasValueAsync(mqttClient, $"{baseTopic}/player/team", team);
        await PublishIfHasValueAsync(mqttClient, $"{baseTopic}/map/phase", mapPhase);
        if (bombCountdown is not null) await PublishStringAsync(mqttClient, $"{baseTopic}/bomb/countdown", bombCountdown.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        if (phaseTimeLeft is not null) await PublishStringAsync(mqttClient, $"{baseTopic}/phase/time_left", phaseTimeLeft.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        if (health is not null) await PublishStringAsync(mqttClient, $"{baseTopic}/player/health", health.Value.ToString());

        var blinkMs = ComputeBlinkIntervalMs(bombState, bombCountdown, phaseName, phaseTimeLeft);
        if (blinkMs is not null) await PublishStringAsync(mqttClient, $"{baseTopic}/light/blink_interval_ms", blinkMs.Value.ToString());

        var recommendedColor = ComputeRecommendedColor(bombState, bombCountdown, roundPhase, activity);
        if (!string.IsNullOrWhiteSpace(recommendedColor))
            await PublishStringAsync(mqttClient, $"{baseTopic}/light/recommended_color", recommendedColor!);

        return Results.Ok(new
        {
            ok = true,
            bombState,
            bombCountdown,
            roundPhase,
            phaseName,
            phaseTimeLeft,
            blinkMs,
            recommendedColor
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex);
        return Results.BadRequest(ex.Message);
    }
});

app.MapGet("/health", () => Results.Ok(new { ok = true }));

await app.RunAsync();

static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;

static async Task EnsureConnectedAsync(IMqttClient client, MQTTnet.Client.MqttClientOptions options)
{
    if (client.IsConnected) return;
    await client.ConnectAsync(options, CancellationToken.None);
}

static async Task PublishStringAsync(IMqttClient client, string topic, string payload, bool retain = true)
{
    var message = new MqttApplicationMessageBuilder()
        .WithTopic(topic)
        .WithPayload(payload)
        .WithRetainFlag(retain)
        .Build();

    await client.PublishAsync(message, CancellationToken.None);
}

static async Task PublishIfHasValueAsync(IMqttClient client, string topic, string? payload, bool retain = true)
{
    if (!string.IsNullOrWhiteSpace(payload))
        await PublishStringAsync(client, topic, payload!, retain);
}

static async Task PublishDiscoveryAsync(IMqttClient client, string discoveryPrefix, string baseTopic)
{
    var device = new
    {
        identifiers = new[] { "cs2_gsi_bridge" },
        name = "CS2 GSI Bridge",
        manufacturer = "OpenAI scaffold",
        model = "CS2 GSI -> MQTT",
        sw_version = "0.1.0"
    };

    async Task PublishSensor(string key, string name, string stateTopic, string? deviceClass = null, string? unit = null, string? icon = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["unique_id"] = $"cs2_gsi_bridge_{key}",
            ["default_entity_id"] = $"sensor.cs2_gsi_bridge_{key}",
            ["state_topic"] = stateTopic,
            ["device"] = device,
        };
        if (deviceClass is not null) payload["device_class"] = deviceClass;
        if (unit is not null) payload["unit_of_measurement"] = unit;
        if (icon is not null) payload["icon"] = icon;

        var topic = $"{discoveryPrefix}/sensor/cs2_gsi_bridge/{key}/config";
        await PublishStringAsync(client, topic, JsonSerializer.Serialize(payload));
    }

    await PublishSensor("bomb_state", "CS2 Bomb State", $"{baseTopic}/bomb/state", icon: "mdi:bomb");
    await PublishSensor("bomb_countdown", "CS2 Bomb Countdown", $"{baseTopic}/bomb/countdown", unit: "s", icon: "mdi:timer-outline");
    await PublishSensor("round_phase", "CS2 Round Phase", $"{baseTopic}/round/phase", icon: "mdi:timer-play-outline");
    await PublishSensor("phase_name", "CS2 Phase Name", $"{baseTopic}/phase/name", icon: "mdi:timeline-clock-outline");
    await PublishSensor("phase_time_left", "CS2 Phase Time Left", $"{baseTopic}/phase/time_left", unit: "s", icon: "mdi:timer-sand");
    await PublishSensor("player_activity", "CS2 Player Activity", $"{baseTopic}/player/activity", icon: "mdi:run-fast");
    await PublishSensor("player_team", "CS2 Player Team", $"{baseTopic}/player/team", icon: "mdi:account-group");
    await PublishSensor("player_health", "CS2 Player Health", $"{baseTopic}/player/health", icon: "mdi:heart-pulse");
    await PublishSensor("blink_interval_ms", "CS2 Blink Interval", $"{baseTopic}/light/blink_interval_ms", unit: "ms", icon: "mdi:led-on");
    await PublishSensor("recommended_color", "CS2 Recommended Color", $"{baseTopic}/light/recommended_color", icon: "mdi:palette");
}

static string? GetString(JsonElement root, params string[] path)
{
    if (!TryGet(root, out var value, path)) return null;
    return value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };
}

static int? GetInt(JsonElement root, params string[] path)
{
    if (!TryGet(root, out var value, path)) return null;
    return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}

static double? GetDouble(JsonElement root, params string[] path)
{
    if (!TryGet(root, out var value, path)) return null;
    return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
}

static bool TryGet(JsonElement element, out JsonElement value, params string[] path)
{
    value = element;
    foreach (var part in path)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value))
            return false;
    }
    return true;
}

static int? ComputeBlinkIntervalMs(string? bombState, double? bombCountdown, string? phaseName, double? phaseTimeLeft)
{
    if (string.Equals(bombState, "planted", StringComparison.OrdinalIgnoreCase) && bombCountdown is > 0)
    {
        var t = bombCountdown.Value;
        if (t > 35) return 900;
        if (t > 30) return 820;
        if (t > 25) return 720;
        if (t > 20) return 620;
        if (t > 15) return 500;
        if (t > 10) return 380;
        if (t > 6) return 260;
        if (t > 3) return 180;
        return 120;
    }

    if (string.Equals(phaseName, "live", StringComparison.OrdinalIgnoreCase) && phaseTimeLeft is > 0)
    {
        var t = phaseTimeLeft.Value;
        if (t <= 20) return 700;
    }

    return null;
}

static string? ComputeRecommendedColor(string? bombState, double? bombCountdown, string? roundPhase, string? activity)
{
    if (string.Equals(bombState, "planted", StringComparison.OrdinalIgnoreCase))
    {
        if (bombCountdown is <= 10) return "red";
        return "yellow";
    }

    if (string.Equals(roundPhase, "freezetime", StringComparison.OrdinalIgnoreCase)) return "white";
    if (string.Equals(activity, "menu", StringComparison.OrdinalIgnoreCase)) return "off";
    return null;
}
