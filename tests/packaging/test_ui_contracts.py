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


if __name__ == '__main__':
    unittest.main()
