# Registro de Mudancas - ThermoSync

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
