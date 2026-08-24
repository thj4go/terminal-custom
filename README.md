# Terminal Custom

Terminal nativo para Windows criado em C# e WPF, com interface translúcida, sessão interativa via ConPTY e integração opcional com IA.

## Recursos

- PowerShell contínuo e interativo dentro da própria janela.
- Interface translúcida com cantos arredondados e controles personalizados.
- Histórico e navegação de comandos pelo PSReadLine, sem sugestões preditivas.
- Copiar com `Ctrl+Shift+C` e colar com `Ctrl+Shift+V`.
- Mouse direito copia o texto selecionado ou cola quando não há seleção.
- Suporte a cores ANSI, Unicode e redimensionamento do pseudoterminal.
- IA opcional pela OpenRouter usando `deepseek/deepseek-v4-pro`.
- Comandos válidos são executados normalmente; textos que não são comandos são enviados à IA.
- Chave e personalidade da IA permanecem somente na memória da sessão.

## Requisitos

- Windows 10 ou Windows 11.
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) para compilar o projeto.
- Chave da OpenRouter apenas para utilizar a IA.

## Executar

Clique duas vezes em `iniciar_terminal.bat`. Na primeira execução, o inicializador compila o aplicativo caso a pasta `app` ainda não exista.

Também é possível compilar manualmente:

```powershell
dotnet run --project .\src\CustomTerminal.csproj
```

## Comandos da IA

| Comando | Função |
| --- | --- |
| `ai-key` | Abre o campo protegido para informar a chave da OpenRouter. |
| `ai-key -remover` | Remove imediatamente a chave da memória. |
| `ai-status` | Mostra o modelo, a chave e o tipo de personalidade atual. |
| `ai-prompt` | Abre o editor do system prompt para mudar a personalidade. |
| `ai-prompt -remover` | Restaura a personalidade padrão. |

No editor de personalidade, use `Ctrl+Enter` para confirmar. O histórico da conversa é reiniciado sempre que a personalidade muda.

## Segurança

A chave da OpenRouter não é gravada em arquivos, configurações ou no código-fonte. Ela existe apenas na memória do processo e é removida quando o terminal é fechado.

## Estrutura

- `src/`: código-fonte C#, XAML e recursos visuais.
- `iniciar_terminal.bat`: inicializador para Windows.
- `app/`: saída compilada local, ignorada pelo Git.
