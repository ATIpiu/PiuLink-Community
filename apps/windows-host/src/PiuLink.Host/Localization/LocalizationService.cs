using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace PiuLink.Host.Localization;

public sealed record LanguageOption(string Code, string DisplayName);

public sealed class LocalizationService : INotifyPropertyChanged
{
    private const string FallbackCode = "en-US";
    private static readonly IReadOnlyDictionary<string, string> English = Dictionary(
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
