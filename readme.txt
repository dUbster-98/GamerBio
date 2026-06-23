일상 배포 (코드 수정 후 재배포)

  PC(Windows)의 프로젝트 루트에서:

  # 1) ARM64 리눅스용 빌드 (RPi5에 .NET 런타임이 깔려 있으므로 --self-contained false)
  dotnet publish .\GamerBio\GamerBio.csproj -c Release -r linux-arm64 --self-contained false -o .\publish

  # 2) RPi5로 전송
  scp -r .\publish\* tjdgus@192.168.0.104:/opt/biomonitor/

  # 3) 서비스 재시작 (sudo 비밀번호 입력)
  ssh tjdgus@192.168.0.104 "sudo systemctl restart biomonitor-api.service"

  # 4) 동작 확인
  ssh tjdgus@192.168.0.104 "systemctl status biomonitor-api.service --no-pager"

  접속: http://192.168.0.104:5000 (외부에서는 Tailscale 호스트네임)

  로그 실시간 확인: ssh tjdgus@192.168.0.104 "journalctl -u biomonitor-api -f"

  ---
  신규 RPi5에 처음 셋업하는 경우

  1) .NET 10 ASP.NET 런타임 설치 (RPi5 SSH)

  curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0 --runtime aspnetcore --install-dir
  $HOME/.dotnet
  echo 'export DOTNET_ROOT=$HOME/.dotnet' >> ~/.bashrc
  echo 'export PATH=$PATH:$HOME/.dotnet' >> ~/.bashrc
  source ~/.bashrc
  dotnet --list-runtimes   # Microsoft.AspNetCore.App 10.x 확인

  2) PostgreSQL 17 설치

  sudo apt update && sudo apt install -y postgresql postgresql-contrib
  sudo systemctl enable --now postgresql
  sudo -u postgres psql <<'SQL'
  CREATE USER biomonitor WITH PASSWORD '여기에_강한_비밀번호';
  CREATE DATABASE biomonitor OWNER biomonitor;
  SQL
  # pg_hba.conf에서 local/host TCP md5 인증 활성화 후 sudo systemctl restart postgresql

  3) 배포 디렉터리 + 시크릿

  sudo mkdir -p /opt/biomonitor && sudo chown tjdgus:tjdgus /opt/biomonitor

  /opt/biomonitor/appsettings.Production.json (RPi5에만 존재, git 제외):
  {
    "ConnectionStrings": {
      "BioMonitor": "Host=127.0.0.1;Port=5432;Database=biomonitor;Username=biomonitor;Password=여기에_강한_비밀번호"
    }
  }

  4) systemd 유닛

  /etc/systemd/system/biomonitor-api.service:
  [Unit]
  Description=GamerBio biomonitor API (ASP.NET 10 + Blazor)
  After=network.target postgresql.service
  Requires=postgresql.service

  [Service]
  WorkingDirectory=/opt/biomonitor
  ExecStart=/home/tjdgus/.dotnet/dotnet /opt/biomonitor/GamerBio.dll
  Restart=always
  RestartSec=5
  User=tjdgus
  Environment=ASPNETCORE_ENVIRONMENT=Production
  Environment=ASPNETCORE_URLS=http://0.0.0.0:5000
  Environment=DOTNET_ROOT=/home/tjdgus/.dotnet

  [Install]
  WantedBy=multi-user.target

  sudo systemctl daemon-reload
  sudo systemctl enable --now biomonitor-api.service


1) RPi5에 cloudflared 설치 (ARM64)

  SSH 접속 후:
  curl -L https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-arm64.deb -o
  /tmp/cloudflared.deb
  sudo dpkg -i /tmp/cloudflared.deb
  cloudflared --version

  2) Cloudflare 로그인 + 터널 생성

  cloudflared tunnel login          # 출력 URL을 PC 브라우저에서 열어 도메인 선택 → 인증서 받음
  cloudflared tunnel create biomonitor

  생성 시 출력되는 터널 UUID와 자격증명 파일 경로(/home/tjdgus/.cloudflared/<UUID>.json)를 메모해두세요.

  3) DNS 라우팅 + 설정 파일

  cloudflared tunnel route dns biomonitor biomonitor.example.com

  /home/tjdgus/.cloudflared/config.yml:
  tunnel: <위에서_받은_UUID>
  credentials-file: /home/tjdgus/.cloudflared/<UUID>.json

  ingress:
    - hostname: biomonitor.example.com
      service: http://localhost:5000
    - service: http_status:404

  테스트 실행: cloudflared tunnel run biomonitor → 브라우저에서 https://biomonitor.example.com 접속 확인 후 Ctrl+C.

  4) systemd 등록

  sudo cloudflared service install
  sudo systemctl enable --now cloudflared
  systemctl status cloudflared --no-pager

  ▎ service install은 시스템 위치(/etc/cloudflared/)로 config/credentials를 복사합니다. 이후 설정 변경은
  ▎ /etc/cloudflared/config.yml을 수정하고 sudo systemctl restart cloudflared.








