# Terminal Custom

Terminal para Windows criado em C# e WPF. A aplicação tem um shell próprio, interface translúcida, programas interativos via ConPTY e integração opcional com IA.

## O que mudou

O Terminal Custom não inicia mais o PowerShell automaticamente. Comandos internos são interpretados pelo shell C# e executáveis são localizados no diretório atual, `PATH` e `PATHEXT`. PowerShell, CMD, Python, SSH e Node continuam disponíveis como programas opcionais quando instalados.

```text
WPF
  └─ ShellEngine (C#)
       ├─ built-ins
       ├─ parser, ambiente, histórico, pipes e redirecionamentos
       ├─ executáveis do Windows
       ├─ ConPTY para programas interativos
       └─ OpenRouter para linguagem natural
```

## Recursos

- Prompt e estado de diretório próprios.
- Cores ANSI, Unicode, cursor e redimensionamento preservados pelo `TerminalBuffer`.
- Programas interativos como `python`, `pwsh`, `powershell`, `cmd` e `ssh` via ConPTY.
- Pipes (`|`) e redirecionamentos (`>`, `>>`, `<`) sem PowerShell.
- Variáveis `%NOME%`, `PATH` e `PATHEXT` herdadas pelos processos filhos.
- Histórico em memória com `↑`, `↓` e `history`; entradas sensíveis não são guardadas.
- Copiar com `Ctrl+Shift+C`; colar com `Ctrl+Shift+V` ou botão direito sem seleção.
- `Ctrl+L` limpa apenas a tela. `Ctrl+C` cancela a linha ou é enviado ao processo ativo.
- Suporte a AltGr em teclado ABNT2.
- IA opcional via OpenRouter usando `deepseek/deepseek-v4-pro`.

## Comandos internos

```text
cd  pwd  dir/ls  cls/clear  echo  mkdir/md  rmdir/rd  del/rm
copy/cp  move/mv  type/cat  touch  where/which  set/env
history  help  exit
```

Exemplos:

```text
cd "%USERPROFILE%\Downloads"
echo teste > arquivo.txt
echo linha2 >> arquivo.txt
type arquivo.txt
ipconfig | findstr IPv4
set TESTE=123
echo %TESTE%
python
powershell
```

Arquivos `.exe` e `.com` são iniciados diretamente. Somente scripts `.cmd` e `.bat` usam `cmd.exe /d /s /c`, pois esse é o interpretador exigido por esses formatos.

## IA

| Comando | Função |
| --- | --- |
| `ai-key` | Informa a chave da OpenRouter em um campo protegido. |
| `ai-key -remover` | Remove a chave da memória. |
| `ai-status` | Mostra modelo e estado da personalidade. |
| `ai-prompt` | Altera o system prompt somente nesta sessão. |
| `ai-prompt -remover` | Restaura a personalidade padrão. |
| `ai <pergunta>` | Envia uma pergunta explícita à IA. |

Frases reconhecidas como linguagem natural também podem ir para a IA. Erros comuns, como `gitt status`, continuam sendo erros de comando e podem receber sugestão. Textos com padrões de chaves, tokens, senhas, cookies, `.env` ou chaves privadas não são enviados automaticamente. A chave da OpenRouter nunca é gravada em arquivo ou histórico.

## Executar

Requer Windows 10/11 e .NET 9 SDK para compilar. Clique duas vezes em `iniciar_terminal.bat`; ele publica a versão atual antes de abrir, evitando executar binários antigos. Também é possível executar:

```powershell
dotnet run --project .\src\CustomTerminal.csproj
```

## Compilar e testar

```powershell
dotnet build .\src\CustomTerminal.csproj -c Release
dotnet run --project .\tests\TerminalCustom.Tests\TerminalCustom.Tests.csproj -c Release
```

## Estrutura

- `src/Shell/`: parser, contexto, executor, resolução de programas, ambiente, histórico, entrada e motor do shell.
- `src/ConPtySession.cs`: pseudoconsole temporário para processos interativos.
- `src/TerminalBuffer.cs`: interpretação ANSI e modelo visual do terminal.
- `src/AiBridgeServer.cs`: diálogos e cliente OpenRouter direto em C#.
- `src/MainWindow.xaml(.cs)`: visual, eventos de entrada e renderização.
- `tests/TerminalCustom.Tests/`: testes automatizados sem dependências externas.
- `iniciar_terminal.bat`: inicializador do Windows.
