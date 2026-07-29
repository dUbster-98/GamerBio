idf_component_register(SRCS "main.c"
                       PRIV_REQUIRES esp_adc esp_driver_i2c esp_driver_gpio esp_timer
                                     nvs_flash esp_wifi esp_event esp_netif lwip
                                     esp_http_client json
                       INCLUDE_DIRS "")
