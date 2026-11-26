using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace PiuLink.Host.Localization;

public sealed record LanguageOption(string Code, string DisplayName);

public sealed class LocalizationService : INotifyPropertyChanged
{
    private const string FallbackCode = "en-US";
    private static readonly IReadOnlyDictionary<string, string> English = Dictionary(
        ("Section.Address", "Network addresses"),
        ("Section.Native", "Client invitation"),
        ("Section.Browser", "Web browser"),
        ("Section.BrowserHint", "One-time access to the desktop, keyboard and mouse."),
        ("Section.Services", "Network and streaming services"),
        ("Section.Device", "Computer and graphics adapters"),
        ("Section.NetworkSetup", "Install and connect Tailscale"),
        ("Section.NetworkOptions", "Tailscale preferences"),
        ("Section.Microphone", "Virtual microphone"),
        ("Section.Terminal", "Remote terminal (SSH)"),
        ("Section.Configuration", "Sunshine configuration"),
        ("Section.Recovery", "Desktop and window recovery"),
        ("Section.Driver", "Install the display driver"),
        ("Section.WindowSafety", "Window recovery"),
        ("Section.WakeSetup", "Set up Magic Packet wake"),
        ("Section.NoAddress", "No reachable address yet"),
        ("Common.Host", "Host"),
        ("Nav.Section", "HOST"),
        ("Nav.Overview", "Overview"),
        ("Nav.PrivateNetwork", "Private network"),
        ("Nav.StreamingHost", "Streaming host"),
        ("Nav.Displays", "Displays"),
        ("Nav.VirtualDisplay", "Virtual display"),
        ("Nav.WakeOnLan", "Wake-on-LAN"),
        ("Nav.AdvancedSettings", "Advanced settings"),
        ("Nav.Diagnostics", "Diagnostics"),
        ("Sidebar.FreeHostCompanion", "Free host companion"),
        ("Language.Label", "Language"),
        ("Status.Checking", "Checking…"),
        ("Toolbar.RefreshDiagnostics", "Refresh diagnostics"),
        ("Page.Overview.Subtitle", "Everything you need to connect to this PC."),
        ("Page.PrivateNetwork.Subtitle", "Install and connect Tailscale with guided steps."),
        ("Page.StreamingHost.Subtitle", "Manage Sunshine locally without leaving PiuLink."),
        ("Page.Displays.Subtitle", "Choose a physical or virtual screen for remote sessions."),
        ("Page.VirtualDisplay.Subtitle", "Create a streaming screen or install its trusted driver."),
        ("Page.WakeOnLan.Subtitle", "Find this PC's MAC address and verify Windows wake readiness."),
        ("Page.AdvancedSettings.Subtitle", "Expert controls for terminal access and the complete streaming configuration."),
        ("Page.Diagnostics.Subtitle", "A detailed, read-only health check for your host."));

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Translations =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            [FallbackCode] = English,
            ["zh-CN"] = Dictionary(
                ("Section.Address", "局域网与组网地址"),
                ("Section.Native", "客户端邀请"),
                ("Section.Browser", "浏览器访问"),
                ("Section.BrowserHint", "一次性链接，可查看桌面并使用键鼠。"),
                ("Section.Services", "网络与串流服务"),
                ("Section.Device", "本机与显卡"),
                ("Section.NetworkSetup", "安装与连接 Tailscale"),
                ("Section.NetworkOptions", "Tailscale 连接设置"),
                ("Section.Microphone", "虚拟麦克风"),
                ("Section.Terminal", "远程终端（SSH）"),
                ("Section.Configuration", "Sunshine 配置编辑器"),
                ("Section.Recovery", "桌面布局与窗口恢复"),
                ("Section.Driver", "安装虚拟显示驱动"),
                ("Section.WindowSafety", "窗口恢复"),
                ("Section.WakeSetup", "配置网络唤醒"),
                ("Section.NoAddress", "暂未检测到可用地址"),
                ("Common.Host", "主机端"), ("Nav.Section", "主机"), ("Nav.Overview", "概览"),
                ("Nav.PrivateNetwork", "私有网络"), ("Nav.StreamingHost", "串流主机"),
                ("Nav.Displays", "显示器"), ("Nav.VirtualDisplay", "虚拟显示器"),
                ("Nav.WakeOnLan", "远程唤醒"), ("Nav.AdvancedSettings", "高级设置"), ("Nav.Diagnostics", "诊断"), ("Sidebar.FreeHostCompanion", "免费主机端"),
                ("Language.Label", "语言"), ("Status.Checking", "正在检查…"),
                ("Toolbar.RefreshDiagnostics", "刷新诊断"),
                ("Page.Overview.Subtitle", "连接到这台电脑所需的一切。"),
                ("Page.PrivateNetwork.Subtitle", "按照引导安装并连接 Tailscale。"),
                ("Page.StreamingHost.Subtitle", "无需离开 PiuLink 即可在本机管理 Sunshine。"),
                ("Page.Displays.Subtitle", "选择用于远程会话的实体或虚拟屏幕。"),
                ("Page.VirtualDisplay.Subtitle", "新建串流屏幕或安装可信虚拟显示驱动。"),
                ("Page.WakeOnLan.Subtitle", "查看本机 MAC 地址并检查 Windows 网络唤醒状态。"),
                ("Page.AdvancedSettings.Subtitle", "管理终端访问和完整串流配置等专家级控制。"),
                ("Page.Diagnostics.Subtitle", "查看主机详细的只读健康检查。")),
            ["zh-TW"] = Dictionary(
                ("Section.Address", "區域網路與組網位址"),
                ("Section.Native", "用戶端邀請"),
                ("Section.Browser", "瀏覽器存取"),
                ("Section.BrowserHint", "一次性連結，可檢視桌面並使用鍵盤滑鼠。"),
                ("Section.Services", "網路與串流服務"),
                ("Section.Device", "本機與顯示卡"),
                ("Section.NetworkSetup", "安裝與連線 Tailscale"),
                ("Section.NetworkOptions", "Tailscale 連線設定"),
                ("Section.Microphone", "虛擬麥克風"),
                ("Section.Terminal", "遠端終端機（SSH）"),
                ("Section.Configuration", "Sunshine 設定編輯器"),
                ("Section.Recovery", "桌面配置與視窗復原"),
                ("Section.Driver", "安裝虛擬顯示驅動程式"),
                ("Section.WindowSafety", "視窗復原"),
                ("Section.WakeSetup", "設定網路喚醒"),
                ("Section.NoAddress", "尚未偵測到可用位址"),
                ("Common.Host", "主機端"), ("Nav.Section", "主機"), ("Nav.Overview", "總覽"),
                ("Nav.PrivateNetwork", "私人網路"), ("Nav.StreamingHost", "串流主機"),
                ("Nav.Displays", "顯示器"), ("Nav.VirtualDisplay", "虛擬顯示器"),
                ("Nav.WakeOnLan", "遠端喚醒"), ("Nav.Diagnostics", "診斷"), ("Sidebar.FreeHostCompanion", "免費主機端"),
                ("Language.Label", "語言"), ("Status.Checking", "正在檢查…"),
                ("Toolbar.RefreshDiagnostics", "重新整理診斷"),
                ("Page.Overview.Subtitle", "連線到這台電腦所需的一切。"),
                ("Page.PrivateNetwork.Subtitle", "依照引導安裝並連線 Tailscale。"),
                ("Page.StreamingHost.Subtitle", "無需離開 PiuLink 即可在本機管理 Sunshine。"),
                ("Page.Displays.Subtitle", "選擇遠端工作階段使用的實體或虛擬螢幕。"),
                ("Page.VirtualDisplay.Subtitle", "建立串流螢幕或安裝受信任的虛擬顯示驅動程式。"),
                ("Page.WakeOnLan.Subtitle", "查看本機 MAC 位址並檢查 Windows 網路喚醒狀態。"),
                ("Page.Diagnostics.Subtitle", "檢視主機詳細的唯讀健康檢查。")),
            ["ru-RU"] = Dictionary(
                ("Section.Address", "Сетевые адреса"),
                ("Section.Native", "Приглашение для клиента"),
                ("Section.Browser", "Браузер"),
                ("Section.BrowserHint", "Одноразовый доступ к рабочему столу с клавиатурой и мышью."),
                ("Section.Services", "Сеть и трансляция"),
                ("Section.Device", "Компьютер и видеокарты"),
                ("Section.NetworkSetup", "Установка и подключение Tailscale"),
                ("Section.NetworkOptions", "Настройки Tailscale"),
                ("Section.Microphone", "Виртуальный микрофон"),
                ("Section.Terminal", "Удалённый терминал (SSH)"),
                ("Section.Configuration", "Конфигурация Sunshine"),
                ("Section.Recovery", "Восстановление рабочего стола и окон"),
                ("Section.Driver", "Установка драйвера дисплея"),
                ("Section.WindowSafety", "Восстановление окон"),
                ("Section.WakeSetup", "Настройка Wake-on-LAN"),
                ("Section.NoAddress", "Нет доступных адресов"),
                ("Common.Host", "Хост"), ("Nav.Section", "ХОСТ"), ("Nav.Overview", "Обзор"),
                ("Nav.PrivateNetwork", "Частная сеть"), ("Nav.StreamingHost", "Хост трансляции"),
                ("Nav.Displays", "Дисплеи"), ("Nav.VirtualDisplay", "Виртуальный дисплей"),
                ("Nav.Diagnostics", "Диагностика"), ("Sidebar.FreeHostCompanion", "Бесплатный хост-компаньон"),
                ("Language.Label", "Язык"), ("Status.Checking", "Проверка…"),
                ("Toolbar.RefreshDiagnostics", "Обновить диагностику"),
                ("Page.Overview.Subtitle", "Всё необходимое для подключения к этому ПК."),
                ("Page.PrivateNetwork.Subtitle", "Установите и подключите Tailscale по инструкции."),
                ("Page.StreamingHost.Subtitle", "Управляйте Sunshine локально, не выходя из PiuLink."),
                ("Page.Displays.Subtitle", "Выберите физический или виртуальный экран для удалённых сеансов."),
                ("Page.VirtualDisplay.Subtitle", "Создайте экран для трансляции или установите доверенный драйвер."),
                ("Page.Diagnostics.Subtitle", "Подробная проверка состояния хоста без внесения изменений.")),
            ["es-419"] = Dictionary(
                ("Section.Address", "Direcciones de red"),
                ("Section.Native", "Invitación para clientes"),
                ("Section.Browser", "Navegador web"),
                ("Section.BrowserHint", "Acceso de un solo uso al escritorio con teclado y ratón."),
                ("Section.Services", "Red y transmisión"),
                ("Section.Device", "Equipo y gráficos"),
                ("Section.NetworkSetup", "Instalar y conectar Tailscale"),
                ("Section.NetworkOptions", "Preferencias de Tailscale"),
                ("Section.Microphone", "Micrófono virtual"),
                ("Section.Terminal", "Terminal remoto (SSH)"),
                ("Section.Configuration", "Configuración de Sunshine"),
                ("Section.Recovery", "Recuperar escritorio y ventanas"),
                ("Section.Driver", "Instalar controlador de pantalla"),
                ("Section.WindowSafety", "Recuperar ventanas"),
                ("Section.WakeSetup", "Configurar Wake-on-LAN"),
                ("Section.NoAddress", "Sin dirección accesible"),
                ("Common.Host", "Host"), ("Nav.Section", "HOST"), ("Nav.Overview", "Resumen"),
                ("Nav.PrivateNetwork", "Red privada"), ("Nav.StreamingHost", "Host de streaming"),
                ("Nav.Displays", "Pantallas"), ("Nav.VirtualDisplay", "Pantalla virtual"),
                ("Nav.Diagnostics", "Diagnóstico"), ("Sidebar.FreeHostCompanion", "Complemento gratuito para host"),
                ("Language.Label", "Idioma"), ("Status.Checking", "Comprobando…"),
                ("Toolbar.RefreshDiagnostics", "Actualizar diagnóstico"),
                ("Page.Overview.Subtitle", "Todo lo necesario para conectarte a esta PC."),
                ("Page.PrivateNetwork.Subtitle", "Instala y conecta Tailscale con pasos guiados."),
                ("Page.StreamingHost.Subtitle", "Administra Sunshine localmente sin salir de PiuLink."),
                ("Page.Displays.Subtitle", "Elige una pantalla física o virtual para las sesiones remotas."),
                ("Page.VirtualDisplay.Subtitle", "Crea una pantalla de streaming o instala su controlador de confianza."),
                ("Page.Diagnostics.Subtitle", "Una comprobación detallada y de solo lectura del host.")),
            ["pt-BR"] = Dictionary(
                ("Section.Address", "Endereços de rede"),
                ("Section.Native", "Convite para clientes"),
                ("Section.Browser", "Navegador"),
                ("Section.BrowserHint", "Acesso único à área de trabalho com teclado e mouse."),
                ("Section.Services", "Rede e transmissão"),
                ("Section.Device", "Computador e gráficos"),
                ("Section.NetworkSetup", "Instalar e conectar o Tailscale"),
                ("Section.NetworkOptions", "Preferências do Tailscale"),
                ("Section.Microphone", "Microfone virtual"),
                ("Section.Terminal", "Terminal remoto (SSH)"),
                ("Section.Configuration", "Configuração do Sunshine"),
                ("Section.Recovery", "Restaurar área de trabalho e janelas"),
                ("Section.Driver", "Instalar driver de vídeo"),
                ("Section.WindowSafety", "Restaurar janelas"),
                ("Section.WakeSetup", "Configurar Wake-on-LAN"),
                ("Section.NoAddress", "Nenhum endereço acessível"),
                ("Common.Host", "Host"), ("Nav.Section", "HOST"), ("Nav.Overview", "Visão geral"),
                ("Nav.PrivateNetwork", "Rede privada"), ("Nav.StreamingHost", "Host de streaming"),
                ("Nav.Displays", "Telas"), ("Nav.VirtualDisplay", "Tela virtual"),
                ("Nav.Diagnostics", "Diagnóstico"), ("Sidebar.FreeHostCompanion", "Companheiro de host gratuito"),
                ("Language.Label", "Idioma"), ("Status.Checking", "Verificando…"),
                ("Toolbar.RefreshDiagnostics", "Atualizar diagnóstico"),
                ("Page.Overview.Subtitle", "Tudo o que você precisa para se conectar a este PC."),
                ("Page.PrivateNetwork.Subtitle", "Instale e conecte o Tailscale com etapas guiadas."),
                ("Page.StreamingHost.Subtitle", "Gerencie o Sunshine localmente sem sair do PiuLink."),
                ("Page.Displays.Subtitle", "Escolha uma tela física ou virtual para sessões remotas."),
                ("Page.VirtualDisplay.Subtitle", "Crie uma tela de streaming ou instale seu driver confiável."),
                ("Page.Diagnostics.Subtitle", "Uma verificação detalhada e somente leitura do host.")),
            ["de-DE"] = Dictionary(
                ("Section.Address", "Netzwerkadressen"),
                ("Section.Native", "Einladung für Clients"),
                ("Section.Browser", "Webbrowser"),
                ("Section.BrowserHint", "Einmaliger Desktopzugriff mit Tastatur und Maus."),
                ("Section.Services", "Netzwerk- und Streamingdienste"),
                ("Section.Device", "Computer und Grafikkarten"),
                ("Section.NetworkSetup", "Tailscale einrichten"),
                ("Section.NetworkOptions", "Tailscale-Einstellungen"),
                ("Section.Microphone", "Virtuelles Mikrofon"),
                ("Section.Terminal", "Remote-Terminal (SSH)"),
                ("Section.Configuration", "Sunshine-Konfiguration"),
                ("Section.Recovery", "Desktop und Fenster wiederherstellen"),
                ("Section.Driver", "Anzeigetreiber installieren"),
                ("Section.WindowSafety", "Fenster wiederherstellen"),
                ("Section.WakeSetup", "Wake-on-LAN einrichten"),
                ("Section.NoAddress", "Keine erreichbare Adresse"),
                ("Common.Host", "Host"), ("Nav.Section", "HOST"), ("Nav.Overview", "Übersicht"),
                ("Nav.PrivateNetwork", "Privates Netzwerk"), ("Nav.StreamingHost", "Streaming-Host"),
                ("Nav.Displays", "Bildschirme"), ("Nav.VirtualDisplay", "Virtueller Bildschirm"),
                ("Nav.Diagnostics", "Diagnose"), ("Sidebar.FreeHostCompanion", "Kostenlose Host-Begleitapp"),
                ("Language.Label", "Sprache"), ("Status.Checking", "Wird geprüft…"),
                ("Toolbar.RefreshDiagnostics", "Diagnose aktualisieren"),
                ("Page.Overview.Subtitle", "Alles, was du für die Verbindung mit diesem PC brauchst."),
                ("Page.PrivateNetwork.Subtitle", "Tailscale mit geführten Schritten installieren und verbinden."),
                ("Page.StreamingHost.Subtitle", "Sunshine lokal verwalten, ohne PiuLink zu verlassen."),
                ("Page.Displays.Subtitle", "Einen physischen oder virtuellen Bildschirm für Remotesitzungen wählen."),
                ("Page.VirtualDisplay.Subtitle", "Einen Streaming-Bildschirm erstellen oder den vertrauenswürdigen Treiber installieren."),
                ("Page.Diagnostics.Subtitle", "Eine detaillierte, schreibgeschützte Zustandsprüfung des Hosts.")),
            ["ja-JP"] = Dictionary(
                ("Section.Address", "ネットワークアドレス"),
                ("Section.Native", "クライアントの招待"),
                ("Section.Browser", "ブラウザー接続"),
                ("Section.BrowserHint", "一度限りのリンクでデスクトップを表示し、キーボードとマウスを操作します。"),
                ("Section.Services", "ネットワークと配信サービス"),
                ("Section.Device", "PC とグラフィックス"),
                ("Section.NetworkSetup", "Tailscale の導入と接続"),
                ("Section.NetworkOptions", "Tailscale の設定"),
                ("Section.Microphone", "仮想マイク"),
                ("Section.Terminal", "リモート端末（SSH）"),
                ("Section.Configuration", "Sunshine の設定"),
                ("Section.Recovery", "デスクトップとウィンドウの復元"),
                ("Section.Driver", "ディスプレイドライバーの導入"),
                ("Section.WindowSafety", "ウィンドウの復元"),
                ("Section.WakeSetup", "Wake-on-LAN の設定"),
                ("Section.NoAddress", "使用可能なアドレスがありません"),
                ("Common.Host", "ホスト"), ("Nav.Section", "ホスト"), ("Nav.Overview", "概要"),
                ("Nav.PrivateNetwork", "プライベートネットワーク"), ("Nav.StreamingHost", "ストリーミングホスト"),
                ("Nav.Displays", "ディスプレイ"), ("Nav.VirtualDisplay", "仮想ディスプレイ"),
                ("Nav.Diagnostics", "診断"), ("Sidebar.FreeHostCompanion", "無料ホストコンパニオン"),
                ("Language.Label", "言語"), ("Status.Checking", "確認中…"),
                ("Toolbar.RefreshDiagnostics", "診断を更新"),
                ("Page.Overview.Subtitle", "このPCに接続するために必要なものを確認できます。"),
                ("Page.PrivateNetwork.Subtitle", "ガイドに沿ってTailscaleをインストールし、接続します。"),
                ("Page.StreamingHost.Subtitle", "PiuLinkを離れずにSunshineをローカル管理します。"),
                ("Page.Displays.Subtitle", "リモートセッションで使用する物理または仮想画面を選びます。"),
                ("Page.VirtualDisplay.Subtitle", "ストリーミング画面を作成するか、信頼済みドライバーをインストールします。"),
                ("Page.Diagnostics.Subtitle", "ホストの詳細な読み取り専用ヘルスチェックです。")),
            ["fr-FR"] = Dictionary(
                ("Section.Address", "Adresses réseau"),
                ("Section.Native", "Invitation client"),
                ("Section.Browser", "Navigateur web"),
                ("Section.BrowserHint", "Accès unique au bureau avec clavier et souris."),
                ("Section.Services", "Réseau et diffusion"),
                ("Section.Device", "Ordinateur et cartes graphiques"),
                ("Section.NetworkSetup", "Installer et connecter Tailscale"),
                ("Section.NetworkOptions", "Préférences Tailscale"),
                ("Section.Microphone", "Microphone virtuel"),
                ("Section.Terminal", "Terminal distant (SSH)"),
                ("Section.Configuration", "Configuration Sunshine"),
                ("Section.Recovery", "Restaurer le bureau et les fenêtres"),
                ("Section.Driver", "Installer le pilote vidéo"),
                ("Section.WindowSafety", "Restaurer les fenêtres"),
                ("Section.WakeSetup", "Configurer le réveil réseau"),
                ("Section.NoAddress", "Aucune adresse accessible"),
                ("Common.Host", "Hôte"), ("Nav.Section", "HÔTE"), ("Nav.Overview", "Vue d’ensemble"),
                ("Nav.PrivateNetwork", "Réseau privé"), ("Nav.StreamingHost", "Hôte de streaming"),
                ("Nav.Displays", "Écrans"), ("Nav.VirtualDisplay", "Écran virtuel"),
                ("Nav.Diagnostics", "Diagnostic"), ("Sidebar.FreeHostCompanion", "Compagnon hôte gratuit"),
                ("Language.Label", "Langue"), ("Status.Checking", "Vérification…"),
                ("Toolbar.RefreshDiagnostics", "Actualiser le diagnostic"),
                ("Page.Overview.Subtitle", "Tout ce qu’il faut pour vous connecter à ce PC."),
                ("Page.PrivateNetwork.Subtitle", "Installez et connectez Tailscale étape par étape."),
                ("Page.StreamingHost.Subtitle", "Gérez Sunshine localement sans quitter PiuLink."),
                ("Page.Displays.Subtitle", "Choisissez un écran physique ou virtuel pour les sessions à distance."),
                ("Page.VirtualDisplay.Subtitle", "Créez un écran de streaming ou installez son pilote approuvé."),
                ("Page.Diagnostics.Subtitle", "Un contrôle détaillé et en lecture seule de l’état de l’hôte.")),
            ["pl-PL"] = Dictionary(
                ("Section.Address", "Adresy sieciowe"),
                ("Section.Native", "Zaproszenie klienta"),
                ("Section.Browser", "Przeglądarka"),
                ("Section.BrowserHint", "Jednorazowy dostęp do pulpitu z klawiaturą i myszą."),
                ("Section.Services", "Sieć i transmisja"),
                ("Section.Device", "Komputer i grafika"),
                ("Section.NetworkSetup", "Instalacja i połączenie Tailscale"),
                ("Section.NetworkOptions", "Ustawienia Tailscale"),
                ("Section.Microphone", "Mikrofon wirtualny"),
                ("Section.Terminal", "Terminal zdalny (SSH)"),
                ("Section.Configuration", "Konfiguracja Sunshine"),
                ("Section.Recovery", "Przywracanie pulpitu i okien"),
                ("Section.Driver", "Instalacja sterownika ekranu"),
                ("Section.WindowSafety", "Przywracanie okien"),
                ("Section.WakeSetup", "Konfiguracja Wake-on-LAN"),
                ("Section.NoAddress", "Brak dostępnego adresu"),
                ("Common.Host", "Host"), ("Nav.Section", "HOST"), ("Nav.Overview", "Przegląd"),
                ("Nav.PrivateNetwork", "Sieć prywatna"), ("Nav.StreamingHost", "Host strumieniowania"),
                ("Nav.Displays", "Ekrany"), ("Nav.VirtualDisplay", "Ekran wirtualny"),
                ("Nav.Diagnostics", "Diagnostyka"), ("Sidebar.FreeHostCompanion", "Bezpłatny moduł hosta"),
                ("Language.Label", "Język"), ("Status.Checking", "Sprawdzanie…"),
                ("Toolbar.RefreshDiagnostics", "Odśwież diagnostykę"),
                ("Page.Overview.Subtitle", "Wszystko, czego potrzebujesz, aby połączyć się z tym komputerem."),
                ("Page.PrivateNetwork.Subtitle", "Zainstaluj i połącz Tailscale zgodnie z instrukcją."),
                ("Page.StreamingHost.Subtitle", "Zarządzaj Sunshine lokalnie bez opuszczania PiuLink."),
                ("Page.Displays.Subtitle", "Wybierz ekran fizyczny lub wirtualny dla sesji zdalnych."),
                ("Page.VirtualDisplay.Subtitle", "Utwórz ekran do strumieniowania lub zainstaluj zaufany sterownik."),
                ("Page.Diagnostics.Subtitle", "Szczegółowa kontrola stanu hosta tylko do odczytu.")),
            ["ko-KR"] = Dictionary(
                ("Section.Address", "네트워크 주소"),
                ("Section.Native", "클라이언트 초대"),
                ("Section.Browser", "웹 브라우저"),
                ("Section.BrowserHint", "일회용 링크로 데스크톱을 보고 키보드와 마우스를 사용합니다."),
                ("Section.Services", "네트워크 및 스트리밍 서비스"),
                ("Section.Device", "컴퓨터 및 그래픽"),
                ("Section.NetworkSetup", "Tailscale 설치 및 연결"),
                ("Section.NetworkOptions", "Tailscale 설정"),
                ("Section.Microphone", "가상 마이크"),
                ("Section.Terminal", "원격 터미널 (SSH)"),
                ("Section.Configuration", "Sunshine 구성"),
                ("Section.Recovery", "바탕 화면 및 창 복원"),
                ("Section.Driver", "디스플레이 드라이버 설치"),
                ("Section.WindowSafety", "창 복원"),
                ("Section.WakeSetup", "네트워크 깨우기 설정"),
                ("Section.NoAddress", "연결 가능한 주소 없음"),
                ("Common.Host", "호스트"), ("Nav.Section", "호스트"), ("Nav.Overview", "개요"),
                ("Nav.PrivateNetwork", "사설 네트워크"), ("Nav.StreamingHost", "스트리밍 호스트"),
                ("Nav.Displays", "디스플레이"), ("Nav.VirtualDisplay", "가상 디스플레이"),
                ("Nav.Diagnostics", "진단"), ("Sidebar.FreeHostCompanion", "무료 호스트 도우미"),
                ("Language.Label", "언어"), ("Status.Checking", "확인 중…"),
                ("Toolbar.RefreshDiagnostics", "진단 새로 고침"),
                ("Page.Overview.Subtitle", "이 PC에 연결하는 데 필요한 모든 항목입니다."),
                ("Page.PrivateNetwork.Subtitle", "안내에 따라 Tailscale을 설치하고 연결하세요."),
                ("Page.StreamingHost.Subtitle", "PiuLink를 벗어나지 않고 Sunshine을 로컬에서 관리하세요."),
                ("Page.Displays.Subtitle", "원격 세션에 사용할 물리 또는 가상 화면을 선택하세요."),
                ("Page.VirtualDisplay.Subtitle", "스트리밍 화면을 만들거나 신뢰할 수 있는 드라이버를 설치하세요."),
                ("Page.Diagnostics.Subtitle", "호스트의 상세한 읽기 전용 상태 검사입니다.")),
            ["tr-TR"] = Dictionary(
                ("Section.Address", "Ağ adresleri"),
                ("Section.Native", "İstemci daveti"),
                ("Section.Browser", "Web tarayıcısı"),
                ("Section.BrowserHint", "Klavye ve fare ile masaüstüne tek kullanımlık erişim."),
                ("Section.Services", "Ağ ve yayın hizmetleri"),
                ("Section.Device", "Bilgisayar ve grafik bağdaştırıcıları"),
                ("Section.NetworkSetup", "Tailscale kurulumu ve bağlantısı"),
                ("Section.NetworkOptions", "Tailscale tercihleri"),
                ("Section.Microphone", "Sanal mikrofon"),
                ("Section.Terminal", "Uzak terminal (SSH)"),
                ("Section.Configuration", "Sunshine yapılandırması"),
                ("Section.Recovery", "Masaüstü ve pencereleri geri yükle"),
                ("Section.Driver", "Ekran sürücüsünü yükle"),
                ("Section.WindowSafety", "Pencereleri geri yükle"),
                ("Section.WakeSetup", "Wake-on-LAN ayarları"),
                ("Section.NoAddress", "Erişilebilir adres yok"),
                ("Common.Host", "Ana makine"), ("Nav.Section", "ANA MAKİNE"), ("Nav.Overview", "Genel bakış"),
                ("Nav.PrivateNetwork", "Özel ağ"), ("Nav.StreamingHost", "Yayın ana makinesi"),
                ("Nav.Displays", "Ekranlar"), ("Nav.VirtualDisplay", "Sanal ekran"),
                ("Nav.Diagnostics", "Tanılama"), ("Sidebar.FreeHostCompanion", "Ücretsiz ana makine yardımcısı"),
                ("Language.Label", "Dil"), ("Status.Checking", "Kontrol ediliyor…"),
                ("Toolbar.RefreshDiagnostics", "Tanılamayı yenile"),
                ("Page.Overview.Subtitle", "Bu bilgisayara bağlanmak için gereken her şey."),
                ("Page.PrivateNetwork.Subtitle", "Yönlendirmeli adımlarla Tailscale’i kurun ve bağlayın."),
                ("Page.StreamingHost.Subtitle", "PiuLink’ten ayrılmadan Sunshine’ı yerel olarak yönetin."),
                ("Page.Displays.Subtitle", "Uzak oturumlar için fiziksel veya sanal bir ekran seçin."),
                ("Page.VirtualDisplay.Subtitle", "Bir yayın ekranı oluşturun veya güvenilir sürücüsünü kurun."),
                ("Page.Diagnostics.Subtitle", "Ana makinenin ayrıntılı, salt okunur sistem denetimi."))
        };

    private string _selectedCode = string.Empty;
    private string _effectiveCode = FallbackCode;

    private LocalizationService()
    {
    }

    public static LocalizationService Instance { get; } = new();

    public IReadOnlyList<LanguageOption> SupportedLanguages { get; } =
    [
        new("system", "System / 跟随系统"),
        new("en-US", "English"),
        new("zh-CN", "中文（简体）"),
        new("zh-TW", "繁體中文"),
        new("ru-RU", "Русский"),
        new("es-419", "Español"),
        new("pt-BR", "Português (Brasil)"),
        new("de-DE", "Deutsch"),
        new("ja-JP", "日本語"),
        new("fr-FR", "Français"),
        new("pl-PL", "Polski"),
        new("ko-KR", "한국어"),
        new("tr-TR", "Türkçe")
    ];

    public string SelectedCode => _selectedCode;

    public string EffectiveCode => _effectiveCode;

    public string this[string key]
    {
        get
        {
            if (Translations.TryGetValue(_effectiveCode, out var localized) &&
                localized.TryGetValue(key, out var value))
            {
                return value;
            }

            return English.TryGetValue(key, out var fallback) ? fallback : key;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public void SetLanguage(string? requestedCode)
    {
        var selected = SupportedLanguages.Any(option =>
            string.Equals(option.Code, requestedCode, StringComparison.OrdinalIgnoreCase))
            ? requestedCode!
            : "system";
        var effective = string.Equals(selected, "system", StringComparison.OrdinalIgnoreCase)
            ? MatchSupportedCulture(CultureInfo.InstalledUICulture)
            : selected;

        if (string.Equals(_selectedCode, selected, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_effectiveCode, effective, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _selectedCode = selected;
        _effectiveCode = effective;
        var culture = CultureInfo.GetCultureInfo(effective);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        OnPropertyChanged("Item[]");
        OnPropertyChanged(nameof(SelectedCode));
        OnPropertyChanged(nameof(EffectiveCode));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Translate(string key) => this[key];

    public string TranslateLiteral(string source) =>
        LiteralTranslations.Translate(source, _effectiveCode);

    private static string MatchSupportedCulture(CultureInfo culture)
    {
        var name = culture.Name;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return name.Contains("TW", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("HK", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("MO", StringComparison.OrdinalIgnoreCase) ||
                   culture.EnglishName.Contains("Traditional", StringComparison.OrdinalIgnoreCase)
                ? "zh-TW"
                : "zh-CN";
        }

        return culture.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "ru" => "ru-RU",
            "es" => "es-419",
            "pt" => "pt-BR",
            "de" => "de-DE",
            "ja" => "ja-JP",
            "fr" => "fr-FR",
            "pl" => "pl-PL",
            "ko" => "ko-KR",
            "tr" => "tr-TR",
            _ => FallbackCode
        };
    }

    private static IReadOnlyDictionary<string, string> Dictionary(
        params (string Key, string Value)[] entries) =>
        entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
