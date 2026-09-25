# Brisa

Brisa roteia apenas o tráfego do Discord pelo WireGuard, sem colocar os outros aplicativos do computador na VPN. É um aplicativo nativo para Windows, feito com .NET e WPF; um backend Node coordena a rede e um auxiliar Go gera perfis Proton.

**[Português (Brasil)](#português-brasil) · [English](#english)**

## Português (Brasil)

### Índice

- [Instalar](#instalar)
- [Primeira conexão](#primeira-conexão)
- [Atualizações](#atualizações)
- [Janela e bandeja](#janela-e-bandeja)
- [Desenvolvimento e licença](#desenvolvimento-e-licença)

### Instalar

Abra o PowerShell e cole este comando. O [script de instalação](scripts/install.ps1) fica disponível para consulta no repositório:

```powershell
irm https://raw.githubusercontent.com/insxnsive/brisa/brisa/scripts/install.ps1 | iex
```

O instalador verifica se o WebView2 Runtime está presente e, se faltar, baixa o instalador oficial da Microsoft e confere a assinatura digital. Depois baixa o instalador mais recente publicado do Brisa pelo GitHub e confere o SHA-256 antes de executá-lo. As janelas dos instaladores ficam visíveis; não há instalação silenciosa.

O comando não precisa ser executado como administrador. Ele instala o Brisa e o WebView2 quando necessário. Requer Windows 64 bits (x64). O aplicativo e os runtimes do .NET e Node já vêm no pacote. O WireSock não é incluído no instalador do Brisa.

### Primeira conexão

1. Abra o Brisa pelo menu Iniciar usando **Executar como administrador**.
2. Entre na sua conta Proton ou importe seu próprio perfil WireGuard em Configurações → Avançado.
3. Ao conectar pela primeira vez, se o WireSock estiver ausente, o Brisa baixa o instalador oficial, confere o hash e mostra a instalação do WireSock. Leia e aceite os termos do WireSock e aprove a solicitação do Windows. O Brisa continua a conexão depois da instalação.

O WireSock é um componente de terceiros. O próprio fornecedor diferencia uso não comercial e comercial; o nível gratuito é somente para uso não comercial e não é autorizado para produção comercial. O Brisa não inclui nem instala o WireSock sem mostrar os avisos do fornecedor e do Windows.

### Atualizações

O Brisa verifica novas versões publicadas e baixa atualizações em segundo plano. Ele só aplica uma atualização quando for seguro reiniciar; uma conexão ativa não é interrompida.

### Janela e bandeja

Fechar a janela mantém o Brisa e a conexão ativos na bandeja do sistema. Use **Abrir** no ícone da bandeja para voltar; escolha **Sair** para desconectar e fechar o aplicativo.

### Desenvolvimento e licença

O guia de [configuração, desenvolvimento e testes](docs/development.md) explica como compilar o Brisa. O Brisa é um fork somente para Windows do [GoLiveBypass](https://github.com/bezumiya/GoLiveBypass). A interface Electron e o plugin foram removidos. O código do Brisa está sob [GPL-3.0-or-later](LICENSE), sem garantia. Os avisos de direitos autorais originais foram mantidos. Consulte os [avisos de terceiros](THIRD-PARTY-NOTICES.md).

O auxiliar Proton vem de [hatemosphere/protonvpn-wg-confgen](https://github.com/hatemosphere/protonvpn-wg-confgen), com mudanças herdadas do GoLiveBypass e correções do Brisa. O Brisa não é afiliado ao Discord, Proton ou WireSock. As primeiras versões são previews; relate problemas em [Issues](https://github.com/insxnsive/brisa/issues). Nunca publique senhas, códigos de verificação, arquivos de sessão ou perfis VPN.

## English

Brisa routes Discord traffic through WireGuard without putting the rest of your PC on a VPN. It is a native Windows app built with .NET and WPF; a Node backend coordinates networking, and a Go helper creates Proton profiles.

### Contents

- [Quick Install](#quick-install)
- [First Connection](#first-connection)
- [Updates](#updates)
- [Window and Tray](#window-and-tray)
- [Development and License](#development-and-license)

### Quick Install

The [installer script](scripts/install.ps1) is available in the repository to inspect. Open PowerShell and paste:

```powershell
irm https://raw.githubusercontent.com/insxnsive/brisa/brisa/scripts/install.ps1 | iex
```

The installer checks for the WebView2 Runtime. If it is missing, it downloads Microsoft's official installer and verifies its digital signature. It then downloads the latest published Brisa setup from GitHub and checks the SHA-256 digest before running it. Installer windows stay visible; nothing is installed silently.

You do not need to run the command as administrator. It installs Brisa and WebView2 if needed and requires 64-bit Windows (x64). The app, .NET and Node runtimes are already included in the package. WireSock is not bundled with Brisa Setup.

### First Connection

1. Open Brisa from the Start menu using **Run as administrator**.
2. Sign in to Proton, or import your own WireGuard profile under Settings → Advanced.
3. On the first connection, if WireSock is missing, Brisa downloads the official installer, verifies its hash, and opens WireSock Setup. Review and accept WireSock's terms and approve the Windows prompt. Brisa continues connecting after installation.

WireSock is a third-party component. Its vendor distinguishes non-commercial and commercial use; the free tier is for non-commercial use only and is not licensed for commercial production. Brisa does not bundle WireSock or install it without showing the vendor's installer and Windows prompts.

### Updates

Brisa checks for published releases and downloads updates in the background. It applies them only when a safe restart is possible; an active connection is not interrupted.

### Window and Tray

Closing the window keeps Brisa and the connection running in the system tray. Choose **Open** from the tray icon to return, or **Exit** to disconnect and quit.

### Development and License

See the [development and test guide](docs/development.md) to build Brisa. Brisa is a Windows-only fork of [GoLiveBypass](https://github.com/bezumiya/GoLiveBypass). The Electron interface and plugin were removed. Brisa is licensed under [GPL-3.0-or-later](LICENSE), without warranty. Original copyright notices remain. See [third-party notices](THIRD-PARTY-NOTICES.md).

The Proton helper comes from [hatemosphere/protonvpn-wg-confgen](https://github.com/hatemosphere/protonvpn-wg-confgen), with changes inherited from GoLiveBypass and fixes made for Brisa. Brisa is not affiliated with Discord, Proton or WireSock. Early releases are previews; report problems in [Issues](https://github.com/insxnsive/brisa/issues). Never post passwords, verification codes, session files or VPN profiles.
