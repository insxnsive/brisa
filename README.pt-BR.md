<p align="center">
  <img src="src/Brisa/Assets/Brisa.png" width="112" alt="Brisa">
</p>

<h1 align="center">Brisa</h1>

<p align="center">
  <a href="https://github.com/insxnsive/brisa/releases"><img src="https://img.shields.io/github/v/release/insxnsive/brisa?style=flat-square&label=vers%C3%A3o" alt="versão"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/insxnsive/brisa?style=flat-square&label=licen%C3%A7a" alt="licença"></a>
  <a href="#requisitos"><img src="https://img.shields.io/badge/plataforma-Windows%20x64-blue?style=flat-square" alt="plataforma"></a>
  <a href="docs/development.md"><img src="https://img.shields.io/badge/feito%20com-.NET%2010-512BD4?style=flat-square" alt="feito com"></a>
</p>

**O Discord passa pelo WireGuard. O resto continua na sua conexão normal.** Sem VPN no navegador, nos jogos ou no resto do PC, e sem Electron por cima. Feito em .NET e WPF, interface em inglês e português do Brasil.

[Baixar](https://github.com/insxnsive/brisa/releases) · [Instalar](#instalar) · [Recursos](#recursos) · [Uso](#uso) · [Requisitos](#requisitos) · [Documentação](#documentação) · [Licença](#licença) · [English](README.md)

## Instalar

Baixe o **`Brisa-win-Setup.exe`** em [Releases](https://github.com/insxnsive/brisa/releases), ou pegue o ZIP portátil na mesma página.

O instalador ainda não tem assinatura digital, então o SmartScreen pode avisar sobre editor desconhecido. Se o arquivo veio das Releases deste repositório, clique em **Mais informações → Executar assim mesmo**.

O Brisa se instala por usuário, sem precisar de administrador; os instaladores do WebView2 e do WireSock que ele abrir podem pedir. Nada é instalado às escondidas.

Prefere um script que confere o download antes? No Windows PowerShell (não no Prompt de Comando):

```powershell
irm https://raw.githubusercontent.com/insxnsive/brisa/brisa/scripts/install.ps1 | iex
```

Ele confere a assinatura da Microsoft no WebView2 e o SHA-256 do instalador do Brisa antes de rodar qualquer um dos dois. O script fixado em um commit, e o que cada verificação garante ou não: [verificações de instalação](docs/install.md).

## Recursos

- Só o Discord entra no túnel. Navegador, jogos e o resto seguem pela rota normal.
- Entre com a sua conta Proton, ou importe um `.conf` que você já tem.
- Fechar a janela mantém o Brisa e a conexão na bandeja.
- As atualizações são baixadas em segundo plano e instaladas quando você sai do Brisa.

## Uso

1. Abra o Brisa pelo menu Iniciar. Para conectar é preciso ser administrador; para só abrir o app, não.
2. Entre na sua conta Proton, ou importe o seu perfil em Configurações → Avançado.
3. Escolha a rota se quiser, e clique em Conectar.

Conectar e desconectar fecham e reabrem o Discord, em qualquer versão. Saia de qualquer chamada antes; ao desconectar, o Brisa reabre o Discord na rota normal.

Se o WireSock não estiver instalado, o Brisa abre o instalador oficial na primeira conexão e segue com a conexão em seguida. Mais em [configuração](docs/setup.md).

## Requisitos

- Windows x64, feito para o Windows 11.
- Permissão de administrador para conectar ou desconectar.
- [WireSock Secure Connect SDK](https://wiresock.net/wiresock-sdk), baixado na primeira conexão se faltar.
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) para a página de verificação da Proton.
- Uma conta Proton ou o seu próprio `.conf` do WireGuard.
- Nenhum outro túnel do WireSock ou do GoLiveBypass ativo. O Brisa nunca assume o túnel de outro app.

## Documentação

| | |
| --- | --- |
| [Verificações de instalação](docs/install.md) | Instalação fixada em um commit, verificação de hash |
| [Configuração](docs/setup.md) | Requisitos, instalador, versão portátil, primeira conexão, [janela e bandeja](docs/setup.md#window-and-tray), dados salvos |
| [Desenvolvimento](docs/development.md) | Compilar, testar, empacotar, branch do macOS |
| [Verificação](docs/verification.md) | O que foi testado e como |
| [Privacidade](docs/privacy.md) | O que o Brisa guarda e o que ele nunca envia |
| [Publicação de versões](docs/releasing.md) | Versões, tags e feed de atualização |
| [Avisos de terceiros](THIRD-PARTY-NOTICES.md) | Licenças de tudo que vem dentro |

## Contribuir

Bugs e sugestões vão nas [Issues](https://github.com/insxnsive/brisa/issues). Nunca publique senhas, códigos de verificação, arquivos de sessão ou perfis de VPN. O log gerado em Avançado → Gerar log pode ser compartilhado: nomes de conta, caminhos e endereços são removidos antes de o log ser escrito.

## Licença

[GPL-3.0-or-later](LICENSE), sem garantia. O Brisa é um fork nativo do [GoLiveBypass](https://github.com/bezumiya/GoLiveBypass). A interface Electron e o plugin foram removidos, e os avisos de direitos autorais originais foram mantidos. A ferramenta que gera os perfis da Proton vem do [hatemosphere/protonvpn-wg-confgen](https://github.com/hatemosphere/protonvpn-wg-confgen).

O Brisa não tem vínculo com Discord, Proton ou WireSock.
