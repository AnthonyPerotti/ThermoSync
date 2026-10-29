# Registro de Mudancas - ThermoSync

## Versao 1.5.6

### Diagnostico e Correcao da Telemetria CSV
- Centralizado caminho de armazenamento de telemetria em `TelemetryLogger.LogDir`, unificando a gravacao em `%AppData%\ThermoSync\telemetry` e a abertura da pasta pela interface.
- Adicionadas colunas eletricas (`CpuPowerW`, `GpuPowerW`) e diagnostico de corte (`ThrottlingStatus`) ao schema CSV dos logs diarios rotativos.
- Adicionado indicador de arquivo ativo e contagem de amostras gravadas em tempo real na aba de Configuracoes.

### Controle Termico com Histerese e Suavizacao Acustica (SYNC FAN)
- Implementada histerese termica configuravel (padrao: 3 C) e rampa de amortecimento de desaceleracao (ramp-down) em `FanControlService.cs`, eliminando oscilacoes bruscas de rotacao (fan revving).
- Aceleracao imediata sob aumento de temperatura preservando protecao de hardware.
- Integracao do ajuste de histerese na interface grafica e avaliacao continua de perfil ativo no loop principal.

### Eficiencia Energetica (Pontos por Watt) e Comparador de Benchmarks (SYNC BENCH)
- Calculo integrado de consumo eletrico medio (Watts) durante benchmarks de CPU (Render e Simulacao) e GPU.
- Exibicao da metrica de eficiencia energetica `pts/W` para avaliacao precisa de undervolt e overclock.
- Indicador comparativo com o teste imediatamente anterior do historico exibindo delta percentual de pontuacao, delta de temperatura e delta de frequencia.

### Watchdog de TDR para Estresse de GPU e VRAM (SYNC OC)
- Monitoramento de integridade e recuperacao de driver via DirectX TDR (`DeviceRemovedReason`) em `GpuVramBench` e `GpuBurn`.
- Registro do status de integridade do driver de video no relatorio de estabilidade.

### Homelab: Metricas S.M.A.R.T. no Prometheus e Dashboard Grafana
- Exportacao de metricas de armazenamento no Prometheus: `thermosync_disk_temperature_celsius`, `thermosync_disk_health_percentage` e `thermosync_disk_load_percent`.
- Adicionado arquivo de provisionamento `homelab/grafana_dashboard.json` com gauges termicos, metricas de potencia integrada, clocks e status de throttling.

### Alternancia Automatica de Plano de Energia para Jogos (SYNC GAMES)
- Criado `PowerPlanManager.cs` com chamadas nativas Win32 PowrProf (`PowerSetActiveScheme` e `PowerGetActiveScheme`), eliminando spawning de processos externos.
- Alternancia automatica para Alto Desempenho ao iniciar jogos pelo launcher ou ao detectar jogos em primeiro plano pelo `FrameMonitor`, restaurando o plano anterior ao encerrar.

## Versao 1.5.5

- Corrigido comportamento de inicializacao: a janela nao minimiza mais sozinha apos o carregamento dos sensores.
- A inicializacao minimizada na bandeja agora so ocorre quando acionada com o parametro `--minimized` (ex.: no boot com o Windows), iniciando diretamente oculta sem piscar na tela.
- Abertura interativa manual (duplo clique / atalho) mantem a janela aberta continuamente.

## Versao 1.5.4

- Corrigido layout do painel de benchmark Render (CPU) em monitores ultrawide: a area de render agora tem largura maxima limitada e o painel lateral mantem scroll correto sem cortar conteudo.

## Versao 1.5.3

- Bump de versao para release de atualizacao automatica.
- Inclui todas as funcionalidades da v1.5.2 (throttling, REST API, curvas de ventoinha, VRAM stress, perfis de disco, export Markdown).

## Versao 1.5.2

### Deteccao e Registro de Throttling (Thermal e Power Limit)
- Implementada deteccao ativa de estrangulamento termico (TjMax, Thermal Slowdown) e limitadores eletricos (Power Limit / Power Brake) via `Hardware/ThrottlingDetector.cs`.
- Adicionados badges de status nos dashboards, nas abas de benchmark (Render, GPU, Simulacao) e no painel de Teste de Estresse (completando a grade 3x3 de monitoramento).
- O teste de estabilidade (SYNC OC) agora audita continuamente se houve corte termico ou limitador de energia ativo durante o teste e grava o resultado no historico e no relatorio.

### API Homelab Ampliada (JSON, Health e CORS)
- Implementados novos endpoints HTTP internos em `Services/HomelabService.cs`:
  - `GET /api/telemetry`: retorna snapshot consolidado em JSON contendo temperaturas, clocks, potencias, dados de throttling, uso de memoria e FPS de jogos ativos.
  - `GET /api/health`: endpoint para checagem de integridade e uptime da aplicacao.
- Adicionado suporte completo a CORS (`Access-Control-Allow-Origin: *`, metodos GET e OPTIONS) para consumo direto da telemetria a partir de dashboards web, Home Assistant e containers.

### Perfis Sinteticos de Armazenamento (DiskBench)
- Adicionado seletor de perfil de teste no SYNC BENCH (Disco):
  - **Completo:** Leitura e escrita sequenciais (1 MB) + acessos aleatorios 4K (Q1 e Q8).
  - **Apenas Sequencial:** Teste de taxa de transferencia bruta (MB/s).
  - **Apenas 4K & Latencia:** Foco em IOPS e latencia media em microssegundos (us) para diagnostico de tempo de resposta em SSDs NVMe e SATA.

### Curva Customizada de Ventoinhas (SYNC FAN)
- Novo perfil de controle de ventiladores "Curva Customizada", mantendo intacto o modo "Manual (Fixo)".
- Permite calibrar a resposta PWM em 4 estagios termicos (35 C, 55 C, 70 C e 85 C) com interpolacao linear continua.
- Selecao do sensor de referencia termico para a curva: temperatura da CPU, da GPU ou a maior entre ambas (`max(CPU, GPU)`).

### Relatorios em Markdown e HTML Aprimorado
- Adicionado suporte nativo a exportacao de relatorios em formato Markdown (`.md`) para documentacao em repositorios e bancadas de teste.
- Atualizacao visual dos relatorios gerados com tabelas estruturadas, omitindo numeros de serie e incluindo diagnosticos de throttling e estabilidade.

### Teste de Estresse de VRAM Dedicado (Direct3D 11)
- Implementada classe de teste de memoria de video `Benchmarks/GpuVramBench.cs` baseada em buffers Direct3D 11 em espaco de memoria GDDR.
- Gravacao e validacao continua de padroes de tensao e alternancia de bits (0x55555555, 0xAAAAAAAA, walking bits e ruido pseudo-aleatorio) com contagem de erros de integridade.
- Integrado como opcao de alvo de verificacao ("VRAM (GPU)") no SYNC OC.

### Instalador do Driver PawnIO com Auto-Recuperacao
- Corrigida a falha de instalacao em sistemas Windows onde as fontes do WinGet estao corrompidas ("Falha ao abrir a origem(s); tente o comando 'source reset' se o problema persistir").
- Implementada estrategia de auto-recuperacao em tres camadas no instalador do PawnIO:
  1. Tentativa padrao de instalacao via WinGet.
  2. Execucao automatica de `winget source reset --force` e atualizacao de fontes caso a primeira tentativa falhe, com nova tentativa de instalacao.
  3. Fallback automatico para download direto do binario oficial `PawnIO_setup.exe` a partir das releases do GitHub para a pasta temporaria (`%TEMP%`) e execucao assistida.
- Adicionada rotina de download direto do GitHub quando o utilitario WinGet nao estiver disponivel no sistema operacional.

## Versao 1.5.1

### Visao Geral com Multiplos Discos
- Corrigida a tabela de componentes da Visao Geral que exibia apenas a particao C. O ThermoSync agora lista todas as unidades e discos instalados no sistema (C:, D:, etc.) com capacidade, espaco livre e status.

### Telemetria em Tempo Real nos Benchmarks ("Durante o Teste")
- Implementado monitoramento de hardware sob estresse em tempo real em todas as abas do SYNC BENCH:
  - **Render (CPU):** temperatura, clock medio de todos os nucleos, consumo (W), carga da CPU e contagem de threads.
  - **Cena de Jogo (GPU) e Ray Tracing (GPU):** temperatura do nucleo, hotspot, clock da GPU, carga da GPU, consumo (W) e uso de VRAM (usado/total).
  - **Simulacao de Jogo (CPU):** temperatura, clock medio, consumo e carga dos nucleos em tempo real durante a fisica e IA das unidades.
  - **Memoria e Carregamento (RAM):** memoria instalada, em uso, livre, percentual de ocupacao e temperatura/carga da CPU.
  - **Disco:** unidade selecionada, modelo fisico do dispositivo, temperatura do sensor de armazenamento, espaco livre e carga de atividade do disco.
- Removido o bloqueio que pausava as leituras de sensores durante os benchmarks, permitindo telemetria contínua e sem congelamentos.

### Reformulacao Grafica da Cena de Jogo (GPU)
- Corrigida a cena desbotada: ajustada dispersao atmosferica com reducao da neblina cinza opaca e transicao de ceu azul realista.
- Leito do rio esculpido no relevo para fluxo de agua continuo por todo o vale (WaterY = 7.5f).
- Shader de agua aprimorado com reflexos especulares do sol, calculo de Fresnel e tonalidade alpina translucida.
- Albedos de grama, rocha e neve reequilibrados com iluminacao solar de alto contraste e sombras difusas.

### Script de Publicacao (publicar.bat)
- Adicionada solicitacao automatica de elevacao administrativa (UAC). Evita falha de acesso negado no fechamento de instancias ativas do ThermoSync antes do build.

### Historico de Benchmarks Melhorado
- Novo template `BenchHistory` para todas as secoes de historico: data completa (dd/MM/yyyy HH:mm), nota destacada, barra proporcional e detalhes expandidos.

## Versao 1.4.0

### Instalador de Apps e Utilitarios Pos-Formatacao
- Suporte a instalacao direta via releases oficiais do GitHub: integrado para ferramentas sem pacote atualizado no winget (`storytold/pdfcraft` e `storytold/wordcraft`). O ThermoSync consulta a API do GitHub, faz o download do binario instalador `.msi` e executa a instalacao silenciosa via `msiexec`.
- Catalogo expandido com 15 softwares essenciais organizados por categorias:
  - **Runtimes Essenciais:** Microsoft Visual C++ 2015-2022 Redistributable (x64 e x86), DirectX End-User Runtimes, .NET Desktop Runtime 8.0.
  - **Produtividade & Documentos:** PdfCraft (manipulador de PDFs com OCR e split/merge) e WordCraft (editor e conversor de documentos).
  - **Navegadores:** Google Chrome e Mozilla Firefox.
  - **Compactadores:** 7-Zip e WinRAR.
  - **Jogos & Comunicacao:** Steam, Epic Games Launcher e Discord.
  - **Midia & Drivers:** VLC media player, Lightshot e Intel Driver & Support Assistant.
- Interface visual aprimorada na aba de instalacao de aplicativos com identificacao visual da categoria de cada pacote por badges.
- Flexibilidade no instalador: aplicativos com instaladores proprios ou do GitHub agora podem ser instalados mesmo se o `winget` nao estiver disponivel no sistema operacional.

## Versao 1.3.0

### Nova Aba de Configuracoes (SYNC TOOLS)
- Criada pagina dedicada `PageSettings` sob o grupo SYNC TOOLS, desacoplando o gerenciamento do ciclo de vida da aplicacao da aba de Integracoes.
- Adicionada opcao de **Inicializacao com o Windows** (desativada por padrao) via registro `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, compativel com o executavel portatil.
- Suporte a inicializacao minimizada diretamente na bandeja do sistema com passagem automatica do parametro `--minimized`.
- Migrados os modulos de Modo Segundo Plano (Tray), Gravacao Continua de Logs CSV, Backup/Restauracao em JSON e Auto-Update via GitHub Releases para a nova tela.
- Tela de Integracao mantida 100% focada em Iluminacao RGB (iCUE, OpenRGB, Razer), Servicos de Homelab (Prometheus, MQTT) e Webhooks de Alerta (Gotify, Discord, Telegram).

## Versao 1.2.2

### Auto-Update e Publicacao de Releases
- Publicacao automatica de Releases versionadas no GitHub Actions com base na tag `<Version>` do `ThermoSync.csproj`, gerando tags sem sobrescrever historico.
- Suporte a extracao semantica de versao no `UpdateService` a partir do titulo e corpo da release quando a tag da release for generica (`latest`).

## Versao 1.2.1

### Correcoes de Inicializacao e Estabilidade
- Corrigida falha de `NullReferenceException` ao abrir a aplicacao, causada pelo disparo prematuro do evento `Checked` do seletor de perfis de ventoinhas durante o `InitializeComponent()` antes da instanciacao dos servicos.
- Corrigido icone transparente/invisivel na bandeja do sistema (System Tray), implementando extracao nativa do icone 16x16 diretamente do executavel compilado (`ExtractIconEx`) e copia redundante de `Assets/app.ico` na saida.
- Ajustado fluxo do GitHub Actions para preservar releases historicas de tags versionadas, impedindo sobrescrita acidental via `--clobber`.

## Versao 1.2.0

### Reorganizacao Estrutural de Pastas
- Migracao de mais de 70 arquivos soltos na raiz para uma arquitetura modular por camadas: `Benchmarks/`, `Hardware/`, `Models/`, `Services/`, `UI/` e `Utils/`.
- Preservacao do historico do Git via `git mv` e compatibilidade total com o globbing automatico do SDK do .NET 8.

### Auto-Update via GitHub Releases
- Implementado servico de atualizacao in-app (`Services/UpdateService.cs`) que consulta releases oficiais no GitHub.
- Download direto do executavel compilado e execucao de helper script desacoplado para substituicao do binario em execucao, contornando o bloqueio de arquivo nativo do Windows.

### Historico Continuo de Telemetria (Logs CSV)
- Adicionado `Services/TelemetryLogger.cs` para gravacao assincrona e nao-bloqueante de telemetria em arquivos CSV rotativos diarios (`telemetry-YYYY-MM-DD.csv`).
- Retencao e limpeza automatica de logs anteriores a 7 dias.

### Alerta de Memoria RAM
- Integrada verificacao de consumo de memoria RAM no `CheckAlerts()` contra o limite configurado (`AlertThresholdRamPct`), com notificacoes via webhooks (Discord, Gotify, Telegram) e balao de bandeja.

### Backup e Restauracao de Configuracoes
- Implementadas opcoes de exportacao e importacao de arquivo JSON em SYNC INTEGRATION para backup portavel das configuracoes e integracoes do sistema.

### Graficos Inline no Dashboard
- Adicionados mini-graficos vetoriais (sparklines) com curvas de tendencia para CPU, GPU e RAM nos ultimos 5 minutos no Dashboard principal.

### Perfis de Refrigeracao por Jogo
- Suporte a definicao de perfil de ventoinha (Quiet, Balanced, Performance, Extreme) em cada cartao do SYNC GAMES, com aplicacao automatica no inicio da partida e restauracao automatica ao finalizar.

### Correcao de Estabilidade
- Corrigida liberacao de handle GDI (`DestroyIcon`) no encerramento do `Services/TrayManager.cs`.

## Versao 1.1.0

### Integracao Homelab e Automacao
- Novo exportador nativo Prometheus no endpoint `/metrics` (porta 9182) para coleta e dashboards no Grafana.
- Publicador MQTT integrado (MQTT 3.1.1) com suporte a Home Assistant MQTT Auto-Discovery para criacao automatica de sensores no dashboard.
- Painel de controle dedicado em SYNC INTEGRATION para ativacao, portas e brokers.

### Exportacao de Relatorio em LaTeX
- Novo formato de exportacao `.tex` utilizando `booktabs`, `tabularx` e `xcolor` para documentacao tecnica e academica de engenharia.
- Manutencao integral dos formatos HTML e JSON existentes.

### Correcao de Execucao do winget e Driver PawnIO
- Implementada busca em cascata de caminhos absolutos do `winget.exe` (PATH, LocalAppData, UserProfile elevado e WindowsApps nativo).
- Invocacao do winget com caminho absoluto no terminal, corrigindo o erro de comando nao reconhecido em ambientes elevados por UAC.
- Adicionado fallback inteligente para download direto do instalador oficial no GitHub do PawnIO quando o winget nao estiver presente no sistema.

### Otimizacao do Binario e Compilacao Limpa
- Ativada compressao nativa Single-File no `ThermoSync.csproj`, reduzindo o tamanho do executavel de ~197 MB para ~65-85 MB.
- Ajustado o script `publicar.bat` para gerar a distribuicao na pasta `dist\` dentro da raiz do projeto.
- Corrigidos todos os avisos de nulidade estatica do compilador (CS8600, CS8621, CS8625, CS0649).

## Versao 1.0.0

### SYNC TOOLS - Inicializacao
- Listagem de programas iniciados com o Windows a partir do registro (HKCU/HKLM, 32 e 64 bits) e pastas de inicializacao.
- Controle individual para ativar ou desativar itens via chaves StartupApproved.
- Opcao de remocao com backup para restauracao reversivel.

### SYNC TOOLS - Instalar Apps
- Integracao com Windows Package Manager (winget) para instalacao automatizada e silenciosa de utilitarios essenciais (Steam, Chrome, Discord, Lightshot, 7-Zip, WinRAR, VLC).
- Deteccao automatica de instalacao previa de pacotes.

### SYNC MAKER - Portas COM e Drivers Seriais
- Utilitario de diagnostico e liberacao de portas COM travadas ou orfas (dispositivos fantasma).
- Deteccao de chips conversores serial-USB (CH340/CH341, CP2102/CP210x, FTDI, Prolific PL2303, Arduino).
- Instalacao offline de drivers assinados homologados embutidos no binario.
- Gerenciamento de servicos auxiliares e liberacao de portas bloqueadas para a Arduino IDE.

### SYNC BENCH - Ray Tracing em GPU (Direct3D 12 / DXR 1.1)
- Benchmark dedicado em DXR 1.1 utilizando compute shaders com raios inline em hardware compativel.
- Cargas com iluminacao global, sombras suaves e reflexos iterativos sobre 4.000 primitivas dinamicas.
- Metricas baseadas em mediana de 3 passagens com medicao direta via hardware timestamps e verificacao de desvio padrao.

### Overlay de Telemetria Dinamico
- Leitura de taxa de quadros e tempo de apresentacao via Event Tracing for Windows (ETW) sem injecao de DLL no processo-alvo.
- Suporte a multiplos monitores com sincronizacao automatica com a janela do jogo em foco.
- Selecao granular de telemetria exibida (metricas de CPU, GPU, VRAM, RAM, limitadores termicos e PerfCap).

### SYNC GAMES
- Deteccao de bibliotecas Steam, Epic Games, GOG e executaveis manuais.
- Perfis de execucao com prioridade de processo, plano de energia dedicado e injecao de parametros via VDF para Steam.

### Otimizacao de Inicializacao
- Inicializacao assincrona e paralela dos subsistemas de WMI, NVML e sensores de hardware.
- Reducao do tempo de apresentacao da interface para menos de 0,5 segundo.
