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

## 📍 현재 진행 상황 스냅샷 (2026-06-24)

| 항목 | 상태 |
|------|------|
| RPi5 OS + .NET 10 ASP.NET 런타임 | ✅ 설치 완료 (`/home/tjdgus/.dotnet`) |
| systemd 서비스 (`biomonitor-api.service`) | ✅ `Active (running)`, 자동 시작 등록 |
| Blazor Server 기본 화면 (`http://192.168.0.104:5000`) | ✅ 브라우저 접속 확인 |
| `POST /api/biosignal` 엔드포인트 | ✅ 더미 데이터 → DB 저장 동작 |
| `GET /api/biosignal/recent` 엔드포인트 | ✅ 조회 동작 |
| PostgreSQL 17 + `biomonitor` DB/유저 | ✅ TCP+비밀번호 인증 |
| EF Core 마이그레이션 (`biosignals` 테이블) | ✅ 앱 시작 시 자동 적용 |
| 배포 파이프라인 | ✅ PC `dotnet publish` → `scp` → systemd restart |
| SignalR Hub (`BioSignalHub`) + Blazor 실시간 푸시 | ✅ `BioSignalReceived` / `TensionUpdated` 동작 |
| `TensionAnalyzer` 멀티모달 융합 + 상태 분류 | ✅ BPM/GSR/저변동성/**감정** 가중 융합 → Relaxed/Focused/Stressed |
| Blazor `/dashboard` 라이브 대시보드 | ✅ 캠 패널 + 감정 라벨 + 텐션 게이지 + 바이탈 + 피드 |
| **[PC]** DeepFace 감정 분석 앱 (`deepface/emotion_webcam.py`) | ✅ 게임 특화 최적화 (아래 참고) |
| **[PC]** MJPEG 스트림 서버 (Python, `0.0.0.0:8080`) | ✅ 오버레이 영상 `multipart/x-mixed-replace` 서빙 |
| **[PC]** 감정 송신 (`EmotionPoster` → `/api/emotion`) | ✅ 주 얼굴 감정 throttled POST (`--post-url`) |
| RPi5 `/cam` MJPEG 리버스 프록시 | ✅ PC LAN 스트림을 HTTPS 동일 출처로 중계 |
| `POST /api/emotion` (PC DeepFace score 수신) | ✅ 감정+생체 융합 후 SignalR 푸시, 로컬 검증 완료 |
| Blazor `/gallery` 갤러리 (사진 업로드/조회/삭제) | ✅ `InputFile` 업로드 + 그리드, RPi 디스크 저장 (메타데이터는 DB) |
| `POST /api/gallery/capture` + surprise 자동 캡처 | ✅ PC가 **분석한 그 프레임**을 전송 → 갤러리 저장 + 실시간 갱신 |
| 공개 도메인 `https://bio-monitor.uk` (Cloudflare Tunnel) | ✅ 대시보드 + 캠 영상 외부 접속 확인 |
| ESP32 펌웨어 | ⏳ 부품 대기 중 (더미 데이터로 검증 중) |
| 감정 DB 영속화 (`Emotion` 엔티티) | ⏳ 미착수 (현재 메모리상 최신값만 융합) |
| Discord 알림 / PC 시선·졸음(MediaPipe) / MQTT | ⏳ 미착수 |

**DeepFace 감정 분석 앱 최적화 (이번 세션):**
- 게임 무관 라벨 `disgust`/`sad` 제거 → 남은 5개(`angry`/`fear`/`happy`/`surprise`/`neutral`) 재정규화
- 클래스별 보정 가중치(`EMOTION_WEIGHTS`)로 모델의 `fear` 과소평가 상쇄 (`--fear-boost`)
- `sad`로 오분류된 fear 신호 일부를 fear로 회수 (`SAD_TO_FEAR_RATIO`, `--sad-to-fear`)
- EMA 시간적 평활화로 정지 시 라벨 떨림 제거 (`EMOTION_SMOOTHING`, `--smoothing`)
- 오버레이(박스+라벨+확률막대) 입힌 프레임을 MJPEG로 송출 (`--stream --port 8080`)

**멀티모달 융합 (이번 세션):**
- `TensionAnalyzer`가 최신 생체(`UpdateBio`)·감정(`UpdateEmotion`)을 각각 보관하고 둘 중 무엇이 들어오든 재계산
- 4-요소 가중 융합: BPM 0.35 / GSR 0.25 / 저변동성 0.15 / **감정 0.25** (감정 부재·만료 시 나머지 3개로 재정규화 = 생체 전용 폴백)
- 감정→스트레스 매핑: `angry/fear ×1.0`, `surprise ×0.5`, `neutral/happy ×0.0` → 0~100
- 감정 staleness 10초: PC 중단/끊김 시 자동으로 생체 전용 점수로 폴백
- 로컬 검증: 동일 생체 입력에서 감정만 바꿔 composite `49(생체만) → 59(fear) → 38(happy)` 반응 확인
- SignalR: `EmotionUpdated`(감정 원본) + `TensionUpdated`(융합 결과) 동시 푸시 → 대시보드 감정 라벨 + `EMOTION` 기여도 표시

**갤러리 + surprise 자동 캡처 (이번 세션):**
- Blazor `/gallery` 페이지: `InputFile` 다중 업로드(캡션·드래그) + 썸네일 그리드 + 삭제, 메뉴에 `Gallery` 탭 추가
- **저장 구조**: 이미지 파일은 **RPi 디스크**(`GalleryStorage`, `Gallery:StoragePath` 설정), 메타데이터(`GalleryPhoto` 엔티티 → `gallery_photos` 테이블, 마이그레이션 `AddGalleryPhotos`)만 PostgreSQL. `GET /gallery/media/{id}`가 디스크 파일을 스트리밍(파일은 wwwroot 밖, 경로 비노출)
- **surprise 자동 캡처**: surprise가 임계값(기본 90) 초과 시 캡처. **타이밍 정확도를 위해 캡처 트리거를 PC로 이동** — DeepFace가 분석한 raw(평활화 전) 점수로 트리거하고 **그 분석 프레임 자체**를 `POST /api/gallery/capture`로 전송 → 서버는 받은 이미지를 그대로 저장. 서버가 사후에 라이브 프레임을 재촬영하던 방식(지연·불일치)을 폐기
- PC `CapturePoster`(별도 스레드, 재무장+쿨다운 가드)가 인코딩·전송 담당. 설정은 파일 상단 `DEFAULT_*` 블록 또는 `--capture-url`/`--capture-threshold`/`--capture-cooldown`/`--api-key`/`--insecure`(BooleanOptionalAction)로 오버라이드
- SignalR `GalleryPhotoAdded` → 갤러리 페이지가 캡처 사진을 실시간으로 맨 앞에 추가
- 로컬 검증 교훈: https 자체서명 인증서로 PC POST가 SSL 실패하는데 스크립트가 에러를 삼켜 무증상 → `--insecure`(verify off) + 1회성 실패 로그로 해결. 프로덕션(LAN 평문 http)은 `--insecure` 불필요

**개발 환경:**
- PC: Windows 11 + .NET 10 SDK (10.0.201), `dotnet-ef` 10.0.9, Python(OpenCV/DeepFace)
- RPi5: `tjdgus@192.168.0.104`, Debian 13, PostgreSQL 17.10, `cloudflared`(Cloudflare Tunnel)
- 배포 경로: `/opt/biomonitor` (사용자 `tjdgus` 소유)
- 비밀 설정: `appsettings.Production.json`은 RPi5 로컬에만 존재 (`.gitignore` 처리됨)
  - `Camera:StreamUrl` = `/cam` (브라우저 노출용 동일 출처 경로)
  - `Camera:UpstreamUrl` = `http://<PC-LAN-IP>:8080/` (RPi5 내부 전용, 외부 비노출)
  - `Gallery:StoragePath` = 사진 저장 경로. **배포 폴더(`/opt/biomonitor`) 밖**으로 지정해야 `scp` 재배포 시 사진이 보존됨 (예: `/home/tjdgus/gallery-store`). 미설정 시 `ContentRoot/gallery-store` 기본값

---

## 🏗️ 최종 확정 아키텍처

```
[ESP32-S3 - FreeRTOS]
  └─ MAX30102 심박센서 (I2C)
  └─ Grove GSR 피부전도도 센서 (Analog)
  └─ DS18B20 피부온도 센서 (1-Wire) [선택]
  └─ WiFi HTTP POST → MQTT (리팩토링 예정)
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
  └─ ASP.NET 10 Web API (C#)
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
  └─ 심박 이상 알림 ⚠️
  └─ 스트레스 감지 알림 🔥
  └─ 졸음 감지 알림 😴
  └─ 표정 이상 알림 😤
```

---

## 🛠️ 확정 기술 스택

| 레이어 | 기술 |
|--------|------|
| **펌웨어** | ESP32-S3 / ESP-IDF / FreeRTOS / C |
| **통신 (1단계)** | WiFi / HTTP POST |
| **통신 (2단계)** | MQTT (Mosquitto 브로커) |
| **서버 OS** | Raspberry Pi OS 64-bit (Debian 13) |
| **백엔드** | ASP.NET 10 Web API / C# (.NET 10 runtime) |
| **ORM** | EF Core 10 + Npgsql.EntityFrameworkCore.PostgreSQL (code-first migrations, 앱 시작 시 자동 적용) |
| **DB** | PostgreSQL 17 |
| **컴퓨터 비전 (PC)** | Python / OpenCV / MediaPipe / DeepFace |
| **캠 스트리밍** | MJPEG 스트림 서버 (Python http.server, `0.0.0.0:8080`) → RPi5 `/cam` 리버스 프록시 → Blazor `<img src="/cam">` |
| **외부 접근** | Cloudflare Tunnel (`cloudflared`) → `https://bio-monitor.uk` |
| **알림** | Discord Webhook |
| **프론트엔드** | Blazor Server |
| **실시간 통신** | SignalR (RPi5 → Blazor) |

> **버전 변경 메모:** 초기 계획은 ASP.NET 8 LTS였으나, 프로젝트 스캐폴딩 단계에서 .NET 10 SDK가 설치돼 있어 그대로 진행. .NET 10도 LTS (2025-11 GA)라 장기 지원 측면 무리 없음.

---

## 🛒 준비물 리스트

| 품목 | 용도 | 통신 | 가격 (예상) |
|------|------|------|-------------|
| **ESP32-S3 DevKit** | 메인 MCU | - | ₩8,000~12,000 |
| **MAX30102** | 심박수 + HRV | I2C | ₩3,000~5,000 |
| **Grove GSR 센서** | 피부전도도 (스트레스) | Analog | ₩8,000~15,000 |
| **DS18B20** | 피부온도 (선택) | 1-Wire | ₩2,000~3,000 |
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
😴 Drowsy   : 눈 깜빡임 증가 / EAR 수치 임계값 이하 / emotion = neutral (무표정 지속)

※ 표정 데이터 (DeepFace, PC) + 생체 데이터 (ESP32) 융합으로 판정 정확도 향상
```

---

## 📅 3개월 개발 로드맵

### 🟦 1개월차 — ESP32-S3 펌웨어 (ESP-IDF + FreeRTOS)

**목표:** 센서 데이터 수집 + WiFi 전송 안정화

- [ ] ESP-IDF 개발환경 세팅 (VS Code + ESP-IDF Extension)
- [ ] FreeRTOS 멀티태스크 구조 설계
  ```
  HeartRateTask  ─ MAX30102 I2C 읽기 (심박 + SpO2)
  GsrTask        ─ ADC 읽기 (피부전도도)
  TransmitTask   ─ WiFi HTTP POST 전송
  ```
- [ ] MAX30102 I2C 드라이버 작성
- [ ] Grove GSR ADC 읽기 구현
- [ ] HTTP POST로 RPi5 서버에 데이터 전송 확인

**체크포인트:** 심박 + GSR 데이터가 서버에 수신됨

---

### 🟨 2개월차 — RPi5 서버 + 알림 시스템

**목표:** 데이터 수신·저장·알림 파이프라인 완성

- [x] ASP.NET 10 Web API 구성
  - [x] `/api/biosignal` (POST) — ESP32 생체 데이터 수신
  - [x] `/api/biosignal/recent` (GET) — 검증용 최근 N개 조회
  - [x] `/api/emotion` (POST) — PC DeepFace 수신 → 생체와 융합 후 SignalR 푸시
- [x] PostgreSQL 스키마 설계 및 연동
  - [x] EF Core 10 + Npgsql 추가, `BioSignal` 엔티티 + `BioMonitorContext` 작성
  - [x] `biosignals` 테이블 + Timestamp 인덱스 (마이그레이션 `InitialCreate`)
  - [x] 앱 시작 시 `MigrateAsync()` 자동 적용
  - [x] `IDesignTimeDbContextFactory` 도입 (PC에 DB 없어도 `dotnet ef` 명령 작동)
  - [x] 연결 문자열은 RPi5의 `appsettings.Production.json`에 격리 (gitignore)
- [x] SignalR Hub 구성 → Blazor 실시간 푸시 (`BioSignalHub`: `BioSignalReceived` / `TensionUpdated` / `EmotionUpdated`)
- [x] `TensionAnalyzer` 멀티모달 융합 + 상태 분류 (BPM/GSR/저변동성/감정 가중 융합 → Relaxed/Focused/Stressed, 감정 staleness 폴백)
- [x] **[PC]** DeepFace 감정 송신 (`EmotionPoster` → `/api/emotion`, throttled, `--post-url`/`--api-key`/`--post-interval`)
- [ ] Discord Webhook 알림 구현
  - 심박 140 이상 → `⚠️ 심박 이상 감지!`
  - GSR 급등 → `🔥 스트레스 감지!`
  - emotion = angry/fear 지속 → `😤 표정 이상 감지!`
- [ ] **[PC]** OpenCV + MediaPipe 시선/졸음 감지 구현
- [x] **[PC]** DeepFace 표정 분석 구현 (`deepface/emotion_webcam.py`)
  - 백그라운드 추론 스레드 + 풀 FPS 디스플레이 (분석 지연과 영상 부드러움 분리)
  - 게임 특화: `disgust`/`sad` 제거 + 5개 재정규화, `fear` 가중치 보정, `sad`→`fear` 회수, EMA 평활화
  - [x] dominant_emotion + 감정별 score → RPi5 `/api/emotion` HTTP POST 전송 (`EmotionPoster`)
- [x] **[PC]** MJPEG 스트림 서버 구현 (Python `http.server`, `0.0.0.0:8080`)
  - 오버레이 입힌 프레임을 `multipart/x-mixed-replace`로 서빙 (`--stream`, `_FrameBuffer` + `ThreadingHTTPServer`)
- [x] **[Blazor]** `/dashboard` 대시보드 구현
  - `<img src="/cam">`로 MJPEG 캠 화면 표시 (RPi5 `/cam` 리버스 프록시 경유)
  - SignalR로 감정 라벨 / BPM / GSR / 융합 텐션 상태 실시간 수신 (`EMOTION` 기여도 포함)
- [x] **[Blazor]** `/gallery` 갤러리 구현 (`InputFile` 업로드 + 그리드 + 삭제, 메뉴 탭)
  - 사진 파일은 RPi 디스크(`GalleryStorage`/`Gallery:StoragePath`), 메타데이터는 PostgreSQL(`gallery_photos`)
  - `POST /api/gallery/capture` — PC가 surprise(raw) 임계값 초과 시 **분석 프레임**을 전송 → 저장
  - `GET /gallery/media/{id}` 스트리밍 + SignalR `GalleryPhotoAdded` 실시간 갱신
- [x] 외부 접근 — Cloudflare Tunnel(`cloudflared`) → `https://bio-monitor.uk` (Tailscale 대신 채택)
- [ ] MQTT 리팩토링 (Mosquitto 브로커)

**체크포인트:** Discord에 실시간 알림 수신 확인

---

### 🟥 3개월차 — 완성도 + 포트폴리오화

**목표:** 안정화, 대시보드, 문서화

- [ ] FreeRTOS 저전력 모드 (tickless idle)
- [ ] Watchdog 타이머 적용
- [ ] 웹 대시보드 완성 (실시간 그래프)
- [ ] 시스템 아키텍처 문서 작성
- [ ] README 정리
- [ ] 시연 영상 제작

**체크포인트:** 전체 시스템 End-to-End 시연 가능

---

## 💼 포트폴리오 어필 포인트

| 항목 | 내용 |
|------|------|
| **RTOS 실설계** | FreeRTOS 멀티태스크 + 저전력 구조 |
| **HW-SW 인터페이스** | I2C / Analog / 1-Wire 프로토콜 직접 구현 |
| **무선 통신** | WiFi HTTP POST + MQTT 전환 경험 |
| **컴퓨터 비전** | OpenCV + MediaPipe + DeepFace 실사용 |
| **분산 처리 설계** | PC(추론) ↔ RPi5(서버) 역할 분리 아키텍처 |
| **실시간 스트리밍** | MJPEG(영상) + SignalR(데이터) 이중 채널 설계 |
| **멀티모달 융합** | 생체신호(ESP32) + 표정(DeepFace) 복합 판정 |
| **풀스택 시스템** | MCU → Linux 서버 → 웹 대시보드 |
| **도메인** | 헬스케어 + 게이밍 웨어러블 |
| **웹 스킬 통합** | ASP.NET + Blazor + PostgreSQL 기존 역량 연결 |

---

## 🚀 RPi5 배포 전략 — 전체 systemd 네이티브 (Docker 미사용)

> **결정:** RPi5(4GB RAM)에서는 Docker 대신 **모든 서비스를 systemd로 직접 실행**한다.

| 항목 | 내용 |
|------|------|
| **배경** | 회사에서는 Blazor 웹앱 + MSSQL을 Docker로 운영했으나, RPi5는 4GB RAM의 리소스 제약 환경이라 다른 전략 필요 |
| **MSSQL 미사용 이유** | MSSQL은 ARM64 공식 Docker 이미지 미지원 (RPi5 = ARM64) → PostgreSQL로 대체 (기존 결정 유지) |
| **Docker 미사용 이유** | 4GB RAM 환경에서 Docker 데몬 + containerd 자체 오버헤드(약 100~200MB+)가 부담. ASP.NET API + PostgreSQL + (추후) Mosquitto MQTT + 이미지 처리·SignalR 동시 접속까지 감당해야 해서 메모리 효율이 격리성보다 우선순위 높음 |
| **최종 구성** | DB(PostgreSQL)와 API(ASP.NET 10 + Blazor Server) 모두 systemd 네이티브로 통일 |

### 서비스 구성

```
postgresql.service       ← apt install postgresql 시 자동 등록
biomonitor-api.service   ← ASP.NET 8 Web API + Blazor Server, 직접 작성한 unit 파일
mosquitto.service        ← apt install mosquitto 시 자동 등록 (MQTT 리팩토링 단계에서 추가)
```

### 설치 방식

- **PostgreSQL**: `sudo apt install postgresql postgresql-contrib` → 설치 시 systemd 서비스 자동 등록, `enable`만 추가
- **ASP.NET API**: PC에서 `dotnet publish -c Release -r linux-arm64 --self-contained false`로 빌드 → `scp`로 `/opt/biomonitor`에 배치. 커스텀 unit 파일(`biomonitor-api.service`)이 `/home/<user>/.dotnet/dotnet /opt/biomonitor/GamerBio.dll`을 실행. PostgreSQL이 추가된 이후엔 `After=network.target postgresql.service` + `Requires=postgresql.service`로 의존성 명시
- **공통**: 모든 서비스 `Restart=always`로 자동 재시작, 부팅 시 자동 시작 (`systemctl enable`)

### 트레이드오프 (포트폴리오 talking point)

- **잃는 것**: 컨테이너 격리(재현성 약화), 회사 환경과의 Docker 패턴 일관성
- **얻는 것**: Docker 데몬 오버헤드 제거로 메모리 절약, 더 빠른 부팅, `journalctl -u biomonitor-api`로 즉시 디버깅 가능
- **선택 근거**: RPi5는 멀티테넌트 환경이 아닌 단일 영구 배포 환경이므로, "재현성"보다 "리소스 효율"이 더 가치 있는 트레이드오프 → 의도적 아키텍처 결정으로 포트폴리오에서 설명 가능

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
| Visual Studio / Rider | ASP.NET 8 백엔드 개발 |
| Python 3.x (PC) | OpenCV / MediaPipe / DeepFace 컴퓨터 비전 |
| DBeaver | PostgreSQL 관리 |
| Mosquitto | MQTT 브로커 (RPi5) |

---

*최종 업데이트 (2026-06-19): .NET 10 / ASP.NET 10 / PostgreSQL 17로 버전 확정. RPi5 배포 파이프라인(publish → scp → systemd) 완성. EF Core code-first 마이그레이션 + 앱 시작 시 자동 적용. `/api/biosignal` POST/GET 동작 및 DB 영속성 검증 완료.*

*업데이트 (2026-06-24): SignalR Hub + `TensionAnalyzer` + Blazor `/dashboard` 라이브 대시보드 동작. PC DeepFace 감정 분석 앱(`deepface/emotion_webcam.py`) 게임 특화 최적화(disgust/sad 제거·fear 보정·sad→fear 회수·EMA 평활화) 및 MJPEG 스트림 서버 구현. RPi5 `/cam` 리버스 프록시 + Cloudflare Tunnel로 `https://bio-monitor.uk/dashboard`에서 캠 영상 외부 송출 검증 완료.*

*업데이트 (2026-06-24, 추가): 감정+생체 멀티모달 융합 완성. `POST /api/emotion` 신설, `TensionAnalyzer`를 4-요소 가중 융합(BPM/GSR/저변동성/감정 0.35/0.25/0.15/0.25)으로 리팩토링, 감정 staleness 10초 폴백. PC `EmotionPoster`가 주 얼굴 감정을 throttled POST. 대시보드에 감정 라벨 + `EMOTION` 기여도 표시. 로컬 검증: 동일 생체에서 감정만 바꿔 composite 49→59(fear)→38(happy) 반응 확인. 다음: 감정 DB 영속화(선택), Discord 알림(융합 `TensionState`/`EmotionScore` 트리거), ESP32 펌웨어.*

*업데이트 (2026-06-24, 갤러리+자동캡처): Blazor `/gallery` 사진 갤러리(업로드/조회/삭제, 메뉴 탭) 구현 — 파일은 RPi 디스크(`GalleryStorage`/`Gallery:StoragePath`, wwwroot 밖), 메타데이터는 `gallery_photos`(마이그레이션 `AddGalleryPhotos`), `GET /gallery/media/{id}` 스트리밍. surprise 자동 캡처는 **타이밍 정확도를 위해 트리거를 PC로 이동**: DeepFace가 raw(평활화 전) 점수로 트리거하고 분석 프레임 자체를 `POST /api/gallery/capture`로 전송 → 서버는 그대로 저장 + SignalR `GalleryPhotoAdded` 실시간 갱신(서버 측 사후 재촬영 방식 폐기). PC `CapturePoster`(별도 스레드, 재무장+쿨다운), 설정은 파일 상단 `DEFAULT_*` 또는 `--capture-*`/`--insecure`(BooleanOptionalAction). 로컬 검증 완료(캡처 200·중복방지·빈바디 400). 로컬 https 자체서명 인증서 이슈는 `--insecure`로 해결(프로덕션 LAN 평문 http는 불필요). 정리: 죽은 서버측 자동캡처(`AutoCaptureService`/`CameraFrameGrabber`) 및 송신 성공 로그 제거. 프로덕션 주의: PC `--capture-url`을 RPi5(`http://<RPi>:5000/...`)로 + `--api-key`, RPi5 `Gallery:StoragePath`를 배포 폴더 밖으로.*
