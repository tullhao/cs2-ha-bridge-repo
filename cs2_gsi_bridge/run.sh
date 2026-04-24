#!/usr/bin/with-contenv bashio
set -e

CONFIG_PATH=/data/options.json
export MQTT_HOST="$(bashio::config 'mqtt_host')"
export MQTT_PORT="$(bashio::config 'mqtt_port')"
export MQTT_USERNAME="$(bashio::config 'mqtt_username')"
export MQTT_PASSWORD="$(bashio::config 'mqtt_password')"
export MQTT_BASE_TOPIC="$(bashio::config 'mqtt_base_topic')"
export DISCOVERY_PREFIX="$(bashio::config 'discovery_prefix')"
export PUBLISH_DISCOVERY="$(bashio::config 'publish_discovery')"

bashio::log.info "Starting CS2 GSI Bridge on port 3001"
bashio::log.info "MQTT host: ${MQTT_HOST}:${MQTT_PORT}"
bashio::log.info "MQTT base topic: ${MQTT_BASE_TOPIC}"

exec dotnet /app/CS2GsiBridge.dll
