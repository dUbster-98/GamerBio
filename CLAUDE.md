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

## 📍 현재 진행 상황 스냅샷 (2026-08-24)

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
| 펌웨어 → MQTT 전환 | ⏸️ 보류 (아래 "MQTT 전환 보류 결정" 참고) |
| 감정 DB 영속화 (`Emotion` 엔티티) | ⏳ 미착수 (현재 메모리상 최신값만 융합) |
| Discord 봇 (`DiscordBotService`) — 알림 + 슬래시 명령 | ✅ 호스팅 서비스로 통합, `/status`·`/bpm` + Stressed/**Deadly** 진입 알림 (로컬 빌드 검증) |
| **Deadly 단계 + 이벤트 로그** (`deadly_events` 테이블, `/event` 페이지) | ✅ 진입 시각+파라미터 DB 저장, 실시간 페이지 갱신 (로컬 검증) |
| Blazor `/news` 뉴스 열람 (날짜별 아카이브) | ✅ RPi 스케줄러가 저녁마다 저장한 `yyyy-MM-dd.html`을 날짜 목록 + iframe으로 열람 (로컬 검증) |

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
  - 서버 `TensionAnalyzer.GsrAbsLow/High`(300/800) — 센서 미연결 플로팅 상태에서 이미 582가 나온다. 실제 이완/긴장 raw 값을 재고 다시 잡아야 한다.
- **⚠️ `WIFI_PASS` / `API_KEY`가 소스에 하드코딩되어 있다.** 아직 커밋되진 않았으나 파일은 git 추적 대상이므로, 공개 전에 Kconfig나 NVS로 빼고 키를 재발급할 것.

**BPM nullable 전환 (2026-08-24):**

- 웨어러블은 PPG 센서에 피부 접촉이 있을 때만 심박을 보고한다. 접촉이 없을 때 0을 보내면 `TensionAnalyzer`가 "심박 0 = 완전히 평온"으로 채점하고, 더 나쁘게는 60샘플 변동성 창을 가짜 평탄선으로 오염시킨다(기존 `PLACEHOLDER_BPM 60`은 stdDev가 항상 0이라 모든 판정에 +15점을 상수로 더하고 있었다).
- `BioSignal.Bpm` / `DeadlyEvent.Bpm` / `BioSignalDto.Bpm` → `int?`. 마이그레이션 `MakeBpmNullable` (두 테이블 `AlterColumn`, `Down()`은 NULL을 0으로 먼저 채운다). 펌웨어는 `"bpm":null`을 보낸다 — `skinTemp`가 쓰던 방식과 동일.
- 융합은 감정이 stale일 때 쓰던 재정규화 패턴을 그대로 확장했다. 빠질 수 있는 요소가 셋: **감정**(PC 미연결/5초 초과), **BPM**(접촉 없음), **저변동성**(BPM을 따라감 + 유효 샘플 `VariabilityMinSamples`=10 미만). GSR만 항상 있으므로 가중치 합이 0이 되지 않는다. 변동성 창은 `Bpm`이 있는 샘플만 쓴다 — 접촉 공백을 이어 붙이면 그 공백 자체가 "심한 변동"으로 읽혀 오히려 스트레스가 낮게 나온다.
- `BioOnlyExtreme`(생체 전용 90+ → Deadly) 오버라이드는 **BPM이 있을 때만** 적용한다. BPM이 없으면 `bioComposite`가 GSR 단독이 되는데, GSR 절대 임계값이 아직 미보정이라 접촉이 끊길 때마다 Deadly가 뜰 수 있다.
- **배포 순서 주의**: 서버를 먼저 올려야 한다. 새 펌웨어의 `"bpm":null`을 구버전 서버(`int Bpm`)는 400으로 거부한다. 반대(새 서버 + 구 펌웨어)는 문제없다.

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
※ 입력이 없는 요소는 가중치를 빼고 나머지를 재정규화한다 (감정 stale / BPM 접촉 없음 / 변동성 샘플 부족).
   GSR만 항상 존재하므로 가중치 합이 0이 되는 경우는 없다.
※ 위 표의 "HRV"는 아직 진짜 HRV가 아니다 — 현재는 BPM 시계열의 표준편차(저변동성 점수)로 근사한다.
   펌웨어가 박동별 IBI를 이미 계산하므로, 그 값을 서버로 올리면 RMSSD/SDNN 실측이 다음 단계로 가능하다.
```
---

## 💼 포트폴리오 어필 포인트

| 항목 | 내용 |
|------|------|
| **RTOS 실설계** | FreeRTOS 멀티태스크 + 코어 친화도 설계 (타이밍 크리티컬 센서를 WiFi와 분리) |
| **HW-SW 인터페이스** | I2C / Analog(ADC) / 단선 디지털(DHT) 프로토콜 직접 구현 |
| **무선 통신** | WiFi STA 재연결 설계 + HTTPS keep-alive + 전송 백오프 (MQTT는 근거를 갖고 보류) |
| **신호처리** | PPG 원시 파형 → DC 제거 / 저역통과 / 적응 임계 피크 검출 → BPM 직접 구현 |
| **컴퓨터 비전** | OpenCV + MediaPipe + DeepFace 실사용 |
| **분산 처리 설계** | PC(추론) ↔ RPi5(서버) 역할 분리 아키텍처 |
| **실시간 스트리밍** | MJPEG(영상) + SignalR(데이터) 이중 채널 설계 |
| **멀티모달 융합** | 생체신호(ESP32) + 표정(DeepFace) 복합 판정, 결측 요소 가중치 재정규화 |
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
