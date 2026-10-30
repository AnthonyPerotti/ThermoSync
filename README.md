# ThermoSync

ThermoSync e um utilitario de diagnostico, telemetria em tempo real, benchmarking e otimizacao de hardware para o sistema operacional Windows. O aplicativo e desenvolvido em C# com WPF (.NET 8) e opera diretamente sobre subsistemas de baixo nivel do sistema.

## Arquitetura e Componentes

O software divide suas funcoes em modulos especializados:

### 1. Telemetria e Monitoramento de Hardware
- **Sensores em Tempo Real (`Services/SensorMonitor.cs`):** Integracao com LibreHardwareMonitorLib para medicao de frequencia, temperatura, consumo eletrico, tensao e carga de processadores, unidades graficas, controladores de memoria e ventiladores.
- **Identificacao de Sistema (`Hardware/SystemInfoCollector.cs`):** Coleta de topologia de hardware via Windows Management Instrumentation (WMI), detalhando modulos de memoria RAM (SPD, fabricante, frequencia configurada e part number) e placa-mae.
- **Especificacoes Avancadas de GPU (`Hardware/GpuInfoCollector.cs`, `Hardware/GpuSpecDb.cs`, `Hardware/Nvml.cs`):** Leitura de limites de potencia, clocks nominais, estados de energia (P-states), limitadores de desempenho (PerfCap), contadores NVENC/NVDEC e barramento PCIe via NVIDIA Management Library (NVML) e DXGI.
- **Armazenamento e S.M.A.R.T. (`Hardware/DiskHealth.cs`, `Models/DriveMap.cs`):** Leitura de contadores de confiabilidade de disco (MSFT_StorageReliabilityCounter), desgaste percentual de SSDs, setores realocados, correlacao entre unidades logicas e discos fisicos e exibicao detalhada de multiplos discos no painel de Visao Geral.
- **Projecao de Longevidade de SSD (`Hardware/DiskHealth.cs`):** Calculo da taxa media de gravacao diaria (GB/dia) e estimativa de vida util restante em anos com base no desgaste acumulado e horas de funcionamento S.M.A.R.T.
- **Historico Continuo em CSV (`Services/TelemetryLogger.cs`, `Services/TelemetryAnalyzer.cs`):** Gravacao assincrona em segundo plano da telemetria em arquivos CSV rotativos diarios em `%AppData%\ThermoSync\telemetry\`. O formato inclui declaracao `sep=;`, delimitador por ponto e virgula e codificacao UTF-8 com BOM para abertura automatica em colunas no Excel brasileiro. Analisador com deteccao dinamica de delimitador (compatibilidade total com logs legados). Retencao automatica dos ultimos 7 dias.

### 2. Controle Ativo de Ventoinhas (SYNC FAN)
- **Gerenciamento PWM (`Services/FanControlService.cs`):** Controle direto de curva e velocidade de ventiladores de placa-mae e GPU suportados via interfaces `IControl`.
- **Perfis Termicos Dinamicos:** Modos Automatico, Silencioso (Quiet), Equilibrado (Balanced), Desempenho (Performance), Manual (Fixo com slider percentual) e Curva Customizada.
- **Histerese Termica e Amortecimento Acustico:** Histerese configuravel (padrao 3 C) e rampa suave de desaceleracao (ramp-down amortecido) para evitar aceleracoes bruscas (fan revving) em limiares de temperatura, com resposta imediata para subidas termicas.
- **Watchdog de Ventoinhas Travadas:** Monitoramento de seguranca que detecta falhas mecanicas ou bloqueios fisicos quando o comando PWM permanece acima de 35% com rotacao nula (0 RPM) por 5 ciclos consecutivos.
- **Curva Customizada via Software:** Calibracao de 4 pontos termicos PWM (35 C a 85 C) com selecao de sensor de referencia (CPU, GPU ou o maior entre ambos) e interpolacao linear continua.
- **Perfis Vinculados a Jogos (Per-Game Profiles):** Aplicacao e ajuste continuo de curva de refrigeracao especifica ao detectar a execucao do jogo (tanto pelo launcher integrado quanto por inicializacao externa via Steam/Epic/desktop) com restauracao confiavel do perfil global ao encerrar a partida.

### 3. Mecanismos de Benchmark e Estresse (SYNC BENCH / SYNC OC)
- **Renderizador de CPU (`Benchmarks/BenchRenderer.cs`):** Path tracer multithreaded com amostragem deterministica (1024 amostras/pixel), reflexao e refracao. Executa em modos single-thread ou multi-thread com balanceamento de carga do centro para a borda.
- **Cena Grafica 3D (`Benchmarks/GpuGameBench.cs`):** Renderizacao em tempo real via Direct3D 11 com 70 mil arvores instanciadas, malha de terreno fractal com 409 mil vertices, sombras em tempo real (shadow map 4096 com PCF), agua com 10 ondas trocoidais, iluminacao PBR Blinn-Phong/Schlick com sub-surface scattering nas folhas e raios crepusculares volumetricos (God Rays) em tela cheia que escalam agressivamente a carga computacional e fillrate em resolucoes 1080p, 1440p e 4K.
- **Ray Tracing DXR 1.1 (`Benchmarks/GpuRtBench.cs`):** Carga computacional em Direct3D 12 utilizando raios inline em compute shaders diretamente nas unidades aceleradoras de hardware (RT Cores).
- **Simulacao de Fisica (`Benchmarks/GameSimBench.cs`):** Simulacao concorrente com mais de 30.000 entidades ativas para teste de escalabilidade e sincronizacao de threads de processador.
- **Eficiencia Energetica (pts/W) e Comparador de Deltas:** Integracao de consumo medio em Watts durante o teste para calculo da relacao pontos por Watt (pts/W), comparando variacoes percentuais de pontuacao, temperatura de pico e frequencia de clock em relacao ao historico previo.
- **I/O e Memoria (`Benchmarks/DiskBench.cs`, `Benchmarks/MemoryBench.cs`):** Testes com perfis dedicados (Completo, Apenas Sequencial e Apenas 4K & Latencia) sem cache do Windows (`FILE_FLAG_NO_BUFFERING`), alem de medicao de IOPS e latencia de resposta em microssegundos.
- **Estresse de VRAM Dedicado e Watchdog de TDR (`Benchmarks/GpuVramBench.cs`, `Benchmarks/GpuBurn.cs`):** Memtest de memoria de video GDDR via Direct3D 11 com deteccao ativa de recuperacoes de driver pelo Windows (TDR - Timeout Detection and Recovery via DeviceRemovedReason) e contagem de bits corrompidos.
- **Diagnostico de Nucleos em Falhas WHEA (`Hardware/OcCheck.cs`):** Inspecao do log de eventos do Windows (`Microsoft-Windows-WHEA-Logger` IDs 18 e 19) com extracao do APIC ID do processador no payload XML, identificando com precisao qual nucleo fisico falhou durante testes de estabilidade e overclock.
- **Deteccao de Throttling (`Hardware/ThrottlingDetector.cs`):** Avaliacao em tempo real de limites termicos (TjMax, Thermal Slowdown) e de consumo (Power Limit, Power Brake) com badges visuais nos dashboards, nos benchmarks e no teste de estresse.

### 4. Telemetria em Jogos, Gerenciamento de Energia e Overlay
- **Gerenciador de Afinidade de Processador (`Services/CpuAffinityManager.cs`):** Mapeamento de topologia via `GetLogicalProcessorInformationEx` (Win32), permitindo alocacao de processos de jogos para P-Cores exclusivos (Intel Alder Lake a Raptor Lake), CCD com V-Cache (AMD Ryzen 3D) ou isolamento de threads fisicas sem Hyper-Threading/SMT.
- **Captura ETW (`Hardware/FrameMonitor.cs`):** Coleta de tempos de apresentacao de quadros (frametime) via provedores do Event Tracing for Windows (DXGI e D3D9), eliminando a necessidade de injecao de bibliotecas dinamicas (DLL injection) em espaco de usuario do processo-alvo.
- **Alternancia Dinamica de Plano de Energia (`Utils/PowerPlanManager.cs`):** Comutacao sem latencia para o plano de Alto Desempenho do Windows via APIs nativas de `powrprof.dll` (`PowerSetActiveScheme`), disparada ao iniciar jogos pelo launcher ou por deteccao automatica em primeiro plano pelo `FrameMonitor`, com restauracao confiavel do plano prévio.
- **Interface de Overlay (`UI/OverlayWindow.xaml`):** Janela com renderizacao transparente com atributos WS_EX_TRANSPARENT e TOPMOST adaptativo para sobreposicao em aplicacoes em modo de tela cheia sem bordas. Integracao de hook WinEvent `EVENT_OBJECT_LOCATIONCHANGE` para re-afirmar a sobreposicao e posicionamento contra mudancas de resolucao ou geometria da janela do jogo.
- **Ativacao Automatica por Jogo:** Opcao nos perfis de inicializacao para habilitar o overlay de telemetria automaticamente ao iniciar a partida e restaura-lo ao estado anterior ao sair.
- **Relatorio de Sessao Pos-Jogo:** Contabilizacao automatica da duracao da sessao e exibicao de notificacao com resumo de encerramento da partida.
- **Estimativa e Diagnostico de Gargalo (`Hardware/BottleneckAnalyzer.cs`, `Utils/GameCatalog.cs`):** Analise comparativa de balanco entre capacidade computacional de CPU e GPU, monitoramento de tempos ociosos e deteccao de estrangulamento termico.

### 5. Otimizacao e Gerenciamento de Sistema
- **Diagnostico de Latencia de Drivers e DPC (`Hardware/LatencyWatchdog.cs`):** Avaliacao de jitter de despacho de interrupcoes do kernel via `QueryPerformanceCounter` com temporizacao de alta resolucao em thread critica, apontando se drivers de video, rede ou armazenamento estao monopolizando ciclos de CPU e causando micro-travamentos (stutters) em jogos.
 (SYNC OPTIMIZE / SYNC TOOLS)
- **Gerenciamento de Memoria (`Services/Optimizer.cs`):** Chamadas diretas as APIs nativas do Windows (`NtSetSystemInformation` e esvaziamento de Working Set) para liberacao de listas de paginas em espera (Standby List) e processos ociosos.
- **Ajustes de Sistema Operacional (`Services/WindowsTweaks.cs`):** Controle de chaves de registro para ativacao de Hardware Accelerated GPU Scheduling (HAGS), Game Mode, desativacao de Game DVR e ajuste de fila de resposta de entrada do mouse.
- **Inicializacao do Windows com Previlegios Elevados (`MainWindow.Settings.cs`):** Registro de inicializacao com o Windows via Agendador de Tarefas (`schtasks.exe`) com XML v1.2 em nivel `HighestAvailable`, contornando o bloqueio silencioso do UAC no logon do usuario.
- **Instalador de Aplicativos (`Utils/AppInstaller.cs`):** Automacao de instalacao via Windows Package Manager (winget) com resolucao de caminhos absolutos e suporte a instalacao sem conexao.
- **Relatorios de Diagnostico (`Utils/ReportBuilder.cs`):** Exportacao em HTML visual escuro, Markdown padronizado (.md) para documentacao, JSON estruturado e codigo LaTeX com tabelas booktabs/tabularx e graficos de telemetria e benchmark compilaveis via pgfplots/siunitx.

### 6. Integracao Homelab e Automacao
- **Dashboard Web Embutido (/dash):** Servidor HTTP interno servindo painel SPA responsivo para monitoramento em tempo real a partir de qualquer dispositivo na rede local (celular, tablet ou painel no ZimaOS/Homelab) consumindo o fluxo continuo SSE sem necessidade de stack externa, alem de controles remotos de energia e monitoramento de latencia DPC.
 (`Services/HomelabService.cs`, `Services/AlertWebhookService.cs`, `Services/NetworkTest.cs`)
- **API REST e Telemetria HTTP (`Services/HomelabService.cs`):** Endpoints HTTP internos em `/api/telemetry` (payload JSON com telemetria completa, dados de throttling e jogos) e `/api/health`, com cabecalhos CORS liberados para integracao com navegadores e dashboards locais.
- **Controle Remoto de Energia e Webhooks REST (`Services/HomelabService.cs`):** Endpoints HTTP assincronos (`/api/power/sleep`, `/api/power/lock`, `/api/power/reboot`, `/api/power/shutdown`, `/api/power/cancel`) para automacoes com Home Assistant e execucao rapida pelo dashboard web com confirmacao de seguranca.
- **Stream Continuo Server-Sent Events (`/api/stream`):** Fornece fluxo de dados HTTP em tempo real (`text/event-stream`) a 1 Hz com snapshots JSON para graficos ao vivo e paineis web de monitoramento sem necessidade de polling manual.
- **Exportador Prometheus (/metrics):** Servidor HTTP interno assincrono expondo telemetria completa em formato OpenMetrics na porta 9182 (temperaturas, consumo eletrico em Watts, clocks, uso de VRAM/RAM, saude S.M.A.R.T. e carga dos discos, estados de throttling e FPS de jogos).
- **Dashboard Grafana Provisionado (`homelab/grafana_dashboard.json`):** Painel Grafana pronto para importacao com gauges termicos de CPU, GPU, Hotspot e unidades de disco, integracao de potencia em Watts, alertas visuais de corte por temperatura ou energia e frequencias de operacao.
- **Publicador MQTT:** Cliente TCP leve compativel com MQTT 3.1.1 para envio de telemetria periodica ao broker da rede local (Mosquitto), com suporte a Home Assistant MQTT Auto-Discovery.
- **Webhooks de Alerta Critico (`AlertWebhookService.cs`):** Notificacao remota imediata para Discord, Gotify (ZimaOS/Docker) e Telegram quando limites de CPU, GPU, Hotspot ou RAM forem ultrapassados, com controle de intervalo (cooldown).
- **Diagnostico de Rede Homelab (SYNC NET - `NetworkTest.cs`):** Medicoes de latencia ICMP, jitter, deteccao de Path MTU (padrao 1500 vs Jumbo Frames 9000) e teste de vazao TCP via protocolo compativel com iPerf3.

### 7. Atualizacao e Backup
- **Auto-Update com Download Direto e Spinner Visual (`Services/UpdateService.cs`, `MainWindow.xaml`):** Verificacao in-app de novas releases no GitHub. Indicador visual no cabecalho que alerta sobre novas versoes. Ao clicar, o sistema navega para a tela de configuracoes, inicia o download imediato exibindo spinner giratorio e porcentagem no tooltip, executando a substituicao automatica do binario via script auxiliar.
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
├── homelab/                 # Provisionamento de telemetria e dashboard Grafana
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
