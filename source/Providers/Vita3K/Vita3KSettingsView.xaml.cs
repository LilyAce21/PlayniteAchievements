using System;
using System.Linq;
using System.Windows;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Vita3K
{
    public partial class Vita3KSettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private readonly IPlayniteAPI _playniteApi;
        private Vita3KSettings _vita3kSettings;

        public static readonly DependencyProperty IsAuthenticatedProperty =
            DependencyProperty.Register(nameof(IsAuthenticated), typeof(bool), typeof(Vita3KSettingsView), new PropertyMetadata(false));

        public bool IsAuthenticated
        {
            get => (bool)GetValue(IsAuthenticatedProperty);
            set => SetValue(IsAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(nameof(AuthStatus), typeof(string), typeof(Vita3KSettingsView), new PropertyMetadata(string.Empty));

        public string AuthStatus
        {
            get => (string)GetValue(AuthStatusProperty);
            set => SetValue(AuthStatusProperty, value);
        }

        public new Vita3KSettings Settings => _vita3kSettings;

        public Vita3KSettingsView(IPlayniteAPI playniteApi)
        {
            _playniteApi = playniteApi;
            InitializeComponent();
            ConnectionLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderConnection"),
                ResourceProvider.GetString("LOCPlayAch_Provider_Vita3K"));
            ExecutablePathsEditor.Configure(
                Vita3KInstallationResolver.ValidateExecutablePath,
                () => _playniteApi?.Dialogs?.SelectFile("Vita3K.exe|Vita3K*.exe|Executable files|*.exe"));
            ExecutablePathsEditor.PathsChanged += ExecutablePathsEditor_PathsChanged;
        }

        public override void Initialize(IProviderSettings settings)
        {
            _vita3kSettings = settings as Vita3KSettings;
            base.Initialize(settings);
            ExecutablePathsEditor.SetPaths(_vita3kSettings?.ExecutablePaths);
            CheckVita3KAuth();
        }

        public Task RefreshAuthStatusAsync()
        {
            ExecutablePathsEditor.Revalidate();
            CheckVita3KAuth();
            return Task.CompletedTask;
        }

        private void ExecutablePathsEditor_PathsChanged(object sender, EventArgs e)
        {
            if (_vita3kSettings != null)
            {
                _vita3kSettings.ExecutablePaths = ExecutablePathsEditor.GetPaths();
            }

            CheckVita3KAuth();
        }

        private void CheckVita3KAuth()
        {
            var paths = ProviderPathList.Normalize(_vita3kSettings?.ExecutablePaths);
            if (paths.Count == 0)
            {
                SetAuthenticated(false);
                SetAuthStatus(string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_NotConfigured"), ResourceProvider.GetString("LOCPlayAch_Provider_Vita3K")));
                return;
            }

            var results = paths.Select(Vita3KInstallationResolver.ValidateExecutablePath).ToList();
            if (results.Any(result => result.IsValid))
            {
                SetAuthenticated(true);
                SetAuthStatusByKey("LOCPlayAch_Status_Succeeded");
                return;
            }

            SetAuthenticated(false);
            SetAuthStatusByKey(results[0].MessageKey);
        }

        private void SetAuthStatusByKey(string key)
        {
            var value = ResourceProvider.GetString(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                SetAuthStatus(value);
            }
        }

        private void SetAuthStatus(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                AuthStatus = status;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => AuthStatus = status));
            }
        }

        private void SetAuthenticated(bool authenticated)
        {
            if (Dispatcher.CheckAccess())
            {
                IsAuthenticated = authenticated;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => IsAuthenticated = authenticated));
            }
        }

    }
}
