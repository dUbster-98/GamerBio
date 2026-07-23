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
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_log.h"
#include "esp_rom_sys.h"          // esp_rom_delay_us
#include "esp_timer.h"
#include "driver/gpio.h"
#include "esp_adc/adc_oneshot.h"
#include "esp_adc/adc_cali.h"
#include "esp_adc/adc_cali_scheme.h"
#include "driver/i2c_master.h"

static const char *TAG = "sensors";

/* ================= 핀 설정 (필요하면 여기만 바꾸면 됨) ================= */
#define GSR_ADC_UNIT      ADC_UNIT_1
#define GSR_ADC_CHANNEL   ADC_CHANNEL_3   // GPIO4
#define GSR_ADC_ATTEN     ADC_ATTEN_DB_12 // 0~약 3.1V 입력 범위

#define I2C_PORT          I2C_NUM_0
#define I2C_SDA_GPIO      8
#define I2C_SCL_GPIO      9

#define DHT_GPIO        5

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

/* ================= main ================= */
void app_main(void)
{
    ESP_LOGI(TAG, "GamerBio 센서 데모 시작");

    gsr_init();
    max30102_init();
    gpio_reset_pin(DHT_GPIO);   // DHT 핀 준비

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

        vTaskDelay(pdMS_TO_TICKS(2000));
    }
}
