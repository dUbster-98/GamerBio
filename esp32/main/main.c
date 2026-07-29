/*
 * GamerBio - ESP32-S3 sensor read demo
 *
 * 3개 센서 값을 2초마다 시리얼로 출력한다.
 *   - Grove GSR (아날로그)  : GPIO4 (ADC1_CH3)   VCC=3.3V, GND
 *   - MAX30102 (심박, I2C)  : SDA=GPIO8, SCL=GPIO9  VIN=3.3V, GND
 *   - DHT   (온습도, 1선): DATA=GPIO5           VCC=3.3V, GND (+4.7k~10k 풀업)
 *
 * ESP-IDF v6.x / ESP32-S3
 *
 * SPDX-License-Identifier: CC0-1.0
 */

#include <stdio.h>
#include <string.h>
#include <time.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/event_groups.h"
#include "esp_log.h"
#include "esp_rom_sys.h"          // esp_rom_delay_us
#include "esp_timer.h"
#include "driver/gpio.h"
#include "esp_adc/adc_oneshot.h"
#include "esp_adc/adc_cali.h"
#include "esp_adc/adc_cali_scheme.h"
#include "driver/i2c_master.h"
#include "nvs_flash.h"
#include "esp_wifi.h"
#include "esp_event.h"
#include "esp_netif.h"
#include "esp_netif_sntp.h"
#include "esp_http_client.h"
#include "cJSON.h"

static const char *TAG = "sensors";

/* ================= 핀 설정 (필요하면 여기만 바꾸면 됨) ================= */
#define GSR_ADC_UNIT      ADC_UNIT_1
#define GSR_ADC_CHANNEL   ADC_CHANNEL_3   // GPIO4
#define GSR_ADC_ATTEN     ADC_ATTEN_DB_12 // 0~약 3.1V 입력 범위

#define I2C_PORT          I2C_NUM_0
#define I2C_SDA_GPIO      8
#define I2C_SCL_GPIO      9

#define DHT_GPIO        5

/* ================= WiFi / API 설정 (배포 전 실제 값으로 교체) ================= */
#define WIFI_SSID           "YOUR_WIFI_SSID"
#define WIFI_PASS           "YOUR_WIFI_PASSWORD"
#define WIFI_MAX_RETRY      5

#define API_URL             "https://bio-monitor.uk/api/biosignal"
#define API_KEY             "YOUR_API_KEY"      // appsettings의 BioMonitor:ApiKey 와 동일해야 함
#define API_TIMEOUT_MS      5000

// TODO: MAX30102 raw RED/IR로부터 실제 BPM을 계산하는 로직 추가 전까지 쓰는 임시값.
#define PLACEHOLDER_BPM     60

/* ================= GSR (ADC) ================= */
static adc_oneshot_unit_handle_t s_adc = NULL;
static adc_cali_handle_t         s_adc_cali = NULL;
static bool                      s_adc_cali_ok = false;

static void gsr_init(void)
{
    adc_oneshot_unit_init_cfg_t unit_cfg = { .unit_id = GSR_ADC_UNIT };
    ESP_ERROR_CHECK(adc_oneshot_new_unit(&unit_cfg, &s_adc));

    adc_oneshot_chan_cfg_t chan_cfg = {
        .atten    = GSR_ADC_ATTEN,
        .bitwidth = ADC_BITWIDTH_DEFAULT,
    };
    ESP_ERROR_CHECK(adc_oneshot_config_channel(s_adc, GSR_ADC_CHANNEL, &chan_cfg));

    // 전압 변환용 캘리브레이션 (ESP32-S3 = curve fitting). 실패해도 raw 값은 계속 사용.
    adc_cali_curve_fitting_config_t cali_cfg = {
        .unit_id  = GSR_ADC_UNIT,
        .chan     = GSR_ADC_CHANNEL,
        .atten    = GSR_ADC_ATTEN,
        .bitwidth = ADC_BITWIDTH_DEFAULT,
    };
    if (adc_cali_create_scheme_curve_fitting(&cali_cfg, &s_adc_cali) == ESP_OK) {
        s_adc_cali_ok = true;
    } else {
        ESP_LOGW(TAG, "ADC calibration 미지원, raw 값만 출력");
    }
}

// raw 값 반환, mv에는 밀리볼트(캘리브레이션 되면) 저장
static int gsr_read(int *mv)
{
    int raw = 0;
    if (adc_oneshot_read(s_adc, GSR_ADC_CHANNEL, &raw) != ESP_OK) {
        *mv = -1;
        return -1;
    }
    if (s_adc_cali_ok) {
        adc_cali_raw_to_voltage(s_adc_cali, raw, mv);
    } else {
        *mv = -1;
    }
    return raw;
}

/* ================= MAX30102 (I2C) ================= */
#define MAX30102_ADDR        0x57
#define REG_INTR_STATUS_1    0x00
#define REG_FIFO_WR_PTR      0x04
#define REG_OVF_COUNTER      0x05
#define REG_FIFO_RD_PTR      0x06
#define REG_FIFO_DATA        0x07
#define REG_FIFO_CONFIG      0x08
#define REG_MODE_CONFIG      0x09
#define REG_SPO2_CONFIG      0x0A
#define REG_LED1_PA          0x0C   // RED
#define REG_LED2_PA          0x0D   // IR
#define REG_TEMP_INTG        0x1F
#define REG_TEMP_FRAC        0x20
#define REG_TEMP_CONFIG      0x21
#define REG_PART_ID          0xFF   // 0x15 이어야 정상

static i2c_master_bus_handle_t s_i2c_bus = NULL;
static i2c_master_dev_handle_t s_max = NULL;
static bool                    s_max_ok = false;

static esp_err_t max_write(uint8_t reg, uint8_t val)
{
    uint8_t buf[2] = { reg, val };
    return i2c_master_transmit(s_max, buf, sizeof(buf), 100);
}

static esp_err_t max_read(uint8_t reg, uint8_t *data, size_t len)
{
    return i2c_master_transmit_receive(s_max, &reg, 1, data, len, 100);
}

static void max30102_init(void)
{
    i2c_master_bus_config_t bus_cfg = {
        .i2c_port          = I2C_PORT,
        .sda_io_num        = I2C_SDA_GPIO,
        .scl_io_num        = I2C_SCL_GPIO,
        .clk_source        = I2C_CLK_SRC_DEFAULT,
        .glitch_ignore_cnt = 7,
        .flags.enable_internal_pullup = true,
    };
    if (i2c_new_master_bus(&bus_cfg, &s_i2c_bus) != ESP_OK) {
        ESP_LOGE(TAG, "I2C 버스 생성 실패");
        return;
    }

    i2c_device_config_t dev_cfg = {
        .dev_addr_length = I2C_ADDR_BIT_LEN_7,
        .device_address  = MAX30102_ADDR,
        .scl_speed_hz    = 400000,
    };
    if (i2c_master_bus_add_device(s_i2c_bus, &dev_cfg, &s_max) != ESP_OK) {
        ESP_LOGE(TAG, "MAX30102 디바이스 추가 실패");
        return;
    }

    uint8_t part_id = 0;
    if (max_read(REG_PART_ID, &part_id, 1) != ESP_OK) {
        ESP_LOGW(TAG, "MAX30102 응답 없음 (배선/전원 확인)");
        return;
    }
    ESP_LOGI(TAG, "MAX30102 PART_ID=0x%02X (기대값 0x15)", part_id);

    // 리셋
    max_write(REG_MODE_CONFIG, 0x40);
    vTaskDelay(pdMS_TO_TICKS(10));

    // FIFO 포인터 초기화
    max_write(REG_FIFO_WR_PTR, 0x00);
    max_write(REG_OVF_COUNTER, 0x00);
    max_write(REG_FIFO_RD_PTR, 0x00);

    // FIFO: 샘플평균 4, rollover 활성
    max_write(REG_FIFO_CONFIG, 0x50);
    // SpO2 모드 (RED + IR)
    max_write(REG_MODE_CONFIG, 0x03);
    // ADC range, 100Hz, 411us(18bit)
    max_write(REG_SPO2_CONFIG, 0x27);
    // LED 전류 (~7mA)
    max_write(REG_LED1_PA, 0x24);
    max_write(REG_LED2_PA, 0x24);

    s_max_ok = true;
}

// FIFO에서 최신 1샘플 읽기 (red, ir는 18bit)
static bool max30102_read_sample(uint32_t *red, uint32_t *ir)
{
    uint8_t d[6];
    if (max_read(REG_FIFO_DATA, d, sizeof(d)) != ESP_OK) return false;
    *red = ((uint32_t)(d[0] & 0x03) << 16 | (uint32_t)d[1] << 8 | d[2]);
    *ir  = ((uint32_t)(d[3] & 0x03) << 16 | (uint32_t)d[4] << 8 | d[5]);
    return true;
}

// 온보드 온도 센서 (섭씨)
static bool max30102_read_temp(float *temp_c)
{
    if (max_write(REG_TEMP_CONFIG, 0x01) != ESP_OK) return false; // 측정 트리거
    vTaskDelay(pdMS_TO_TICKS(30));
    uint8_t intg = 0, frac = 0;
    if (max_read(REG_TEMP_INTG, &intg, 1) != ESP_OK) return false;
    if (max_read(REG_TEMP_FRAC, &frac, 1) != ESP_OK) return false;
    *temp_c = (int8_t)intg + (frac & 0x0F) * 0.0625f;
    return true;
}

/* ================= DHT (1선 비트뱅잉) ================= */
// 비트 타이밍(us) 측정 중 인터럽트 차단용
static portMUX_TYPE s_dht_mux = portMUX_INITIALIZER_UNLOCKED;

// 지정 레벨이 될 때까지 대기, 그 전까지 걸린 시간(us) 반환. 타임아웃 시 -1.
static int dht_wait_level(int pin, int level, int timeout_us)
{
    int64_t start = esp_timer_get_time();
    while (gpio_get_level(pin) != level) {
        if (esp_timer_get_time() - start > timeout_us) return -1;
    }
    return (int)(esp_timer_get_time() - start);
}

// 성공 시 true, 온도(C)/습도(%) 반환.
// 센서가 DHT22(AM2302) 포맷(16비트, 0.1 단위)이라 그렇게 디코딩한다.
static bool dht_read_once(int pin, float *temp_c, float *humidity)
{
    uint8_t data[5] = { 0 };
    bool ok = true;

    // 시작 신호: 최소 18ms LOW 후 릴리즈
    gpio_set_direction(pin, GPIO_MODE_OUTPUT);
    gpio_set_level(pin, 0);
    esp_rom_delay_us(20000);      // 20ms
    gpio_set_level(pin, 1);
    esp_rom_delay_us(30);
    // 입력으로 전환 + 내부 풀업 ON (외부 풀업 저항 없어도 라인 유지)
    gpio_set_direction(pin, GPIO_MODE_INPUT);
    gpio_set_pull_mode(pin, GPIO_PULLUP_ONLY);

    // 타이밍이 민감한 구간만 인터럽트 차단 (약 4~5ms)
    taskENTER_CRITICAL(&s_dht_mux);
    do {
        // 응답 신호: LOW 80us -> HIGH 80us
        if (dht_wait_level(pin, 0, 200) < 0) { ok = false; break; } // DHT가 LOW 당기기 시작
        if (dht_wait_level(pin, 1, 150) < 0) { ok = false; break; } // 80us LOW 통과
        if (dht_wait_level(pin, 0, 150) < 0) { ok = false; break; } // 80us HIGH 통과 (bit0 시작)

        // 40비트 수신: 각 비트 = LOW 50us + HIGH(26us=0 / 70us=1)
        // 펄스 폭을 "측정"하지 않고, HIGH 시작 40us 뒤에 딱 한 번 샘플한다:
        //   0비트(HIGH 26us) → 그때 이미 LOW,  1비트(HIGH 70us) → 아직 HIGH
        for (int i = 0; i < 40; i++) {
            if (dht_wait_level(pin, 1, 100) < 0) { ok = false; break; } // 50us LOW 통과 -> HIGH 시작
            esp_rom_delay_us(40);                                       // HIGH 시작 후 40us 대기
            data[i / 8] <<= 1;
            if (gpio_get_level(pin)) data[i / 8] |= 1;                  // 아직 HIGH면 1
            if (dht_wait_level(pin, 0, 100) < 0) { ok = false; break; } // 다음 비트 위해 LOW까지 재동기화
        }
    } while (0);
    taskEXIT_CRITICAL(&s_dht_mux);

    // 진단용: 원시 바이트 + 체크섬 상태 출력 (문제 해결되면 제거)
    uint8_t sum = data[0] + data[1] + data[2] + data[3];
    ESP_LOGI(TAG, "DHT raw=%02X %02X %02X %02X %02X  sum=%02X  %s",
             data[0], data[1], data[2], data[3], data[4], sum,
             !ok ? "타임아웃" : (sum == data[4] ? "OK" : "체크섬불일치"));

    if (!ok) return false;
    if (sum != data[4]) return false;

    // DHT22(AM2302): 16비트 big-endian, 단위 0.1
    *humidity = (((uint16_t)data[0] << 8) | data[1]) * 0.1f;
    int16_t raw_t = (((uint16_t)(data[2] & 0x7F)) << 8) | data[3];
    *temp_c = raw_t * 0.1f;
    if (data[2] & 0x80) *temp_c = -*temp_c;   // 최상위 비트 = 음수
    return true;
}

// 최대 3회 재시도
static bool dht_read(int pin, float *temp_c, float *humidity)
{
    for (int i = 0; i < 3; i++) {
        if (dht_read_once(pin, temp_c, humidity)) return true;
        vTaskDelay(pdMS_TO_TICKS(50));
    }
    return false;
}

/* ================= WiFi ================= */
// wifi_init_sta()를 호출한 태스크(app_main)를 연결 완료/실패까지 블로킹시키기 위한 이벤트 그룹.
// WiFi 이벤트는 별도의 시스템 이벤트 태스크에서 비동기로 발생하므로, 그 결과를
// app_main으로 "전달"할 통로가 필요하다 — 그게 이 이벤트 그룹의 역할.
static EventGroupHandle_t s_wifi_event_group;
#define WIFI_CONNECTED_BIT BIT0   // IP를 받아 연결이 확정됐을 때 set
#define WIFI_FAIL_BIT      BIT1   // 재시도를 다 소진하고 포기했을 때 set
static int s_retry_num = 0;

// esp_event 시스템이 WiFi/IP 이벤트가 발생할 때마다 호출하는 콜백.
// esp_wifi_start()는 즉시 연결해주지 않고 STA_START 이벤트만 발생시키므로,
// 실제 연결 시도(esp_wifi_connect)는 여기서 이벤트에 반응해 수행한다.
static void wifi_event_handler(void *arg, esp_event_base_t event_base,
                                int32_t event_id, void *event_data)
{
    if (event_base == WIFI_EVENT && event_id == WIFI_EVENT_STA_START) {
        // 드라이버가 준비 완료됐다는 신호 → 첫 연결 시도
        esp_wifi_connect();
    } else if (event_base == WIFI_EVENT && event_id == WIFI_EVENT_STA_DISCONNECTED) {
        // AP를 못 찾음/비번 틀림/신호 끊김 등으로 끊어질 때마다 여기로 온다.
        // WIFI_MAX_RETRY까지는 자동 재연결을 시도하고, 다 쓰면 포기(FAIL_BIT)한다.
        if (s_retry_num < WIFI_MAX_RETRY) {
            esp_wifi_connect();
            s_retry_num++;
            ESP_LOGW(TAG, "WiFi 연결 재시도 (%d/%d)", s_retry_num, WIFI_MAX_RETRY);
        } else {
            xEventGroupSetBits(s_wifi_event_group, WIFI_FAIL_BIT);
        }
    } else if (event_base == IP_EVENT && event_id == IP_EVENT_STA_GOT_IP) {
        // DHCP로 IP를 받은 시점이 곧 "진짜 연결됨"이다 (링크만 붙었다고 통신 가능한 게 아님).
        ip_event_got_ip_t *event = (ip_event_got_ip_t *)event_data;
        ESP_LOGI(TAG, "WiFi 연결됨, IP=" IPSTR, IP2STR(&event->ip_info.ip));
        s_retry_num = 0;
        xEventGroupSetBits(s_wifi_event_group, WIFI_CONNECTED_BIT);
    }
}

// WiFi STA 모드로 초기화하고 연결(또는 최종 실패)까지 대기한다.
// 성공 시 true. 실패해도 앱은 계속 동작(센서 값은 시리얼로만 출력됨) — WiFi는 부가 기능이지
// 센서 읽기의 필수 조건이 아니기 때문.
static bool wifi_init_sta(void)
{
    // ESP-IDF WiFi 스택이 요구하는 표준 초기화 순서:
    // NVS(설정 저장) → netif(TCP/IP) → 이벤트 루프 → WiFi 드라이버, 순서를 바꾸면 에러 발생.
    ESP_ERROR_CHECK(nvs_flash_init());
    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    esp_netif_create_default_wifi_sta();

    wifi_init_config_t init_cfg = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&init_cfg));

    s_wifi_event_group = xEventGroupCreate();
    // ESP_EVENT_ANY_ID: WIFI_EVENT 계열은 전부 이 핸들러 하나가 받아서 event_id로 분기
    ESP_ERROR_CHECK(esp_event_handler_register(WIFI_EVENT, ESP_EVENT_ANY_ID, &wifi_event_handler, NULL));
    ESP_ERROR_CHECK(esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP, &wifi_event_handler, NULL));

    wifi_config_t wifi_cfg = {
        .sta = {
            .ssid = WIFI_SSID,
            .password = WIFI_PASS,
        },
    };
    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_STA, &wifi_cfg));
    ESP_ERROR_CHECK(esp_wifi_start());   // 이 호출 자체는 비동기 — 실제 연결은 위 이벤트 핸들러가 처리

    // 두 비트 중 하나라도 켜질 때까지 여기서 블로킹(성공/실패가 확정되기 전엔 app_main 진행 안 함).
    // pdFALSE, pdFALSE: 비트를 클리어하지 않고, AND가 아니라 OR로 대기(둘 중 하나만 있어도 깨어남)
    EventBits_t bits = xEventGroupWaitBits(s_wifi_event_group,
                                           WIFI_CONNECTED_BIT | WIFI_FAIL_BIT,
                                           pdFALSE, pdFALSE, portMAX_DELAY);
    return (bits & WIFI_CONNECTED_BIT) != 0;
}

// WiFi 연결 후 SNTP로 실제 시각 동기화.
// ESP32는 RTC 배터리가 없어 부팅 시 time()이 1970-01-01부터 다시 시작한다.
// 서버 DTO의 Timestamp(DateTimeOffset)는 필수 필드라 이 동기화 없이는 엉뚱한 옛날 시각이
// 그대로 DB에 저장되므로, POST 시작 전 반드시 한 번 맞춰준다.
static void time_sync(void)
{
    ESP_LOGI(TAG, "SNTP 시각 동기화 중...");
    esp_sntp_config_t cfg = ESP_NETIF_SNTP_DEFAULT_CONFIG("pool.ntp.org");
    esp_netif_sntp_init(&cfg);
    // 최대 10초 대기. 실패해도 앱은 계속 진행(그 경우 timestamp가 부정확한 채로 전송됨)
    if (esp_netif_sntp_sync_wait(pdMS_TO_TICKS(10000)) != ESP_OK) {
        ESP_LOGW(TAG, "SNTP 동기화 실패, 타임스탬프가 부정확할 수 있음");
    }
}

// 현재 UTC 시각을 ISO-8601 문자열(예: 2026-07-29T12:34:56Z)로 buf에 기록.
// .NET의 DateTimeOffset은 이 포맷(끝의 'Z' = UTC)을 그대로 파싱할 수 있다.
static void iso8601_now(char *buf, size_t len)
{
    time_t now;
    struct tm tm_utc;
    time(&now);
    gmtime_r(&now, &tm_utc);   // 로컬 타임존 개념이 없으므로 항상 UTC로 변환해서 사용
    strftime(buf, len, "%Y-%m-%dT%H:%M:%SZ", &tm_utc);
}

/* ================= API POST ================= */
// GamerBio 서버(Program.cs)의 record BioSignalDto(int Bpm, int Gsr, double? SkinTemp,
// DateTimeOffset Timestamp)와 필드명(대소문자 포함) & 타입을 정확히 맞춰야 모델 바인딩이 성공한다.
// skin_temp는 DHT 읽기에 실패했을 수도 있으므로(has_skin_temp==false) null 허용 타입으로 보낸다.
static void post_biosignal(int bpm, int gsr, bool has_skin_temp, double skin_temp)
{
    char ts[32];
    iso8601_now(ts, sizeof(ts));

    // --- 1) JSON 바디 조립 ---
    // { "bpm": .., "gsr": .., "skinTemp": ..|null, "timestamp": "..." }
    cJSON *root = cJSON_CreateObject();
    cJSON_AddNumberToObject(root, "bpm", bpm);
    cJSON_AddNumberToObject(root, "gsr", gsr);
    if (has_skin_temp) {
        cJSON_AddNumberToObject(root, "skinTemp", skin_temp);
    } else {
        cJSON_AddNullToObject(root, "skinTemp");
    }
    cJSON_AddStringToObject(root, "timestamp", ts);

    char *body = cJSON_PrintUnformatted(root);   // 힙에 할당된 문자열 → 아래서 반드시 free 필요

    // --- 2) HTTP 클라이언트는 요청 1회용으로 매번 init → perform → cleanup ---
    // (연결을 계속 유지하는 게 아니라 2초 주기 단발 요청이라 이 패턴이 단순하고 안전함)
    esp_http_client_config_t http_cfg = {
        .url = API_URL,
        .method = HTTP_METHOD_POST,
        .timeout_ms = API_TIMEOUT_MS,
    };
    esp_http_client_handle_t client = esp_http_client_init(&http_cfg);
    esp_http_client_set_header(client, "Content-Type", "application/json");
    esp_http_client_set_header(client, "X-Api-Key", API_KEY);   // 서버의 BioMonitor:ApiKey와 대조됨
    esp_http_client_set_post_field(client, body, strlen(body));

    // --- 3) 전송 (동기 호출: 응답 받거나 timeout_ms 지날 때까지 이 태스크가 블로킹됨) ---
    esp_err_t err = esp_http_client_perform(client);
    if (err == ESP_OK) {
        ESP_LOGI(TAG, "POST /api/biosignal -> HTTP %d", esp_http_client_get_status_code(client));
    } else {
        // DNS 실패, TLS 실패, timeout 등 전송 자체가 안 된 경우 (HTTP 상태코드와는 별개)
        ESP_LOGE(TAG, "POST /api/biosignal 실패: %s", esp_err_to_name(err));
    }

    // --- 4) 정리: client 핸들, JSON 문자열, cJSON 트리 모두 매 호출마다 해제 ---
    esp_http_client_cleanup(client);
    cJSON_free(body);
    cJSON_Delete(root);
}

/* ================= main ================= */
void app_main(void)
{
    ESP_LOGI(TAG, "GamerBio 센서 데모 시작");

    gsr_init();
    max30102_init();
    gpio_reset_pin(DHT_GPIO);   // DHT 핀 준비

    // WiFi가 안 붙어도 앱 자체는 죽지 않고 센서 읽기+시리얼 출력은 그대로 계속된다.
    // (wifi_ok는 아래 루프에서 POST 여부를 결정하는 플래그로만 쓰임)
    bool wifi_ok = wifi_init_sta();
    if (wifi_ok) {
        time_sync();
    } else {
        ESP_LOGW(TAG, "WiFi 연결 실패, 센서 값은 시리얼 출력만 진행");
    }

    while (1) {
        // --- GSR ---
        int gsr_mv = -1;
        int gsr_raw = gsr_read(&gsr_mv);

        // --- DHT (DHT22/AM2302 포맷) ---
        float t = 0, h = 0;
        bool dht_ok = dht_read(DHT_GPIO, &t, &h);

        // --- MAX30102 ---
        uint32_t red = 0, ir = 0;
        float max_temp = 0;
        bool max_sample = s_max_ok && max30102_read_sample(&red, &ir);
        bool max_temp_ok = s_max_ok && max30102_read_temp(&max_temp);

        // --- 출력 ---
        printf("\n===== 센서 값 =====\n");
        if (gsr_mv >= 0)
            printf("GSR   : raw=%4d  (%d mV)\n", gsr_raw, gsr_mv);
        else
            printf("GSR   : raw=%4d\n", gsr_raw);

        if (dht_ok)
            printf("DHT   : %.1f C / %.1f %%RH\n", t, h);
        else
            printf("DHT   : 읽기 실패 (배선/풀업 확인)\n");

        if (s_max_ok) {
            if (max_sample)
                printf("MAX30102 : RED=%6lu  IR=%6lu\n", (unsigned long)red, (unsigned long)ir);
            if (max_temp_ok)
                printf("MAX30102 : 온도=%.2f C\n", max_temp);
        } else {
            printf("MAX30102 : 미연결\n");
        }

        // --- API 전송 ---
        // gsr_raw(ADC raw count)를 그대로 "gsr"로 보낸다 — 서버 TensionAnalyzer가 기대하는
        // 절대 임계값(GsrAbsLow~GsrAbsHigh)도 아직 raw 단위 기준이라 캘리브레이션 전에는 그대로 사용.
        // bpm은 PLACEHOLDER_BPM 고정값(위 TODO 참고), skinTemp는 dht_ok가 false면 null로 전송됨.
        if (wifi_ok) {
            post_biosignal(PLACEHOLDER_BPM, gsr_raw, dht_ok, t);
        }

        vTaskDelay(pdMS_TO_TICKS(2000));
    }
}
