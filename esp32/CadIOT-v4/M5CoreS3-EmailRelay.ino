// File: main.ino (fix: remove extern/static conflict; Azure status + footer smaller + auto SAS renew + CST/CDT)
#include <M5Unified.h>
#include <WiFi.h>
#include "iot_configs.h"               // Must define IOT_CONFIG_* values
#include <time.h>

// ===== CoreS3 hardware =====
#define RELAY_PIN 18                    // Safe GPIO (Port C); avoids LCD SPI pins
#include "Alert.h"

// Optional dark color defs
#ifndef TFT_DARKRED
#define TFT_DARKRED    0x8800
#endif
#ifndef TFT_DARKGREEN
#define TFT_DARKGREEN  0x03A0
#endif
#ifndef TFT_DARKGRAY
#define TFT_DARKGRAY   0x3186
#endif

#include <sys/time.h>
#include "esp_timer.h"

// ===== Azure IoT Hub =====
#include <mqtt_client.h>
#include <az_core.h>
#include <az_iot.h>
#include <azure_ca.h>
#include "AzIoTSasToken.h"
#define AZURE_SDK_CLIENT_USER_AGENT "c%2F" AZ_SDK_VERSION_STRING " (ard;esp32)"
static esp_mqtt_client_handle_t mqtt_client = nullptr;
static az_iot_hub_client hub_client;
static char mqtt_client_id[128];
static char mqtt_username[128];
static char mqtt_password[256];
static uint8_t sas_sig_buf[256];
#ifndef IOT_CONFIG_USE_X509_CERT
static AzIoTSasToken sasToken(
  &hub_client,
  AZ_SPAN_FROM_STR(IOT_CONFIG_DEVICE_KEY),
  AZ_SPAN_FROM_BUFFER(sas_sig_buf),
  AZ_SPAN_FROM_BUFFER(mqtt_password));
#endif

// ===== App state =====
struct Rect { int x, y, w, h; };
static String g_lastAlertTs;            // human-readable timestamp
static const int DISPLAY_ROTATION = 1;  // landscape-right
static bool g_azureConnected = false;   // Azure MQTT status

// Footer log buffer
static const int FOOTER_LINES = 3;
static String g_footer[FOOTER_LINES];

// SAS auto-renew
#ifndef IOT_CONFIG_USE_X509_CERT
static const uint32_t g_sas_lifetime_sec        = 3600;  // 1 hour
static const uint32_t g_sas_refresh_margin_sec  = 300;   // renew 5 min early
static uint64_t       g_sas_next_renew_ms       = 0;
#endif

// Forward prototype to avoid Arduino prototype generation issues
static void startMqtt(bool alreadyGenerated = false);

// --------------------------------------------------------------------------------------
// TIME HELPERS (CST/CDT + robust NTP)
// --------------------------------------------------------------------------------------
static String nowIso8601Millis()
{
  struct timeval tv; gettimeofday(&tv, nullptr);
  time_t sec = tv.tv_sec;
  struct tm tm_local{};
  localtime_r(&sec, &tm_local);                   // local time per TZ

  char datepart[32];
  strftime(datepart, sizeof(datepart), "%Y-%m-%dT%H:%M:%S", &tm_local);
  int ms = tv.tv_usec / 1000;
  char ms_part[8];
  snprintf(ms_part, sizeof(ms_part), ".%03d", ms);

  // timezone offset in "+HHMM" form
  char zraw[8];
  strftime(zraw, sizeof(zraw), "%z", &tm_local); // e.g., -0600
  // convert to "+HH:MM"
  char zfmt[8];
  if (strlen(zraw) == 5) {
    snprintf(zfmt, sizeof(zfmt), "%c%c%c:%c%c", zraw[0], zraw[1], zraw[2], zraw[3], zraw[4]);
  } else {
    snprintf(zfmt, sizeof(zfmt), "%s", zraw);   // fallback
  }

  String s(datepart);
  s += ms_part;
  s += zfmt;
  return s;
}

static bool initTime()
{
  setenv("TZ", "CST6CDT,M3.2.0/2,M11.1.0/2", 1); // US Central Time with DST
  tzset();
  configTime(0, 0, "pool.ntp.org", "time.nist.gov");
  struct tm tm{}; uint32_t deadline = millis() + 10000;
  while (!getLocalTime(&tm) && millis() < deadline) { delay(200); }
  if (!getLocalTime(&tm)) { Serial.println("[Time] NTP sync failed"); return false; }
  Serial.print("[Time] Local time: "); Serial.println(&tm, "%Y-%m-%d %H:%M:%S");
  return true;
}

// --------------------------------------------------------------------------------------
// DISPLAY / UI HELPERS
// --------------------------------------------------------------------------------------
static void printCentered(const char* msg) {
  int w = M5.Display.width(); int h = M5.Display.height();
  M5.Display.fillScreen(TFT_BLACK);
  M5.Display.setTextDatum(MC_DATUM);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  M5.Display.drawString(msg, w / 2, h / 2);
}

static bool inRect(int x, int y, const Rect& r) {
  return (x >= r.x && x <= r.x + r.w && y >= r.y && y <= r.y + r.h);
}

static void getButtonRects(Rect &test, Rect &off) {
  int w = M5.Display.width(); int h = M5.Display.height();
  const int bw = 120, bh = 50, margin = 10;
  test = { margin, h - (bh + margin), bw, bh };
  off  = { w - (bw + margin), h - (bh + margin), bw, bh };
}

static void drawButton(const Rect& r, const char* label, uint16_t outline, uint16_t fill, uint16_t text) {
  M5.Display.fillRoundRect(r.x, r.y, r.w, r.h, 8, fill);
  M5.Display.drawRoundRect(r.x, r.y, r.w, r.h, 8, outline);
  M5.Display.setTextDatum(MC_DATUM);
  M5.Display.setTextColor(text, fill);
  M5.Display.drawString(label, r.x + r.w / 2, r.y + r.h / 2);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
}

static void drawButtons() {
  Rect test, off; getButtonRects(test, off);
  drawButton(test, "Test Relay", TFT_DARKGREEN, TFT_GREEN, TFT_BLACK);
  drawButton(off,  "Relay OFF",  TFT_DARKRED,   TFT_RED,   TFT_BLACK);
}

static void drawAzureStatus(bool connected)
{
  g_azureConnected = connected;
  int y = 100; // below Time synced line
  M5.Display.fillRect(0, y-2, M5.Display.width(), 20, TFT_BLACK);
  M5.Display.setTextDatum(TL_DATUM);
  if (connected) { M5.Display.setTextColor(TFT_GREEN, TFT_BLACK); M5.Display.setCursor(10, y); M5.Display.print("Azure Hub: Connected"); }
  else           { M5.Display.setTextColor(TFT_YELLOW, TFT_BLACK); M5.Display.setCursor(10, y); M5.Display.print("Azure Hub: Connecting…"); }
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
}

static void renderFooter()
{
  int w = M5.Display.width(); int h = M5.Display.height();
  const int lineH = 14;                // compact line height
  const int margin = 4;
  int footerH = FOOTER_LINES * lineH + margin * 2;
  int yTop = h - 62 - footerH;         // above buttons area
  M5.Display.fillRect(0, yTop, w, footerH, TFT_BLACK);
  M5.Display.setTextDatum(TL_DATUM);
  M5.Display.setTextSize(1);           // smaller footer text
  M5.Display.setTextColor(TFT_CYAN, TFT_BLACK);
  for (int i=0; i<FOOTER_LINES; ++i) {
    if (g_footer[i].length()) {
      M5.Display.setCursor(10, yTop + margin + i*lineH);
      M5.Display.print(g_footer[i]);
    }
  }
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  M5.Display.setTextSize(2);           // restore for main content
}

static void footerLog(const String& msg)
{
  for (int i=0; i<FOOTER_LINES-1; ++i) g_footer[i] = g_footer[i+1];
  g_footer[FOOTER_LINES-1] = msg;
  renderFooter();
}

static void showLastAlert() {
  int w = M5.Display.width(); int h = M5.Display.height();
  M5.Display.fillScreen(TFT_BLACK);
  M5.Display.setTextDatum(TL_DATUM);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  M5.Display.setCursor(10, 10);
  if (g_lastAlertTs.length() > 0) {
    M5.Display.println("Last alert:");
    M5.Display.setTextColor(TFT_YELLOW, TFT_BLACK);
    M5.Display.setCursor(10, 40);
    M5.Display.print(g_lastAlertTs);
    M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  } else {
    M5.Display.println("Awaiting Activation...");
  }
  M5.Display.drawFastHLine(0, h - 62, w, TFT_DARKGRAY);
  drawButtons();
  renderFooter();
}

// --------------------------------------------------------------------------------------
// AZURE IOT HUB HELPERS
// --------------------------------------------------------------------------------------
static void initHubClient() {
  az_iot_hub_client_options opts = az_iot_hub_client_options_default();
  opts.user_agent = AZ_SPAN_FROM_STR(AZURE_SDK_CLIENT_USER_AGENT);
  if (az_result_failed(az_iot_hub_client_init(&hub_client,
      AZ_SPAN_FROM_STR(IOT_CONFIG_IOTHUB_FQDN), AZ_SPAN_FROM_STR(IOT_CONFIG_DEVICE_ID), &opts))) {
    Serial.println("az_iot_hub_client_init failed");
    return;
  }
  size_t cid_len = 0;
  if (az_result_failed(az_iot_hub_client_get_client_id(&hub_client, mqtt_client_id, sizeof(mqtt_client_id), &cid_len))) {
    Serial.println("get_client_id failed");
    return;
  }
  if (az_result_failed(az_iot_hub_client_get_user_name(&hub_client, mqtt_username, sizeof(mqtt_username), NULL))) {
    Serial.println("get_user_name failed");
    return;
  }
  Serial.printf("[Azure] ClientId: %s\n[Azure] Username: %s\n", mqtt_client_id, mqtt_username);
}

static void sendTelemetry(const char* eventName, const char* source)
{
  String ts = nowIso8601Millis();
  int64_t us = esp_timer_get_time();
  String body = String("{\"event\":\"") + eventName +
                "\",\"source\":\"" + source +
                "\",\"timestamp\":\"" + ts +
                "\",\"uptime_ms\":" + String(us / 1000) + "}";
  char telemetry_topic[128]; size_t topic_len = 0;
  az_result r = az_iot_hub_client_telemetry_get_publish_topic(
      &hub_client, NULL, telemetry_topic, sizeof(telemetry_topic), &topic_len);
  if (az_result_failed(r)) { Serial.println("[Azure] Failed to get telemetry topic"); return; }
  int msg_id = esp_mqtt_client_publish(mqtt_client, telemetry_topic, body.c_str(), body.length(), 1, 0);
  if (msg_id == -1) Serial.println("[Azure] Telemetry publish failed");
  else Serial.printf("[Azure] Telemetry sent (msg_id=%d): %s\n", msg_id, body.c_str());
}

#if defined(ESP_ARDUINO_VERSION_MAJOR) && ESP_ARDUINO_VERSION_MAJOR >= 3
static void mqtt_event(void* /*args*/, esp_event_base_t /*base*/, int32_t event_id, void* event_data) {
  auto* e = (esp_mqtt_event_handle_t)event_data;
#else
static esp_err_t mqtt_event(esp_mqtt_event_handle_t e) {
#endif
  switch (e->event_id) {
    case MQTT_EVENT_CONNECTED: {
      Serial.println("[Azure] MQTT connected");
      drawAzureStatus(true);
      footerLog(String("Azure: connected @ ") + nowIso8601Millis());
      esp_mqtt_client_subscribe(mqtt_client, AZ_IOT_HUB_CLIENT_C2D_SUBSCRIBE_TOPIC, 1);
#ifdef AZ_IOT_HUB_CLIENT_METHODS_SUBSCRIBE_TOPIC
      esp_mqtt_client_subscribe(mqtt_client, AZ_IOT_HUB_CLIENT_METHODS_SUBSCRIBE_TOPIC, 0);
#else
      esp_mqtt_client_subscribe(mqtt_client, "$iothub/methods/POST/#", 0);
#endif
      break; }
    case MQTT_EVENT_DISCONNECTED: {
      Serial.println("[Azure] MQTT disconnected");
      drawAzureStatus(false);
      footerLog(String("Azure: disconnected @ ") + nowIso8601Millis());
      break; }
    case MQTT_EVENT_DATA: {
      String topic; topic.reserve(e->topic_len + 1); for (int i=0;i<e->topic_len;++i) topic += (char)e->topic[i];
      String payload; payload.reserve(e->data_len + 1); for (int i=0;i<e->data_len;++i) payload += (char)e->data[i];
      Serial.printf("[Azure] Topic: %s\n", topic.c_str());
      Serial.printf("[Azure] Payload: %s\n", payload.c_str());

      if (topic.startsWith("$iothub/methods/POST/")) {
        int start = String("$iothub/methods/POST/").length();
        int slash = topic.indexOf('/', start);
        String method = (slash > start) ? topic.substring(start, slash) : "";
        int ridPos = topic.indexOf("?$rid="); String rid = (ridPos > 0) ? topic.substring(ridPos + 6) : "0";
        footerLog(String("Method: ") + method + " @ " + nowIso8601Millis());
        int status = 404; String body = "{\"error\":\"method_not_found\"}";
        if (method == "activateRelay") {
          activateRelay("direct_method");
          status = 200; String ts = nowIso8601Millis();
          body = String("{\"status\":\"relay_on\",\"timestamp\":\"") + ts + "\"}";
        }
        String respTopic = String("$iothub/methods/res/") + status + "/?$rid=" + rid;
        esp_mqtt_client_publish(mqtt_client, respTopic.c_str(), body.c_str(), body.length(), 0, 0);
      }
      else if (topic.startsWith("devices/") && topic.indexOf("/messages/devicebound") > 0) {
        footerLog(String("C2D @ ") + nowIso8601Millis());
        if (payload.indexOf("\"cmd\":\"activateRelay\"") >= 0) { activateRelay("c2d"); }
      }
      break; }
    default: break;
  }
#if defined(ESP_ARDUINO_VERSION_MAJOR) && ESP_ARDUINO_VERSION_MAJOR >= 3
#else
  return ESP_OK;
#endif
}

#ifndef IOT_CONFIG_USE_X509_CERT
static void renewSasAndReconnect()
{
  Serial.println("[Azure] Renewing SAS token…");
  footerLog(String("Azure: renewing SAS @ ") + nowIso8601Millis());
  if (mqtt_client) {
    esp_mqtt_client_stop(mqtt_client);
    esp_mqtt_client_destroy(mqtt_client);
    mqtt_client = nullptr;
  }
  if (sasToken.Generate(g_sas_lifetime_sec) != 0) {
    Serial.println("[Azure] SAS generation failed");
    drawAzureStatus(false);
    footerLog(String("Azure: SAS renew FAILED @ ") + nowIso8601Millis());
    return;
  }
  // Restart MQTT with existing SAS
  startMqtt(true);
}
#endif

static void startMqtt(bool alreadyGenerated) {
#ifndef IOT_CONFIG_USE_X509_CERT
  if (!alreadyGenerated) {
    if (sasToken.Generate(g_sas_lifetime_sec) != 0) { Serial.println("[Azure] SAS generation failed"); return; }
  }
  g_sas_next_renew_ms = millis() + (uint64_t)(g_sas_lifetime_sec - g_sas_refresh_margin_sec) * 1000ULL;
#endif
  esp_mqtt_client_config_t cfg{};
#if defined(ESP_ARDUINO_VERSION_MAJOR) && ESP_ARDUINO_VERSION_MAJOR >= 3
  cfg.broker.address.uri = "mqtts://" IOT_CONFIG_IOTHUB_FQDN;
  cfg.broker.address.port = AZ_IOT_DEFAULT_MQTT_CONNECT_PORT;
  cfg.credentials.client_id = mqtt_client_id;
  cfg.credentials.username = mqtt_username;
#ifdef IOT_CONFIG_USE_X509_CERT
  cfg.credentials.authentication.certificate = IOT_CONFIG_DEVICE_CERT;
  cfg.credentials.authentication.certificate_len = sizeof(IOT_CONFIG_DEVICE_CERT);
  cfg.credentials.authentication.key = IOT_CONFIG_DEVICE_CERT_PRIVATE_KEY;
  cfg.credentials.authentication.key_len = sizeof(IOT_CONFIG_DEVICE_CERT_PRIVATE_KEY);
#else
  cfg.credentials.authentication.password = (const char*)az_span_ptr(sasToken.Get());
#endif
  cfg.broker.verification.certificate = (const char*)ca_pem;
  cfg.broker.verification.certificate_len = (size_t)ca_pem_len;
#else
  cfg.uri = "mqtts://" IOT_CONFIG_IOTHUB_FQDN;
  cfg.port = AZ_IOT_DEFAULT_MQTT_CONNECT_PORT;
  cfg.client_id = mqtt_client_id;
  cfg.username = mqtt_username;
#ifdef IOT_CONFIG_USE_X509_CERT
  cfg.client_cert_pem = IOT_CONFIG_DEVICE_CERT;
  cfg.client_key_pem = IOT_CONFIG_DEVICE_CERT_PRIVATE_KEY;
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
  drawAzureStatus(false);
  footerLog(String("Azure: connecting @ ") + nowIso8601Millis());
  esp_mqtt_client_start(mqtt_client);
}

// --------------------------------------------------------------------------------------
// RELAY / TOUCH
// --------------------------------------------------------------------------------------
static void activateRelay(const char* source)
{
  digitalWrite(RELAY_PIN, HIGH);
  g_lastAlertTs = nowIso8601Millis();
  int64_t us = esp_timer_get_time();
  Serial.printf("activateRelay at %s (uptime +%lld.%03lld s)\n",
                g_lastAlertTs.c_str(), us / 1000000, (us / 1000) % 1000);

  M5.Display.fillScreen(TFT_BLACK);
  M5.Display.setTextDatum(MC_DATUM);
  M5.Display.setTextColor(TFT_RED, TFT_BLACK);
  M5.Display.drawString("ALERT RECEIVED", M5.Display.width()/2, M5.Display.height()/2);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  M5.Display.fillRect(0, 0, M5.Display.width(), 10, TFT_RED);

  if (M5.Speaker.isEnabled())
  {
    M5.Speaker.setVolume(64);
    M5.Speaker.tone(2000, 100);
    while (M5.Speaker.isPlaying()) { M5.delay(1); }
    M5.Speaker.tone(1000, 100);
    while (M5.Speaker.isPlaying()) { M5.delay(1); }
    M5.Speaker.playRaw(wav_8bit_44100, sizeof(wav_8bit_44100), 44100, false);
  }

  sendTelemetry("relay_on", source);
  footerLog(String("Relay ON @ ") + g_lastAlertTs);

  const uint32_t alertDurationMs = 5000;
  uint32_t startMs = millis();
  while (millis() - startMs < alertDurationMs) { M5.update(); M5.delay(10); }
  showLastAlert();
}

static void handleTouch() {
  auto points = M5.Touch.getDetail();
  if (!points.isPressed()) return;
  int tx = points.x, ty = points.y;
  static uint32_t lastActMs = 0; if (millis() - lastActMs < 250) return;

  Rect test, off; getButtonRects(test, off);
  if (inRect(tx, ty, test)) {
    lastActMs = millis(); Serial.println("[Touch] Test Relay"); activateRelay("touch_test");
    while (M5.Touch.getDetail().isPressed()) { M5.update(); M5.delay(10); }
    return;
  }
  if (inRect(tx, ty, off)) {
    lastActMs = millis(); Serial.println("[Touch] Relay OFF"); digitalWrite(RELAY_PIN, LOW);
    sendTelemetry("relay_off", "touch_off");

    M5.Display.fillScreen(TFT_BLACK);
    M5.Display.setTextDatum(TL_DATUM);
    M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
    M5.Display.setCursor(10, 10);
    M5.Display.println("Relay OFF");
    if (g_lastAlertTs.length() > 0) {
      M5.Display.println(); M5.Display.println("Last alert:");
      M5.Display.setTextColor(TFT_YELLOW, TFT_BLACK); M5.Display.setCursor(10, 60);
      M5.Display.println(g_lastAlertTs); M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
    }
    drawButtons();
    footerLog(String("Relay OFF @ ") + nowIso8601Millis());

    while (M5.Touch.getDetail().isPressed()) { M5.update(); M5.delay(10); }
    return;
  }
}

// --------------------------------------------------------------------------------------
// SETUP / LOOP
// --------------------------------------------------------------------------------------
void setup() {
  auto cfg = M5.config();
  M5.begin(cfg);
  pinMode(RELAY_PIN, OUTPUT); digitalWrite(RELAY_PIN, LOW);

  M5.Display.wakeup(); M5.Display.powerSaveOff();
  M5.Display.setBrightness(200);
  M5.Display.setColorDepth(16);
  M5.Display.setRotation(DISPLAY_ROTATION);
  M5.Display.setTextSize(2);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);

#ifdef IOT_CONFIG_WIFI_SSID
#ifdef IOT_CONFIG_WIFI_PASSWORD
  const char* ssid = IOT_CONFIG_WIFI_SSID; const char* password = IOT_CONFIG_WIFI_PASSWORD;
#else
  printCentered("Missing IOT_CONFIG_WIFI_PASSWORD in iot_configs.h"); while (true) { delay(1000); }
#endif
#else
  printCentered("Missing IOT_CONFIG_WIFI_SSID in iot_configs.h"); while (true) { delay(1000); }
#endif

  Serial.begin(115200);
  Serial.println("\n[M5CoreS3] Wi-Fi init...");
  M5.Display.setTextDatum(TL_DATUM); M5.Display.setCursor(10, 10);
  M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
  M5.Display.println("Wi-Fi init..."); M5.Display.printf("SSID: %s\n", ssid);

  WiFi.mode(WIFI_STA); WiFi.begin(ssid, password);

  printCentered("Connecting to Wi-Fi...");
  uint32_t start = millis();
  while (WiFi.status() != WL_CONNECTED && (millis() - start) < 20000) { M5.delay(250); }

  if (WiFi.status() == WL_CONNECTED) {
    IPAddress ip = WiFi.localIP(); Serial.printf("[M5CoreS3] Connected. IP: %s\n", ip.toString().c_str());
    M5.Display.fillScreen(TFT_BLACK); M5.Display.setTextDatum(TL_DATUM); M5.Display.setCursor(10, 10);
    M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
    M5.Display.println("✅ Wi-Fi Connected");
    M5.Display.printf("SSID: %s\n", ssid);
    M5.Display.printf("IP: %s\n", ip.toString().c_str());

    bool ok = initTime();
    M5.Display.setCursor(10, 80);
    if (ok) { M5.Display.setTextColor(TFT_GREEN, TFT_BLACK); M5.Display.println("Time synced (CST/CDT)"); }
    else    { M5.Display.setTextColor(TFT_RED,   TFT_BLACK); M5.Display.println("Time sync FAILED"); }
    M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);

    initHubClient();
    startMqtt(false);
  } else {
    Serial.println("[M5CoreS3] Wi-Fi connect FAILED.");
    M5.Display.fillScreen(TFT_BLACK); M5.Display.setTextDatum(TL_DATUM); M5.Display.setCursor(10, 10);
    M5.Display.setTextColor(TFT_WHITE, TFT_BLACK);
    M5.Display.println("❌ Wi-Fi connect FAILED");
    M5.Display.println("Check SSID/PASSWORD in iot_configs.h");
    M5.Display.println("Retrying in loop...");
  }

  showLastAlert();
}

void loop() {
  M5.update();
  handleTouch();

  static uint32_t lastRetry = 0;
  if (WiFi.status() != WL_CONNECTED) {
    if (millis() - lastRetry > 5000) { lastRetry = millis(); WiFi.disconnect(); WiFi.begin(IOT_CONFIG_WIFI_SSID, IOT_CONFIG_WIFI_PASSWORD); printCentered("Retrying Wi-Fi..."); }
  }

#ifndef IOT_CONFIG_USE_X509_CERT
  if (mqtt_client && (millis() >= g_sas_next_renew_ms)) {
    renewSasAndReconnect();
  }
#endif

  M5.delay(30);
}
