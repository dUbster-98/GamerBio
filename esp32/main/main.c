/*
 * GamerBio - ESP32-S3 펌웨어
 *
 * 역할: 2초마다 센서를 읽어 시리얼로 출력하고, 서버(/api/biosignal)로 POST 한다.
 *
 * 배선
 *   Grove GSR (아날로그)  : SIG=GPIO4 (ADC1_CH3)
 *   MAX30102  (I2C)      : SDA=GPIO8, SCL=GPIO9
 *   DHT22     (1-Wire)   : DATA=GPIO5  (+4.7k~10k 풀업, 내부 풀업으로도 동작)
 *
 * 부팅 순서: 센서 init → WiFi 연결 → SNTP 시각 동기화 → API 자가진단 → 측정 루프
 *
 * ESP-IDF v6.x / ESP32-S3
 * SPDX-License-Identifier: CC0-1.0
 */

#include <stdio.h>
#include <string.h>
#include <time.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/event_groups.h"
#include "esp_log.h"
#include "esp_rom_sys.h"
#include "esp_timer.h"
#include "driver/gpio.h"
#include "esp_adc/adc_oneshot.h"
#include "driver/i2c_master.h"
#include "nvs_flash.h"
#include "esp_wifi.h"
#include "esp_event.h"
#include "esp_netif.h"
#include "esp_netif_sntp.h"
#include "esp_http_client.h"
#include "esp_crt_bundle.h"   // HTTPS 인증서 검증용 내장 루트 CA 번들
#include "esp_system.h"       // esp_reset_reason

static const char *TAG = "gamerbio";

/* ===== 설정: 바꿀 값은 전부 여기 ===== */
#define GSR_ADC_CHANNEL   ADC_CHANNEL_3     // GPIO4
#define I2C_SDA_GPIO      8
#define I2C_SCL_GPIO      9
#define DHT_GPIO          5

#define WIFI_SSID         "KT_GiGA_73B6" 
#define WIFI_PASS         "5ax69ee549"
#define WIFI_MAX_RETRY    5

// 최대 송신 출력 (0.25dBm 단위, 8~84). 기본값 80 = 20dBm이면 송신 순간 350mA 넘게 당겨서
// USB 포트/케이블이 약하면 VBUS가 무너지고 보드가 리부팅된다(리셋 사유가 POWERON으로 찍힘).
// 52 = 13dBm 정도면 집 안 거리에서는 충분하면서 스파이크가 크게 준다.
// 전원이 넉넉한 환경(외부 5V, 배터리)에서는 80으로 되돌려도 된다.
#define WIFI_MAX_TX_POWER 52

// LAN 평문으로 먼저 테스트하려면(TLS/DNS 문제 분리): "http://192.168.0.104:5000/api"
#define API_BASE          "https://bio-monitor.uk/api"
#define API_KEY           "989a860f8d8b9b8aaa819007a470e911a52caae243365a8d0074ac25a5c69c21"
#define API_TIMEOUT_MS 5000
#define SAMPLE_PERIOD  2000

/* ===== GSR: ADC 원시값만 읽는다 (서버 임계값도 raw 기준) =====
 *
 * 아두이노였다면 analogRead(A0) 한 줄이다. IDF가 긴 이유는 일을 더 해서가 아니라,
 * 아두이노 코어가 라이브러리 안에 숨겨 둔 결정들(어느 ADC를 쓸지 / 입력 전압 범위 /
 * 해상도 / 실패 시 처리)을 전부 코드에 드러내기 때문이다. 하는 일은 두 가지뿐:
 *   gsr_init()  — "ADC를 이렇게 쓰겠다"고 부팅 시 1회 설정
 *   gsr_read()  — 설정된 ADC에서 값 1회 읽기 (2초마다)
 *
 * ── 핸들(handle) 패턴 ──
 * adc_oneshot_unit_handle_t는 속을 알 수 없는 포인터(opaque pointer)다. 실제 설정과
 * 레지스터 상태·락은 드라이버가 힙에 들고 있고, 우리는 "그 물건을 가리키는 표"만 받는다.
 * IDF 전체가 이 규칙이라 이 파일의 I2C(s_i2c_bus/s_max)와 HTTP(s_client)도 같은 모양이다.
 * 아두이노식 전역 상태와 달리 ADC 유닛이나 I2C 버스를 여러 개 만들 수 있기 때문에,
 * 매번 "어느 것에 대고 말하는지"를 핸들로 지정해야 한다.
 *
 * 참고: 파일 최상단의 static은 "정적"이 아니라 **이 파일 밖으로 이름을 노출하지 않는다**는
 * 뜻이다(internal linkage). s_ 접두사도 같은 표시. 아래 함수들에 붙은 static도 같은 이유.
 */
static adc_oneshot_unit_handle_t s_adc;

static void gsr_init(void)
{
    // { .필드 = 값 }은 C99 지정 초기화자. **명시하지 않은 나머지 필드는 전부 0으로 채워진다**
    // (여기서 안 적은 clk_src/ulp_mode는 0 = 기본값). 순서에 안 묶이고 뭘 설정했는지가
    // 코드에 그대로 보여서 IDF가 이 스타일을 쓴다.
    //
    // ADC_UNIT_1은 취향이 아니라 **강제**다: ADC2는 WiFi와 하드웨어를 공유해서, WiFi가
    // 켜져 있으면 읽기가 ESP_ERR_TIMEOUT으로 실패하거나 값이 튄다. 이 프로젝트는 WiFi를
    // 항상 켜 두므로 ADC1만 쓸 수 있고, 그래서 센서 핀도 ADC1에 매핑된 것 중에 골라야 한다
    // (GSR_ADC_CHANNEL = ADC_CHANNEL_3 = GPIO4).
    adc_oneshot_unit_init_cfg_t unit = { .unit_id = ADC_UNIT_1 };
    // ESP_ERROR_CHECK: 반환값이 ESP_OK가 아니면 파일/줄/에러명을 찍고 abort()(→재부팅).
    // IDF 함수는 대부분 esp_err_t를 돌려주고, 그걸 무시하지 말라는 게 기본 태도다.
    // 여기서 죽이는 게 맞는 이유: ADC 초기화 실패는 배선이 아니라 **코드가 틀렸다**는 뜻
    // (핀 오타, 이미 점유된 유닛)이라 조용히 넘어가면 원인을 영영 못 찾는다.
    // 반대로 max30102_init()은 실패해도 s_max_ok=false로 두고 넘어간다 — 그건 "센서를
    // 안 꽂았을 수도 있는" 정상 상황이라서. 같은 실패라도 원인이 코드냐 환경이냐로 갈린다.
    //
    // &unit은 입력(이 설정으로 만들어줘), &s_adc는 출력(만든 핸들을 여기 넣어줘).
    // s_adc가 이미 포인터인데 &를 또 붙이는 건, 함수가 s_adc 변수 자체를 바꿔야 하기 때문.
    ESP_ERROR_CHECK(adc_oneshot_new_unit(&unit, &s_adc));

    adc_oneshot_chan_cfg_t chan = {
        // atten(감쇠) — 아두이노에는 없는 개념. ESP32의 ADC는 실제로는 내부 기준전압
        // 약 1.1V까지밖에 못 잰다. 3.3V 신호를 재려면 입력단에서 눌러서 넣어야 하고,
        // 그 "얼마나 누를까"가 atten이다. ESP32-S3 기준 측정 범위:
        //     DB_0   → 0~0.95V     DB_2_5 → 0~1.25V
        //     DB_6   → 0~1.75V     DB_12  → 0~3.1V   ← Grove GSR이 0~3.3V로 출력하므로 이것
        // 트레이드오프: 범위를 넓히면 같은 4096단계를 넓게 펴 바르므로 분해능이 나빠지고
        // (3.1V÷4096 ≈ 0.76mV/step), 3.1V를 넘는 입력은 4095에 붙어버린다(포화).
        // → GSR raw가 계속 4095면 배선보다 먼저 이 포화를 의심할 것.
        // ※ 옛 예제의 ADC_ATTEN_DB_11과 같은 물건이다 (IDF 5.2에서 이름이 바뀌었다).
        .atten    = ADC_ATTEN_DB_12,
        // 해상도. DEFAULT = 이 칩이 낼 수 있는 최대 → ESP32-S3는 12비트 = 0~4095.
        // (아두이노 우노는 10비트/0~1023이었지만 ESP32 아두이노 코어도 기본 12비트라 같다)
        .bitwidth = ADC_BITWIDTH_DEFAULT,
    };
    // atten/bitwidth는 유닛이 아니라 **채널마다** 건다. 그래서 설정이 유닛/채널 2단계로
    // 나뉜다 — 한 유닛 안에서 핀마다 다른 전압 범위를 쓸 수 있다.
    ESP_ERROR_CHECK(adc_oneshot_config_channel(s_adc, GSR_ADC_CHANNEL, &chan));
}

// "oneshot"은 ADC 사용 방식 두 가지 중 하나다.
//   · oneshot    — 부르면 그 자리에서 한 번 변환하고 돌려준다. 느리게 변하는 값에 적합.
//   · continuous — ADC가 DMA로 계속 샘플링해 버퍼에 쌓고, 우리는 주기적으로 퍼간다.
//                  초당 수천 샘플이 필요할 때(오디오, 파형 분석).
// GSR은 2초에 한 번이면 충분하니 oneshot. (아래 MAX30102 FIFO 처리가 continuous와 같은
// 발상이다 — 센서가 알아서 쌓아두고 우리가 몰아서 꺼낸다)
//
// 측정값을 반환하지 않고 &raw로 받는 이유: IDF는 **반환값 자리를 에러 코드가 차지**하고
// 실제 결과는 포인터로 넘겨준다(out-parameter). 아두이노 analogRead()는 값을 바로
// 반환하는 대신 실패를 표현할 방법이 아예 없어서, 위의 ADC2+WiFi 같은 문제가 나도
// 그냥 이상한 숫자를 돌려준다.
//
// ⚠ 실패 시 -1을 반환하는데, 호출부(app_main)는 이 값을 그대로 서버로 보낸다.
//    서버 BioSignalDto.Gsr은 non-nullable int라 -1이 저장되고, TensionAnalyzer가
//    MapScore(-1, GsrAbsLow=300, ...) → 0점 = "완전 이완"으로 채점한다.
//    게다가 GSR은 융합에서 유일하게 "항상 있다"고 가정되는 요소라, BPM처럼 가중치를
//    빼는 폴백 경로조차 없다. gsr도 nullable로 가는 게 일관되지만 그러면 모든 요소가
//    빠질 수 있게 되어 서버에서 가중치 합이 0인 경우를 새로 다뤄야 한다.
//    → GSR 실측 보정(GsrAbsLow/High) 할 때 같이 결정할 것.
//      ADC1은 사실상 실패하지 않으므로 지금 당장 터지는 버그는 아니다.
static int gsr_read(void)
{
    int raw = 0;
    // 삼항 연산자. if (읽기 성공) return raw; else return -1; 과 같다.
    return adc_oneshot_read(s_adc, GSR_ADC_CHANNEL, &raw) == ESP_OK ? raw : -1;
}

/* ===== MAX30102 (I2C) =====
 *
 * 아래 REG_* 는 전부 **레지스터(register) 주소**다. (Windows의 "레지스트리"와는 무관)
 *
 * 센서 칩 안에는 1바이트짜리 칸이 여러 개 있고, 각 칸에 번호(주소)가 붙어 있다.
 * 이 칸들이 곧 센서의 설정 스위치이자 측정 결과 창구다 —
 * 칸에 값을 **쓰면** 동작이 바뀌고(예: LED 밝기), 칸을 **읽으면** 상태/데이터가 나온다.
 * I2C로 할 수 있는 일은 "몇 번 칸에 이 값 써줘 / 몇 번 칸 읽어줘" 두 가지뿐이고,
 * 그래서 아래 max_write()/max_read()가 이 파일에서 센서와 대화하는 유일한 수단이다.
 *
 * ※ 왜 라이브러리를 안 쓰고 직접 짰나 (제조사 코드는 존재한다):
 *   · 제조사 Maxim(현 Analog Devices)은 **MAXREFDES117#** 레퍼런스 디자인으로
 *     MAX30102.cpp(레지스터 드라이버) + algorithm.cpp(심박·SpO2 계산)를 공개했다.
 *     다만 대상이 **Arduino / mbed**이고 ESP-IDF용 공식 컴포넌트는 없다.
 *     (ESP Component Registry·esp-idf-lib에도 MAX30102는 없고 서드파티 포팅만 있다)
 *   · 포팅해 오더라도 I2C 호출부는 결국 아래와 똑같은 레지스터 읽기/쓰기로 바뀐다.
 *     이 프로젝트는 임베디드 포트폴리오이고 "데이터시트 보고 드라이버를 직접 쓴다"가
 *     어필 포인트이므로 직접 구현한다.
 *   · 다만 algorithm.cpp의 maxim_heart_rate_and_oxygen_saturation()에는
 *     **SpO2(산소포화도) 계산식**이 들어 있다. 우리 코드는 BPM만 내므로,
 *     나중에 SpO2로 확장할 때는 그 알고리즘을 참고할 것.
 *     (RED LED를 이미 켜 두고 있어서 데이터는 지금도 나오고 있다 — 아래 MODE_SPO2 참고)
 *
 * 주소와 비트 배치는 우리가 정하는 게 아니라 **칩 제조사 데이터시트에 박혀 있는 값**이다.
 * (MAX30102 datasheet, Maxim Integrated — "Register Map" 절)
 *
 * 통신 형태:
 *   쓰기 : [슬레이브주소 0x57] [레지스터 주소] [값]
 *   읽기 : [슬레이브주소 0x57] [레지스터 주소] → 다시 시작 → [값 ...]
 * 읽기가 두 단계인 이유는, 먼저 "몇 번 칸 볼 거야"를 알려주고 나서 읽어야 하기 때문.
 * 그래서 max_read()가 transmit_receive(보내고 곧바로 받기)를 쓴다.
 */

// I2C 버스에서 이 칩을 부르는 이름표(슬레이브 주소). 위의 레지스터 주소와는 다른 층위다 —
// "0x57번 칩아, 네 0x09번 칸에 써라"처럼 두 주소가 항상 짝으로 쓰인다.
// 이 값은 칩에 고정되어 있어 바꿀 수 없다. 그래서 MAX30102는 한 버스에 1개만 붙는다.
#define MAX30102_ADDR     0x57

// --- FIFO(측정 데이터 큐) 관련 ---
// FIFO = 센서가 측정한 샘플을 32칸짜리 링버퍼에 알아서 쌓아두는 공간.
// 덕분에 ESP32가 매 샘플(20ms)마다 달라붙지 않고 100ms에 한 번 몰아서 퍼갈 수 있다.
#define REG_FIFO_WR_PTR   0x04   // 센서가 "다음에 쓸" 칸 번호 (센서가 증가시킴)
#define REG_OVF_COUNTER   0x05   // 우리가 안 퍼가서 덮어쓴 샘플 수 (0이 아니면 신호가 끊긴 것)
#define REG_FIFO_RD_PTR   0x06   // 우리가 "다음에 읽을" 칸 번호 (읽으면 자동 증가)
#define REG_FIFO_DATA     0x07   // 실제 샘플이 나오는 창구. 연속으로 읽으면 RD_PTR이 따라 올라간다
                                 // → 쌓인 개수 = WR_PTR - RD_PTR (아래 max30102_available)

// --- 설정 레지스터 ---
// 한 칸(8비트) 안에 여러 설정이 비트 단위로 욱여넣어져 있다. 그래서 0x27 같은
// "의미 없어 보이는 숫자"가 나오는데, 2진수로 펴 보면 항목별로 잘린다.
// 실제 분해는 아래 max30102_init()의 MAX_WR 호출부 주석 참고.
#define REG_FIFO_CONFIG   0x08   // 샘플 평균 개수, 넘쳤을 때 덮어쓸지 여부
#define REG_MODE_CONFIG   0x09   // 리셋 / 동작 모드(꺼짐·심박전용·SpO2)
#define REG_SPO2_CONFIG   0x0A   // ADC 범위, 샘플레이트, LED 펄스 폭
#define REG_LED1_PA       0x0C   // RED LED 전류 (PA = Pulse Amplitude, 밝기)
#define REG_LED2_PA       0x0D   // IR  LED 전류

// --- 신원 확인 ---
// 읽기 전용. 이 칩이면 항상 0x15가 나온다. 배선이 맞는지, 엉뚱한 칩(가짜/유사품)이
// 붙었는지 부팅 때 한 번 확인하는 용도. (MAX30100은 0x11이라 여기서 걸러진다)
#define REG_PART_ID       0xFF   // 0x15면 정상

// --- 레지스터에 써 넣을 값들 ---
// MODE_CONFIG 한 칸을 비트로 쪼개면: [7] SHDN | [6] RESET | [2:0] MODE
#define MODE_RESET        0x40   // 0b0100_0000 = RESET 비트만 1.
                                 // 쓰면 칩이 전원 켠 직후 상태로 돌아가고,
                                 // 리셋이 끝나면 **하드웨어가 스스로 이 비트를 0으로 내린다**.
                                 // → 그래서 아래 init에서 이 비트를 폴링해 완료를 기다린다.
#define MODE_SPO2         0x03   // 0b011 = SpO2 모드(RED+IR 둘 다 점등).
                                 // 0b010은 심박 전용(IR만)이라 더 절전이지만,
                                 // 나중에 SpO2(산소포화도)로 확장하려면 RED가 필요해 이쪽을 쓴다.

#define FIFO_SAMPLE_BYTES 6      // SpO2 모드는 한 샘플이 RED 3바이트 + IR 3바이트
                                 // (심박 전용 모드면 IR 3바이트뿐이라 3이 된다)
#define MAX_FIFO_DEPTH    32     // 하드웨어 FIFO 깊이 (고정)

// 유효 샘플레이트 = SPO2_SR ÷ SMP_AVE = 100Hz ÷ 2 = 50Hz.
//   · 왜 50Hz인가: 25Hz(평균4)는 샘플 간격이 40ms라 60bpm에서 BPM 양자화 오차가 ±2.4bpm이다.
//     50Hz면 20ms 간격 → ±1.2bpm. 반대로 더 올리면 FIFO(32칸)가 640ms만에 차서 폴링 여유가 준다.
//   · FIFO가 차는 데 32 ÷ 50 = 640ms 걸리므로, 100ms마다 비우면 6배 이상 여유가 있다.
#define PPG_SAMPLE_RATE   50
#define PPG_POLL_MS       100

static i2c_master_bus_handle_t s_i2c_bus;
static i2c_master_dev_handle_t s_max;
static bool s_max_ok = false;

static esp_err_t max_write(uint8_t reg, uint8_t val)
{
    uint8_t buf[2] = { reg, val };
    return i2c_master_transmit(s_max, buf, sizeof(buf), 100);
}

static esp_err_t max_read(uint8_t reg, uint8_t *data, size_t len)
{
    return i2c_master_transmit_receive(s_max, &reg, 1, data, len, 100);
}

// 버스에 응답하는 주소를 전부 훑어 로그로 남긴다. 센서가 안 잡힐 때
// "배선이 죽었나(아무것도 안 뜸) / 주소가 다른가(다른 주소가 뜸)"를 가르는 용도.
static void i2c_scan(void)
{
    ESP_LOGW(TAG, "I2C 스캔 (SDA=%d SCL=%d)...", I2C_SDA_GPIO, I2C_SCL_GPIO);
    int found = 0;
    for (uint8_t addr = 0x08; addr < 0x78; addr++) {
        // 타임아웃을 짧게: 센서를 하나도 안 붙였을 때 112개 주소를 다 기다리면 부팅이 몇 초 밀린다.
        if (i2c_master_probe(s_i2c_bus, addr, 10) == ESP_OK) {
            ESP_LOGW(TAG, "  응답: 0x%02X", addr);
            found++;
        }
    }
    if (found == 0) ESP_LOGE(TAG, "  응답 장치 없음 — SDA/SCL/VIN/GND 배선 확인");
}

// 센서가 없거나 응답이 없으면 s_max_ok=false로 두고 조용히 넘어간다(앱은 계속 동작).
static void max30102_init(void)
{
    i2c_master_bus_config_t bus_cfg = {
        .i2c_port          = I2C_NUM_0,
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
        // 내부 풀업(약 45k)만으로 400kHz는 상승엣지가 못 따라가 NACK이 난다.
        // 외부 4.7k 풀업을 달면 400kHz로 올려도 된다.
        .scl_speed_hz    = 100000,
    };
    if (i2c_master_bus_add_device(s_i2c_bus, &dev_cfg, &s_max) != ESP_OK) return;

    uint8_t part_id = 0;
    if (max_read(REG_PART_ID, &part_id, 1) != ESP_OK) {
        ESP_LOGW(TAG, "MAX30102 응답 없음 (배선/전원 확인)");
        i2c_scan();
        return;
    }
    ESP_LOGI(TAG, "MAX30102 PART_ID=0x%02X (기대값 0x15)", part_id);

    // 설정 중 한 번이라도 NACK가 나면 센서는 반쯤 설정된 상태로 남는다.
    // 그걸 모른 채 s_max_ok=true로 두면 나중에 쓰레기 파형을 읽고도 원인을 못 찾는다.
#define MAX_WR(reg, val)                                                     \
    do {                                                                     \
        if (max_write((reg), (val)) != ESP_OK) {                             \
            ESP_LOGE(TAG, "MAX30102 설정 실패 (reg=0x%02X)", (reg));         \
            return;                                                          \
        }                                                                    \
    } while (0)

    MAX_WR(REG_MODE_CONFIG, MODE_RESET);
    // 리셋 완료를 고정 지연으로 때려 맞추지 않고 RESET 비트가 내려갈 때까지 폴링한다.
    // 리셋이 안 끝난 상태에서 밀어 넣은 설정은 조용히 날아간다.
    bool reset_done = false;
    for (int i = 0; i < 20 && !reset_done; i++) {
        uint8_t mode = MODE_RESET;
        if (max_read(REG_MODE_CONFIG, &mode, 1) == ESP_OK && !(mode & MODE_RESET)) reset_done = true;
        else vTaskDelay(pdMS_TO_TICKS(5));
    }
    if (!reset_done) {
        ESP_LOGE(TAG, "MAX30102 리셋이 끝나지 않음");
        return;
    }

    MAX_WR(REG_FIFO_WR_PTR, 0x00);   // 포인터 3개를 0으로 맞춰 FIFO를 빈 상태로 정렬
    MAX_WR(REG_OVF_COUNTER, 0x00);
    MAX_WR(REG_FIFO_RD_PTR, 0x00);

    // ▼ 여기부터가 "레지스터 한 칸(8비트)에 설정 여러 개가 들어 있다"의 실물이다.
    //   숫자를 2진수로 펴서 구간별로 잘라 읽어야 의미가 보인다.

    // FIFO_CONFIG(0x08) = 0x30 = 0b 001 1 0000
    //                              └┬┘ │ └┬─┘
    //   [7:5] SMP_AVE   = 0b001  ──┘   │  │   센서가 2샘플을 평균내서 1샘플로 넣는다
    //                                  │  │   (내부 100Hz ÷ 2 = 우리가 받는 50Hz. 잡음도 줄어든다)
    //   [4]   ROLLOVER_EN = 1  ────────┘  │   가득 차면 오래된 것부터 덮어쓴다
    //   [3:0] A_FULL    = 0b0000 ─────────┘   "거의 참" 인터럽트 미사용(우리는 폴링하므로)
    // ROLLOVER_EN을 켜 두면 FIFO가 가득 차도 멈추지 않고 오래된 것부터 덮어쓴다.
    // (끄면 가득 찬 순간 수집이 멈춰서, 폴링이 한 번만 늦어도 신호가 영영 끊긴다)
    MAX_WR(REG_FIFO_CONFIG, 0x30);

    // SPO2_CONFIG(0x0A) = 0x27 = 0b 0 01 001 11
    //   [6:5] ADC_RGE = 0b01  → 측정 전류 범위 4096nA (신호가 포화되면 키운다)
    //   [4:2] SR      = 0b001 → 내부 샘플레이트 100Hz (위 SMP_AVE=2와 합쳐 실효 50Hz)
    //   [1:0] LED_PW  = 0b11  → LED 펄스 폭 411us = ADC 분해능 18비트
    //                           (길수록 빛을 오래 쬐어 신호가 커지지만 전력을 더 쓴다)
    MAX_WR(REG_SPO2_CONFIG, 0x27);

    // LED 전류. 단위가 0.2mA라 레지스터 값 × 0.2mA가 실제 전류다.
    // 밝을수록 살을 깊이 통과해 파형이 커지지만, 전력 소모와 발열이 늘고
    // 너무 밝으면 ADC가 포화된다. 손가락이 두꺼우면 올리고, IR 값이 계속
    // 최대치에 붙어 있으면 내린다 — 실측 보정 대상.
    MAX_WR(REG_LED1_PA, 0x24);       // RED, 0x24(36) × 0.2mA = 7.2mA
    MAX_WR(REG_LED2_PA, 0x24);       // IR  — BPM은 이쪽 채널만 쓴다

    MAX_WR(REG_MODE_CONFIG, MODE_SPO2);   // 모드는 맨 마지막에 켠다 (설정이 다 올라간 뒤 측정 시작)
#undef MAX_WR

    s_max_ok = true;
}

/* ===== MAX30102 FIFO 읽기 =====
 * 이전 구현은 2초에 한 번 FIFO_DATA에서 6바이트만 꺼냈다. 그런데 FIFO는 초당 50샘플이
 * 쌓이고 깊이는 32칸뿐이라, 읽는 값은 "언제 기록됐는지 알 수 없는 위치"의 데이터였다.
 * 센서가 살아있는지 확인하는 용도로는 됐지만 파형이 아니라서 박동 검출이 불가능했다.
 *
 * 정석은 WR/RD 포인터로 쌓인 개수를 구해 그만큼 한 번에 꺼내는 것이다.
 * FIFO_DATA를 연속으로 읽으면 RD_PTR이 샘플마다 자동으로 증가한다.
 */

// 지금 FIFO에 쌓인 샘플 수. 실패하면 -1.
// 오버플로가 있었으면 *gap=true — 신호가 끊긴 것이므로 호출자가 박동 검출기를 리셋해야 한다.
static int max30102_available(bool *gap)
{
    // 0x04(WR_PTR) / 0x05(OVF_COUNTER) / 0x06(RD_PTR)은 연속 주소라 한 번에 읽을 수 있다.
    uint8_t p[3];
    if (max_read(REG_FIFO_WR_PTR, p, sizeof(p)) != ESP_OK) return -1;

    uint8_t wr = p[0] & 0x1F, ovf = p[1] & 0x1F, rd = p[2] & 0x1F;
    *gap = (ovf > 0);

    if (ovf > 0) {
        // 넘쳤다 = 읽기 전에 덮어쓴 샘플이 있다. 남아 있는 32칸은 서로 연속이지만
        // 그 앞과는 이어지지 않으므로, 전부 꺼내되 박동 연속성은 끊어야 한다.
        return MAX_FIFO_DEPTH;
    }
    int n = (int)wr - (int)rd;
    if (n < 0) n += MAX_FIFO_DEPTH;   // 링버퍼가 한 바퀴 돈 경우
    return n;
}

// FIFO를 비우면서 IR 채널만 out에 담는다. 반환값은 담은 샘플 수(실패 시 -1).
// RED는 SpO2 계산용이라 BPM에는 쓰지 않지만, 하드웨어가 RED→IR 순으로 6바이트를
// 한 묶음으로 내보내므로 읽어서 건너뛰어야 한다.
static int max30102_drain(uint32_t *out, bool *gap)
{
    int n = max30102_available(gap);
    if (n <= 0) return n;

    uint8_t buf[MAX_FIFO_DEPTH * FIFO_SAMPLE_BYTES];
    if (max_read(REG_FIFO_DATA, buf, (size_t)n * FIFO_SAMPLE_BYTES) != ESP_OK) return -1;

    for (int i = 0; i < n; i++) {
        const uint8_t *b = &buf[i * FIFO_SAMPLE_BYTES];
        // 18비트 값이 3바이트에 담겨 오므로 상위 6비트를 잘라낸다(= & 0x3FFFF).
        out[i] = ((uint32_t)(b[3] & 0x03) << 16) | ((uint32_t)b[4] << 8) | b[5];
    }
    return n;
}

/* ===== PPG(IR 파형) → BPM =====
 * 파이프라인:
 *   원시 IR ─▶ DC 제거(1차 IIR 하이패스) ─▶ 이동평균(저역통과) ─▶ 적응 임계 상승교차 ─▶ IBI ─▶ 중앙값 ─▶ BPM
 *
 * 시각을 esp_timer_get_time()으로 재지 않고 **샘플 개수**로 세는 게 중요하다.
 * FIFO에서 한 번에 5~10샘플을 몰아 읽기 때문에, 읽은 시각을 쓰면 그 샘플들이
 * 전부 같은 순간에 찍힌 것처럼 뭉개져 IBI가 엉망이 된다. FIFO는 정확히
 * 1/PPG_SAMPLE_RATE 간격으로 채워지므로 인덱스 차이가 곧 시간 차이다.
 * (대신 MAX30102 내부 오실레이터 오차 ±2%가 BPM에 그대로 실린다 — 60bpm에서 ±1.2bpm)
 */
// 손가락 감지 임계값. ON/OFF를 다르게 둔 히스테리시스인데, 하나로 두면 IR이 경계선
// 근처에 있을 때 매 샘플 붙었다 떨어졌다를 반복하며 검출기를 계속 리셋해 버린다.
// LED 전류(LED2_PA)와 센서 개체차에 따라 달라지므로, 시리얼에 찍히는 IR 값을 보고 맞출 것.
#define PPG_FINGER_IR_ON   20000
#define PPG_FINGER_IR_OFF  15000
#define PPG_MA_LEN         5        // 이동평균 탭 수 (50Hz에서 -3dB ≈ 4.4Hz)
#define PPG_DC_ALPHA       0.95f    // 하이패스 계수 → 차단 ≈ 0.4Hz(=24bpm) 아래를 제거
#define PPG_ENV_DECAY      0.01f    // 진폭 포락선 수축 속도 (시정수 ≈ 2초)
#define PPG_MIN_AMPLITUDE  50.0f    // 진폭이 이보다 작으면 맥파가 아니라 잡음으로 본다
#define PPG_IBI_MIN_MS     273      // 220 bpm — 이보다 짧은 간격은 중복 검출
#define PPG_IBI_MAX_MS     2000     // 30 bpm  — 이보다 길면 박동을 놓친 것
#define PPG_IBI_SLOTS      5        // 중앙값을 낼 IBI 개수
#define PPG_STALE_MS       5000     // 이만큼 박동이 없으면 그동안의 IBI를 버린다

typedef struct {
    float    dc_w;                  // 하이패스 상태 변수
    float    ma[PPG_MA_LEN];        // 이동평균 링버퍼
    int      ma_idx, ma_count;
    float    ma_sum;
    bool     primed;                // 포락선을 첫 유효 샘플로 초기화했는가
    float    prev;                  // 직전 필터 출력 (상승 교차 판정용)
    float    env_max, env_min;      // 최근 진폭 포락선
    uint32_t idx;                   // 지금까지 처리한 샘플 수 = 시간축
    uint32_t last_beat_idx;         // 마지막 박동의 샘플 인덱스 (0 = 아직 없음)
    int      ibi[PPG_IBI_SLOTS];
    int      ibi_count, ibi_idx;
} ppg_t;

// 필터/검출 상태를 전부 버린다. 손가락을 뗐거나 FIFO가 넘쳐 신호가 끊겼을 때 호출.
static void ppg_reset(ppg_t *p)
{
    memset(p, 0, sizeof(*p));
}

// 한 샘플을 파이프라인에 흘려 넣는다. 박동이 검출되면 true.
static bool ppg_feed(ppg_t *p, uint32_t ir)
{
    p->idx++;

    // ① DC 제거.  w[n] = x[n] + α·w[n-1],  y[n] = w[n] - w[n-1]
    // PPG는 수만 카운트의 DC 위에 수백 카운트의 맥동이 얹힌 신호라, DC를 먼저
    // 걷어내지 않으면 임계값을 잡을 수가 없다(손가락 압력만 바뀌어도 DC가 통째로 움직인다).
    float w = (float)ir + PPG_DC_ALPHA * p->dc_w;
    float y = w - p->dc_w;
    p->dc_w = w;

    // ② 이동평균 저역통과. 광원 잡음과 양자화 지터를 없앤다.
    p->ma_sum -= p->ma[p->ma_idx];
    p->ma[p->ma_idx] = y;
    p->ma_sum += y;
    p->ma_idx = (p->ma_idx + 1) % PPG_MA_LEN;
    if (p->ma_count < PPG_MA_LEN) {      // 버퍼가 찰 때까지는 출력이 의미 없다
        p->ma_count++;
        return false;
    }
    float s = p->ma_sum / PPG_MA_LEN;

    // ③ 진폭 포락선. 손가락 압력이나 혈류가 바뀌어도 임계값이 따라가도록,
    //    최대/최소를 현재값 쪽으로 천천히 수축시킨다(고정 임계값은 금방 못 쓰게 된다).
    if (!p->primed) {
        p->env_max = p->env_min = p->prev = s;
        p->primed = true;
        return false;
    }
    if (s > p->env_max) p->env_max = s; else p->env_max += (s - p->env_max) * PPG_ENV_DECAY;
    if (s < p->env_min) p->env_min = s; else p->env_min += (s - p->env_min) * PPG_ENV_DECAY;

    float amp = p->env_max - p->env_min;
    float threshold = p->env_min + amp * 0.5f;

    // ④ 상승 교차 + 불응기.
    //    꼭대기가 아니라 **중간 지점을 올라가며 지나는 순간**을 박동으로 잡는다.
    //    맥파 꼭대기는 평평해서 샘플마다 위치가 흔들리는 반면 상승 구간은 기울기가 급해
    //    시각 지터가 작다 = IBI가 정확해진다.
    bool beat = false;
    if (amp >= PPG_MIN_AMPLITUDE && p->prev <= threshold && s > threshold) {
        if (p->last_beat_idx != 0) {
            int ibi_ms = (int)((p->idx - p->last_beat_idx) * 1000 / PPG_SAMPLE_RATE);
            if (ibi_ms >= PPG_IBI_MIN_MS && ibi_ms <= PPG_IBI_MAX_MS) {
                p->ibi[p->ibi_idx] = ibi_ms;
                p->ibi_idx = (p->ibi_idx + 1) % PPG_IBI_SLOTS;
                if (p->ibi_count < PPG_IBI_SLOTS) p->ibi_count++;
                beat = true;
            }
            // 범위를 벗어난 간격은 버리되 기준점은 갱신한다 —
            // 안 그러면 한 번 튄 뒤로 계속 긴 간격만 계산된다.
        }
        p->last_beat_idx = p->idx;
    }

    // 한동안 박동이 없으면 옛 IBI는 더 이상 현재 심박이 아니다.
    if (p->last_beat_idx != 0
        && (p->idx - p->last_beat_idx) > (uint32_t)(PPG_STALE_MS * PPG_SAMPLE_RATE / 1000)) {
        p->ibi_count = p->ibi_idx = 0;
        p->last_beat_idx = 0;
    }

    p->prev = s;
    return beat;
}

// 최근 IBI들의 **중앙값**으로 BPM을 낸다. 평균이 아닌 이유:
// 박동을 하나 놓치면 그 구간 IBI가 정확히 2배로 튀는데, 평균은 그 영향을 그대로 받지만
// 중앙값은 통째로 무시한다. 0을 반환하면 "아직 신뢰할 값 없음" — 이 0 규약은
// 여기와 호출부(ppg_task) 한 곳에만 존재하고, 그 밖으로는 has_bpm 플래그로만 나간다.
static int ppg_bpm(const ppg_t *p)
{
    if (p->ibi_count < 3) return 0;

    int v[PPG_IBI_SLOTS];
    memcpy(v, p->ibi, sizeof(int) * (size_t)p->ibi_count);
    for (int i = 1; i < p->ibi_count; i++) {     // 삽입 정렬 (최대 5개)
        int key = v[i], j = i - 1;
        while (j >= 0 && v[j] > key) { v[j + 1] = v[j]; j--; }
        v[j + 1] = key;
    }
    int median = v[p->ibi_count / 2];
    return median > 0 ? 60000 / median : 0;
}

/* ===== PPG 태스크 =====
 * 별도 태스크로 빼고 **core 1에 고정**한다. app_main과 WiFi는 core 0에서 도는데,
 * PPG는 100ms마다 I2C를 쳐야 하고 app_main은 HTTP에서 수 초씩 블로킹되기 때문에
 * 한 태스크에 같이 두면 폴링이 밀려 FIFO가 넘친다.
 */
static portMUX_TYPE s_ppg_mux = portMUX_INITIALIZER_UNLOCKED;
// "값이 있는가"를 BPM 안의 매직 넘버(0)가 아니라 별도 플래그로 들고 다닌다.
// 서버 BioSignalDto의 Bpm이 nullable(int?)이라 전송 시점에 필요한 정보가 정확히 이 둘이고,
// DHT(s_dht_valid + 값)와 같은 모양이라 두 센서를 같은 방식으로 다루게 된다.
// s_max_ok가 false면 ppg_task 자체가 뜨지 않으므로 has_bpm은 false로 남는다(= null 전송).
static bool     s_ppg_has_bpm;
static int      s_ppg_bpm;      // has_bpm이 false면 의미 없음
static bool     s_ppg_finger;
static uint32_t s_ppg_ir;       // 원시 IR DC 수준 (PPG_FINGER_IR_ON/OFF 보정용으로 노출)

// 측정 태스크(core 1)와 전송 루프(core 0)가 공유하는 값이라 스핀락으로 감싼다.
// 복사만 하는 아주 짧은 구간이라 임계영역 길이는 문제되지 않는다.
static void ppg_snapshot(bool *has_bpm, int *bpm, bool *finger, uint32_t *ir)
{
    taskENTER_CRITICAL(&s_ppg_mux);
    *has_bpm = s_ppg_has_bpm;
    *bpm     = s_ppg_bpm;
    *finger  = s_ppg_finger;
    *ir      = s_ppg_ir;
    taskEXIT_CRITICAL(&s_ppg_mux);
}

static void ppg_task(void *arg)
{
    ppg_t ppg;
    ppg_reset(&ppg);

    uint32_t ir[MAX_FIFO_DEPTH];
    uint32_t ir_dc = 0;        // 원시 IR의 느린 평균 = 손가락 감지 기준
    bool finger = false;

    while (1) {
        bool gap = false;
        int n = max30102_drain(ir, &gap);

        if (n < 0) {
            ESP_LOGW(TAG, "MAX30102 FIFO 읽기 실패");
        } else if (gap) {
            // 폴링이 밀려 샘플을 잃었다. 끊긴 지점을 사이에 두고 IBI를 재면
            // 실제보다 긴 간격이 나오므로 검출기를 통째로 리셋한다.
            ESP_LOGW(TAG, "MAX30102 FIFO 오버플로 — 박동 검출 재시작");
            ppg_reset(&ppg);
        }

        for (int i = 0; i < n; i++) {
            // 원시 IR의 EMA(계수 1/16). 손가락이 닿으면 수만 단위로 뛴다.
            ir_dc = ir_dc ? (uint32_t)((int32_t)ir_dc + ((int32_t)ir[i] - (int32_t)ir_dc) / 16)
                          : ir[i];

            bool now_finger = finger ? (ir_dc > PPG_FINGER_IR_OFF)   // 붙어 있을 땐 낮은 문턱
                                     : (ir_dc >= PPG_FINGER_IR_ON);  // 떨어져 있을 땐 높은 문턱
            if (now_finger != finger) {
                // 접촉이 바뀌면 파형이 통째로 달라진다 → 필터 상태를 버리고 다시 수렴시킨다.
                ppg_reset(&ppg);
                finger = now_finger;
                ESP_LOGI(TAG, "MAX30102 %s (IR=%lu)",
                         finger ? "손가락 감지" : "손가락 떨어짐", (unsigned long)ir_dc);
            }
            if (finger) ppg_feed(&ppg, ir[i]);
        }

        // ppg_bpm()의 "0 = 아직 신뢰할 값 없음"을 여기서 플래그로 바꾼다.
        // 이 줄 밖으로는 0이 BPM인 척 새어 나가지 않는다.
        int bpm = finger ? ppg_bpm(&ppg) : 0;

        taskENTER_CRITICAL(&s_ppg_mux);
        s_ppg_has_bpm = (bpm > 0);
        s_ppg_bpm     = bpm;
        s_ppg_finger  = finger;
        s_ppg_ir      = ir_dc;
        taskEXIT_CRITICAL(&s_ppg_mux);

        vTaskDelay(pdMS_TO_TICKS(PPG_POLL_MS));
    }
}

/* ===== DHT22 (1-Wire 비트뱅잉) ===== */
// 비트 폭이 26us(0) vs 70us(1)라 인터럽트가 끼면 값이 깨진다 → 수신 구간만 임계영역으로 감싼다.
static portMUX_TYPE s_dht_mux = portMUX_INITIALIZER_UNLOCKED;

// 핀이 지정 레벨이 될 때까지 대기. 타임아웃이면 false.
static bool dht_wait(int pin, int level, int timeout_us)
{
    int64_t start = esp_timer_get_time();
    while (gpio_get_level(pin) != level) {
        if (esp_timer_get_time() - start > timeout_us) return false;
    }
    return true;
}

static bool dht_read_once(int pin, float *temp_c, float *humidity)
{
    uint8_t data[5] = { 0 };
    bool ok = true;

    // 시작 신호: 20ms LOW로 당겼다가 놓고 입력으로 전환
    gpio_set_direction(pin, GPIO_MODE_OUTPUT);
    gpio_set_level(pin, 0);
    esp_rom_delay_us(20000);
    gpio_set_level(pin, 1);
    esp_rom_delay_us(30);
    gpio_set_direction(pin, GPIO_MODE_INPUT);
    gpio_set_pull_mode(pin, GPIO_PULLUP_ONLY);

    taskENTER_CRITICAL(&s_dht_mux);
    do {
        // 응답 신호: LOW 80us → HIGH 80us
        if (!dht_wait(pin, 0, 200) || !dht_wait(pin, 1, 150) || !dht_wait(pin, 0, 150)) {
            ok = false;
            break;
        }
        // 40비트: 각 비트 = LOW 50us + HIGH(26us=0 / 70us=1)
        // 펄스 폭을 재지 않고 HIGH 시작 40us 뒤에 한 번만 샘플한다 (0이면 이미 LOW, 1이면 아직 HIGH).
        for (int i = 0; i < 40; i++) {
            if (!dht_wait(pin, 1, 100)) { ok = false; break; }
            esp_rom_delay_us(40);
            data[i / 8] <<= 1;
            if (gpio_get_level(pin)) data[i / 8] |= 1;
            if (!dht_wait(pin, 0, 100)) { ok = false; break; }   // 다음 비트 전에 재동기화
        }
    } while (0);
    taskEXIT_CRITICAL(&s_dht_mux);

    if (!ok) return false;
    if ((uint8_t)(data[0] + data[1] + data[2] + data[3]) != data[4]) return false;  // 체크섬

    // DHT22(AM2302): 16비트 big-endian, 0.1 단위. 온도 최상위 비트 = 부호.
    *humidity = (((uint16_t)data[0] << 8) | data[1]) * 0.1f;
    *temp_c   = ((((uint16_t)(data[2] & 0x7F)) << 8) | data[3]) * 0.1f;
    if (data[2] & 0x80) *temp_c = -*temp_c;
    return true;
}

static bool dht_read(int pin, float *temp_c, float *humidity)
{
    for (int i = 0; i < 3; i++) {                 // 타이밍 센서라 간헐 실패가 정상 → 3회 재시도
        if (dht_read_once(pin, temp_c, humidity)) return true;
        vTaskDelay(pdMS_TO_TICKS(50));
    }
    return false;
}

/* ===== DHT 태스크 =====
 * 40비트를 받는 약 5ms 동안 taskENTER_CRITICAL이 그 코어의 인터럽트를 막는다.
 * app_main과 같은 core 0에서 돌리면 WiFi/TCP 인터럽트가 2초마다 5ms씩 밀린다
 * (ESP-IDF는 임계영역을 수십 µs로 유지하라고 권고한다).
 * 그래서 PPG와 함께 core 1로 보낸다 — 거기서 막히는 건 서로의 태스크 스케줄링뿐이고,
 * PPG는 FIFO에 640ms 여유가 있어 5ms 지연을 신경 쓰지 않는다.
 *
 * 읽기 주기도 늘렸다. DHT22 데이터시트 최소 샘플 주기가 2초인 데다 온습도는 천천히
 * 변하므로, 2초마다 읽어서 인터럽트를 막을 이유가 없다.
 */
#define DHT_PERIOD_MS  10000

static portMUX_TYPE s_dht_pub_mux = portMUX_INITIALIZER_UNLOCKED;
static bool  s_dht_valid;
static float s_dht_temp, s_dht_humidity;

// 마지막으로 성공한 측정값. 반환값이 false면 아직 한 번도 못 읽었거나 최근 읽기가 실패한 것.
static bool dht_snapshot(float *temp_c, float *humidity)
{
    taskENTER_CRITICAL(&s_dht_pub_mux);
    bool ok    = s_dht_valid;
    *temp_c    = s_dht_temp;
    *humidity  = s_dht_humidity;
    taskEXIT_CRITICAL(&s_dht_pub_mux);
    return ok;
}

static void dht_task(void *arg)
{
    while (1) {
        float t = 0, h = 0;
        bool ok = dht_read(DHT_GPIO, &t, &h);

        taskENTER_CRITICAL(&s_dht_pub_mux);
        s_dht_valid = ok;
        if (ok) { s_dht_temp = t; s_dht_humidity = h; }
        taskEXIT_CRITICAL(&s_dht_pub_mux);

        vTaskDelay(pdMS_TO_TICKS(DHT_PERIOD_MS));
    }
}

/* ===== WiFi ===== */
// WiFi 이벤트는 별도 시스템 태스크에서 비동기로 온다. 그 결과를 app_main으로 넘기는 통로가 이 이벤트 그룹.
static EventGroupHandle_t s_wifi_events;
// CONNECTED_BIT는 "부팅 때 붙었다"가 아니라 **지금 IP를 들고 있다**는 실시간 상태다.
// 끊기면 내리고 다시 받으면 올린다 → wifi_is_up()으로 아무 때나 현재 상태를 물어볼 수 있다.
#define WIFI_CONNECTED_BIT BIT0
#define WIFI_FAIL_BIT      BIT1         // 부팅 재시도를 다 썼다 (app_main을 깨우는 1회성 신호)

static int  s_retry = 0;
static bool s_connected_once = false;   // 한 번이라도 붙은 적이 있는가 (로그 소음 조절용)
static bool s_link_lost = false;        // 링크가 끊긴 적 있음 → TLS 소켓을 새로 맺어야 함

static void wifi_event_handler(void *arg, esp_event_base_t base, int32_t id, void *data)
{
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_START) {
        esp_wifi_connect();                       // start는 비동기 → 실제 연결은 여기서 시작
    } else if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        // 끊긴 사유를 남긴다 — "AP를 못 찾음"과 "비번 틀림"은 대응이 완전히 다르다.
        uint8_t reason = ((wifi_event_sta_disconnected_t *)data)->reason;
        const char *why =
            reason == WIFI_REASON_NO_AP_FOUND            ? "AP 못 찾음 (SSID 오타 / 5GHz 전용 / 거리)" :
            reason == WIFI_REASON_AUTH_FAIL              ? "인증 실패 (비밀번호 확인)" :
            reason == WIFI_REASON_4WAY_HANDSHAKE_TIMEOUT ? "핸드셰이크 타임아웃 (비밀번호 확인)" :
            reason == WIFI_REASON_AUTH_EXPIRE            ? "인증 만료 (신호 약함)" :
            reason == WIFI_REASON_CONNECTION_FAIL        ? "연결 실패 (AP가 거부/신호 불안정)" : "기타";
        ESP_LOGW(TAG, "WiFi 끊김: reason=%d (%s)", reason, why);

        // 링크가 죽었다는 사실을 즉시 반영한다. 이 비트를 안 내리면 측정 루프가
        // "아직 연결돼 있다"고 믿고 매 샘플마다 API_TIMEOUT_MS를 두 번씩(1차+재시도)
        // 기다린다 — 2초 주기가 10초로 늘어지고 로그는 에러로 도배된다.
        xEventGroupClearBits(s_wifi_events, WIFI_CONNECTED_BIT);
        // 끊겼다 다시 붙으면 살아 있던 TLS 소켓은 이미 죽은 것이다. 다만 여기는
        // 이벤트 태스크라, 메인 태스크가 요청 중일 수 있는 HTTP 핸들을 직접 건드리면
        // 위험하다 → 플래그만 세우고 실제 정리는 측정 루프에서 한다.
        s_link_lost = true;

        // 재연결 시도는 **절대 멈추지 않는다**. 부팅 시 5회로 포기해 버리면
        // 공유기가 보드보다 늦게 켜지는 상황(정전 복구 등)에서 영구 오프라인이 된다.
        // 이 핸들러는 이벤트 태스크에서 도므로 delay 금지. 다음 disconnect 이벤트 자체가
        // auth/scan 타임아웃만큼 뒤에 오기 때문에 즉시 재시도해도 바쁜 대기가 되지 않는다.
        esp_wifi_connect();

        // WIFI_MAX_RETRY는 "재연결 포기 시점"이 아니라 "app_main을 언제까지 붙잡아 둘까"의 기준.
        // 한도를 넘기면 FAIL_BIT으로 app_main을 풀어 주고, 로그는 딱 한 번만 남긴 뒤 조용히 계속 시도한다.
        if (!s_connected_once) {
            if (s_retry < WIFI_MAX_RETRY) {
                ESP_LOGW(TAG, "WiFi 재시도 (%d/%d)", ++s_retry, WIFI_MAX_RETRY);
            } else if (s_retry == WIFI_MAX_RETRY) {
                s_retry++;   // 이 분기를 한 번만 타게 하는 표식
                ESP_LOGW(TAG, "WiFi %d회 실패 — 센서는 시리얼로만 출력하고 재연결은 계속 시도한다",
                         WIFI_MAX_RETRY);
                xEventGroupSetBits(s_wifi_events, WIFI_FAIL_BIT);
            }
        }
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        ip_event_got_ip_t *e = (ip_event_got_ip_t *)data;   // IP를 받아야 진짜 통신 가능
        ESP_LOGI(TAG, "WiFi 연결됨, IP=" IPSTR, IP2STR(&e->ip_info.ip));
        s_retry = 0;
        s_connected_once = true;
        xEventGroupSetBits(s_wifi_events, WIFI_CONNECTED_BIT);
    }
}

// 지금 이 순간 IP를 들고 있는가. 측정 루프가 매 샘플 이걸 확인해서,
// 링크가 죽은 동안에는 HTTP를 아예 시도하지 않는다(무의미한 타임아웃 방지).
static bool wifi_is_up(void)
{
    return s_wifi_events && (xEventGroupGetBits(s_wifi_events) & WIFI_CONNECTED_BIT);
}

// 연결 실패 시 주변 AP를 훑어 로그로 남긴다.
// SSID 오타 / 5GHz 전용 / 신호 세기 중 무엇이 원인인지 한 번에 판별할 수 있다.
static void wifi_scan_dump(void)
{
    static wifi_ap_record_t recs[20];   // 스택이 아니라 정적 버퍼 (레코드 하나가 꽤 크다)
    wifi_scan_config_t cfg = { .show_hidden = true };

    if (esp_wifi_scan_start(&cfg, true) != ESP_OK) return;   // true = 끝날 때까지 블로킹

    uint16_t n = sizeof(recs) / sizeof(recs[0]);
    if (esp_wifi_scan_get_ap_records(&n, recs) != ESP_OK) return;

    ESP_LOGW(TAG, "주변 2.4GHz AP %d개 (찾는 SSID: \"%s\")", n, WIFI_SSID);
    for (int i = 0; i < n; i++) {
        // rssi: -50 이상=매우 좋음, -70=양호, -80 이하=불안정, -90 이하=사실상 연결 불가
        ESP_LOGW(TAG, "  %-32s ch=%2d rssi=%d", (char *)recs[i].ssid, recs[i].primary, recs[i].rssi);
    }
}

// 연결(또는 최종 실패)까지 블로킹. 실패해도 앱은 죽지 않고 시리얼 출력만 계속한다.
static bool wifi_init_sta(void)
{
    // 필수 순서: NVS → netif → 이벤트 루프 → WiFi 드라이버
    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());       // 옛 펌웨어 잔재로 init 실패 시 지우고 재시도
        err = nvs_flash_init();
    }
    ESP_ERROR_CHECK(err);
    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    esp_netif_create_default_wifi_sta();

    wifi_init_config_t init_cfg = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&init_cfg));

    s_wifi_events = xEventGroupCreate();
    ESP_ERROR_CHECK(esp_event_handler_register(WIFI_EVENT, ESP_EVENT_ANY_ID, &wifi_event_handler, NULL));
    ESP_ERROR_CHECK(esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP, &wifi_event_handler, NULL));

    wifi_config_t cfg = {
        .sta = {
            .ssid     = WIFI_SSID,
            .password = WIFI_PASS,
            // 기본값(WIFI_FAST_SCAN)은 채널을 훑다가 SSID가 맞는 **첫** AP에 바로 붙는다.
            // 공유기+중계기처럼 같은 SSID가 여러 대면 신호가 약한 쪽을 잡아 자주 끊길 수 있다.
            // 전 채널을 다 훑고 RSSI가 가장 센 AP를 고르게 바꾼다 (부팅이 1초 남짓 느려지는 대신 안정적).
            .scan_method = WIFI_ALL_CHANNEL_SCAN,
            .sort_method = WIFI_CONNECT_AP_BY_SIGNAL,
        },
    };
    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_STA, &cfg));
    // IDF 기본 국가코드는 "01"(월드세이프)이라 1~11채널만 스캔한다.
    // 한국은 13채널까지 쓰므로, 공유기가 12/13번에 있으면 바로 옆에 있어도 안 보인다(reason=201).
    wifi_country_t country = {
        .cc = "KR", .schan = 1, .nchan = 13, .policy = WIFI_COUNTRY_POLICY_MANUAL,
    };
    ESP_ERROR_CHECK(esp_wifi_set_country(&country));

    ESP_ERROR_CHECK(esp_wifi_start());
    esp_wifi_set_max_tx_power(WIFI_MAX_TX_POWER);   // start() 이후에만 적용된다

    EventBits_t bits = xEventGroupWaitBits(s_wifi_events, WIFI_CONNECTED_BIT | WIFI_FAIL_BIT,
                                           pdFALSE, pdFALSE, portMAX_DELAY);
    if (bits & WIFI_CONNECTED_BIT) return true;

    wifi_scan_dump();   // 왜 못 붙었는지 단서를 남기고 실패 반환
    return false;
}

// ESP32는 RTC 배터리가 없어 부팅 시 시각이 1970년부터 시작한다.
// 서버 DTO의 Timestamp가 필수라 POST 전에 한 번 맞춰야 한다.
//
// wait_first=false로 불러도 SNTP 데몬은 뜬다. WiFi가 부팅 때 안 붙었더라도 데몬을
// 띄워 두면, 나중에 백그라운드 재연결이 성공하는 순간 알아서 시각을 맞춰 준다.
// (예전엔 wifi_ok일 때만 init을 불러서, 늦게 붙은 경우 시계가 영원히 1970에 머물렀다)
static void time_sync(bool wait_first)
{
    esp_sntp_config_t cfg = ESP_NETIF_SNTP_DEFAULT_CONFIG("pool.ntp.org");
    if (esp_netif_sntp_init(&cfg) != ESP_OK) {
        ESP_LOGW(TAG, "SNTP 초기화 실패 — 타임스탬프를 맞출 수 없다");
        return;
    }
    if (!wait_first) return;

    if (esp_netif_sntp_sync_wait(pdMS_TO_TICKS(10000)) != ESP_OK) {
        // 실패해도 데몬은 계속 재시도하므로 여기서 할 일은 없다.
        // 그동안 전송은 time_is_valid() 가드가 알아서 막아 준다.
        ESP_LOGW(TAG, "SNTP 첫 동기화 실패 — 백그라운드 재시도. 맞춰질 때까지 전송은 보류한다");
    }
}

// 시계가 실제로 맞춰졌는가. 미동기화 상태(1970년)로 POST하면 서버 DB에
// 1970년 레코드가 쌓이고 /dashboard·/event의 시간순 정렬이 통째로 망가진다.
// 임계값은 "이 펌웨어가 존재하기 전 시각이면 무조건 미동기화"라는 뜻의 하한선.
#define TIME_VALID_EPOCH  1600000000L   // 2020-09-13T12:26:40Z

static bool time_is_valid(void)
{
    time_t now;
    time(&now);
    return now > TIME_VALID_EPOCH;
}

// .NET DateTimeOffset이 그대로 파싱하는 UTC ISO-8601 (예: 2026-08-24T12:34:56Z)
static void iso8601_now(char *buf, size_t len)
{
    time_t now;
    struct tm tm_utc;
    time(&now);
    gmtime_r(&now, &tm_utc);
    strftime(buf, len, "%Y-%m-%dT%H:%M:%SZ", &tm_utc);
}

/* ===== HTTP ===== */
// TLS 핸드셰이크는 1~2초가 걸린다. 2초마다 새 연결을 맺으면 샘플 주기를 거의 다 잡아먹고,
// Cloudflare 경로가 조금만 느려져도 응답을 기다리다 타임아웃이 난다.
// 그래서 클라이언트 핸들을 하나만 만들어 두고 연결을 계속 재사용한다(HTTP keep-alive).
// 핸드셰이크는 최초 1회만 일어나고, 이후 요청은 열려 있는 TLS 세션 위로 흘러간다.
static esp_http_client_handle_t s_client;
static bool s_alive;            // 직전 요청이 성공 = 연결이 살아 있다고 보는 상태
static bool s_dirty;            // 응답 본문을 끝까지 못 읽음 = 이 핸들은 재사용 금지

// 핸들을 통째로 버린다. close()가 아니라 cleanup()인 이유:
// IDF는 헤더 수신(fetch_headers) 중에 딸려 온 본문을 heap 버퍼(orig_raw_data)에 캐시하는데,
// 이 캐시는 read()로 "정확히 끝까지" 읽었을 때(raw_len==0)와 cleanup() 때만 해제된다.
// close()는 소켓만 닫고 캐시를 그대로 남기므로, 그 상태로 다음 요청을 보내면
// http_on_body의 assert(orig_raw_data == raw_data)가 터져 패닉 → 리부팅한다.
static void http_client_reset(void)
{
    if (s_client) {
        esp_http_client_cleanup(s_client);
        s_client = NULL;
    }
    s_alive = false;
    s_dirty = false;
}

static bool http_client_ready(void)
{
    if (s_client) return true;

    esp_http_client_config_t cfg = {
        .url               = API_BASE "/biosignal",   // 실제 URL은 요청마다 set_url로 바꾼다
        .timeout_ms        = API_TIMEOUT_MS,
        .crt_bundle_attach = esp_crt_bundle_attach,   // https면 필수 (없으면 핸드셰이크 실패)
        .keep_alive_enable = true,                    // TCP keepalive: 죽은 상대를 빨리 감지
    };
    s_client = esp_http_client_init(&cfg);
    if (!s_client) {
        ESP_LOGE(TAG, "HTTP 클라이언트 생성 실패");
        return false;
    }
    // 요청 헤더는 핸들에 남아 다음 요청에도 그대로 쓰인다 → 한 번만 설정하면 된다.
    esp_http_client_set_header(s_client, "X-Api-Key", API_KEY);   // 서버 BioMonitor:ApiKey와 대조
    esp_http_client_set_header(s_client, "Content-Type", "application/json");
    return true;
}

// 요청 1건을 실제로 주고받고 HTTP 상태 코드를 돌려준다(전송 자체가 실패하면 -1).
// quiet=true면 실패해도 로그를 남기지 않는다 — 재사용 연결의 1차 시도는 실패가
// 정상 범주라서, 재시도까지 실패했을 때만 에러를 찍기 위함이다.
static int http_try(esp_http_client_method_t method, const char *url,
                    const char *body, char *resp, size_t resp_size, bool quiet)
{
    int len = body ? (int)strlen(body) : 0;

    s_dirty = false;
    esp_http_client_set_url(s_client, url);
    esp_http_client_set_method(s_client, method);

    esp_err_t err = esp_http_client_open(s_client, len);      // (재)연결 + 헤더 전송
    if (err != ESP_OK) {
        if (!quiet) ESP_LOGE(TAG, "%s 연결 실패: %s", url, esp_err_to_name(err));  // DNS/TLS/timeout
        return -1;
    }
    if (len > 0 && esp_http_client_write(s_client, body, len) != len) {
        if (!quiet) ESP_LOGE(TAG, "%s 본문 전송 실패", url);
        return -1;
    }
    if (esp_http_client_fetch_headers(s_client) < 0) {
        if (!quiet) ESP_LOGE(TAG, "%s 응답 헤더 없음", url);
        return -1;
    }
    int status = esp_http_client_get_status_code(s_client);

    // 본문은 무조건 끝까지 읽어낸다. resp에는 앞부분만 담고, 넘치는 나머지는 sink로 버린다.
    // read_response()로 resp 크기만큼만 읽으면 두 가지가 깨진다:
    //   ① 소켓에 남은 바이트를 다음 요청이 응답 헤더로 오해한다.
    //   ② fetch_headers 단계에서 IDF가 캐시해 둔 본문 버퍼가 해제되지 않아,
    //      다음 응답의 http_on_body에서 assert가 터지고 보드가 리부팅한다.
    // (POST /api/biosignal 응답은 tension 객체까지 붙어 200바이트를 훌쩍 넘는다.)
    char sink[64];
    size_t used = 0;
    for (;;) {
        bool keep = resp && resp_size && used + 1 < resp_size;
        char *dst = keep ? resp + used : sink;
        int cap = keep ? (int)(resp_size - 1 - used) : (int)sizeof(sink);

        int n = esp_http_client_read(s_client, dst, cap);
        if (n <= 0) {
            if (n < 0) {   // 타임아웃/전송 오류 → 캐시가 남았을 수 있으니 핸들을 버리게 한다
                s_dirty = true;
                if (!quiet) ESP_LOGW(TAG, "%s 응답 본문 read 실패 (%d)", url, n);
            }
            break;
        }
        if (keep) used += n;
    }
    if (resp && resp_size) resp[used] = 0;
    return status;
}

// perform() 대신 open/write/read를 쓰는 이유: 응답 본문까지 읽어야 자가진단에서
// "정말 서버가 처리했는지"를 눈으로 확인할 수 있기 때문.
static int http_request(esp_http_client_method_t method, const char *url,
                        const char *body, char *resp, size_t resp_size)
{
    if (!http_client_ready()) return -1;

    bool reused = s_alive;      // 살아 있던 연결을 재사용하는 시도인가
    int status = http_try(method, url, body, resp, resp_size, reused);

    // 실패했거나 본문을 다 못 읽은 연결은 재사용하지 않는다.
    // 여기서 close()가 아니라 reset()(=cleanup)을 쓰는 이유는 위 http_client_reset() 주석 참고.
    if (status < 0 || s_dirty) http_client_reset();

    // 서버나 Cloudflare가 유휴 연결을 먼저 닫아버리는 건 흔한 일이다. 이때는 장애가
    // 아니므로 조용히 연결을 새로 맺어 딱 한 번만 다시 시도한다.
    if (status < 0 && reused) {
        ESP_LOGW(TAG, "연결이 닫혀 있었음 — 새로 맺어 재시도");
        if (!http_client_ready()) return -1;    // 새 핸들 = 새 TLS 연결
        status = http_try(method, url, body, resp, resp_size, false);
        if (status < 0 || s_dirty) http_client_reset();
    }

    s_alive = (s_client != NULL && status >= 0);
    return status;
}

// 서버 record BioSignalDto(int? Bpm, int Gsr, double? SkinTemp, DateTimeOffset Timestamp)와
// 필드명/타입이 정확히 맞아야 모델 바인딩이 된다.
// bpm/skinTemp는 둘 다 선택 항목이고, 그래서 인자도 (값이 있는가, 값) 쌍으로 대칭이다.
static bool post_biosignal(bool has_bpm, int bpm, int gsr, bool has_temp, float temp_c)
{
    char ts[32], body[192], resp[192];
    iso8601_now(ts, sizeof(ts));

    // 없는 값은 0이 아니라 **null**로 보낸다. 서버 TensionAnalyzer는 null을 "측정 없음"으로
    // 보고 그 요소의 가중치를 빼고 나머지를 재정규화하지만, 0을 보내면 "심박 0 = 완전히
    // 평온"으로 채점하고 60샘플 변동성 창까지 가짜 평탄선으로 오염시킨다.
    char bpm_field[16], temp_field[16];
    if (has_bpm)  snprintf(bpm_field, sizeof(bpm_field), "%d", bpm);
    else          snprintf(bpm_field, sizeof(bpm_field), "null");
    if (has_temp) snprintf(temp_field, sizeof(temp_field), "%.1f", temp_c);
    else          snprintf(temp_field, sizeof(temp_field), "null");

    snprintf(body, sizeof(body),
             "{\"bpm\":%s,\"gsr\":%d,\"skinTemp\":%s,\"timestamp\":\"%s\"}",
             bpm_field, gsr, temp_field, ts);

    int status = http_request(HTTP_METHOD_POST, API_BASE "/biosignal", body, resp, sizeof(resp));
    // 200만 보지 않고 2xx 전체를 성공으로 본다 — 서버가 나중에 201 Created나
    // 204 No Content로 바꿔도 펌웨어가 멀쩡히 굴러가도록.
    if (status >= 200 && status < 300) {
        ESP_LOGI(TAG, "POST /biosignal %d %s", status, resp);
        return true;
    }
    // status가 뭐냐에 따라 해야 할 일이 완전히 다르니 힌트를 같이 남긴다.
    ESP_LOGE(TAG, "POST /biosignal 실패 (status=%d)%s", status,
             status == 401 ? " — API_KEY 불일치" :
             status == 400 ? " — JSON 필드/시각 형식 확인" :
             status <  0   ? " — 서버 도달 실패 (WiFi/DNS/TLS)" : "");
    return false;
}

/* ===== API 자가진단 =====
 * 측정 루프에 들어가기 전에 서버 왕복이 되는지 한 번에 확인한다.
 *  1) GET  /api/biosignal/recent : DNS + TLS + API 키 + 서버 생존 (읽기 전용)
 *  2) POST /api/biosignal        : 쓰기 경로까지 (테스트 샘플 1건이 DB에 실제로 저장됨)
 * 실패해도 앱은 계속 돈다 — 어디서 막혔는지 로그로 알려주는 게 목적.
 */
static bool api_selftest(void)
{
    char resp[256];

    ESP_LOGI(TAG, "[자가진단 1/2] GET %s/biosignal/recent?take=1", API_BASE);
    int status = http_request(HTTP_METHOD_GET, API_BASE "/biosignal/recent?take=1",
                              NULL, resp, sizeof(resp));
    if (status != 200) {
        if (status == 401) ESP_LOGE(TAG, "  ❌ 401 Unauthorized — API_KEY가 서버 BioMonitor:ApiKey와 다름");
        else if (status < 0) ESP_LOGE(TAG, "  ❌ 서버에 도달 못함 — WiFi/DNS/TLS 또는 URL 확인");
        else ESP_LOGE(TAG, "  ❌ HTTP %d", status);
        return false;
    }
    ESP_LOGI(TAG, "  ✅ 200 OK: %s", resp);

    ESP_LOGI(TAG, "[자가진단 2/2] POST %s/biosignal (테스트 샘플)", API_BASE);
    // 자가진단용 더미 샘플 — 경로만 확인하는 목적이라 측정값은 넣지 않는다(bpm/skinTemp 모두 null).
    if (!post_biosignal(false, 0, 0, false, 0)) {
        ESP_LOGE(TAG, "  ❌ POST 실패 — 위 status 참고 (400이면 JSON 필드/시각 형식 확인)");
        return false;
    }

    ESP_LOGI(TAG, "🎉 API 자가진단 통과 — 서버 연동 정상");
    return true;
}

/* ===== main ===== */
// 직전 리셋 원인. 재부팅 루프를 만났을 때 "왜 죽었는지"를 다음 부팅에서 알려준다.
// 특히 네이티브 USB(USB-JTAG)로 모니터하면 리셋 순간 포트가 끊겨 패닉 메시지가 유실되므로,
// 살아난 뒤에 찍히는 이 한 줄이 유일한 단서가 되는 경우가 많다.
static const char *reset_reason_str(void)
{
    switch (esp_reset_reason()) {
    case ESP_RST_POWERON:  return "전원 인가";
    case ESP_RST_EXT:      return "외부 리셋(RST 버튼)";
    case ESP_RST_SW:       return "소프트웨어 재시작";
    case ESP_RST_PANIC:    return "패닉/예외 (스택 오버플로·널 참조 등)";
    case ESP_RST_INT_WDT:  return "인터럽트 워치독";
    case ESP_RST_TASK_WDT: return "태스크 워치독";
    case ESP_RST_WDT:      return "기타 워치독";
    case ESP_RST_BROWNOUT: return "브라운아웃 (전원 전압 강하)";
    case ESP_RST_DEEPSLEEP:return "딥슬립 복귀";
    default:               return "알 수 없음";
    }
}

void app_main(void)
{
    ESP_LOGI(TAG, "GamerBio 시작 (직전 리셋: %s)", reset_reason_str());

    gsr_init();
    max30102_init();
    gpio_reset_pin(DHT_GPIO);

    // 타이밍이 걸린 센서 작업은 전부 core 1로 보낸다. core 0은 app_main(HTTP에서 수 초씩
    // 블로킹)과 WiFi가 쓰기 때문에, 여기 두면 PPG 폴링이 밀려 FIFO가 넘치고
    // DHT의 임계영역이 WiFi 인터럽트를 막는다.
    // 우선순위는 기본 태스크(1)보다 높고 WiFi(23) / TCP-IP(18)보다는 낮게.
    if (s_max_ok) {
        xTaskCreatePinnedToCore(ppg_task, "ppg", 4096, NULL, 5, NULL, 1);
    } else {
        ESP_LOGW(TAG, "MAX30102 미검출 — BPM 측정 없이 진행한다");
    }
    xTaskCreatePinnedToCore(dht_task, "dht", 3072, NULL, 4, NULL, 1);

    bool wifi_ok = wifi_init_sta();
    if (!wifi_ok) ESP_LOGW(TAG, "WiFi 연결 실패 — 센서 값은 시리얼로만 출력 (재연결은 계속 시도 중)");

    // WiFi 성공 여부와 무관하게 SNTP 데몬은 띄운다. 늦게 붙어도 자동으로 시각이 맞는다.
    time_sync(wifi_ok);

    bool api_ok = false;
    if (wifi_ok && time_is_valid()) {
        api_ok = api_selftest();
    } else if (wifi_ok) {
        // 시계가 1970이면 자가진단 POST가 쓰레기 타임스탬프를 DB에 남긴다 → 건너뛴다.
        ESP_LOGW(TAG, "시각 미동기화 — 자가진단 생략, 측정 루프에서 자동 재시도한다");
    }

    // 연속 실패 백오프. 서버가 죽었거나 API 키가 틀리면 2초마다 최대 16초짜리
    // 타임아웃을 물면서 로그만 도배하게 되므로, 실패가 쌓이면 전송 간격을 벌린다.
    int  fail_streak = 0;
    int  skip_left = 0;         // 이번 샘플부터 몇 번 전송을 건너뛸지
    bool time_warned = false;   // "시각 미동기화" 경고를 2초마다 도배하지 않기 위한 1회용 표식

    while (1) {
        int gsr = gsr_read();

        // DHT와 BPM은 core 1의 센서 태스크들이 계속 갱신한다 — 여기선 최신값을 훔쳐볼 뿐이다.
        float temp_c = 0, humidity = 0;
        bool dht_ok = dht_snapshot(&temp_c, &humidity);

        bool has_bpm = false;
        int bpm = 0;
        bool finger = false;
        uint32_t ir_dc = 0;
        ppg_snapshot(&has_bpm, &bpm, &finger, &ir_dc);

        printf("\n===== 센서 =====\n");
        printf("GSR      : %d\n", gsr);
        if (dht_ok) printf("DHT22    : %.1f C / %.1f %%RH\n", temp_c, humidity);
        else        printf("DHT22    : 읽기 실패\n");
        // IR 값을 항상 같이 찍는다 — PPG_FINGER_IR_ON/OFF를 실제 센서/LED 전류에 맞춰
        // 보정하려면 손가락을 댔을 때와 뗐을 때의 IR을 눈으로 비교해야 한다.
        if (!s_max_ok)   printf("MAX30102 : 미연결\n");
        else if (!finger) printf("MAX30102 : 손가락 없음 (IR=%lu, 감지 임계 %d)\n",
                                 (unsigned long)ir_dc, PPG_FINGER_IR_ON);
        else if (!has_bpm) printf("MAX30102 : 측정 중... (IR=%lu)\n", (unsigned long)ir_dc);
        else               printf("MAX30102 : %d BPM (IR=%lu)\n", bpm, (unsigned long)ir_dc);

        // 링크가 한 번이라도 끊겼으면 붙잡고 있던 TLS 소켓은 이미 죽은 것이다.
        // 이벤트 태스크가 아니라 요청을 실제로 보내는 이 태스크에서 정리해야 안전하다.
        // (안 버리면 재연결 후 첫 요청이 API_TIMEOUT_MS를 통째로 날린 뒤에야 실패한다)
        if (s_link_lost) {
            s_link_lost = false;
            http_client_reset();
        }

        // 전송 가드 3단계. 하나라도 아니면 HTTP를 아예 시도하지 않는다 —
        // 어차피 실패할 요청에 타임아웃을 쓰지 않는 게 핵심.
        //   ① 지금 IP를 들고 있는가        ② 시계가 맞았는가        ③ 백오프 중이 아닌가
        // gsr은 ADC raw 그대로 보낸다 — 서버 TensionAnalyzer의 절대 임계값도 아직 raw 기준.
        if (!wifi_is_up()) {
            if (api_ok) ESP_LOGW(TAG, "WiFi 끊김 — 재연결될 때까지 전송 보류");
            api_ok = false;
        } else if (!time_is_valid()) {
            if (!time_warned) {
                ESP_LOGW(TAG, "시각 미동기화 — SNTP를 기다리는 중 (전송 보류)");
                time_warned = true;
            }
        } else if (skip_left > 0) {
            skip_left--;                                  // 백오프 대기 중, 센서 출력만 계속
        } else {
            bool sent = post_biosignal(has_bpm, bpm, gsr, dht_ok, temp_c);
            if (sent) {
                if (!api_ok) ESP_LOGI(TAG, "서버 연동 복구됨");
                api_ok = true;
                fail_streak = 0;                          // 한 번 성공하면 즉시 원래 주기로 복귀
            } else {
                api_ok = false;
                // 3회까지는 매 주기 재시도(일시적인 끊김은 대개 여기서 회복된다).
                // 그 뒤부터 건너뛰는 샘플 수를 2배씩 늘리되 30샘플(=60초)에서 멈춘다.
                if (++fail_streak > 3) {
                    int shift = fail_streak - 3;
                    if (shift > 5) shift = 5;             // 시프트 폭 고정: 1<<32는 정의되지 않은 동작
                    int n = 1 << shift;
                    skip_left = n > 30 ? 30 : n;
                    ESP_LOGW(TAG, "연속 %d회 실패 — 다음 전송까지 %d초 대기",
                             fail_streak, skip_left * SAMPLE_PERIOD / 1000);
                }
            }
        }

        vTaskDelay(pdMS_TO_TICKS(SAMPLE_PERIOD));
    }
}
