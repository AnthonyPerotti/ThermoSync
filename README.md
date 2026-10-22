# ThermoSync

ThermoSync e um utilitario de diagnostico, telemetria em tempo real, benchmarking e otimizacao de hardware para o sistema operacional Windows. O aplicativo e desenvolvido em C# com WPF (.NET 8) e opera diretamente sobre subsistemas de baixo nivel do sistema.

## Arquitetura e Componentes

O software divide suas funcoes em modulos especializados:

### 1. Telemetria e Monitoramento de Hardware
- **Sensores em Tempo Real (`Services/SensorMonitor.cs`):** Integracao com LibreHardwareMonitorLib para medicao de frequencia, temperatura, consumo eletrico, tensao e carga de processadores, unidades graficas, controladores de memoria e ventiladores.
- **Identificacao de Sistema (`Hardware/SystemInfoCollector.cs`):** Coleta de topologia de hardware via Windows Management Instrumentation (WMI), detalhando modulos de memoria RAM (SPD, fabricante, frequencia configurada e part number) e placa-mae.
- **Especificacoes Avancadas de GPU (`Hardware/GpuInfoCollector.cs`, `Hardware/GpuSpecDb.cs`, `Hardware/Nvml.cs`):** Leitura de limites de potencia, clocks nominais, estados de energia (P-states), limitadores de desempenho (PerfCap), contadores NVENC/NVDEC e barramento PCIe via NVIDIA Management Library (NVML) e DXGI.
- **Armazenamento e S.M.A.R.T. (`Hardware/DiskHealth.cs`, `Models/DriveMap.cs`):** Leitura de contadores de confiabilidade de disco (MSFT_StorageReliabilityCounter), desgaste percentual de SSDs, setores realocados e correlacao entre unidades logicas e discos fisicos.
- **Historico Continuo em CSV (`Services/TelemetryLogger.cs`):** Gravacao assincrona em segundo plano da telemetria em arquivos CSV rotativos diarios em `%AppData%\ThermoSync\telemetry\`. Retencao automatica dos ultimos 7 dias.

### 2. Controle Ativo de Ventoinhas (SYNC FAN)
- **Gerenciamento PWM (`Services/FanControlService.cs`):** Controle direto de curva e velocidade de ventiladores de placa-mae e GPU suportados via interfaces `IControl`.
- **Perfis Termicos Dinamicos:** Modos Automatico, Silencioso (Quiet), Equilibrado (Balanced), Desempenho (Performance), Extremo e Manual.
- **Perfis Vinculados a Jogos (Per-Game Profiles):** Aplicacao automatica de curva de refrigeracao especifica ao iniciar cada jogo no launcher e restauracao automatica do perfil padrao ao encerrar a partida.

### 3. Mecanismos de Benchmark (SYNC BENCH)
- **Renderizador de CPU (`Benchmarks/BenchRenderer.cs`):** Path tracer monocromatico multithreaded com amostragem deterministica (1024 amostras/pixel), reflexao e refracao. Executa em modos single-thread ou multi-thread com balanceamento de carga do centro para a borda.
- **Cena Grafica 3D (`Benchmarks/GpuGameBench.cs`):** Renderizacao em tempo real via Direct3D 11 com instanciamento massivo de geometria, mapas de sombra em alta resolucao e mapeamento de tons HDR.
- **Ray Tracing DXR 1.1 (`Benchmarks/GpuRtBench.cs`):** Carga computacional em Direct3D 12 utilizando raios inline em compute shaders diretamente nas unidades aceleradoras de hardware (RT Cores).
- **Simulacao de Fisica (`Benchmarks/GameSimBench.cs`):** Simulacao concorrente com mais de 30.000 entidades ativas para teste de escalabilidade e sincronizacao de threads de processador.
- **I/O e Memoria (`Benchmarks/DiskBench.cs`, `Benchmarks/MemoryBench.cs`):** Testes de leitura e escrita sequencial e aleatoria 4K sem cache de arquivo do Windows (`FILE_FLAG_NO_BUFFERING`), alem de medicao de largura de banda e latencia de memoria principal.

### 4. Telemetria em Jogos e Overlay
- **Captura ETW (`Hardware/FrameMonitor.cs`):** Coleta de tempos de apresentacao de quadros (frametime) via provedores do Event Tracing for Windows (DXGI e D3D9), eliminando a necessidade de injecao de bibliotecas dinamicas (DLL injection) em espaco de usuario do processo-alvo.
- **Interface de Overlay (`UI/OverlayWindow.xaml`):** Janela com renderizacao transparente com atributos WS_EX_TRANSPARENT e TOPMOST adaptativo para sobreposicao em aplicacoes em modo de tela cheia sem bordas.
- **Estimativa e Diagnostico de Gargalo (`Hardware/BottleneckAnalyzer.cs`, `Utils/GameCatalog.cs`):** Analise comparativa de balanco entre capacidade computacional de CPU e GPU, monitoramento de tempos ociosos e deteccao de estrangulamento termico.

### 5. Otimizacao e Gerenciamento de Sistema (SYNC OPTIMIZE / SYNC TOOLS)
- **Gerenciamento de Memoria (`Services/Optimizer.cs`):** Chamadas diretas as APIs nativas do Windows (`NtSetSystemInformation` e esvaziamento de Working Set) para liberacao de listas de paginas em espera (Standby List) e processos ociosos.
- **Ajustes de Sistema Operacional (`Services/WindowsTweaks.cs`):** Controle de chaves de registro para ativacao de Hardware Accelerated GPU Scheduling (HAGS), Game Mode, desativacao de Game DVR e ajuste de fila de resposta de entrada do mouse.
- **Inicializacao do Windows (`Services/StartupManager.cs`):** Analise e modificacao de registros de inicializacao automatica em HKLM e HKCU com capacidade de reversao.
- **Instalador de Aplicativos (`Utils/AppInstaller.cs`):** Automacao de instalacao via Windows Package Manager (winget) com resolucao de caminhos absolutos e suporte a instalacao sem conexao.
- **Relatorios de Diagnostico (`Utils/ReportBuilder.cs`):** Exportacao em HTML visual com graficos, JSON para integracao automatizada e codigo LaTeX profissional estruturado com booktabs e tabularx.

### 6. Integracao Homelab e Automacao (`Services/HomelabService.cs`, `Services/AlertWebhookService.cs`, `Services/NetworkTest.cs`)
- **Exportador Prometheus (/metrics):** Servidor HTTP interno assincrono expondo telemetria completa em formato OpenMetrics na porta 9182 (temperaturas, consumo eletrico, clock, uso de VRAM/RAM, saude de disco e FPS de jogos) para Grafana.
- **Publicador MQTT:** Cliente TCP leve compativel com MQTT 3.1.1 para envio de telemetria periodica ao broker da rede local (Mosquitto), com suporte a Home Assistant MQTT Auto-Discovery.
- **Webhooks de Alerta Critico (`AlertWebhookService.cs`):** Notificacao remota imediata para Discord, Gotify (ZimaOS/Docker) e Telegram quando limites de CPU, GPU, Hotspot ou RAM forem ultrapassados, com controle de intervalo (cooldown).
- **Diagnostico de Rede Homelab (SYNC NET - `NetworkTest.cs`):** Medicoes de latencia ICMP, jitter, deteccao de Path MTU (padrao 1500 vs Jumbo Frames 9000) e teste de vazao TCP via protocolo compativel com iPerf3.

### 7. Atualizacao e Backup
- **Auto-Update via GitHub Releases (`Services/UpdateService.cs`):** Verificacao in-app de novas releases no GitHub, download do binario e script auxiliar desacoplado para substituicao de executavel sem conflito de arquivo em uso (file lock).
- **Exportacao e Importacao de Backup (`Models/AppSettings.cs`):** Exportacao e restauracao completa das configuracoes em arquivo JSON unificado.

## Requisitos do Sistema

- **Sistema Operacional:** Windows 10 (versao 1809 ou superior) ou Windows 11 (64 bits).
- **Privilegios:** Execucao com privilegios de Administrador (necessario para leitura de contadores MSR de CPU, WMI restrito e manipulacao de servicos do sistema).
- **Driver de Telemetria de CPU:** Driver PawnIO para acesso seguro a registradores de hardware:
  ```powershell
  winget install namazso.PawnIO
  ```

## Compilacao e Publicacao

### Requisitos de Desenvolvimento
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Compilacao Local em Modo Debug
```powershell
dotnet build ThermoSync.csproj
```

### Execucao Direta
```powershell
dotnet run --project ThermoSync.csproj
```

### Geracao de Binario Autocontido (Single-File)
Para gerar o executavel distribuivel sem dependencia de instalacao previa do .NET Runtime:

```powershell
dotnet publish ThermoSync.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o dist
```

Ou execute o script automatizado:
```cmd
publicar.bat
```

## Estrutura de Diretorios

```
ThermoSync/
├── .github/workflows/       # Automacao de CI/CD para releases no GitHub
├── Assets/                  # Identidade visual (icones e logotipos vetoriais)
├── Benchmarks/              # Motores de teste sinteticos, graficos e de estresse
├── Drivers/                 # Pacotes de drivers de comunicacao serial assinados
├── Hardware/                # Camada de baixo nivel, interop nativo e P/Invoke
├── Models/                  # Entidades de dados, DTOs e configuracoes do usuario
├── Services/                # Servicos de segundo plano, telemetria, ventoinhas e rede
├── UI/                      # Janelas secundarias de overlay, toasts e dicionario de temas
├── Utils/                   # Parsers, clientes HTTP e utilitarios de sistema
├── App.xaml / .cs           # Ponto de entrada e ciclo de vida da aplicacao
├── MainWindow.xaml / .cs    # Interface principal e classes parciais (MainWindow.*.cs)
├── README.md                # Documentacao tecnica do projeto
├── CHANGELOG.md             # Historico de versoes e alteracoes
└── ThermoSync.csproj        # Definicoes de projeto e dependencias NuGet
```

## Licenca

Este projeto e disponibilizado sob os termos da licenca MIT. Consulte o arquivo [LICENSE](LICENSE) para maiores informacoes.
