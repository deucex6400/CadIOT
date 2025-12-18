// SPDX-License-Identifier: MIT
// ESP32 + Azure SDK for Embedded C (Arduino) with Direct Method + C2D fallback

#include <WiFi.h>
#include <mqtt_client.h>
#include <az_core.h>
#include <az_iot.h>
#include <azure_ca.h>

#include "AzIoTSasToken.h"
#include "iot_configs.h"

#define RELAY_PIN 23
#ifndef RELAY_PULSE_MS
#define RELAY_PULSE_MS 0
#endif
#define AZURE_SDK_CLIENT_USER_AGENT "c%2F" AZ_SDK_VERSION_STRING " (ard;esp32)"

static esp_mqtt_client_handle_t mqtt_client;
static az_iot_hub_client hub_client;

static char mqtt_client_id[128];
static char mqtt_username[128];
static char mqtt_password[256];
static uint8_t sas_sig_buf[256];

#ifndef IOT_CONFIG_USE_X509_CERT
#ifndef SAS_VALIDITY_SECS
#define SAS_VALIDITY_SECS 3600
#endif
static time_t g_sas_expiry = 0;
static AzIoTSasToken sasToken(
  &hub_client,
  AZ_SPAN_FROM_STR(IOT_CONFIG_DEVICE_KEY),
  AZ_SPAN_FROM_BUFFER(sas_sig_buf),
  AZ_SPAN_FROM_BUFFER(mqtt_password));
#endif

static void connectWiFi() {
  Serial.printf("Connecting WiFi SSID: %s\n", IOT_CONFIG_WIFI_SSID);
  Serial.println("");
  WiFi.mode(WIFI_STA);
  WiFi.begin(IOT_CONFIG_WIFI_SSID, IOT_CONFIG_WIFI_PASSWORD);
  while (WiFi.status() != WL_CONNECTED) { delay(300); Serial.print("."); }
  Serial.printf("\nWiFi connected: %s\n", WiFi.localIP().toString().c_str());
  Serial.println("");
}

static void initTime() {
  configTime(0, 0, "pool.ntp.org", "time.nist.gov");
  time_t now = time(nullptr);
  while (now < 1609459200) { delay(250); now = time(nullptr); }
}

static void initHubClient() {
  az_iot_hub_client_options opts = az_iot_hub_client_options_default();
  opts.user_agent = AZ_SPAN_FROM_STR(AZURE_SDK_CLIENT_USER_AGENT);

  if (az_result_failed(az_iot_hub_client_init(
        &hub_client,
        AZ_SPAN_FROM_STR(IOT_CONFIG_IOTHUB_FQDN),
        AZ_SPAN_FROM_STR(IOT_CONFIG_DEVICE_ID),
        &opts))) {
    Serial.println("az_iot_hub_client_init failed"); 
    return;
  }

  size_t cid_len;
  if (az_result_failed(az_iot_hub_client_get_client_id(
        &hub_client, mqtt_client_id, sizeof(mqtt_client_id), &cid_len))) {
    Serial.println("get_client_id failed"); 
    return;
  }
  if (az_result_failed(az_iot_hub_client_get_user_name(
        &hub_client, mqtt_username, sizeof(mqtt_username), NULL))) {
    Serial.println("get_user_name failed"); 
    return;
  }

  Serial.printf("ClientId: %s\nUsername: %s\n", mqtt_client_id, mqtt_username);
  Serial.println("");
}

static void activateRelay() {
  digitalWrite(RELAY_PIN, HIGH);
#if RELAY_PULSE_MS > 0
  delay(RELAY_PULSE_MS);
  digitalWrite(RELAY_PIN, LOW);
#endif
}

#if defined(ESP_ARDUINO_VERSION_MAJOR) && ESP_ARDUINO_VERSION_MAJOR >= 3
static void mqtt_event(void* /*args*/, esp_event_base_t /*base*/, int32_t event_id, void* event_data) {
  auto* e = (esp_mqtt_event_handle_t)event_data;
#else
static esp_err_t mqtt_event(esp_mqtt_event_handle_t e) {
#endif
  switch (e->event_id) {
    case MQTT_EVENT_CONNECTED: {
      Serial.println("\nMQTT connected");
      esp_mqtt_client_subscribe(mqtt_client, AZ_IOT_HUB_CLIENT_C2D_SUBSCRIBE_TOPIC, 1);
#ifdef AZ_IOT_HUB_CLIENT_METHODS_SUBSCRIBE_TOPIC
      esp_mqtt_client_subscribe(mqtt_client, AZ_IOT_HUB_CLIENT_METHODS_SUBSCRIBE_TOPIC, 0);
#else
      esp_mqtt_client_subscribe(mqtt_client, "$iothub/methods/POST/#", 0);
#endif
      break;
    }
    case MQTT_EVENT_DATA: {
      String topic; topic.reserve(e->topic_len + 1);
      for (int i = 0; i < e->topic_len; ++i) topic += (char)e->topic[i];

      String payload; payload.reserve(e->data_len + 1);
      for (int i = 0; i < e->data_len; ++i) payload += (char)e->data[i];

      Serial.println("");
      Serial.println("\nALERT");
      Serial.printf("Incoming topic: %s\n", topic.c_str());
      Serial.printf("Incoming payload: %s\n", payload.c_str());
      Serial.println("");

      if (topic.startsWith("$iothub/methods/POST/")) 
      {
        const char* prefix = "$iothub/methods/POST/";
        int start = (int)strlen(prefix);
        int slash = topic.indexOf('/', start);
        String method = (slash > start) ? topic.substring(start, slash) : "";
        int ridPos = topic.indexOf("?$rid=");
        String rid = (ridPos > 0) ? topic.substring(ridPos + 6) : "0";

        int status = 404;
        String body = "{\"error\":\"method_not_found\"}";

        if (method == "activateRelay") {
          activateRelay();
          status = 200;
          body = "{\"status\":\"relay_on\"}";
        }

        String respTopic = String("$iothub/methods/res/") + status + "/?$rid=" + rid;
        esp_mqtt_client_publish(mqtt_client, respTopic.c_str(), body.c_str(), body.length(), 0, 0);
      }
      else if (topic.startsWith("devices/") && topic.indexOf("/messages/devicebound") > 0) {
        if (payload.indexOf("\"cmd\":\"activateRelay\"") >= 0) {
          activateRelay();
        }
      }
      break;
    }
    default: break;
  }
#if defined(ESP_ARDUINO_VERSION_MAJOR) && ESP_ARDUINO_VERSION_MAJOR >= 3
#else
  return ESP_OK;
#endif
}

static void startMqtt() {
#ifndef IOT_CONFIG_USE_X509_CERT
  if (sasToken.Generate(SAS_VALIDITY_SECS) != 0) { Serial.println("SAS generation failed"); return; }
  g_sas_expiry = time(nullptr) + SAS_VALIDITY_SECS;
#endif
  esp_mqtt_client_config_t cfg{};
#if defined(ESP_ARDUINO_VERSION_MAJOR) && ESP_ARDUINO_VERSION_MAJOR >= 3
  cfg.broker.address.uri = "mqtts://" IOT_CONFIG_IOTHUB_FQDN;
  cfg.broker.address.port = AZ_IOT_DEFAULT_MQTT_CONNECT_PORT;
  cfg.credentials.client_id = mqtt_client_id;
  cfg.credentials.username  = mqtt_username;
#ifdef IOT_CONFIG_USE_X509_CERT
  cfg.credentials.authentication.certificate     = IOT_CONFIG_DEVICE_CERT;
  cfg.credentials.authentication.certificate_len = sizeof(IOT_CONFIG_DEVICE_CERT);
  cfg.credentials.authentication.key             = IOT_CONFIG_DEVICE_CERT_PRIVATE_KEY;
  cfg.credentials.authentication.key_len         = sizeof(IOT_CONFIG_DEVICE_CERT_PRIVATE_KEY);
#else
  cfg.credentials.authentication.password = (const char*)az_span_ptr(sasToken.Get());
#endif
  cfg.broker.verification.certificate     = (const char*)ca_pem;
  cfg.broker.verification.certificate_len = (size_t)ca_pem_len;
#else
  cfg.uri = "mqtts://" IOT_CONFIG_IOTHUB_FQDN;
  cfg.port = AZ_IOT_DEFAULT_MQTT_CONNECT_PORT;
  cfg.client_id = mqtt_client_id;
  cfg.username  = mqtt_username;
#ifdef IOT_CONFIG_USE_X509_CERT
  cfg.client_cert_pem = IOT_CONFIG_DEVICE_CERT;
  cfg.client_key_pem  = IOT_CONFIG_DEVICE_CERT_PRIVATE_KEY;
#else
  cfg.password = (const char*)az_span_ptr(sasToken.Get());
#endif
  cfg.cert_pem = (const char*)ca_pem;
  cfg.event_handle = mqtt_event;
#endif

  mqtt_client = esp_mqtt_client_init(&cfg);
#if defined(ESP_ARDUINO_VERSION_MAJOR) && ESP_ARDUINO_VERSION_MAJOR >= 3
  esp_mqtt_client_register_event(mqtt_client, MQTT_EVENT_ANY, mqtt_event, nullptr);
#endif
  esp_mqtt_client_start(mqtt_client);
}

void setup() {
  pinMode(RELAY_PIN, OUTPUT);
  digitalWrite(RELAY_PIN, LOW);

  Serial.begin(115200);
  connectWiFi();
  initTime();
  initHubClient();
  startMqtt();
}

void loop() {
#ifndef IOT_CONFIG_USE_X509_CERT
  time_t now = time(nullptr);
  if (g_sas_expiry > 0 && (g_sas_expiry - now) <= 120) {
    Serial.println("Refreshing SAS token...");
    esp_mqtt_client_stop(mqtt_client);
    if (sasToken.Generate(SAS_VALIDITY_SECS) == 0) {
      g_sas_expiry = now + SAS_VALIDITY_SECS;
    }
    esp_mqtt_client_start(mqtt_client);
  }
#endif
  delay(50);
}
