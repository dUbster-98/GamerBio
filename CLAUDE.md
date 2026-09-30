# 🎮 Gamer Bio-Monitor Project

> 게임 플레이어의 실시간 생체 데이터를 수집·분석하여 참가자들과 공유하는 웨어러블 모니터링 시스템

---

## 📌 프로젝트 개요

| 항목 | 내용 |
|------|------|
| **목적** | 게이머의 심박수·스트레스 상태를 실시간으로 시청자/참가자와 공유 |
| **기간** | 3개월 |
| **형태** | 웨어러블 (ESP32) + 고정 서버 (Raspberry Pi 5) |
| **최종 목표** | 임베디드 커리어 포트폴리오 |

---

## 📍 현재 진행 상황 스냅샷 (2026-09-08)

| 항목 | 상태 |
|------|------|
| RPi5 OS + .NET 9 ASP.NET 런타임 | ✅ 설치 완료 (`/home/tjdgus/.dotnet`) |
| systemd 서비스 (`biomonitor-api.service`) | ✅ `Active (running)`, 자동 시작 등록 |
| Blazor Server 기본 화면 (`http://192.168.0.104:5000`) | ✅ 브라우저 접속 확인 |
| `POST /api/biosignal` 엔드포인트 | ✅ 더미 데이터 → DB 저장 동작 |
| `GET /api/biosignal/recent` 엔드포인트 | ✅ 조회 동작 |
| PostgreSQL 17 + `biomonitor` DB/유저 | ✅ TCP+비밀번호 인증 |
| EF Core 마이그레이션 (`biosignals` 테이블) | ✅ 앱 시작 시 자동 적용 |
| 배포 파이프라인 | ✅ PC `dotnet publish` → `scp` → systemd restart |
| SignalR Hub (`BioSignalHub`) + Blazor 실시간 푸시 | ✅ `BioSignalReceived` / `TensionUpdated` 동작 |
| 페이지 실시간 구독 → `BioEventBus`(인프로세스) 전환 + 프리렌더 깜빡임 제거 | ✅ 서버가 자기 자신에게 WebSocket을 걸던 구조 폐기, 첫 렌더에 데이터 확보 (아래 참고) |
| 전역 인터랙티브 렌더링 (`<Routes @rendermode="InteractiveServer" />`) | ✅ 앱 전체가 서킷 1개 위에서 동작 → 메뉴 이동 10~70ms, 문서 재로드 없음 (헤드리스 크롬으로 실측) |
| `TensionAnalyzer` 멀티모달 융합 + 상태 분류 | ✅ BPM/GSR/저변동성/**감정** 가중 융합 → Relaxed/Focused/Stressed/**Deadly** |
| Blazor `/dashboard` 라이브 대시보드 | ✅ 캠 패널 + 감정 라벨 + 텐션 게이지 + 바이탈 + 피드 |
| **[PC]** DeepFace 감정 분석 앱 (`deepface/emotion_webcam.py`) | ✅ 게임 특화 최적화 (아래 참고) |
| **[PC]** MJPEG 스트림 서버 (Python, `0.0.0.0:8080`) | ✅ 오버레이 영상 `multipart/x-mixed-replace` 서빙 |
| **[PC]** 감정 송신 (`EmotionPoster` → `/api/emotion`) | ✅ 주 얼굴 감정 throttled POST (`--post-url`) |
| RPi5 `/cam` MJPEG 리버스 프록시 | ✅ PC LAN 스트림을 HTTPS 동일 출처로 중계 |
| `POST /api/emotion` (PC DeepFace score 수신) | ✅ 감정+생체 융합 후 SignalR 푸시, 로컬 검증 완료 |
| Blazor `/gallery` 갤러리 (사진 업로드/조회/삭제) | ✅ `InputFile` 업로드 + 그리드, RPi 디스크 저장 (메타데이터는 DB) |
| `POST /api/gallery/capture` + surprise 자동 캡처 | ✅ PC가 **분석한 그 프레임**을 전송 → 갤러리 저장 + 실시간 갱신 |
| 공개 도메인 `https://bio-monitor.uk` (Cloudflare Tunnel) | ✅ 대시보드 + 캠 영상 외부 접속 확인 |
| ESP32 펌웨어 (`esp32/main/main.c`, ESP-IDF v6 / ESP32-S3) | ✅ 센서 3종 + WiFi/SNTP + HTTPS POST + MAX30102 BPM 계산까지 동작 (아래 참고). 실센서 연결 상태에서의 실측 보정은 미완 |
| 펌웨어 PI(관류 지수) 계산 + `"pi"` 전송 | ✅ 합성 PPG로 수치 검증 (참값 대비 -2.6%) |
| 서버 PI 수신 + `TensionAnalyzer` 융합 편입 | ✅ baseline 대비 하락률 채점 + GSR과 각성 슬롯 공유. 빌드 + 합성 데이터 6케이스 검증 (아래 참고). **실센서 baseline 실측은 미완** |
| SpO2 스트레스 지표 채택 여부 | ❌ 채택 안 함 (근거는 아래 "SpO2 미채택 결정") |
| 펌웨어 → MQTT 전환 | ⏸️ 보류 (아래 "MQTT 전환 보류 결정" 참고) |
| 감정 DB 영속화 (`Emotion` 엔티티) | ⏳ 미착수 (현재 메모리상 최신값만 융합) |
| Discord 봇 (`DiscordBotService`) — 알림 + 슬래시 명령 | ✅ 호스팅 서비스로 통합, `/status`·`/bpm` + Stressed/**Deadly** 진입 알림 (로컬 빌드 검증) |
| **Deadly 단계 + 이벤트 로그** (`deadly_events` 테이블, `/event` 페이지) | ✅ 진입 시각+파라미터 DB 저장, 실시간 페이지 갱신 (로컬 검증) |
| Blazor `/news` 뉴스 열람 (날짜별 아카이브) | ✅ RPi 스케줄러가 저녁마다 저장한 `yyyy-MM-dd.html`을 날짜 목록 + iframe으로 열람 (로컬 검증) |
| **실센서 로그 기반 오판정 수정 5건** (GSR 재보정/유효성 게이트, 단일신호 Deadly 차단, `sustainedEmotion` 버그, BPM 급변 게이트) | ✅ 실데이터 재생 검증 (Deadly 2→0건). **RPi 배포 미완** |
| **펌웨어 적응 불응기** (BPM 2배 보고 근본 수정) | ✅ **플래시 후 실측 확인 완료** — BPM 유효 23%→100%, 범위 61~157(평균 105) → 68~90, 하모닉 0건 |
| DHT22 읽기 실패 (임계영역 안 플래시 호출) | ✅ **플래시 후 실측 확인 완료** — 29.8°C / 49.6%RH 정상 수신 |
| **BPM 잔여 지터 = 기저선 흔들림** (원인 규명, 알고리즘으로는 해결 불가 판정) | ✅ 원시 IBI로 아티팩트 확정(RMSSD>SDNN). 후보 7종 검증 후 6종 기각, 중앙값 9만 채택(완화). **근본 해결은 센서 고정(기계적)** |

**DeepFace 감정 분석 앱 최적화:**
- 게임 무관 라벨 `disgust`/`sad` 제거 → 남은 5개(`angry`/`fear`/`happy`/`surprise`/`neutral`) 재정규화
- 클래스별 보정 가중치(`EMOTION_WEIGHTS`)로 모델의 `fear` 과소평가 상쇄 (`--fear-boost`)
- `sad`로 오분류된 fear 신호 일부를 fear로 회수 (`SAD_TO_FEAR_RATIO`, `--sad-to-fear`)
- EMA 시간적 평활화로 정지 시 라벨 떨림 제거 (`EMOTION_SMOOTHING`, `--smoothing`)
- 오버레이(박스+라벨+확률막대) 입힌 프레임을 MJPEG로 송출 (`--stream --port 8080`)

**멀티모달 융합 :**
- `TensionAnalyzer`가 최신 생체(`UpdateBio`)·감정(`UpdateEmotion`)을 각각 보관하고 둘 중 무엇이 들어오든 재계산
- 4-요소 가중 융합: BPM 0.35 / GSR 0.25 / 저변동성 0.15 / **감정 0.25** (감정 부재·만료 시 나머지 3개로 재정규화 = 생체 전용 폴백)
- 감정→스트레스 매핑: `angry/fear ×1.0`, `surprise ×2.0`, `neutral/happy ×0.0` → 0~100
- 감정 staleness 10초: PC 중단/끊김 시 자동으로 생체 전용 점수로 폴백
- 로컬 검증: 동일 생체 입력에서 감정만 바꿔 composite `49(생체만) → 59(fear) → 38(happy)` 반응 확인
- SignalR: `EmotionUpdated`(감정 원본) + `TensionUpdated`(융합 결과) 동시 푸시 → 대시보드 감정 라벨 + `EMOTION` 기여도 표시

**갤러리 + surprise 자동 캡처:**
- Blazor `/gallery` 페이지: `InputFile` 다중 업로드(캡션·드래그) + 썸네일 그리드 + 삭제, 메뉴에 `Gallery` 탭 추가
- **저장 구조**: 이미지 파일은 **RPi 디스크**(`GalleryStorage`, `Gallery:StoragePath` 설정), 메타데이터(`GalleryPhoto` 엔티티 → `gallery_photos` 테이블, 마이그레이션 `AddGalleryPhotos`)만 PostgreSQL. `GET /gallery/media/{id}`가 디스크 파일을 스트리밍(파일은 wwwroot 밖, 경로 비노출)
- **surprise 자동 캡처**: surprise가 임계값(기본 90) 초과 시 캡처. **타이밍 정확도를 위해 캡처 트리거를 PC로 이동** — DeepFace가 분석한 raw(평활화 전) 점수로 트리거하고 **그 분석 프레임 자체**를 `POST /api/gallery/capture`로 전송 → 서버는 받은 이미지를 그대로 저장. 서버가 사후에 라이브 프레임을 재촬영하던 방식(지연·불일치)을 폐기
- PC `CapturePoster`(별도 스레드, 재무장+쿨다운 가드)가 인코딩·전송 담당. 설정은 파일 상단 `DEFAULT_*` 블록 또는 `--capture-url`/`--capture-threshold`/`--capture-cooldown`/`--api-key`/`--insecure`(BooleanOptionalAction)로 오버라이드
- SignalR `GalleryPhotoAdded` → 갤러리 페이지가 캡처 사진을 실시간으로 맨 앞에 추가
- 로컬 검증 교훈: https 자체서명 인증서로 PC POST가 SSL 실패하는데 스크립트가 에러를 삼켜 무증상 → `--insecure`(verify off) + 1회성 실패 로그로 해결. 프로덕션(LAN 평문 http)은 `--insecure` 불필요

**실시간 구독 인프로세스화 + 프리렌더 깜빡임 제거 (2026-08-24):**

- **증상**: `/gallery`·`/event`에 들어가면 목록이 잠깐 보였다가 사라지고 몇 초 뒤 다시 나타남. 페이지 전환 클릭 반응도 전반적으로 느림.
- **원인 ①(느림)**: 페이지들이 `HubConnection`으로 `Nav.ToAbsoluteUri("/hubs/biosignal")`에 접속했는데, 이 코드는 **Blazor Server**라 서버에서 돈다 → RPi가 자기 공개 도메인(`bio-monitor.uk`)으로 나갔다가 **Cloudflare Tunnel을 거쳐 자기 자신에게** 돌아오는 WebSocket을 페이지마다 하나씩 열고 있었다. 접속 핸드셰이크가 `OnInitializedAsync` 안에 있어 그 왕복이 끝나야 초기화가 끝났고, 이후 모든 이벤트도 같은 경로를 왕복했다.
- **원인 ②(깜빡임)**: 페이지는 프리렌더(정적 SSR) + 인터랙티브, 두 번 렌더된다. 인터랙티브 인스턴스는 상태가 비어 있는데 `OnInitializedAsync`가 `await`하는 순간 Blazor가 **빈 상태로 한 번 렌더**한다 → 프리렌더된 목록이 지워지고, DB 조회(+위의 WebSocket 왕복)가 끝나야 다시 채워진다.
- **해결 ①**: `BioEventBus`(싱글톤, C# `event`) 도입. 엔드포인트가 `IHubContext` 브로드캐스트와 함께 버스에도 발행하고, 서버 렌더 페이지는 버스를 구독한다(구독은 `OnAfterRender(firstRender)` — 프리렌더 인스턴스가 구독을 남기지 않는다). SignalR Hub는 외부/브라우저 클라이언트용으로 그대로 유지. 페이지 I/O 0.
- **해결 ②**: 초기 로드를 `OnInitializedAsync`가 아니라 **`SetParametersAsync`에서 `base` 호출 전에** 수행 → 컴포넌트의 첫 렌더가 이미 데이터를 갖고 있어 프리렌더 마크업이 동일 내용으로 교체된다(중간 빈 렌더 없음). 추가로 `PersistentComponentState`로 프리렌더가 조회한 목록을 인터랙티브 패스에 넘겨 DB 재조회도 생략. (.NET 9는 서킷 시작 시점에만 저장 상태를 읽으므로, 서킷이 이미 살아 있는 페이지 이동은 `SetParametersAsync` 쪽이 담당한다)
- **대시보드**: 진입 즉시 `TensionAnalyzer.Latest()`로 현재 상태를 표시(예전엔 다음 패킷까지 `Calibrating`). 접속 표시등은 이제 연결 상태가 아니라 **데이터 신선도**(마지막 패킷 10초 이내)를 보여준다 — 인프로세스라 "연결"이라는 개념이 없어졌기 때문. 2초 `PeriodicTimer`가 상태가 바뀔 때만 렌더한다.
- **해결 ③ (클릭 반응)**: `App.razor`를 `<Routes @rendermode="InteractiveServer" />`로 바꿔 **라우터 자체를 인터랙티브**로 전환. 이제 페이지 이동이 SSR 왕복(터널 경유 HTML 재요청)이 아니라 살아 있는 서킷 메시지 1회로 끝난다. 각 페이지의 `@rendermode InteractiveServer` 선언은 라우터에서 상속되므로 제거했고, 인터랙티브 라우터는 앱 내부 링크의 미매칭 경로를 스스로 처리하므로 `Routes.razor`에 `<NotFound>` 분기를 추가했다(주소창 직접 입력은 여전히 `UseStatusCodePagesWithReExecute` 경로).
- **⚠️ `SetParametersAsync` 함정**: `await` 뒤에 `base.SetParametersAsync(parameters)`를 부르면 `ParameterView instance can no longer be read because it has expired`로 터진다(ParameterView는 동기 구간에서만 유효). 반드시 **`parameters.SetParameterProperties(this)`를 먼저 호출**하고, 마지막엔 `base.SetParametersAsync(ParameterView.Empty)`를 넘긴다. DB가 빠르면 `await`가 동기 완료돼 증상이 안 보이다가 느려지는 순간 500이 나므로 특히 위험하다 — 실제로 로컬 검증 중 DB에 1.5초 지연을 넣고서야 드러났다.
- **검증 방법**: 헤드리스 크롬 + CDP로 25ms 간격 DOM 스냅샷. 대조군(옛 코드 + DB 1.5초 지연)은 `사진 1장 → 0장(빈 상태 문구 렌더) → 1장`이 관측됐고, 수정본은 같은 조건에서 0장으로 떨어지는 구간이 없다. 메뉴 5회 클릭 이동 모두 10~70ms, `performance.getEntriesByType('navigation').length`는 끝까지 1(=문서 재로드 없음), 서버 예외 0건.

**Discord 봇:**
- **통합 방식**: 별도 프로세스가 아니라 기존 ASP.NET 호스트 안의 `BackgroundService`(`DiscordBotService`)로 실행 → `TensionAnalyzer` 싱글톤을 웹/SignalR/디스코드가 공유. `Program.cs`에서 싱글톤+`AddHostedService`로 1회 등록 (엔드포인트가 알림용으로 주입받을 수 있게 동일 인스턴스)
- **라이브러리**: `Discord.Net` 3.20.1 (WebSocket + Interactions)
- **슬래시 명령 (양방향)**: `BioCommands` 모듈의 `/status`(융합 텐션 전체), `/bpm`(심박 기여) → `TensionAnalyzer.Latest()`(신규 추가한 읽기 전용 스레드 안전 접근자)로 조회. 글로벌 등록은 반영 ~1시간 → 개발 중엔 `RegisterCommandsToGuildAsync(길드ID)`로 즉시 반영
- **알림 (단방향)**: `/api/biosignal`·`/api/emotion`이 `TensionUpdated` 푸시 직후 `bot.NotifyTensionAsync(tension)` 호출. **상태 전환 시에만** 발송(`_lastNotified` 가드)하여 도배 방지, `Stressed` 진입 시 알림 채널에 메시지
- **비밀 설정**: `Discord:Token`, `Discord:AlertChannelId`. PC 개발은 user-secrets(`UserSecretsId` csproj 등록됨), RPi 배포는 `appsettings.Production.json`(gitignore). 미설정 시 봇 비활성화 + 경고 로그 (앱은 정상 기동)

**ESP32 펌웨어 (2026-08-24) — `esp32/main/main.c` 단일 파일:**

- **부팅 순서**: 센서 init → WiFi(STA) → SNTP → API 자가진단 → 측정 루프. 어느 단계가 실패해도 앱은 죽지 않고 시리얼 출력을 계속하며 백그라운드로 복구를 시도한다.
- **태스크 배치**: core 0 = `app_main`(2초 주기 HTTP 전송) + WiFi/TCP-IP, core 1 = `ppg_task`(100ms) + `dht_task`(10초).
  타이밍이 걸린 센서 작업을 core 1로 몰아낸 이유는, DHT 비트뱅잉의 `taskENTER_CRITICAL`이 40비트 수신 동안 약 5ms 인터럽트를 막는데 그게 WiFi와 같은 코어면 통신이 밀리기 때문. 공유 상태는 `portMUX` 스핀락 스냅샷으로 주고받는다.
- **MAX30102 FIFO**: `WR_PTR`/`OVF_COUNTER`/`RD_PTR`(0x04~0x06 연속 주소)을 한 번에 읽어 쌓인 개수를 구하고 그만큼 버스트로 꺼낸다. 오버플로가 보이면 신호 연속성이 끊긴 것이므로 박동 검출기를 리셋. 유효 샘플레이트 50Hz(`SPO2_SR`=100Hz ÷ `SMP_AVE`=2) → FIFO(32칸)가 640ms에 차므로 100ms 폴링에 6배 여유.
- **BPM 계산**: 원시 IR → DC 제거(1차 IIR 하이패스) → 이동평균(5탭) → 적응 임계 **상승 교차** → IBI → **중앙값** → BPM.
  - 시각을 `esp_timer_get_time()`이 아니라 **샘플 인덱스**로 센다. FIFO에서 5~10샘플을 몰아 읽기 때문에 읽은 시각을 쓰면 전부 같은 순간처럼 뭉개진다. 대가로 MAX30102 내부 오실레이터 오차 ±2%가 BPM에 실린다.
  - 꼭대기가 아니라 진폭 중간의 상승 구간을 잡는 이유: 맥파 꼭대기는 평평해서 샘플마다 흔들리지만 상승 구간은 기울기가 급해 시각 지터가 작다.
  - 평균이 아니라 중앙값인 이유: 박동을 하나 놓치면 그 IBI가 정확히 2배로 튀는데 평균은 끌려가고 중앙값은 무시한다.
  - 손가락 감지는 원시 IR DC의 ON/OFF 히스테리시스(`PPG_FINGER_IR_ON`/`_OFF`).
- **HTTP**: 핸들 하나를 keep-alive로 재사용(TLS 핸드셰이크 1회). 실패하거나 응답 본문을 끝까지 못 읽으면 `esp_http_client_cleanup()`으로 핸들째 버린다 — `close()`는 `fetch_headers` 단계에서 캐시된 본문 버퍼(`orig_raw_data`)를 해제하지 않아, 남겨두면 다음 응답의 `http_on_body`에서 `assert(orig_raw_data == raw_data)`가 터져 리부팅한다. 응답 본문은 버퍼를 넘겨도 sink로 끝까지 읽어낸다.
- **전송 가드 3단계**: ① 지금 IP를 들고 있는가(`WIFI_CONNECTED_BIT`를 끊길 때 내린다) ② 시계가 맞았는가(1970 타임스탬프 차단) ③ 백오프 중이 아닌가(연속 실패 3회 초과 시 간격을 2배씩, 최대 60초). 어차피 실패할 요청에 8초 타임아웃을 쓰지 않는 게 목적.
- **WiFi 재연결은 포기하지 않는다**. `WIFI_MAX_RETRY`는 "app_main을 언제 풀어줄까"의 기준일 뿐, 초과해도 `esp_wifi_connect()`는 계속 돈다 (공유기가 보드보다 늦게 켜지는 경우 대비). `scan_method`는 `WIFI_ALL_CHANNEL_SCAN` + `WIFI_CONNECT_AP_BY_SIGNAL` — 기본 FAST_SCAN은 같은 SSID 중 신호가 약한 중계기를 잡을 수 있다.
- **미보정 항목 (실센서 연결 후 조정 필요)**:
  - `PPG_FINGER_IR_ON/OFF` — LED 전류와 센서 개체차에 따라 다름. 시리얼에 IR 값을 항상 찍으니 손가락 유무를 비교해 맞출 것.
  - ~~서버 `TensionAnalyzer.GsrAbsLow/High`(300/800)~~ → **2026-09-07 실측으로 650/1300으로 보정 완료** (아래 "실센서 로그 분석 → 4건 수정" 참고). 유효성 게이트도 같이 생겨서 미연결 플로팅 값은 이제 아예 걸러진다.
- **⚠️ `WIFI_PASS` / `API_KEY`가 소스에 하드코딩되어 있다.** 아직 커밋되진 않았으나 파일은 git 추적 대상이므로, 공개 전에 Kconfig나 NVS로 빼고 키를 재발급할 것.

**BPM nullable 전환 (2026-08-24):**

- 웨어러블은 PPG 센서에 피부 접촉이 있을 때만 심박을 보고한다. 접촉이 없을 때 0을 보내면 `TensionAnalyzer`가 "심박 0 = 완전히 평온"으로 채점하고, 더 나쁘게는 60샘플 변동성 창을 가짜 평탄선으로 오염시킨다(기존 `PLACEHOLDER_BPM 60`은 stdDev가 항상 0이라 모든 판정에 +15점을 상수로 더하고 있었다).
- `BioSignal.Bpm` / `DeadlyEvent.Bpm` / `BioSignalDto.Bpm` → `int?`. 마이그레이션 `MakeBpmNullable` (두 테이블 `AlterColumn`, `Down()`은 NULL을 0으로 먼저 채운다). 펌웨어는 `"bpm":null`을 보낸다 — `skinTemp`가 쓰던 방식과 동일.
- 융합은 감정이 stale일 때 쓰던 재정규화 패턴을 그대로 확장했다. 빠질 수 있는 요소가 셋: **감정**(PC 미연결/5초 초과), **BPM**(접촉 없음), **저변동성**(BPM을 따라감 + 유효 샘플 `VariabilityMinSamples`=10 미만). GSR만 항상 있으므로 가중치 합이 0이 되지 않는다. 변동성 창은 `Bpm`이 있는 샘플만 쓴다 — 접촉 공백을 이어 붙이면 그 공백 자체가 "심한 변동"으로 읽혀 오히려 스트레스가 낮게 나온다.
- `BioOnlyExtreme`(생체 전용 90+ → Deadly) 오버라이드는 **BPM이 있을 때만** 적용한다. BPM이 없으면 `bioComposite`가 GSR 단독이 되는데, GSR 절대 임계값이 아직 미보정이라 접촉이 끊길 때마다 Deadly가 뜰 수 있다.
- **배포 순서 주의**: 서버를 먼저 올려야 한다. 새 펌웨어의 `"bpm":null`을 구버전 서버(`int Bpm`)는 400으로 거부한다. 반대(새 서버 + 구 펌웨어)는 문제없다.

**SpO2 미채택 결정 + PI 도입 (2026-08-27):**

MAX30102는 RED LED를 이미 켜 두고 있어 SpO2 계산이 가능하지만, **스트레스 지표로는 쓰지 않기로 했다.**
- **신호가 노이즈보다 작다**: 건강한 사람이 게임하는 동안 SpO2는 96~99%에 갇혀 있고 스트레스로 움직이는 폭이 1%p 수준인데, 반사형 손가락 측정의 실측 오차는 ±2~3%p다.
- **방향이 단조롭지 않다**: 스트레스 → 호흡수 증가라 SpO2는 떨어지는 게 아니라 유지/미세상승한다. "높을수록 평온" 같은 관계가 없어 `TensionAnalyzer`의 0~100 점수로 매핑할 근거 자체가 없다.
- **검증 불가**: SpO2 = f(R)의 f는 임상 캘리브레이션 경험식이라 저산소 피험자 없이는 절대값을 확인할 방법이 없다.
- **게임 중이 최악 환경**: R = (AC/DC)_red ÷ (AC/DC)_ir 는 진폭의 절대 비율이라 피크만 잡으면 되는 BPM보다 모션 아티팩트에 훨씬 취약하다.

대신 같은 IR 파형에서 **PI(관류 지수) = AC ÷ DC × 100 [%]** 를 뽑는다. RED 채널이 필요 없다.
- **왜 이게 낫나**: 교감신경 각성 → 말초 혈관 수축 → 맥파 진폭 감소 → **PI 하락**. 방향이 단조롭고, 비율이라 LED 밝기·피부색·밀착도 개체차에 둔감해 절대 캘리브레이션이 필요 없다. GSR(땀샘)과 같은 자율신경 축을 **다른 물리량(혈관)** 으로 재므로 융합에서 독립적으로 기여하고, 둘이 같이 움직이면 교차 검증이 된다 (GSR 절대 임계값이 미보정인 현재 특히 유용).
- **구현**: `ppg_t`에 `dc_slow`(원시 IR의 느린 EMA, 계수 1/64 → 차단 ≈0.12Hz) + `beat_max`/`beat_min`(박동 구간 실측 최대/최소) 추가. 하이패스가 DC를 지우기 전에 `dc_slow`를 챙기고, 박동 하나가 끝날 때마다 그 구간의 peak-to-peak ÷ DC로 PI를 내고 EMA(박동당 0.25, τ≈4박≈3초)로 평활화.
- **⚠️ 분자로 포락선 `env_max - env_min`을 쓰면 안 된다** (실제로 처음엔 그렇게 짰다가 검증에서 잡혔다): 포락선은 박동 사이에 τ≈2초로 서로에게 수축하므로 다음 박동 전에 주저앉는다 → 진폭이 체계적으로 작게 나오고 **그 편차가 심박수에 따라 달라진다**. 스트레스는 심박도 같이 올리므로 이건 가짜 PI 변화로 읽힌다.
- **검증 (합성 PPG를 파이썬으로 포팅해 수치 확인, C 컴파일러 없이)**: 참값 대비 오차 **포락선 방식 -18.5%(50~140bpm에서 -16~-25%로 변동) → 구간 실측 방식 -2.6%(-2.6~-10.8%)**. DC 20000/50000/120000에서 결과 동일(개체차 불변성 확인). 혈관수축 시뮬레이션(2.0%→0.8%)에서 ~5초 내 수렴. 참 PI 0.05%(진폭 < `PPG_MIN_AMPLITUDE`)는 유효 샘플 0건 = 낮은 값이 아니라 **`null`** 로 나가는 것 확인 (포락선 방식은 여기서 노이즈를 진폭으로 오인해 584샘플을 잘못 유효 처리했다).
- **결측 규약**: 접촉 없음/박동 미검출이면 `"pi":null`. BPM과 같은 이유 — PI에서 0은 "혈관이 완전히 수축했다"는 최대 스트레스로 읽히므로 절대 0을 보내면 안 된다. `PPG_MIN_AMPLITUDE`(50카운트)가 곧 PI의 측정 하한이다(DC 50000에서 PI 0.1%).
- **배포 순서 제약 없음**: `pi`는 아직 `BioSignalDto`에 없지만 System.Text.Json이 모르는 프로퍼티를 무시하므로 구버전 서버도 400을 내지 않는다. bpm nullable 전환 때와 달리 펌웨어를 먼저 올려도 안전하다.
- **남은 일**: 실센서로 편안한 상태의 baseline PI 측정 → `PiDropMax` 재조정. (서버 편입은 아래 절에서 완료)

**PI → TensionAnalyzer 융합 편입 (2026-08-27):**

- **채점 방식 — 절대값 금지**: PI에는 절대 스케일이 없다(LED 전류·부착 위치·손 온도로 통째로 이동). `MapScore(pi, low, high)`를 쓰면 `GsrAbsLow/High`가 지금 만들고 있는 부채를 하나 더 만드는 셈이고, 애초에 PI를 SpO2 대신 고른 이유(비율이라 캘리브레이션 불필요)를 스스로 버리는 것이다. 대신 **개인 baseline 대비 하락률**: `piDrop = (baseline - pi) / baseline` → `MapScore(piDrop, 0, PiDropMax=0.5)`. baseline보다 높으면 음수 → clamp 0 (혈관확장은 스트레스가 아니다).
- **baseline = 10분 창의 85퍼센타일** (`_piHistory`, `PiBaselineMinSamples=30` ≈ 60초). PI는 평온할 때 가장 높고 수축은 아래로만 가므로 고분위수 ≈ 이완 상태다. **최대값이 아니라 분위수**인 이유: 단발 아티팩트 하나가 baseline을 부풀리면 그 뒤로 전부 스트레스로 읽힌다. GSR식(창 앞쪽 절반 평균)을 안 쓴 이유는 드리프트 — GSR은 절대 수준 점수를 `Math.Max`로 얹어 때웠지만 PI엔 그 탈출구가 없다.
- **`_window`(60샘플)를 재사용하지 않고 별도 `_piHistory`를 둔 이유**: 10분 창이 필요한데 `_window`를 늘리면 GSR baseline과 BPM 변동성까지 같이 바뀌어 조정된 동작이 회귀한다. `_emotionHistory` 선례를 따랐다.
- **접촉 끊김(`PiContactGap` 30초) 시 이력 폐기**: 재부착하면 위치가 달라 AC/DC 비율 자체가 바뀌므로 그 경계를 넘는 baseline 비교는 무의미하다.
- **융합 위치 — GSR과 한 슬롯 공유** (`WeightArousal = 0.25`, 이전 `WeightGsr`): PI와 GSR은 같은 교감신경 축을 다른 효과기(혈관/땀샘)로 재므로 독립 가중치를 주면 각성을 이중 계상한다. **기존 가중치를 하나도 안 건드려서** 이미 조정된 상태 경계가 그대로 유지되는 실용적 이점도 있다. `arousalScore = hasPi ? (gsr + pi) / 2 : gsr`.
  - **`Math.Max`가 아니라 평균인 이유**: GSR 절대 구간이 미보정이라 헛스파이크가 나는데 max는 그 잘못된 값이 항상 이기게 만든다. 평균이면 정상 PI가 끌어내린다 — PI 도입 목적(미보정 GSR 교차 검증)이 정확히 이것이다.
  - 가중치는 한 슬롯이지만 `TensionReading`/`DeadlyEvent`에는 `GsrScore`와 `PiScore`를 **각각** 싣는다. 어느 쪽이 말하는지 / 둘이 일치하는지가 보여야 미보정 GSR을 판단할 수 있다.
- **`extremeBio` 게이트 완화**: `hasBpm` → `(hasBpm || hasPi)`. 각성이 GSR·PI 평균이라 90을 넘으려면 **둘 다** 90 이상이어야 하고, 미보정 GSR 단독으로는 도달할 수 없다. 접촉은 있는데 심박이 아직 안 잡힌 구간(저심박에선 PI가 먼저 유효)에서 판정이 살아난다.
- **스키마**: `BioSignal.Pi`(double?), `DeadlyEvent.Pi`+`PiScore`, 마이그레이션 `AddPi`. `DeadlyEvent`에 원시 PI를 같이 남기는 이유는 `PiScore`가 당시 baseline 대비 상대값이라 나중에 되짚을 수 없기 때문.
- **검증** (`scratchpad/PiFusionCheck` — 프로덕션 소스를 `<Compile Include>`로 링크해 복사본 없이 실행): 하락률 반응 / GSR 헛스파이크 방어 / baseline 드리프트 / 결측 조합 재정규화 / `extremeBio` 게이트 / 접촉 끊김 폐기 6케이스 통과. 빌드 오류 0.
- **⚠️ 알려진 동작 변화 두 가지 (실센서 측정 시 확인할 것)**:
  1. **PI가 정상일 때 각성 점수가 절반이 된다.** baseline이 85퍼센타일이라 PI는 평상시 대부분 0점이고, 그러면 `arousal = gsr / 2`가 된다. 검증에서 동일 생체 입력이 `PI 없음 40점 → PI 정상 20점`으로 바뀌어 Focused→Relaxed까지 갈렸다. 평균 방식의 필연적 귀결이고, 미보정 GSR을 누르는 게 지금은 오히려 바람직하지만 **GSR 보정 후에는 재검토 대상**이다.
  2. **baseline 드리프트가 완만하지 않고 절벽이다.** 50% 하락을 유지하면 PI 점수가 8분까지 100을 유지하다가 10분에 0으로 떨어진다(85퍼센타일이라 창의 15%만 옛 값이면 baseline이 유지되다가, 완전히 롤오버되는 순간 무너진다). 8분 넘는 연속 고텐션 구간에서만 문제가 되며, 필요하면 퍼센타일 baseline에 더 긴 감쇠 baseline을 섞어 완화할 수 있다.

**기동 지연 수정 (2026-08-27, PI 작업 중 발견한 별건):**
- **증상**: 손가락을 댄 뒤 첫 BPM까지 **10.0초**, 첫 PI까지 **8.4초** (합성 신호 실측). PI 작업 중 최초 유효 시각을 재다가 드러났다.
- **원인**: 하이패스 상태변수 `dc_w`가 0에서 시작해 `y[0] = ir`(수만 카운트)라는 가짜 스텝이 생긴다. 더 나쁜 건 포락선이 **그 값으로 primed**된 뒤 `PPG_ENV_DECAY`(1%/샘플)로만 수축한다는 점 — 40000에서 실제 진폭 1000까지 내려오는 데 `ln(40)/0.01 ≈ 370샘플 ≈ 7.4초`가 걸린다. 하이패스 자체는 τ=20샘플(0.4초)로 금방 가라앉는데, 포락선이 그 트랜지언트를 붙잡고 있는 구조였다.
- **수정**: `if (p->idx == 1) p->dc_w = (float)ir / (1.0f - PPG_DC_ALPHA);` 한 줄. 상수 입력 x에 대한 정상상태가 `w = x + α·w → w = x/(1-α)`이므로 거기서 출발하면 `y[0] = 0`이 되어 가짜 스텝 자체가 없어진다.
- **결과**: 첫 BPM **1.8~4.7초**, 첫 PI **3.0~3.5초** (심박·DC에 따라). 이제 PI 쪽 하한은 `PPG_PI_WARMUP`(3초)이다.
- **회귀 없음 확인**: 수렴 후의 PI 정확도(-2.6%), DC 불변성, 심박 의존성, 하한 처리 결과가 수정 전과 **완전히 동일**하다. 기동 구간만 바뀌고 정상상태는 그대로.
- **부수 사실**: PI와 BPM은 유효 조건이 달라(PI = warmup + 박동 1개, BPM = 박동 3개) 어느 쪽이 먼저 나올지가 심박에 따라 갈린다 (50bpm: PI 3.5s / BPM 4.7s, 140bpm: BPM 1.8s / PI 3.0s). 그래서 두 값은 플래그를 공유하지 않고 각자 들고 다닌다.

**실센서 로그 분석 → 4건 수정 (2026-09-07, 스릴러 시청 세션):**

착용 상태로 영화를 보는 동안 쌓인 실데이터(`/api/biosignal/recent` 200샘플 + `deadly_events`)를 분석해 오판정 4건을 찾아 고쳤다. **이 세션이 지금까지의 "미보정" 항목들을 실제로 보정할 첫 근거**가 됐다.

- **① GSR 절대 구간이 통째로 틀려 있었다 (`GsrAbsLow/High` 300/800 → 650/1300).**
  착용 중 실측이 p05=739 / p50=1016 / p95=1239 / max=1451 이었다. 즉 **착용 샘플의 4분의 3이 상한 800을 넘어** `MapScore`가 항상 100을 반환했다 — GSR 요소가 상수가 되어 정보를 하나도 싣지 못하고 있었다. 관측된 동작 범위(약 600~1450)를 0~100에 펼치도록 다시 잡았다. (⚠️ 스릴러 시청 = 각성 편향이라 진짜 이완 상태 baseline을 따로 재면 하한은 더 내려갈 여지가 있다)

- **② GSR 유효성 게이트 신설 — "항상 존재하는 요소"라는 전제가 틀렸다.**
  전극이 떨어지면 raw가 0~67로 주저앉고(개방), 전극끼리 닿거나 도전면에 놓이면 2446~2580으로 붙박인다. 둘 다 피부 전도도가 아닌데 각각 **Relaxed(0점)와 Deadly(100점)라는 정반대의 거짓 판정**을 만들고 있었다. `(GsrOpenCircuit=100, GsrRailHigh=2200)` 밖은 `null` 처리 → 재정규화에서 빠진다. `esp32/main/main.c`의 "gsr도 nullable로 가는 게 일관되지만… GSR 실측 보정할 때 같이 결정할 것" 주석이 가리키던 문제이고, 펌웨어 `gsr_read()`의 실패값 -1도 같이 걷힌다. **GSR이 빠질 수 있게 되면서 모든 요소가 동시에 빠지는 경우가 생겼고**(가중치 합 0), 그때는 점수를 지어내지 않고 `Calibrating`으로 물러난다.

- **③ 단일 신호만으로 Deadly가 선언되던 구멍 (핵심).**
  재정규화는 요소가 하나만 남으면 그 요소의 점수가 **그대로** 융합 점수가 된다. 그래서 접촉이 끊겨 BPM·PI·감정이 모두 빠진 구간에서 미보정 GSR 단독으로 `score=100` Deadly가 반복 기록됐다 (**당일 12건 중 10건이 `bpm=null, pi=null, gsrScore=100`**, 9/2 세션은 통째로 도배). `extremeBio`의 `(hasBpm || hasPi)` 게이트가 정확히 이 상황을 막으려 한 것이었지만 **그건 승격 경로만 지킨다** — 가중 평균 자체가 이미 85를 넘으면 승격이 필요 없어 게이트를 그냥 지나친다. 같은 원칙을 평균 쪽에도 적용했다: 독립 신호가 1개뿐이면 `StressedCeiling-1`(84)로 상한 → 최대 Stressed까지만. `extremeBio`도 `(hasBpm||hasPi)` → `독립 생체 신호 ≥ 2`로 바꿔 전제 없이 같은 의도를 표현한다. **예외는 `extremeBpm`(160+)** — BPM은 보정이 필요 없는 절대 단위이고 이제 급변 게이트까지 통과한 값이라 단독 승격을 허용한다.

- **④ `sustainedEmotion` 승격이 감정 없이도 발동하고 있었다 (③을 고치다 드러난 별건).**
  조건이 `bioComposite + 0.25 × 창평균 ≥ 85`인데 감정 이력이 비면 창평균이 0이라, `bioComposite`가 혼자 85를 넘는 순간 **감정이 전혀 없어도 참**이 된다. 이름과 달리 감정과 무관한 승격 경로였고, 카메라가 꺼져 있던 이 세션에서 ③의 상한을 그대로 우회해 GSR 단독 Deadly를 되살렸다. `창평균 > 0`을 조건에 추가 (최소 샘플 수 미달이면 0을 반환하므로 이 비교 하나가 "이력이 충분하고 실제로 스트레스를 가리킨다"를 함께 보장한다).

- **⑤ BPM 하모닉 아티팩트 — 서버 게이트 + 펌웨어 근본 수정 (2계층).**
  안정 구간 70~85 사이에 142/136/130/157/**200**이 섞여 들어왔고 142=2×71, 136=2×68, 130=2×65로 **정확히 2배**였다. 200샘플 창 하나에 110 초과가 32개. 서버 쪽은 "직전 채택값 대비 초당 10bpm"을 넘으면 결측 처리하는 급변 게이트를 넣었다(0점이 아니라 `null` — BPM null 규약과 같은 이유). 6초면 70→130까지 오를 수 있는 예산이라 진짜 놀람 반응은 통과한다. 15초 넘게 끊기면 비교 자체를 포기하고 새 기준으로 받는다(폐색 방지).

- **검증**: `scratchpad/Replay`가 **git HEAD 버전과 수정본을 각각 링크**해 같은 실데이터 200샘플을 흘려 넣는다. 결과 **Deadly 2건 → 0건** (둘 다 BPM 200/176 하모닉이 원인), Stressed 10 → 1. 추가로 로그에 기록된 오판정 3종을 재구성: 접촉 끊김 GSR 단독 `Deadly(100) → Stressed(84)`, 미착용 레일 `Deadly(100) → Calibrating`, 전극 개방 `Relaxed(0) → Calibrating`. 회귀 방지 2종(진짜 급성 스트레스, 점진적 심박 상승 70→130)은 수정 전후 동일하게 통과.

**펌웨어 적응 불응기 — BPM 2배 보고의 근본 원인 (2026-09-07):**

위 ⑤의 원인은 펌웨어였다. `PPG_IBI_MIN_MS=273`이 이중맥박파(dicrotic notch)를 막는다고 주석에 적혀 있었지만 **산수가 맞지 않았다**: notch는 심주기의 35~45% 지점에 오므로 심박 70(주기 857ms)이면 300~390ms 뒤 — 273ms 하한보다 **길다**. 그래서 notch 교차가 정상 박동으로 채택되어 IBI가 360/497로 쪼개졌다.

- **고정값으로는 풀 수 없다**: 심박 70의 notch(≈360ms)를 막으려면 하한이 400ms는 돼야 하는데 그러면 150bpm 이상의 진짜 박동도 막힌다 → **적응 불응기** = `clamp(현재 주기 × 0.5, 273, 600)`.
- **부트스트랩이 실제 해법이다 (탈출이 아니라 예방)**: IBI가 3개 미만이면 가장 긴 값(600ms)에서 출발해 안정 심박의 notch를 **첫 박동부터** 막는다. 한번 쪼개진 상태에 빠지면 어떤 추정치로도 못 빠져나오기 때문이다 — 쪼개진 [360,497,…]에서 불응기를 360 위로 올리려면 긴 쪽의 0.72배가 필요한데, 비율을 0.72로 잡으면 이번엔 박동 누락(IBI 2배)에서 진짜 박동을 전부 막는 폭주가 생긴다. **양쪽을 동시에 만족하는 비율은 없다.**
- **적응은 최댓값이 아니라 중앙값으로**: 부트스트랩 덕에 버퍼가 깨끗하므로, 박동 누락으로 2배 튄 IBI가 섞여도 중앙값은 통째로 무시한다(`ppg_bpm`이 평균 대신 중앙값을 쓰는 이유와 같다). `ppg_median_ibi()`로 뽑아 `ppg_bpm`과 공유한다.
- **`crossing` / `accepted` / `beat` 3단 구분**: notch 교차는 버리면서 **기준점(`last_beat_idx`)을 옮기면 안 된다** — 옮기면 다음 진짜 박동까지가 notch부터 재어져 거른 의미가 사라진다. 반대로 너무 긴 간격(박동 누락)은 기준점을 옮기는 게 맞다(기준 자체가 낡았다). PI의 박동 구간 경계도 `crossing` → `accepted`로 바꿨다 — notch에서 구간을 다시 열면 peak-to-peak가 조각에서 측정되어 **notch는 BPM만이 아니라 PI도 같이 망가뜨리고 있었다.**
- **검증** (`scratchpad/ppg_check.py` — main.c 파이프라인을 파이썬으로 포팅, PI 검증과 같은 방식): 뚜렷한 notch에서 **수정 전 7/8 실패 → 수정 후 0/8**. notch 위치 35~50% 전 구간 통과, notch 크기 0.55~0.80 전 구간 통과. 박동 누락 내성 5/5 통과(최댓값 방식은 폭주). 빠른 심박 부트스트랩 회복 100~180bpm 전부 정확값 수렴. notch 없는 깨끗한 신호는 수정 전후 **완전히 동일**(회귀 없음).
- **대가**: 부트스트랩 600ms 때문에 첫 BPM이 100bpm에서 1.9→2.5초, 140bpm에서 1.8→3.1초로 늦어진다. 50/70bpm은 변화 없음.
- **실제 빌드 확인**: `compile_commands.json`의 main.c 컴파일 명령을 그대로 재실행(`-fsyntax-only -Wall -Wextra`) → 오류·경고 0. **아직 플래시하지 않았다.**

**DHT22 읽기 실패 추적 (2026-09-07~08) — 원인은 "임계영역 안의 플래시 호출":**

`skinTemp`가 장시간 100% null이었다(빌드 전 200샘플 전부). 증상만으로는 배선/타이밍/센서 어느 쪽인지 알 수 없어 **실패 원인을 3분류해 시리얼에 찍는 것부터** 했다 — 이게 이후 추적의 전부였다.
- `무응답` = 시작 신호에 응답조차 없음 → 배선·전원·풀업
- `비트 수신 중단` = 응답은 왔는데 40비트 도중 끊김 → 타이밍
- `체크섬 불일치` = 다 받았는데 값이 깨짐 → 샘플링 타이밍

실측 결과는 **`비트 수신 중단`** 이었다. 즉 배선·풀업·전원은 정상이고(핸드셰이크 통과) 순수 타이밍 문제로 범위가 좁혀졌다. 찾은 원인 4가지:

- **① 임계영역 안에서 플래시에 있는 함수를 부르고 있었다 (진짜 원인).**
  `gpio_get_level()`은 IRAM이 아니다 (`esp_driver_gpio/src/gpio.c:255`, 속성 없음). 40비트를 받는 동안 `dht_wait`가 80번 불리고 매번 이 **플래시 상주 함수**를 호출한다. 임계영역(≈5ms) 도중 명령어 캐시 미스가 나면 CPU가 SPI 플래시에서 코드를 읽느라 수십 µs 멈추고, core 0(WiFi)이 플래시에 쓰면 캐시가 통째로 꺼져 core 1이 수 ms 멈춘다. 비트 하나의 여유가 100µs뿐이라 그 순간 타임아웃 → 정확히 관측된 증상. → `dht_wait`/`dht_read_once`에 `IRAM_ATTR`, 핀 접근은 `GPIO.in` 레지스터 직접 읽기(`dht_level`), 임계영역 안의 출력 조작도 `out_w1ts`/`enable_w1tc` 직접 쓰기로 교체. `esp_timer_get_time()`은 IDF가 `ESP_TIMER_IRAM_ATTR`을 붙여둬 그대로 안전하고 `esp_rom_delay_us()`는 ROM 함수라 문제없다.
  - **⚠️ 함정**: 이때 시작 신호를 `GPIO_MODE_OUTPUT`으로 두면 안 된다 — 그 모드는 입력 경로(IE)를 끄는데, 우리는 출력 인에이블 비트만 지워 입력으로 되돌리므로 IE가 꺼진 채면 `GPIO.in`이 핀을 반영하지 않아 응답을 영영 못 본다. `GPIO_MODE_INPUT_OUTPUT`으로 둬야 전환이 한 비트로 끝난다.

- **② 임계영역이 잘못된 위치에서 시작했다.** 센서는 호스트가 라인을 놓은 뒤 20~40µs 만에 80µs 응답 펄스를 보내는데, **놓는 시점부터 `taskENTER_CRITICAL`까지가 임계영역 밖**이었다. `ppg_task`(우선순위 5)가 `dht_task`(4)보다 높고 **둘 다 core 1**이라, 그 사이에 선점당하면 — ppg_task는 100ms마다 I2C 버스트를 돌려 수 ms를 쓴다 — 응답 펄스가 통째로 지나간다. 라인을 놓는 순간부터 수신 끝까지를 원자적으로 만들었다.

- **③ 시작 신호 20ms는 DHT11 값이었다.** DHT22/AM2302 규격은 0.8~20ms라 20ms는 상한에 정확히 걸쳐 있다. 게다가 이 구간은 임계영역 밖 바쁜 대기라 ppg_task에 선점당하면 즉시 규격 위반이 된다. 1.2ms로 내려 선점으로 몇 ms 늘어나도 규격 안에 남게 했다. (센서는 흰색 = DHT22 확인)

- **④ 재시도 간격 50ms가 규격 위반이었다.** DHT22 최소 샘플링 주기가 2초라 2·3번째 재시도는 **실패가 보장**돼 있었다 — "3회 재시도"가 실질 1회였다. → 2초.

- 풀업은 읽기마다 걸지 않고 `app_main`에서 한 번만 건다(임계 경로에서 레지스터 쓰기 하나 제거). ⚠️ 내부 풀업은 ~45kΩ이라 1-Wire에 약하다 — `무응답`이 뜨는 상황이면 4.7k~10k 외부 풀업이 정석이다. 이번 건은 `비트 수신 중단`이라 해당 없음.
- **교훈**: ESP32에서 비트뱅잉 드라이버는 **임계영역 안의 모든 코드가 IRAM에 있어야 한다.** 편의 API(`gpio_get_level` 등)는 대부분 플래시 상주라 그대로 쓰면 안 된다. 이건 로직 버그가 아니라 배치(placement) 버그라 코드를 아무리 읽어도 안 보인다 — 실패 원인 분류 출력이 없었으면 못 찾았다.

**BPM 잔여 지터 = 기저선 흔들림 (2026-09-08) — "알고리즘으로는 못 고친다"를 근거로 확정:**

적응 불응기로 2배 하모닉이 사라진 뒤에도 안정 시 BPM이 69~83으로 흔들렸다. 원시 IBI를 시리얼로
찍게 해서(중앙값 이전 값) 원인을 갈랐다 — **중앙값만 보고는 세 원인을 구분할 수 없기 때문에**
이 진단 출력이 이번 추적의 전부였다.

- **아티팩트임을 통계로 확정**: 링버퍼 8블록을 겹쳐 이어붙여 22개 IBI를 복원 → **SDNN 125ms /
  RMSSD 202ms**. 호흡성 부정맥(RSA)은 5~6박 주기의 **느린** 변조라 SDNN만 키운다. 그래서 생리적
  HRV는 항상 RMSSD < SDNN이고, **뒤집혔다는 것 자체가 박동 대 박동 진동 = 검출 아티팩트**라는
  뜻이다. 안정 시 정상치는 둘 다 30~60ms.
- **박동 누락이 아니라 시각 오배치**: 이상치가 **보상쌍**으로 왔다 — `1160 + 500 = 1660 ≈ 정상
  2박(1611)`, `920 + 640 = 1560`. 고립된 긴 IBI는 0건이고 총 박동 수도 정확했다(23박/17.7초 =
  74.5bpm). 심장은 44% 늦게 뛴 뒤 그만큼 정확히 앞당겨 뛰지 못한다. +355ms는 심주기의 44% =
  **이중맥박파 위치**다.
- **원인**: 손가락 압력이 0.5~1.2Hz로 미세하게 변하면(기저선 흔들림) 그 성분이 박동 사이 골을
  적응 임계값 **위로** 들어올린다 → `prev <= threshold`가 불성립해 **수축기 상승 교차가 아예
  사라지고**, 이후 골이 임계값 아래로 내려간 뒤 그 박동의 notch가 대신 교차를 만든다.
  합성 신호에 흔들림을 넣어 재현했다: `0.5×AC @ 1.2Hz`에서 SD 113 / 최대오차 405ms (실측 125/355).
- **⚠️ 선형 필터로는 원리적으로 불가능하다**: 흔들림(0.5~1.2Hz)이 심박 기본파(75bpm = 1.25Hz)와
  **같은 대역**이다. 후보를 전부 합성 검증하고 전부 기각했다 —
  하이패스 코너 0.8Hz 상향 / 2차 하이패스(12dB/oct) / **기울기(미분) 검출** / 포락선 빠른 감쇠.
  개선이 없거나(최대오차 그대로) 깨끗한 신호를 망가뜨렸다. 특히 기울기 검출은 흔들림 아래서
  참값 75를 **136**으로, 110을 158로 읽었다 — 미분이 저주파에 둔감하다는 이론은 맞지만 흔들림의
  기울기가 이미 상승구간의 절반이라 마진이 부족했다.
- **⚠️ 타당성 게이트(진행 중앙값 ±30% 밖 기각)는 더 위험하다**: 부트스트랩 값에 갇혀 140bpm을
  69.8로, 180을 90.9로 읽고, **70→130 급상승을 53건 전부 기각해 진짜 스트레스 반응을 통째로
  막았다.** 스트레스 모니터에서 그건 지터보다 훨씬 나쁜 실패다. 서버의 BPM 급변 게이트가 이 함정을
  피하는 이유가 여기서 드러난다 — 그건 **시간 비례 예산**(초당 10bpm)이고 15초 뒤 비교를 포기하는
  탈출구가 있어서, 지속되는 진짜 상승은 통과하고 단발 점프만 버린다.
- **채택한 것은 하나뿐**: `PPG_IBI_SLOTS` 5 → 9. 흔들림 10케이스 평균 절대오차 **10.7 → 6.2bpm**,
  회귀 없음(깨끗한 신호 50~180bpm 동일, notch 스위트 0/8 실패 유지, 박동 누락 내성 동일 이상,
  70→130 추종 130.4로 동일). 대가는 응답 지연 약 1.6초(75bpm 기준, 2초 전송 주기의 한 샘플).
  구간 합(평균) 방식도 재봤지만 흔들림 내성이 나쁘고(9.3 vs 6.2) 박동 누락에 약해 기각.
- **근본 해결은 기계적이다**: 센서를 일정한 압력으로 고정(스트랩/밴드). IR 135000은 접촉이 훌륭하다는
  뜻이므로 접촉 세기가 아니라 **압력의 일정함**이 문제다.
- **실제 피해 경로 (BPM 숫자가 아니다)**: `BpmLow=80`이라 관측 범위 69~83은 BPM 점수가 전부 0점 —
  숫자가 튀어도 융합에 거의 영향이 없다. 진짜 피해는 **저변동성 채널**이다. 지터가 stdDev를 부풀려
  `100 - MapScore(stdDev, 2, 10)`이 81점 → 12점으로 떨어지고, 가중치 0.15를 곱하면 **융합 점수가
  10.3점 낮아진다 — 즉 실제보다 더 평온해 보인다.** 게다가 움직일수록(=동요할수록) 커지는 편향이다.
  이건 CLAUDE.md가 이미 적어 둔 "저변동성 점수는 진짜 HRV가 아니다"의 실측 확인이기도 하다.
  **다음 단계**: 펌웨어가 이미 IBI 배열을 스냅샷으로 들고 있으므로(진단 출력용) 그걸 서버로 올려
  RMSSD/SDNN을 직접 계산하면 이 채널이 아티팩트 대신 진짜 HRV를 재게 된다.
- **검증 스크립트**: `scratchpad/ibi_verdict.py`(실측 IBI 통계 판정), `jitter_repro.py`(이상 신호에선
  재현 안 됨을 확인 — SD 14), `wander_test.py`(흔들림으로 재현), `fix_candidates.py`(필터 후보 5종),
  `reject_test.py`(기각 전략), `span_test.py`(구간 합), `syncheck.py`(export.ps1 없이 main.c 문법 검사).

**MQTT 전환 보류 결정 (2026-08-24):**

계획상 "2단계"였으나 지금은 하지 않기로 했다. 근거:
- **Cloudflare Tunnel이 MQTT(TCP 1883/8883)를 넘기지 못한다.** 현재 ESP32는 `https://bio-monitor.uk`로 POST해서 집 밖에서도 동작하는데, MQTT로 가면 LAN 전용으로 후퇴하거나 Tailscale VPN 또는 MQTT-over-WebSocket(`wss://`)을 새로 얹어야 한다. 잘 도는 경로를 복잡하게 만든다.
- 디바이스 1대 / 2초 주기 / 단방향 업링크에서는 실익이 거의 없다. 서버에도 Mosquitto 브로커 + 구독 서비스가 늘어난다.
- MQTT가 실제로 이기는 지점은 **양방향 명령**(서버→ESP32), **페이로드 크기**(HTTP 왕복 ~700B vs MQTT PUBLISH ~100B, 7배), **다중 디바이스**다. 앞의 둘은 배터리 구동으로 넘어가야 측정 가능한 이득이 된다.
- 따라서 순서: ① MAX30102 실측 보정 + HRV → ② 배터리 + TP4056으로 웨어러블 완성 → ③ 그때 MQTT (LAN MQTT + 외부는 Tunnel HTTP 하이브리드). 전력 절감을 실측해 비교하면 그 자체가 포트폴리오 소재가 된다.

**Deadly 단계 + 이벤트 로그 (2026-07-06):**
- `TensionState`에 **Deadly** 추가: `<30 Relaxed / <65 Focused / <85 Stressed / ≥85 Deadly` (`StressedCeiling=85`). 대시보드·홈 범례·Discord 알림(☠️ 전용 메시지) 반영
- **Deadly 이벤트 DB 영속화**: `DeadlyEvent` 엔티티 → `deadly_events` 테이블(마이그레이션 `AddDeadlyEvents`). 발생 시각 + 융합 점수 + 요소별 점수 + 당시 원시 바이탈(BPM/GSR) 저장. `TensionAnalyzer`가 상태를 락 안에서 추적해 **Deadly 진입 시 즉시 1건 + 지속되는 동안 `DeadlyRepeatInterval`(기본 5초)마다 계속** 기록 (`UpdateBio`/`UpdateEmotion`의 out 파라미터, 읽기 전용 `Latest()`는 관여 안 함). 고빈도 샘플 폭주를 막기 위해 진입 외에는 인터벌로 스로틀. `GET /api/deadly/recent` 조회 지원
- Blazor `/event` **Event Log 페이지**: 이벤트 행(점수·시각·요소 미터·바이탈) 목록 + SignalR `DeadlyEventRecorded`로 실시간 prepend. `TensionReading`(SignalR 와이어 포맷)과 `DeadlyEvent`(EF 엔티티)는 의도적으로 분리 — 와이어/스키마 독립 진화
- **융합 로직 개선** (지속 고텐션이 Focused에 갇히던 문제 수정):
  - GSR 점수 = max(변화율, **절대 수준**) — 지속 각성이 baseline에 흡수돼 0점 되던 문제 해결 (`GsrAbsLow=300`~`GsrAbsHigh=800`, 실센서 단위 확정 시 조정)
  - **BPM 160+** (`BpmExtreme`) → 표정 무관 Deadly floor
  - **생체 전용 점수 90+** (`BioOnlyExtreme`) → 무표정이어도 센서 극단이면 Deadly
  - **30초 감정 창 합산 승격**: 생체 점수(Stressed 수준 65+) + 감정가중치(0.25) × 최근 30초 감정 점수 평균 ≥ 85 → Deadly. 감정 이력을 `EmotionStress` 점수로 저장·평균 (최소 5샘플, fear/angry ×1.0·surprise ×2.0). 단발 fear 노이즈는 무시, 지속 공포만 반영
  - 카메라 미연결/감정 stale 시 생체 전용 폴백은 기존 유지 (승격 경로만 자연 비활성)

**개발 환경:**
- PC: Windows 11 + .NET 9 SDK, Python(OpenCV/DeepFace)
- RPi5: `tjdgus@192.168.0.104`, Debian 13, PostgreSQL 17.10, `cloudflared`(Cloudflare Tunnel)
- 배포 경로: `/opt/biomonitor` (사용자 `tjdgus` 소유)
- 비밀 설정: `appsettings.Production.json`은 RPi5 로컬에만 존재 (`.gitignore` 처리됨)
  - `Camera:StreamUrl` = `/cam` (브라우저 노출용 동일 출처 경로)
  - `Camera:UpstreamUrl` = `http://<PC-LAN-IP>:8080/` (RPi5 내부 전용, 외부 비노출)
  - `Gallery:StoragePath` = 사진 저장 경로. **배포 폴더(`/opt/biomonitor`) 밖**으로 지정해야 `scp` 재배포 시 사진이 보존됨 (예: `/home/tjdgus/gallery-store`). 미설정 시 `ContentRoot/gallery-store` 기본값
  - `News:StoragePath` = 저녁 스케줄러가 `yyyy-MM-dd.html` 뉴스 파일을 저장하는 폴더 (`/home/tjdgus/news-store`). 웹 `/news`가 이 폴더를 읽어 날짜별로 열람. 배포 폴더 밖이므로 재배포와 무관. 미설정 시 `ContentRoot/news-store` 기본값

---

## 🏗️ 최종 확정 아키텍처

```
[ESP32-S3 - FreeRTOS]
  └─ MAX30102 심박센서 (I2C)
  └─ Grove GSR 피부전도도 센서 (Analog)
  └─ DHT22 (AM2302) 온습도 센서 (단선 디지털) [선택]
  └─ FreeRTOS 태스크 분리: core 0 = app_main(HTTP 전송) + WiFi / core 1 = PPG·DHT 센서
  └─ WiFi STA → SNTP → HTTPS POST (keep-alive 연결 재사용)
        ↓ WiFi (로컬 or Tailscale VPN)
[데스크탑 PC]
  └─ 웹캠 (USB) + OpenCV + MediaPipe
  │   └─ 시선 방향 감지
  │   └─ 눈 깜빡임 / 졸음 감지 (EAR 알고리즘)
  └─ DeepFace (Python)
  │   └─ 실시간 표정 분석 (angry / fear / neutral / sad / disgust / happy / surprise)
  │   └─ dominant_emotion + 감정별 score → HTTP POST → RPi5 /api/emotion
  │   └─ surprise(raw) ≥ 임계값 → 분석 프레임 JPEG → HTTP POST → RPi5 /api/gallery/capture
  └─ MJPEG 스트림 서버 (Python, 0.0.0.0:8080)
      └─ 오버레이(라벨+확률막대) 입힌 캠 프레임을 multipart/x-mixed-replace 서빙
        ↓ 같은 LAN (RPi5가 PC:8080 직접 접근)
[Raspberry Pi 5]
  └─ ASP.NET 9 Web API (C#)
  │   └─ /api/emotion         ← PC DeepFace HTTP POST 수신 → 생체와 융합
  │   └─ /api/biosignal       ← ESP32 HTTP POST 수신
  │   └─ /api/gallery/capture ← PC surprise 분석 프레임 수신 → 갤러리 저장
  │   └─ /gallery/media/{id}  ← 디스크 사진 스트리밍 (파일은 wwwroot 밖, 경로 비노출)
  │   └─ /cam                 ← PC MJPEG 리버스 프록시 (LAN 스트림을 동일 출처 HTTPS로 중계)
  └─ TensionAnalyzer (생체 + 감정 멀티모달 융합)
  └─ GalleryStorage (사진 = 디스크 파일, 메타데이터 = PostgreSQL)
  └─ SignalR Hub → Blazor 대시보드/갤러리 실시간 푸시
  └─ PostgreSQL
  └─ Discord Webhook 알림
  └─ 웹 대시보드 + 갤러리 (Blazor Server)
  └─ Cloudflare Tunnel (cloudflared) → https://bio-monitor.uk 외부 공개
        ↓
[Blazor 대시보드 (브라우저, https://bio-monitor.uk/dashboard)]
  └─ <img src="/cam"> → 캠 영상 (RPi5 프록시 경유, mixed-content 없음)
  └─ SignalR ← 감정 / BPM / GSR / 스트레스 상태 실시간 수신
        ↓
[Discord 채널]
  └─ 알림

```

---

## 🛠️ 확정 기술 스택

| 레이어 | 기술 |
|--------|------|
| **펌웨어** | ESP32-S3 / ESP-IDF / FreeRTOS / C |
| **통신** | WiFi / HTTPS POST (keep-alive). MQTT 전환은 보류 — 아래 결정 기록 참고 |
| **서버 OS** | Raspberry Pi OS 64-bit (Debian 13) |
| **백엔드** | ASP.NET 9 Web API / C#|
| **ORM** | EF Core 10 + Npgsql.EntityFrameworkCore.PostgreSQL (code-first migrations, 앱 시작 시 자동 적용) |
| **DB** | PostgreSQL 17 |
| **컴퓨터 비전 (PC)** | Python / OpenCV / MediaPipe / DeepFace |
| **캠 스트리밍** | MJPEG 스트림 서버 (Python http.server, `0.0.0.0:8080`) → RPi5 `/cam` 리버스 프록시 → Blazor `<img src="/cam">` |
| **외부 접근** | Cloudflare Tunnel (`cloudflared`) → `https://bio-monitor.uk` |
| **알림** | Discord Webhook |
| **프론트엔드** | Blazor Server |
| **실시간 통신** | SignalR (RPi5 → Blazor) |

---

## 🛒 준비물 리스트

| 품목 | 용도 | 통신 | 가격 (예상) |
|------|------|------|-------------|
| **ESP32-S3 DevKit** | 메인 MCU | - | ₩8,000~12,000 |
| **MAX30102** | 심박수 + HRV | I2C | ₩3,000~5,000 |
| **Grove GSR 센서** | 피부전도도 (스트레스) | Analog | ₩8,000~15,000 |
| **DHT22 (AM2302)** | 온습도 (선택) | 단선 디지털 | ₩2,000~5,000 |
| **LiPo 3.7V 1000mAh** | 웨어러블 배터리 | - | ₩5,000~8,000 |
| **TP4056 모듈** | 배터리 충전 관리 | - | ₩1,000~2,000 |
| 브레드보드 + 점퍼선 | 프로토타이핑 | - | ₩3,000~5,000 |
| **웹캠** | 시선 추적 + 표정 분석 (PC 연결) | USB | ✅ 기보유 |
| **Raspberry Pi 5** | 서버 | - | ✅ 기보유 |

**예상 총 구매 비용: ₩30,000~50,000**

---

## 🎮 스트레스 감지 로직

```
센서 데이터 융합 기반 상태 분류

😌 Relaxed  : BPM 정상   / HRV 안정  / GSR 낮음  / emotion = neutral or happy
😤 Focused  : BPM 상승   / HRV 감소  / GSR 상승  / emotion = neutral
🔥 Stressed : BPM 급상승 / HRV 급감  / GSR 급등  / emotion = angry or fear
☠️ Deadly   : 융합 85+ 또는 BPM 160+ / 생체 전용 90+ / 센서 Stressed + 30초 공포 지속

※ 표정 데이터 (DeepFace, PC) + 생체 데이터 (ESP32) 융합으로 판정 정확도 향상
※ 입력이 없는 요소는 가중치를 빼고 나머지를 재정규화한다 (감정 stale / BPM 접촉 없음 / 변동성 샘플 부족 /
   GSR 전극 개방·레일). GSR도 빠질 수 있으므로 **모든 요소가 동시에 없는 경우**가 존재하고, 그때는
   점수를 지어내지 않고 Calibrating으로 물러난다.
※ 독립 신호가 1개뿐이면 Deadly를 선언하지 못한다 (최대 Stressed). 센서 하나가 100점을 내는 것만으로는
   근거가 부족하기 때문 — 실제로 이것 때문에 미보정 GSR 단독 Deadly가 반복 기록됐다. 예외는 BPM 160+.
※ 위 표의 "HRV"는 아직 진짜 HRV가 아니다 — 현재는 BPM 시계열의 표준편차(저변동성 점수)로 근사한다.
   펌웨어가 박동별 IBI를 이미 계산하므로, 그 값을 서버로 올리면 RMSSD/SDNN 실측이 다음 단계로 가능하다.
※ PI(관류 지수)는 GSR과 **가중치 한 슬롯(자율신경 각성 0.25)을 공유**한다 — 같은 축을 다른 효과기로 재므로
   독립 가중치를 주면 이중 계상이 된다. 둘 다 있으면 평균, PI가 없으면 GSR 단독.
   PI 채점은 절대값이 아니라 **개인 baseline(10분 85퍼센타일) 대비 하락률**이다 — 근거는 "PI → TensionAnalyzer 융합 편입" 참고.
※ SpO2는 측정 가능하지만 스트레스 지표로 채택하지 않았다 (오차 > 신호). 같은 절 참고.
```
---

## 💼 포트폴리오 어필 포인트

| 항목 | 내용 |
|------|------|
| **RTOS 실설계** | FreeRTOS 멀티태스크 + 코어 친화도 설계 (타이밍 크리티컬 센서를 WiFi와 분리) |
| **HW-SW 인터페이스** | I2C / Analog(ADC) / 단선 디지털(DHT) 프로토콜 직접 구현 |
| **무선 통신** | WiFi STA 재연결 설계 + HTTPS keep-alive + 전송 백오프 (MQTT는 근거를 갖고 보류) |
| **신호처리** | PPG 원시 파형 → DC 제거 / 저역통과 / 적응 임계 피크 검출 → BPM + PI(관류 지수) 직접 구현. 합성 신호로 수치 검증 후 알고리즘 교체(포락선 → 박동 구간 실측)해 심박 의존 편차 제거 |
| **지표 선택 판단** | SpO2를 "가능하니까" 넣지 않고, 오차(±2~3%p) > 신호(1%p)임을 근거로 배제하고 같은 센서에서 PI로 대체 |
| **한계 규명** | BPM 잔여 지터를 RMSSD>SDNN으로 아티팩트라 확정하고, 필터·게이트 후보 6종을 합성 검증으로 **기각**한 뒤 "흔들림이 심박과 같은 대역이라 SW로 불가 → 기계적 고정이 해답"이라고 결론. 플라시보 수정을 넣지 않고 한계를 증명한 사례 |
| **컴퓨터 비전** | OpenCV + MediaPipe + DeepFace 실사용 |
| **분산 처리 설계** | PC(추론) ↔ RPi5(서버) 역할 분리 아키텍처 |
| **실시간 스트리밍** | MJPEG(영상) + SignalR(데이터) 이중 채널 설계 |
| **멀티모달 융합** | 생체신호(ESP32) + 표정(DeepFace) 복합 판정, 결측 요소 가중치 재정규화. 상관된 센서(GSR·PI)는 슬롯을 공유해 이중 계상 회피 + 상호 교차 검증 |
| **풀스택 시스템** | MCU → Linux 서버 → 웹 대시보드 |
| **도메인** | 헬스케어 + 게이밍 웨어러블 |
| **웹 스킬 통합** | ASP.NET + Blazor + PostgreSQL 기존 역량 연결 |

---

### 서비스 구성

```
postgresql.service       ← apt install postgresql 시 자동 등록
biomonitor-api.service   ← ASP.NET 9 Web API + Blazor Server, 직접 작성한 unit 파일
```

### 설치 방식

- **PostgreSQL**: `sudo apt install postgresql postgresql-contrib` → 설치 시 systemd 서비스 자동 등록, `enable`만 추가
- **ASP.NET API**: PC에서 `dotnet publish -c Release -r linux-arm64 --self-contained false`로 빌드 → `scp`로 `/opt/biomonitor`에 배치. 커스텀 unit 파일(`biomonitor-api.service`)이 `/home/<user>/.dotnet/dotnet /opt/biomonitor/GamerBio.dll`을 실행. PostgreSQL이 추가된 이후엔 `After=network.target postgresql.service` + `Requires=postgresql.service`로 의존성 명시
- **공통**: 모든 서비스 `Restart=always`로 자동 재시작, 부팅 시 자동 시작 (`systemctl enable`)

---

## 🔗 네트워크 구성

| 환경 | 방식 |
|------|------|
| 개발 (로컬) | 같은 공유기 → 로컬 IP 직접 통신 (대시보드 `http://localhost:5026`, 캠 `http://localhost:8080`) |
| 외부 접근 | Cloudflare Tunnel(`cloudflared`) → `https://bio-monitor.uk` |
| 캠 영상 경로 | 브라우저 → `https://bio-monitor.uk/cam` → Cloudflare Tunnel → RPi5 `/cam` 프록시 → PC `:8080` (LAN) |
| 프로세스 관리 | systemd 네이티브 (Docker 미사용, 위 "RPi5 배포 전략" 참고) |

> **캠 스트리밍 설계 결정 (`/cam` 리버스 프록시):** 공개 대시보드는 HTTPS(`bio-monitor.uk`)인데 PC MJPEG 서버는 LAN 전용 HTTP(`:8080`)라, 브라우저가 직접 PC를 가리키면 ① **mixed-content 차단**(HTTPS 페이지의 HTTP 리소스)과 ② **도달성**(외부 브라우저가 집 PC의 LAN IP에 접근 불가) 두 문제가 생긴다. 해결책으로 RPi5 ASP.NET에 `/cam` 엔드포인트를 두어 PC 스트림을 **동일 출처 HTTPS로 리버스 프록시**한다. `HttpCompletionOption.ResponseHeadersRead` + `IHttpResponseBodyFeature.DisableBuffering()`로 무한 multipart 스트림을 버퍼링 없이 중계하고, HttpClient `Timeout`은 `InfiniteTimeSpan`. PC IP는 서버 측 `Camera:UpstreamUrl`에만 있어 외부에 노출되지 않는다. (포트폴리오 talking point: mixed-content/CORS/NAT 도달성을 리버스 프록시로 동시 해소)

---

## 📝 개발 환경

| 도구 | 용도 |
|------|------|
| VS Code + ESP-IDF Extension | ESP32-S3 펌웨어 개발 |
| ESP-IDF v5.x | FreeRTOS 기반 펌웨어 프레임워크 |
| Visual Studio / Rider | ASP.NET 9 백엔드 개발 |
| Python 3.x (PC) | OpenCV / MediaPipe / DeepFace 컴퓨터 비전 |
| DBeaver | PostgreSQL 관리 |
| Mosquitto | MQTT 브로커 (RPi5) — 전환 보류 중이라 미설치 |

---
