using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Minecraft
{
    public partial class MinecraftSettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private readonly IPlayniteAPI _playniteApi;
        private MinecraftSettings _minecraftSettings;

        public static readonly DependencyProperty IsAuthenticatedProperty =
            DependencyProperty.Register(nameof(IsAuthenticated), typeof(bool), typeof(MinecraftSettingsView), new PropertyMetadata(false));

        public bool IsAuthenticated
        {
            get => (bool)GetValue(IsAuthenticatedProperty);
            set => SetValue(IsAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(nameof(AuthStatus), typeof(string), typeof(MinecraftSettingsView), new PropertyMetadata(string.Empty));

        public string AuthStatus
        {
            get => (string)GetValue(AuthStatusProperty);
            set => SetValue(AuthStatusProperty, value);
        }

        public new MinecraftSettings Settings => _minecraftSettings;

        public MinecraftSettingsView(IPlayniteAPI playniteApi)
        {
            _playniteApi = playniteApi;
            InitializeComponent();
            ConnectionLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderConnection"),
                ResourceProvider.GetString("LOCPlayAch_Provider_Minecraft"));
            GameFoldersEditor.Configure(
                ValidateGameFolder,
                () => _playniteApi?.Dialogs?.SelectFolder());
            GameFoldersEditor.PathsChanged += GameFoldersEditor_PathsChanged;
        }

        public override void Initialize(IProviderSettings settings)
        {
            _minecraftSettings = settings as MinecraftSettings;
            base.Initialize(settings);
            GameFoldersEditor.SetPaths(_minecraftSettings?.GameFolders);
            CheckStatus();
        }

        public Task RefreshAuthStatusAsync()
        {
            GameFoldersEditor.Revalidate();
            CheckStatus();
            return Task.CompletedTask;
        }

        private static ProviderPathValidation ValidateGameFolder(string path)
        {
            return MinecraftPaths.ResolveGameFolder(Environment.ExpandEnvironmentVariables(path ?? string.Empty)) != null
                ? ProviderPathValidation.Valid
                : ProviderPathValidation.Invalid("LOCPlayAch_InvalidPath");
        }

        private void GameFoldersEditor_PathsChanged(object sender, EventArgs e)
        {
            if (_minecraftSettings != null)
            {
                _minecraftSettings.GameFolders = GameFoldersEditor.GetPaths();
            }

            CheckStatus();
        }

        /// <summary>
        /// Configured folders are checked first; with none configured, %AppData%\.minecraft is.
        /// </summary>
        private void CheckStatus()
        {
            var paths = ProviderPathList.Normalize(_minecraftSettings?.GameFolders);
            if (paths.Count == 0)
            {
                if (MinecraftPaths.ResolveGameFolder(MinecraftPaths.DefaultGameFolder) != null)
                {
                    SetStatus(true, ResourceProvider.GetString("LOCPlayAch_Status_Succeeded"));
                    return;
                }

                SetStatus(false, string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_NotConfigured"),
                    ResourceProvider.GetString("LOCPlayAch_Provider_Minecraft")));
                return;
            }

            var results = paths.Select(ValidateGameFolder).ToList();
            if (results.Any(result => result.IsValid))
            {
                SetStatus(true, ResourceProvider.GetString("LOCPlayAch_Status_Succeeded"));
                return;
            }

            SetStatus(false, ResourceProvider.GetString(results[0].MessageKey));
        }

        private void SetStatus(bool authenticated, string status)
        {
            void Apply()
            {
                IsAuthenticated = authenticated;
                if (!string.IsNullOrWhiteSpace(status))
                {
                    AuthStatus = status;
                }
            }

            if (Dispatcher.CheckAccess())
            {
                Apply();
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(Apply));
            }
        }
    }
}
