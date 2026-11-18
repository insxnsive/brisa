"""Source contract checks for event-to-service bindings and Home's UI budget.
Runtime transport and loaded-window checks complement these guards.
"""
from pathlib import Path
import re
import unittest

REPO = Path(__file__).resolve().parents[2]
ROOT = REPO / 'src/Brisa'


class UiContracts(unittest.TestCase):
    def test_primary_action_forwards_verification_answers(self):
        code = (ROOT / 'MainWindow.xaml.cs').read_text(encoding='utf-8')
        handler = code.split('private async void Primary_Click', 1)[1].split('public async Task<CommandResult>', 1)[0]
        self.assertRegex(handler, r'humanVerificationToken\s*=\s*token')
        self.assertRegex(handler, r'humanVerificationMethod\s*=\s*method')

    def test_native_manifest_uses_supported_dpi_elements(self):
        import xml.etree.ElementTree as ET
        manifest = ET.parse(ROOT / 'app.manifest')
        legacy = manifest.find('.//{http://schemas.microsoft.com/SMI/2005/WindowsSettings}dpiAware')
        modern = manifest.find('.//{http://schemas.microsoft.com/SMI/2016/WindowsSettings}dpiAwareness')
        self.assertEqual(legacy.text, 'true/pm')
        self.assertEqual(modern.text, 'PerMonitorV2')

    def test_shared_styles_preserve_microsoft_fluent_templates(self):
        import xml.etree.ElementTree as ET
        tree = ET.parse(ROOT / 'Themes/NativeTheme.xaml')
        ns = {'w': 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'}
        styles = {s.get('TargetType'): s for s in tree.findall('w:Style', ns) if s.get('{http://schemas.microsoft.com/winfx/2006/xaml}Key') is None}
        for control in ('Button', 'TextBox', 'PasswordBox', 'CheckBox', 'RadioButton', 'ListBox', 'ListBoxItem', 'ComboBox', 'Window'):
            self.assertEqual(styles[control].get('BasedOn'), '{StaticResource Default' + control + 'Style}')
            self.assertFalse(any(s.get('Property') == 'Template' for s in styles[control].findall('w:Setter', ns)))

    def test_appearance_harness_never_uses_production_startup(self):
        code = (REPO / 'tests/Brisa.Appearance.Tests/Program.cs').read_text(encoding='utf-8')
        self.assertNotIn('new App(', code)
        self.assertNotIn('new BackendClient(', code)
        self.assertIn('new Application', code)
        self.assertIn('new SettingsStore(root, false)', code)

    def test_bootstrap_never_auto_applies_before_safe_exit(self):
        code = (ROOT / 'App.xaml.cs').read_text(encoding='utf-8')
        self.assertIn('.SetAutoApplyOnStartup(false)', code)

    def test_isolated_launch_never_cleans_real_verification_profiles(self):
        code = (ROOT / 'App.xaml.cs').read_text(encoding='utf-8')
        self.assertIn('if (!isolated) Security.VerificationProfileStore.Cleanup();', code)

    def test_tray_uses_the_packaged_product_icon(self):
        code = (ROOT / 'Services/TrayService.cs').read_text(encoding='utf-8')
        self.assertIn('Icon.ExtractAssociatedIcon', code)

    def test_home_does_not_expose_advanced_tools(self):
        xaml = (ROOT / 'MainWindow.xaml').read_text(encoding='utf-8')
        self.assertNotIn('x:Name="AdvancedButton"', xaml)
        self.assertIn('x:Name="PrimaryButton"', xaml)

    def test_native_manifest_stays_unelevated(self):
        # Autostart runs this binary at logon. Requiring elevation there would either show a
        # UAC prompt nobody asked for or silently fail, so the manifest must stay asInvoker.
        manifest = (ROOT / 'app.manifest').read_text(encoding='utf-8')
        self.assertIn('level="asInvoker"', manifest)
        self.assertNotIn('requireAdministrator', manifest)

    def test_startup_flag_is_written_quoted_and_read_case_insensitively(self):
        settings = (ROOT / 'Services/SettingsStore.cs').read_text(encoding='utf-8')
        self.assertRegex(settings, r'\\"{Environment\.ProcessPath}\\" --startup')
        app = (ROOT / 'App.xaml.cs').read_text(encoding='utf-8')
        self.assertIn('e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase)', app)
        # A logon launch must not jump into Account, or the limited-access Home is never seen.
        self.assertIn('InitializeAsync(openAccount: !startupLaunch)', app)


    def test_tray_and_updater_start_after_first_paint(self):
        # Neither is needed to paint the window: both are created from an idle dispatcher callback.
        app = (ROOT / 'App.xaml.cs').read_text(encoding='utf-8')
        show = app.index('window.Show();')
        self.assertLess(show, app.index('new TrayService('))
        self.assertLess(show, app.index('_updates?.Start()'))
        self.assertNotIn('_updates.Start()', app)
        self.assertIn('DispatcherPriority.ContextIdle', app)
        self.assertIn('initialDelay: UpdateService.DefaultStartupDelay', app)
        updates = (ROOT / 'Services/UpdateService.cs').read_text(encoding='utf-8')
        self.assertIn('Lazy<UpdateManager?>', updates)


def method_body(code, name):
    """Text of one method, from its signature to the closing brace at class level."""
    match = re.search(r'\n    (?:public|private)[^\n]* ' + re.escape(name) + r'\(', code)
    assert match, name
    start = match.start() + 1
    return code[start:code.index('\n    }\n', start) + 7]


class NotificationContracts(unittest.TestCase):
    ANCHOR_URI = 'https://brisa-notify.insxnsive.works/v1/notifications.json'
    ANCHOR_SPKI = 'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEA81c4Qy7srHgHtIMuXiuUkMaAajAjnlD66i2Gahpc4u9UiNWVF0i6YqMpJtfI0gUv+3JAitsejBKukMvEWeFig=='

    def test_notifications_start_after_first_paint_in_the_idle_callback(self):
        app = (ROOT / 'App.xaml.cs').read_text(encoding='utf-8')
        show = app.index('window.Show();')
        self.assertLess(show, app.index('_notifications?.Start()'))
        callback = app[app.index('Dispatcher.InvokeAsync(() =>'):app.index('DispatcherPriority.ContextIdle')]
        self.assertIn('_notifications?.Start()', callback)
        self.assertIn('notifications: _notifications', app[:show])
        # Creating the service must not start it: Start() is what reads disk and later the network.
        self.assertNotIn('_notifications.Start()', app)

    def test_isolated_launches_never_construct_the_http_client(self):
        app = (ROOT / 'App.xaml.cs').read_text(encoding='utf-8')
        create = method_body(app, 'CreateNotifications')
        self.assertLess(create.index('if (isolated)'), create.index('new HttpNotificationFeedClient('))
        isolated_branch = create[create.index('if (isolated)'):create.index('new HttpNotificationFeedClient(')]
        self.assertIn('return', isolated_branch)
        self.assertIn('NullNotificationFeedClient', isolated_branch)
        self.assertIn('--notifications-fixture=', isolated_branch)
        self.assertEqual(app.count('new HttpNotificationFeedClient('), 1)
        sites = [p.name for p in ROOT.rglob('*.cs')
                 if not {'obj', 'bin'} & set(p.parts) and 'new HttpNotificationFeedClient(' in p.read_text(encoding='utf-8')]
        self.assertEqual(sites, ['App.xaml.cs'])

    def test_feed_client_refuses_redirects_and_cookies_and_has_one_endpoint(self):
        service = (ROOT / 'Services/NotificationService.cs').read_text(encoding='utf-8')
        self.assertIn('AllowAutoRedirect = false', service)
        self.assertIn('UseCookies = false', service)
        self.assertIn('NotificationVerifier.MaxEnvelopeBytes', service)
        self.assertEqual(re.findall(r'"https?://[^"]*"', service), [])
        anchors = (ROOT / 'Security/NotificationTrustAnchors.cs').read_text(encoding='utf-8')
        self.assertIn('"' + self.ANCHOR_URI + '"', anchors)
        self.assertIn('"brisa-2026-k1"', anchors)
        self.assertIn(self.ANCHOR_SPKI, anchors)

    def test_track_one_methods_do_not_know_about_notifications(self):
        code = (ROOT / 'MainWindow.xaml.cs').read_text(encoding='utf-8')
        for name in ('InitializeAsync', 'StartAutoConnect', 'StartRouteScan', 'Primary_Click', 'RunPrimaryMutationAsync',
                     'TrackProgressAsync', 'ExecuteWithVerificationAsync', 'Route_Click'):
            self.assertNotRegex(method_body(code, name), r'[Nn]otification', name)
        self.assertRegex(code, r'bool autoConnect = false, INotificationService\? notifications = null\)\n')

    def test_bell_shares_the_back_button_slot_and_the_popup_is_configured(self):
        import xml.etree.ElementTree as ET
        x = '{http://schemas.microsoft.com/winfx/2006/xaml}'
        named = {e.get(x + 'Name'): e for e in ET.parse(ROOT / 'MainWindow.xaml').iter() if e.get(x + 'Name')}
        host, bell, popup, back = named['NotificationsHost'], named['NotificationsButton'], named['NotificationsPopup'], named['BackButton']
        self.assertIsNone(host.get('Grid.Column'))
        self.assertIsNone(back.get('Grid.Column'))
        self.assertEqual(host.get('Visibility'), 'Collapsed')
        self.assertEqual(bell.get('Style'), '{StaticResource ToolbarAction}')
        for attribute, value in (('AllowsTransparency', 'True'), ('StaysOpen', 'False'), ('Width', '340'), ('MaxHeight', '390')):
            self.assertEqual(popup.get(attribute), value, attribute)
        xaml = (ROOT / 'MainWindow.xaml').read_text(encoding='utf-8')
        self.assertIn('Text="&#xEA8F;"', xaml)
        self.assertIn('{DynamicResource AccentButtonBackground}', xaml)
        self.assertIn('{DynamicResource AccentButtonForeground}', xaml)
        code = (ROOT / 'MainWindow.xaml.cs').read_text(encoding='utf-8')
        for name in ('ShowPage', 'OnClosing', 'BeginUiMutation'):
            self.assertIn('CloseNotifications();', method_body(code, name), name)
        self.assertIn('UpdateNotificationsHost();', method_body(code, 'ShowPage'))
        self.assertIn('UpdateNotificationsHost();', method_body(code, 'ReturnHomeCoreAsync'))
        self.assertIn('_notifications.DisposeAsync()', method_body(code, 'OnClosing'))

    def test_new_styles_have_keys_and_the_popover_renders_plain_text_only(self):
        import xml.etree.ElementTree as ET
        x = '{http://schemas.microsoft.com/winfx/2006/xaml}'
        ns = '{http://schemas.microsoft.com/winfx/2006/xaml/presentation}'
        for name in ('Controls/NotificationsPopover.xaml', 'MainWindow.xaml', 'SettingsView.xaml', 'RouteView.xaml'):
            for style in ET.parse(ROOT / name).iter(ns + 'Style'):
                self.assertIsNotNone(style.get(x + 'Key'), name + ' has a style without x:Key')
        popover = ''.join((ROOT / 'Controls' / f).read_text(encoding='utf-8') for f in ('NotificationsPopover.xaml', 'NotificationsPopover.xaml.cs'))
        for banned in ('WebView2', 'WebBrowser', 'Hyperlink', 'RichTextBox', 'XamlReader', 'InnerHtml', 'Process.Start('):
            self.assertNotIn(banned, popover, banned)
        self.assertIn('NotificationUrlPolicy.TryOpen', popover)

    def test_polish_rules_stay_in_place(self):
        popover = (ROOT / 'Controls/NotificationsPopover.xaml.cs').read_text(encoding='utf-8')
        # Opening the popover must never put focus on "Mark all read".
        self.assertRegex(popover, r'public void FocusFirst\(\) => Keyboard\.Focus\(AllPill\);')
        code = (ROOT / 'MainWindow.xaml.cs').read_text(encoding='utf-8')
        handler = code[code.index('_languageChanged = (_, _) =>'):code.index('Localization.LocalizationManager.Current.PropertyChanged += _languageChanged;')]
        self.assertIn('ApplyNotificationBadge(_notifications.Snapshot)', handler)
        service = (ROOT / 'Services/NotificationService.cs').read_text(encoding='utf-8')
        self.assertIn('StartsWith("W/", StringComparison.Ordinal)', service)
        policy = (ROOT / 'Security/NotificationUrlPolicy.cs').read_text(encoding='utf-8')
        self.assertIn(".Contains('@')", policy)
        verifier = (ROOT / 'Security/NotificationVerifier.cs').read_text(encoding='utf-8')
        self.assertIn('raw.Trim() != raw || ContainsForbiddenCodepoint(raw, multiline)', verifier)
        privacy = (REPO / 'docs/privacy.md').read_text(encoding='utf-8')
        self.assertIn('notifications-state.json', privacy)
        self.assertIn('brisa-notify.insxnsive.works/v1/notifications.json', privacy)

    def test_settings_motion_and_navigation_contracts(self):
        code = (ROOT / 'MainWindow.xaml.cs').read_text(encoding='utf-8')
        self.assertNotIn('await RefreshSnapshotAsync', method_body(code, 'ReturnHomeCoreAsync'))
        settings_back = method_body(code, 'GoBackAsync').split(
            'if (origin == AdvancedOrigin.Settings)', 1)[1].split('if (origin == AdvancedOrigin.Account)', 1)[0]
        self.assertIn('SwapPageAsync(', settings_back)
        self.assertNotIn('ReturnHomeAsync(', settings_back)
        settings = (ROOT / 'SettingsView.xaml').read_text(encoding='utf-8')
        self.assertIn('x:Name="SavedText"', settings)
        self.assertIn('x:Key="AnimatedCheck"', settings)
        self.assertIn('x:Key="ThemeSegment"', settings)

    def test_no_new_packages_and_the_state_file_name_is_pinned(self):
        project = (ROOT / 'Brisa.csproj').read_text(encoding='utf-8')
        self.assertEqual(sorted(re.findall(r'<PackageReference Include="([^"]+)"', project)), ['Microsoft.Web.WebView2', 'Velopack'])
        self.assertIn('"notifications-state.json"', (ROOT / 'Services/NotificationService.cs').read_text(encoding='utf-8'))

    def test_notification_strings_exist_in_both_languages_without_dashes_or_emoji(self):
        tables = []
        for name in ('Strings.resx', 'Strings.pt-BR.resx'):
            text = (ROOT / 'Localization' / name).read_text(encoding='utf-8')
            tables.append(dict(re.findall(r'<data name="(Notifications[A-Za-z]*)"[^>]*><value>(.*?)</value>', text)))
        self.assertEqual(set(tables[0]), set(tables[1]))
        self.assertGreaterEqual(len(tables[0]), 20)
        dashes_and_quotes = {chr(c) for c in (0x2013, 0x2014, 0x2018, 0x2019, 0x201C, 0x201D, 0x2026)}
        for table in tables:
            for key, value in table.items():
                self.assertTrue(value.strip(), key)
                self.assertFalse(dashes_and_quotes & set(value), key)
                self.assertFalse(any(ord(ch) >= 0x1F000 or 0x2600 <= ord(ch) <= 0x27BF for ch in value), key)
                self.assertFalse(value.endswith('.') or '!' in value, key)


class ElevationAndStartupContracts(unittest.TestCase):
    def test_primary_click_restart_hook_is_one_line_right_after_the_startup_check(self):
        code = (ROOT / 'MainWindow.xaml.cs').read_text(encoding='utf-8')
        body = method_body(code, 'Primary_Click')
        hook = 'if (_state.CanRestartElevated) { await (RestartElevatedOverride is not null ? RestartElevatedOverride() : RestartElevatedAsync()); return; }'
        self.assertEqual(body.count(hook), 1)
        self.assertLess(body.index('if (_state.StartupFailed)'), body.index(hook))
        self.assertLess(body.index(hook), body.index('_operation.WaitAsync(0)'))
        self.assertNotRegex(body, r'[Nn]otification')
        elevation = (ROOT / 'MainWindow.Elevation.cs').read_text(encoding='utf-8')
        self.assertIn('internal Func<Task>? RestartElevatedOverride { get; init; }', elevation)
        self.assertIn('Verb = "runas"', elevation)
        self.assertIn('"--expect-user-sid"', elevation)
        self.assertIn('"--wait-for-pid"', elevation)
        self.assertIn('Win32Exception', elevation)
        self.assertLess(elevation.index('ReleaseSingleInstance()'), elevation.index('RequestExit()'))

    def test_main_runs_the_helper_before_velopack_and_keeps_the_updater_rules(self):
        app = (ROOT / 'App.xaml.cs').read_text(encoding='utf-8')
        main = method_body(app, 'Main')
        self.assertLess(main.index('StartupTaskHelper.Flag'), main.index('VelopackApp'))
        self.assertIn('Environment.Exit(StartupTaskHelper.Run(args))', main)
        self.assertIn('.SetAutoApplyOnStartup(false)', main)
        self.assertIn('.OnBeforeUninstallFastCallback(_ => StartupCleanup.RemoveRunKeyAndTryDeleteTask())', main)
        self.assertNotIn('Mutex', main)

    def test_single_instance_uses_protected_user_only_descriptors_and_no_new_package(self):
        code = (ROOT / 'Services/SingleInstanceActivation.cs').read_text(encoding='utf-8')
        self.assertIn('MutexAcl.Create(', code)
        self.assertIn('EventWaitHandleAcl.Create(', code)
        self.assertEqual(code.count('SetAccessRuleProtection(true, false)'), 2)
        self.assertIn('EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize', code)
        self.assertIn('catch (UnauthorizedAccessException)', code)
        self.assertIn('catch (AbandonedMutexException)', code)
        self.assertIn('NativeMethods.AllowSetForegroundWindow(-1);', code)
        for banned in ('WorldSid', 'AuthenticatedUserSid', 'BuiltinUsersSid', 'Everyone'):
            self.assertNotIn(banned, code)
        native = (ROOT / 'Services/NativeMethods.cs').read_text(encoding='utf-8')
        self.assertIn('[DllImport("user32.dll")]', native)
        project = (ROOT / 'Brisa.csproj').read_text(encoding='utf-8')
        self.assertEqual(sorted(re.findall(r'<PackageReference Include="([^"]+)"', project)), ['Microsoft.Web.WebView2', 'Velopack'])
        app = (ROOT / 'App.xaml.cs').read_text(encoding='utf-8')
        self.assertIn('ReleaseSingleInstance();', method_body(app, 'OnExit'))
        start = method_body(app, 'OnStartup')
        self.assertIn('if (!_instance.IsPrimary)', start)
        self.assertLess(start.index('if (!_instance.IsPrimary)'), start.index('SingleInstanceActivation.SignalPrimary();'))
        self.assertLess(start.index('SingleInstanceActivation.SignalPrimary();'), start.index('Shutdown();', start.index('SignalPrimary')))
        self.assertIn('if (launch.RefuseUser)', start)
        self.assertLess(start.index('if (launch.RefuseUser)'), start.index('new SingleInstanceActivation('))
        registrar = (ROOT / 'Services/StartupRegistrar.cs').read_text(encoding='utf-8')
        self.assertRegex(registrar, r'catch \(Win32Exception ex\) when \(ex\.NativeErrorCode == 1223\) \{ throw new StartupRegistrationCanceledException\(\); \}')
        self.assertIn('Interlocked.Exchange(ref _instance, null)', app)
        self.assertNotIn('new Mutex(', app)

    def test_schtasks_is_run_by_full_path_without_a_shell(self):
        helper = (ROOT / 'Services/StartupTaskHelper.cs').read_text(encoding='utf-8')
        self.assertIn('Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"', helper)
        for required in ('UseShellExecute = false', 'CreateNoWindow = true', 'info.ArgumentList.Add(argument)', 'FileMode.CreateNew',
                         'SecurityElement.Escape', '<Priority>4</Priority>', '<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>',
                         '<RunLevel>HighestAvailable</RunLevel>', '<LogonType>InteractiveToken</LogonType>', '<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>'):
            self.assertIn(required, helper)
        for banned in ('cmd.exe', 'powershell', 'Process.Start("schtasks', 'UseShellExecute = true'):
            self.assertNotIn(banned, helper)
        self.assertEqual(helper.count('UserId>{sid}</UserId>'), 2)

    def test_settings_keep_the_run_literal_add_the_route_field_and_save_maps_each_failure(self):
        settings = (ROOT / 'Services/SettingsStore.cs').read_text(encoding='utf-8')
        self.assertRegex(settings, r'\\"{Environment\.ProcessPath}\\" --startup')
        self.assertIn('string? PreferredRouteCountry = null', settings)
        self.assertIn('IStartupRegistrar? registrar = null', settings)
        view = (ROOT / 'SettingsView.xaml.cs').read_text(encoding='utf-8')
        save = method_body(view, 'Save_Click')
        for exception, key in (('StartupRegistrationCanceledException', 'StartupElevationCanceled'),
                               ('StartupElevationNeedsAdminException', 'StartupElevationNeedsAdmin')):
            self.assertRegex(save, r'catch \(' + exception + r'\) \{[^}]*"' + key + '"')
        self.assertRegex(save, r'catch \{[^}]*"SettingsSaveFailed"')

    def test_elevation_strings_sit_after_their_anchor_in_both_languages(self):
        order = ['StatusAdministratorRequired', 'RestartAsAdministrator', 'ElevationCanceled', 'ElevationWrongUser',
                 'StartupElevationCanceled', 'StartupElevationNeedsAdmin']
        dashes_and_quotes = {chr(c) for c in (0x2013, 0x2014, 0x2018, 0x2019, 0x201C, 0x201D)}
        for name in ('Strings.resx', 'Strings.pt-BR.resx'):
            text = (ROOT / 'Localization' / name).read_text(encoding='utf-8')
            names = re.findall(r'<data name="([^"]+)"', text)
            start = names.index('StatusAdministratorRequired')
            self.assertEqual(names[start:start + len(order)], order, name)
            for key in order[1:] + ['DetailRequiresElevation']:
                value = re.search(r'<data name="' + key + r'"[^>]*><value>(.*?)</value>', text).group(1)
                self.assertTrue(value.strip(), key)
                self.assertFalse(dashes_and_quotes & set(value), key)
                self.assertFalse(any(ord(ch) >= 0x1F000 for ch in value), key)
        english = (ROOT / 'Localization/Strings.resx').read_text(encoding='utf-8')
        self.assertIn('<value>Restart as administrator</value>', english)

    def test_tests_never_touch_the_live_mutex_the_run_key_or_the_real_scheduler(self):
        tests = REPO / 'tests'
        for path in tests.rglob('*.cs'):
            if {'obj', 'bin'} & set(path.parts):
                continue
            text = path.read_text(encoding='utf-8')
            self.assertNotIn('@"Local\\Brisa"', text, path.name)
            self.assertNotIn('Registry.CurrentUser', text, path.name)
            self.assertNotIn('RunSchtasksProcess(', text, path.name)
            self.assertNotIn('WindowsStartupSystem', text, path.name)


class TrayAndRouteContracts(unittest.TestCase):
    def test_tray_menu_is_driven_by_home_and_never_bypasses_its_guards(self):
        tray = (ROOT / 'Services/TrayService.cs').read_text(encoding='utf-8')
        self.assertIn('Icon.ExtractAssociatedIcon', tray)
        self.assertIn('public TrayService(MainWindow window, SettingsStore settings, bool showNotifyIcon = true)', tray)
        self.assertIn('public const int MaxTooltipLength = 63;', tray)
        self.assertIn('Truncate(line, MaxTooltipLength)', tray)
        self.assertEqual(tray.count('window.ShowFromActivation()'), 2)  # Open and double click
        self.assertIn('window.RunPrimaryFromTray()', tray)
        self.assertIn('window.RequestExit()', tray)
        self.assertIn('_primary.Visible = !state.RequiresSignIn && !state.StartupFailed;', tray)
        self.assertIn('_primary.Enabled = _window.CanRunPrimaryFromTray;', tray)
        self.assertIn('state.Phase == ConnectionPhase.Connected', tray)
        dispose = tray[tray.index('public void Dispose()'):]
        for needed in ('_window.HomeStateChanged -= _stateChanged;', 'PropertyChanged -= _languageChanged;', '_menu.Opening -= OnMenuOpening;', '_menu.Dispose();', '_productIcon?.Dispose();', '_icon.Dispose();'):
            self.assertIn(needed, dispose)
        # TaskbarCreated is allowed only for a real icon, and a failure there can never stop the tray.
        body = tray[tray.index('if (showNotifyIcon)'):tray.index('_stateChanged = ')]
        self.assertIn('AllowTaskbarCreated();', body)
        allow = tray[tray.index('internal static bool AllowTaskbarCreated()'):tray.index('public void Dispose()')]
        self.assertIn('try', allow)
        self.assertIn('catch { return false; }', allow)
        native = (ROOT / 'Services/NativeMethods.cs').read_text(encoding='utf-8')
        self.assertIn('RegisterWindowMessage(string name)', native)
        self.assertIn('ChangeWindowMessageFilter(uint message, uint flag)', native)
        self.assertIn('NativeMethods.ChangeWindowMessageFilter(message, 1)', tray)
        app = (ROOT / 'App.xaml.cs').read_text(encoding='utf-8')
        self.assertIn('new TrayService(window, settings)', app)
        self.assertNotIn('showNotifyIcon: false', app)
        self.assertLess(app.index('window.Show();'), app.index('new TrayService('))

    def test_no_test_can_put_a_real_icon_in_the_tray(self):
        for path in (REPO / 'tests').rglob('*.cs'):
            if {'obj', 'bin'} & set(path.parts):
                continue
            text = path.read_text(encoding='utf-8')
            for match in re.finditer(r'new TrayService\(([^;]*)\)', text):
                self.assertIn('showNotifyIcon: false', match.group(1), path.name + ' creates a visible tray icon')

    def test_main_window_exposes_the_tray_hooks_with_the_same_rules_as_home(self):
        code = (ROOT / 'MainWindow.xaml.cs').read_text(encoding='utf-8')
        body = method_body(code, 'SetState')
        self.assertTrue(body.rstrip().endswith('HomeStateChanged?.Invoke(this, state);\n    }'), 'raised last, after Home has finished painting')
        self.assertIn('public event EventHandler<HomeState>? HomeStateChanged;', code)
        tray = (ROOT / 'MainWindow.Tray.cs').read_text(encoding='utf-8')
        self.assertIn('_currentPage is null && !_navigationBusy && !_closing && !UiMutationBusy', tray)
        self.assertIn('!_state.RequiresSignIn && !_state.StartupFailed && (_state.PrimaryEnabled || _state.CanRestartElevated)', tray)
        self.assertIn('if (CanRunPrimaryFromTray) Primary_Click(this, new RoutedEventArgs());', tray)
        for step in ('if (!IsVisible) Show();', 'WindowState = WindowState.Normal;', 'Activate();', 'Topmost = true;', 'Topmost = false;', 'Focus();'):
            self.assertIn(step, tray)
        app = (ROOT / 'App.xaml.cs').read_text(encoding='utf-8')
        self.assertIn('window.ShowFromActivation();', method_body(app, 'ShowFromActivation'))

    def test_every_optimize_the_app_starts_carries_the_saved_country(self):
        code = (ROOT / 'MainWindow.xaml.cs').read_text(encoding='utf-8')
        self.assertNotIn('"optimize", new { }', code)
        self.assertIn('"optimize", RoutePayload(), lifetime)', method_body(code, 'RunRouteScanAsync'))
        self.assertIn('"optimize", RoutePayload(), scanBudget.Token)', method_body(code, 'RunPrimaryMutationAsync'))
        self.assertIn('string.IsNullOrEmpty(_settings.Current.PreferredRouteCountry)', code)
        route = method_body(code, 'Route_Click')
        self.assertIn('new RouteView(_settings.Current.PreferredRouteCountry, _state.Snapshot?.Route, LoadRoutePingsAsync, _allowMotion)', route)
        self.assertIn('if (result.Success) RememberRoute(country);', method_body(code, 'RunRouteOptimizeAsync'))
        self.assertIn('_settings.Save(_settings.Current with { PreferredRouteCountry = country })', method_body(code, 'RememberRoute'))
        self.assertFalse((ROOT / 'RouteWindow.xaml').exists())
        view = (ROOT / 'RouteView.xaml.cs').read_text(encoding='utf-8')
        self.assertIn('public RouteView(string? preferred, RouteInfo? current, Func<CancellationToken, Task<RoutePingResult>>? loadPings, Func<bool>? allowMotion = null)', view)
        backend = (REPO / 'backend/src/backend.mjs').read_text(encoding='utf-8')
        self.assertIn('"routePings"', backend)
        self.assertIn('ROUTE_PINGS_BUSY', backend)
        preview = backend.rsplit('if (command === "routePings") {', 1)[1].split(
            'if (mutationBusy) return { success: false, message:', 1)[0]
        self.assertNotRegex(preview, r'\bmutationBusy\s*=(?!=)')
        self.assertIn('new DiscreteDoubleKeyFrame(dim, offset)', view)
        self.assertIn('new LinearDoubleKeyFrame(dim, TimeSpan.FromMilliseconds(900))', view)
        for name in ('InitializeAsync', 'StartAutoConnect', 'StartRouteScan', 'Primary_Click', 'RunPrimaryMutationAsync', 'TrackProgressAsync', 'ExecuteWithVerificationAsync', 'Route_Click', 'RunRouteOptimizeAsync'):
            self.assertNotRegex(method_body(code, name), r'[Nn]otification', name)

    def test_light_links_use_a_shade_that_passes_contrast_on_the_page_and_on_cards(self):
        def luminance(h):
            channels = [int(h[i:i + 2], 16) / 255 for i in (1, 3, 5)]
            f = lambda x: x / 12.92 if x <= 0.03928 else ((x + 0.055) / 1.055) ** 2.4
            return 0.2126 * f(channels[0]) + 0.7152 * f(channels[1]) + 0.0722 * f(channels[2])

        def ratio(a, b):
            hi, lo = sorted((luminance(a), luminance(b)), reverse=True)
            return (hi + 0.05) / (lo + 0.05)

        settings = (ROOT / 'Services/SettingsStore.cs').read_text(encoding='utf-8')
        self.assertIn('Brush(dark ? accentFill : "#BF4223")', settings)
        self.assertGreaterEqual(ratio('#BF4223', '#F3F3F3'), 4.5)
        self.assertGreaterEqual(ratio('#BF4223', '#FFFFFF'), 4.5)

    def test_tray_and_route_strings_exist_in_both_languages_without_dashes(self):
        dashes = {chr(0x2013), chr(0x2014)}
        for name in ('Strings.resx', 'Strings.pt-BR.resx'):
            text = (ROOT / 'Localization' / name).read_text(encoding='utf-8')
            names = re.findall(r'<data name="([^"]+)"', text)
            self.assertEqual(names[names.index('TrayOpen') + 1], 'TrayStatusFormat', name)
            for key in ('TrayStatusFormat', 'CurrentRouteFormat', 'CurrentRouteWithPingFormat'):
                value = re.search(r'<data name="' + key + r'"[^>]*><value>(.*?)</value>', text).group(1)
                self.assertTrue(value.strip() and not dashes & set(value), key)
            self.assertIn('{0}', re.search(r'name="TrayStatusFormat"[^>]*><value>(.*?)</value>', text).group(1))
            self.assertIn('{2}', re.search(r'name="CurrentRouteWithPingFormat"[^>]*><value>(.*?)</value>', text).group(1))

    def test_setup_notes_cover_restart_tray_startup_task_and_uninstall(self):
        text = (REPO / 'docs/setup.md').read_text(encoding='utf-8')
        for phrase in ('Restart as administrator', 'Use a WireGuard .conf file instead', 'brings the same window forward',
                       'Brisa: Connected (SV#43)', 'Connect, Disconnect or Restart as administrator',
                       '`\\Brisa\\Startup-<id>`', 'schtasks /Delete /TN \\Brisa\\Startup-<id> /F',
                       'Turn Start with Windows off before you uninstall Brisa', 'remembered'):
            self.assertIn(phrase, text)
        self.assertFalse({chr(0x2013), chr(0x2014)} & set(text))
        self.assertFalse(any(ord(ch) >= 0x1F000 for ch in text))


if __name__ == '__main__':
    unittest.main()
