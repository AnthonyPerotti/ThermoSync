# ThermoSync

ThermoSync e um utilitario de diagnostico, telemetria em tempo real, benchmarking e otimizacao de hardware para o sistema operacional Windows. O aplicativo e desenvolvido em C# com WPF (.NET 8) e opera diretamente sobre subsistemas de baixo nivel do sistema.

## Arquitetura e Componentes

O software divide suas funcoes em modulos especializados:

### 1. Telemetria e Monitoramento de Hardware
- **Sensores em Tempo Real (`SensorMonitor.cs`):** Integracao com LibreHardwareMonitorLib para medicao de frequencia, temperatura, consumo eletrico, tensao e carga de processadores, unidades graficas, controladores de memoria e ventiladores.
- **Identificacao de Sistema (`SystemInfoCollector.cs`):** Coleta de topologia de hardware via Windows Management Instrumentation (WMI), detalhando modulos de memoria RAM (SPD, fabricante, frequencia configurada e part number) e placa-mae.
- **Especificacoes Avancadas de GPU (`GpuInfoCollector.cs`, `GpuSpecDb.cs`, `Nvml.cs`):** Leitura de limites de potencia, clocks nominais, estados de energia (P-states), limitadores de desempenho (PerfCap), contadores NVENC/NVDEC e barramento PCIe via NVIDIA Management Library (NVML) e DXGI.
- **Armazenamento e S.M.A.R.T. (`DiskHealth.cs`, `DriveMap.cs`):** Leitura de contadores de confiabilidade de disco (MSFT_StorageReliabilityCounter), desgaste percentual de SSDs, setores realocados e correlacao entre unidades logicas e discos fisicos.

### 2. Mecanismos de Benchmark (SYNC BENCH)
- **Renderizador de CPU (`BenchRenderer.cs`):** Path tracer monocromatico multithreaded com amostragem deterministica (1024 amostras/pixel), reflexao e refracao. Executa em modos single-thread ou multi-thread com balanceamento de carga do centro para a borda.
- **Cena Grafica 3D (`GpuGameBench.cs`):** Renderizacao em tempo real via Direct3D 11 com instanciamento massivo de geometria, mapas de sombra em alta resolucao e mapeamento de tons HDR.
- **Ray Tracing DXR 1.1 (`GpuRtBench.cs`):** Carga computacional em Direct3D 12 utilizando raios inline em compute shaders diretamente nas unidades aceleradoras de hardware (RT Cores).
- **Simulacao de Fisica (`GameSimBench.cs`):** Simulacao concorrente com mais de 30.000 entidades ativas para teste de escalabilidade e sincronizacao de threads de processador.
- **I/O e Memoria (`DiskBench.cs`, `MemoryBench.cs`):** Testes de leitura e escrita sequencial e aleatoria 4K sem cache de arquivo do Windows (`FILE_FLAG_NO_BUFFERING`), alem de medicao de largura de banda e latencia de memoria principal.

### 3. Telemetria em Jogos e Overlay
- **Captura ETW (`FrameMonitor.cs`):** Coleta de tempos de apresentacao de quadros (frametime) via provedores do Event Tracing for Windows (DXGI e D3D9), eliminando a necessidade de injecao de bibliotecas dinamicas (DLL injection) em espaco de usuario do processo-alvo.
- **Interface de Overlay (`OverlayWindow.xaml`, `MainWindow.Overlay.cs`):** Janela com renderizacao transparente com atributos WS_EX_TRANSPARENT e TOPMOST adaptativo para sobreposicao em aplicacoes em modo de tela cheia sem bordas.
- **Estimativa e Diagnostico de Gargalo (`BottleneckAnalyzer.cs`, `GameCatalog.cs`):** Analise comparativa de balanco entre capacidade computacional de CPU e GPU, monitoramento de tempos ociosos e deteccao de estrangulamento termico.

### 4. Otimizacao e Gerenciamento de Sistema (SYNC OPTIMIZE / SYNC TOOLS)
- **Gerenciamento de Memoria (`Optimizer.cs`):** Chamadas diretas as APIs nativas do Windows (`NtSetSystemInformation` e esvaziamento de Working Set) para liberacao de listas de paginas em espera (Standby List) e processos ociosos.
- **Ajustes de Sistema Operacional (`WindowsTweaks.cs`):** Controle de chaves de registro para ativacao de Hardware Accelerated GPU Scheduling (HAGS), Game Mode, desativacao de Game DVR e ajuste de fila de resposta de entrada do mouse.
- **Inicializacao do Windows (`StartupManager.cs`):** Analise e modificacao de registros de inicializacao automatica em HKLM e HKCU com capacidade de reversao.
- **Instalador de Aplicativos (`AppInstaller.cs`):** Automacao de instalacao via Windows Package Manager (winget).

### 5. Suporte a Prototipagem e Embarcados (SYNC MAKER)
- **Gerenciamento de Portas Seriais (`UsbCleaner.cs`, `MakerDrivers.cs`):** Limpeza de entradas orfas no banco de dados de portas de comunicacao (ComDB) e diagnostico de controladores comuns (CH340, CP2102, FTDI, Prolific).
- **Pacotes de Drivers Embutidos:** Recursos embutidos assinados digitalmente para instalacao em ambientes sem conexao ativa com a internet.

### 6. Integracao de Iluminacao RGB (SYNC INTEGRATION)
- **Controle Unificado (`LightBackends.cs`, `CorsairSdk.cs`):** Camada de abstracao para sincronizacao de iluminacao compativel com Corsair iCUE SDK, clientes locais OpenRGB e Razer Chroma REST API.

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
├── Drivers/                 # Pacotes de drivers de comunicacao serial assinados
├── App.xaml                 # Ponto de entrada e ciclo de vida da aplicacao
├── MainWindow.xaml          # Interface principal em XAML
├── MainWindow.*.cs          # Implementacoes modulares da janela principal
├── *Bench.cs                # Modulos de testes de desempenho
├── SensorMonitor.cs         # Monitor de telemetria de sensores
├── Optimizer.cs             # Servico de limpeza de memoria e processos
└── ThermoSync.csproj        # Definicoes de projeto e dependencias NuGet
```

## Licenca

Este projeto e disponibilizado sob os termos da licenca MIT. Consulte o arquivo [LICENSE](LICENSE) para maiores informacoes.
