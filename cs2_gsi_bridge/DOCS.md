# CS2 GSI Bridge

This app receives Counter-Strike 2 GSI HTTP POSTs on port `3001` and publishes:

- `cs2_bridge/state/raw`
- `cs2_bridge/bomb/state`
- `cs2_bridge/bomb/countdown`
- `cs2_bridge/round/phase`
- `cs2_bridge/round/bomb`
- `cs2_bridge/phase/name`
- `cs2_bridge/phase/time_left`
- `cs2_bridge/player/activity`
- `cs2_bridge/player/team`
- `cs2_bridge/player/health`

If `publish_discovery` is enabled, the app also creates MQTT discovery entities in Home Assistant.

## Configuration

- `mqtt_host`: MQTT broker host. For Home Assistant Mosquitto, `core-mosquitto` usually works.
- `mqtt_port`: MQTT broker port.
- `mqtt_username`: MQTT username.
- `mqtt_password`: MQTT password.
- `mqtt_base_topic`: Prefix for published topics.
- `discovery_prefix`: Usually `homeassistant`.
- `publish_discovery`: Creates Home Assistant MQTT discovery entities.

## CS2 config on the gaming PC

Create or update:

`game/csgo/cfg/gamestate_integration_cs2bridge.cfg`

and point it to your Home Assistant box:

```cfg
"CS2 GSI Bridge"
{
 "uri" "http://192.168.178.111:3001/"
 "timeout" "5.0"
 "buffer"  "0.1"
 "throttle" "0.1"
 "heartbeat" "1.0"
 "data"
 {
   "provider"            "1"
   "map"                 "1"
   "round"               "1"
   "player_id"           "1"
   "player_state"        "1"
   "player_match_stats"  "1"
   "phase_countdowns"    "1"
   "bomb"                "1"
 }
}
```

## Disable cs2mqtt?

Yes, for a clean setup it is recommended to stop or uninstall `cs2mqtt` and remove or rename its old GSI config file so only one bridge receives and publishes your CS2 state.
