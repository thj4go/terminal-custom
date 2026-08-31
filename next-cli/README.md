# NEXT-IO CLI

CLI em Python para conversar com modelos via OpenRouter, com busca web opcional.

## Instalação

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
```

## Configuração

Copie `config.example.json` para `config.json` e preencha as chaves, ou use variáveis de ambiente:

```powershell
$env:OPENROUTER_API_KEY="sua-chave"
$env:WEB_PROVIDER="brave"
$env:WEB_API_KEY="sua-chave-web"
```

Por padrao, o CLI usa o prompt normal do terminal para manter o scroll funcionando.
Se quiser reativar o prompt fixo no rodape:

```powershell
$env:NEXT_IO_FIXED_PROMPT="true"
```

Por padrao, o CLI mostra o raciocinio interno retornado por modelos que
suportam reasoning. Para esconder esse campo:

```powershell
$env:NEXT_IO_SHOW_REASONING="false"
```

Tools locais ficam ligadas em modo tool-first. O modelo e
orientado a usar tools sempre que a resposta depender do host, arquivos,
programas instalados, testes ou execucao. Ele pode listar, ler, criar pasta,
criar arquivo, anexar texto, substituir texto, aplicar patch, copiar, mover e
apagar caminhos no host. `run_terminal` executa PowerShell direto no host e
mostra a saida no chat enquanto roda.

```powershell
/tools
$env:NEXT_IO_WORKSPACE="C:\caminho\seguro"
```

`NEXT_IO_WORKSPACE` so define a pasta base para caminhos relativos. Caminhos
absolutos do host ficam bloqueados por padrao.

Por padrao, as tools rodam em modo `guarded`: caminhos relativos ficam presos
ao workspace, comandos destrutivos/instaladores sao bloqueados e acoes como
`delete_path`, `move_path` e escrita binaria exigem `confirm=true`.

```powershell
$env:NEXT_IO_PERMISSION_MODE="guarded"
$env:NEXT_IO_PERMISSION_MODE="free"
$env:NEXT_IO_ALLOW_INSTALLS="true"
$env:NEXT_IO_ALLOW_ABSOLUTE_PATHS="true"
```

Para economizar tokens, o CLI envia apenas as tools relevantes para a mensagem
atual e compacta os schemas/prompt. Para depurar com tudo completo:

```powershell
$env:NEXT_IO_COMPACT_TOOLS="false"
$env:NEXT_IO_COMPACT_PROMPT="false"
$env:NEXT_IO_MAX_HISTORY_MESSAGES="14"
$env:NEXT_IO_MAX_HISTORY_CHARS="24000"
```

Também dá para configurar pelo próprio CLI:

```text
/modelo key
/web_api set
```

## Uso

```powershell
.\next-io.bat
```

Comandos úteis:

```text
/help
/modelo
/web_api
/web sua busca
/hoje
/permissions
/token_status
/report
sair
```

## Caido

O CLI detecta o Caido via `pentest_tool_status` e pode iniciar o Caido CLI em
background com a tool `caido_start`.

Exemplo de uso no chat:

```text
inicie o caido em 127.0.0.1:8080 com proxy em 127.0.0.1:8081
```

Para expor UI/proxy fora de localhost, a tool exige `confirm=true`.
