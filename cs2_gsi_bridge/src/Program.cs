using System.Text;
using System.Text.Json;
using CounterStrike2GSI;
using CounterStrike2GSI.Nodes;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

record Options(
    string mqtt_host = "core-mosquitto",
    int mqtt_port = 1883,
    string mqtt_username = "",
    string mqtt_password = "",
    string mqtt_base_topic = "cs2_bridge",
    string discovery_prefix = "homeassistant",
    bool publish_discovery = true
);

class Program
{
    static async Task Main(string[] args)
    {
        var options = LoadOptions("/data/options.json");

        Console.WriteLine("Starting CS2 GSI Bridge on port 3001");
        Console.WriteLine($"MQTT host: {options.mqtt_host}:{options.mqtt_port}");
        Console.WriteLine($"MQTT base topic: {options.mqtt_base_topic}");
        Console.WriteLine($"Discovery enabled: {options.publish_discovery}");

        var mqttFactory = new MqttClientFactory();
        using var mqttClient = mqttFactory.CreateMqttClient();

        var mqttOptionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(options.mqtt_host, options.mqtt_port);

        if (!string.IsNullOrWhiteSpace(options.mqtt_username))
        {
            mqttOptionsBuilder = mqttOptionsBuilder.WithCredentials(
                options.mqtt_username,
                options.mqtt_password ?? ""
            );
        }

        var mqttOptions = mqttOptionsBuilder.Build();

        await EnsureConnectedAsync(mqttClient, mqttOptions);

        if (options.publish_discovery)
        {
            await PublishDiscoveryAsync(mqttClient, options);
        }

        var listener = new GameStateListener(3001);

        listener.NewGameState += async (gameState) =>
        {
            try
            {
                await EnsureConnectedAsync(mqttClient, mqttOptions);
                await PublishStateAsync(mqttClient, options, gameState);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Publish error: {ex}");
            }
        };

        if (!listener.Start())
        {
            Console.WriteLine("Failed to start GSI listener on port 3001");
            return;
        }

        Console.WriteLine("GSI listener started on port 3001");

        await Task.Delay(Timeout.Infinite);
    }

    static Options LoadOptions(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Console.WriteLine("No /data/options.json found, using defaults.");
                return new Options();
            }

            var json = File.ReadAllText(path);
            var opts = JsonSerializer.Deserialize<Options>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return opts ?? new Options();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load options.json, using defaults. Error: {ex.Message}");
            return new Options();
        }
    }

    static async Task EnsureConnectedAsync(IMqttClient client, MqttClientOptions options)
    {
        if (client.IsConnected) return;
        await client.ConnectAsync(options, CancellationToken.None);
    }

    static async Task PublishStateAsync(IMqttClient client, Options options, GameState gs)
    {
        var baseTopic = options.mqtt_base_topic.TrimEnd('/');

        await PublishAsync(client, $"{baseTopic}/state/raw", JsonSerializer.Serialize(gs));

        var roundPhase = gs.Round?.Phase ?? "";
        var bombState = gs.Round?.Bomb ?? "";
        var phaseName = gs.Map?.Phase ?? "";

        double phaseTimeLeft = 0;
        if (double.TryParse(gs.PhaseCountdowns?.PhaseEndsIn ?? "", System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var phaseSeconds))
            phaseTimeLeft = phaseSeconds;

        double bombCountdown = 0;
        if (double.TryParse(gs.Bomb?.Countdown ?? "", System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var bombSeconds))
            bombCountdown = bombSeconds;

        var playerActivity = gs.Player?.Activity ?? "";
        var playerTeam = gs.Player?.Team ?? "";
        var playerHealth = gs.Player?.State?.Health ?? 0;

        await PublishAsync(client, $"{baseTopic}/round/phase", roundPhase);
        await PublishAsync(client, $"{baseTopic}/round/bomb", bombState);
        await PublishAsync(client, $"{baseTopic}/phase/name", phaseName);
        await PublishAsync(client, $"{baseTopic}/phase/time_left", phaseTimeLeft.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await PublishAsync(client, $"{baseTopic}/bomb/state", gs.Bomb?.State ?? bombState);
        await PublishAsync(client, $"{baseTopic}/bomb/countdown", bombCountdown.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await PublishAsync(client, $"{baseTopic}/player/activity", playerActivity);
        await PublishAsync(client, $"{baseTopic}/player/team", playerTeam);
        await PublishAsync(client, $"{baseTopic}/player/health", playerHealth.ToString());

        var recommendedColor = "white";
        var blinkIntervalMs = 0;

        if (!string.IsNullOrWhiteSpace(gs.Bomb?.State) &&
            gs.Bomb.State.Equals("planted", StringComparison.OrdinalIgnoreCase))
        {
            recommendedColor = "yellow";
            blinkIntervalMs = CalculateBlinkIntervalMs(bombCountdown);
        }
        else if (phaseName.Equals("freezetime", StringComparison.OrdinalIgnoreCase) ||
                 roundPhase.Equals("freezetime", StringComparison.OrdinalIgnoreCase))
        {
            recommendedColor = "white";
            blinkIntervalMs = 0;
        }

        await PublishAsync(client, $"{baseTopic}/light/recommended_color", recommendedColor);
        await PublishAsync(client, $"{baseTopic}/light/blink_interval_ms", blinkIntervalMs.ToString());
    }

    static int CalculateBlinkIntervalMs(double bombCountdown)
    {
        if (bombCountdown <= 0) return 0;
        if (bombCountdown > 30) return 900;
        if (bombCountdown > 20) return 700;
        if (bombCountdown > 12) return 500;
        if (bombCountdown > 7) return 320;
        if (bombCountdown > 3) return 200;
        return 140;
    }

    static async Task PublishDiscoveryAsync(IMqttClient client, Options options)
    {
        var discovery = options.discovery_prefix.TrimEnd('/');
        var baseTopic = options.mqtt_base_topic.TrimEnd('/');
        var deviceId = "cs2_gsi_bridge";
        var deviceName = "CS2 GSI Bridge";

        await PublishDiscoverySensor(client, discovery, deviceId, "bomb_state", "Bomb State", $"{baseTopic}/bomb/state", "mdi:bomb");
        await PublishDiscoverySensor(client, discovery, deviceId, "bomb_countdown", "Bomb Countdown", $"{baseTopic}/bomb/countdown", "mdi:timer-outline", "s");
        await PublishDiscoverySensor(client, discovery, deviceId, "round_phase", "Round Phase", $"{baseTopic}/round/phase", "mdi:flag-outline");
        await PublishDiscoverySensor(client, discovery, deviceId, "phase_time_left", "Phase Time Left", $"{baseTopic}/phase/time_left", "mdi:timer-sand", "s");
        await PublishDiscoverySensor(client, discovery, deviceId, "blink_interval_ms", "Blink Interval", $"{baseTopic}/light/blink_interval_ms", "mdi:lightbulb-auto", "ms");
        await PublishDiscoverySensor(client, discovery, deviceId, "recommended_color", "Recommended Color", $"{baseTopic}/light/recommended_color", "mdi:palette");
    }

    static async Task PublishDiscoverySensor(
        IMqttClient client,
        string discoveryPrefix,
        string deviceId,
        string objectId,
        string name,
        string stateTopic,
        string icon,
        string? unit = null)
    {
        var topic = $"{discoveryPrefix}/sensor/{deviceId}/{objectId}/config";

        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["unique_id"] = $"{deviceId}_{objectId}",
            ["state_topic"] = stateTopic,
            ["icon"] = icon,
            ["device"] = new Dictionary<string, object?>
            {
                ["identifiers"] = new[] { deviceId },
                ["name"] = "CS2 GSI Bridge",
                ["manufacturer"] = "tullhao",
                ["model"] = "CS2 GSI MQTT Bridge"
            }
        };

        if (!string.IsNullOrWhiteSpace(unit))
            payload["unit_of_measurement"] = unit;

        await PublishAsync(client, topic, JsonSerializer.Serialize(payload), true);
    }

    static async Task PublishAsync(IMqttClient client, string topic, string payload, bool retain = false)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        await client.PublishAsync(message, CancellationToken.None);
    }
}
