using ForkLog = PlayniteAchievements.Services.Logging.ForkLogger;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json.Linq;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.Notifications;

namespace PlayniteAchievements.Services
{
    /// <summary>
    /// The "official SAN" notification template: an imported Steam Achievement Notifier (.san)
    /// theme is drawn by SAN's own notification page (its HTML, CSS, animations and every
    /// customisation value) inside a transparent WebView2, filled with Playnite Achievements'
    /// data. Nothing of the theme is replaced by Playnite elements.
    /// </summary>
    public partial class NotificationPublisher
    {
        private static readonly Dictionary<string, AchievementUnlockNotificationItem> OfficialSanContext =
            new Dictionary<string, AchievementUnlockNotificationItem>(StringComparer.OrdinalIgnoreCase);

        private static string OfficialSanContextKey(string gameName, string achievementName)
        {
            return $"{(gameName ?? string.Empty).Trim()}|{(achievementName ?? string.Empty).Trim()}";
        }

        private static void RememberOfficialSanContext(string gameName, AchievementUnlockNotificationItem item)
        {
            if (item == null)
            {
                return;
            }

            lock (OfficialSanContext)
            {
                OfficialSanContext[OfficialSanContextKey(gameName, item.Name)] = item;
            }
        }

        private static AchievementUnlockNotificationItem TakeOfficialSanContext(string gameName, string achievementName)
        {
            lock (OfficialSanContext)
            {
                var key = OfficialSanContextKey(gameName, achievementName);
                if (OfficialSanContext.TryGetValue(key, out var item))
                {
                    OfficialSanContext.Remove(key);
                    return item;
                }
            }

            return null;
        }

        private static string OfficialSanCacheRoot()
        {
            return Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "SANcache");
        }

        private static string PositionToSan(LocalUnlockOverlayPosition position)
        {
            switch (position)
            {
                case LocalUnlockOverlayPosition.TopLeft: return "top-left";
                case LocalUnlockOverlayPosition.BottomRight: return "bottom-right";
                case LocalUnlockOverlayPosition.BottomLeft: return "bottom-left";
                case LocalUnlockOverlayPosition.TopCenter: return "center-top";
                case LocalUnlockOverlayPosition.BottomCenter: return "center-bottom";
                default: return "top-right";
            }
        }

        private static LocalUnlockOverlayPosition SanToPosition(string san)
        {
            var value = (san ?? string.Empty).Trim().ToLowerInvariant();
            switch (value)
            {
                case "center-top": return LocalUnlockOverlayPosition.TopCenter;
                case "top-right":
                case "middle-right": return LocalUnlockOverlayPosition.TopRight;
                case "bottom-right": return LocalUnlockOverlayPosition.BottomRight;
                case "top-left":
                case "middle-left": return LocalUnlockOverlayPosition.TopLeft;
                case "bottom-left": return LocalUnlockOverlayPosition.BottomLeft;
                default: return LocalUnlockOverlayPosition.BottomCenter;
            }
        }

        private static readonly Random SanRandom = new Random();
        private static readonly List<System.Windows.Media.MediaPlayer> ActiveSanPlayers = new List<System.Windows.Media.MediaPlayer>();

        private static string OfficialSanProfileKey(AchievementUnlockNotificationItem context, RarityTier tier)
        {
            if (context != null && context.IsCompletion)
            {
                return "completion";
            }

            if (context != null && context.IsCapstone)
            {
                return "capstone";
            }

            switch (tier)
            {
                case RarityTier.UltraRare: return "ultrarare";
                case RarityTier.Rare: return "rare";
                case RarityTier.Uncommon: return "uncommon";
                default: return "common";
            }
        }

        private static bool ThemeFileUsable(OfficialSanSlot slot)
        {
            return slot != null && !string.IsNullOrWhiteSpace(slot.ThemePath) && File.Exists(slot.ThemePath);
        }

        /// <summary>
        /// Picks the slot for one unlock: the profile's primary slot, or its second slot with a
        /// 50/50 chance when that is enabled. A profile without a usable theme uses the Default profile.
        /// </summary>
        private static OfficialSanSlot PickOfficialSanSlot(LocalSettings settings, string profileKey, bool? forceAlt, out bool usedAlt)
        {
            usedAlt = false;
            var profiles = settings.OfficialSanProfiles ?? new OfficialSanProfiles();
            var profile = profiles.Get(profileKey);

            var altUsable = ThemeFileUsable(profile.Alt) && (profile.AltEnabled || forceAlt == true);
            if (altUsable)
            {
                bool takeAlt;
                if (forceAlt.HasValue)
                {
                    takeAlt = forceAlt.Value;
                }
                else
                {
                    lock (SanRandom)
                    {
                        takeAlt = SanRandom.Next(2) == 1;
                    }
                }

                if (takeAlt)
                {
                    usedAlt = true;
                    return profile.Alt;
                }
            }

            if (ThemeFileUsable(profile.Primary))
            {
                return profile.Primary;
            }

            var fallback = profiles.Default;
            return ThemeFileUsable(fallback.Primary) ? fallback.Primary : null;
        }

        private void PlayOfficialSanSound(string path, int volumePercent)
        {
            if (string.IsNullOrWhiteSpace(path) || volumePercent <= 0)
            {
                return;
            }

            try
            {
                var resolved = ResolveSoundPath(path);
                if (!File.Exists(resolved))
                {
                    ForkLog.Wrap(_logger)?.Warn($"[OfficialSan] Sound file not found: {resolved}");
                    return;
                }

                var player = new System.Windows.Media.MediaPlayer();
                lock (ActiveSanPlayers)
                {
                    ActiveSanPlayers.Add(player);
                }

                void Done()
                {
                    try { player.Stop(); player.Close(); } catch { }
                    lock (ActiveSanPlayers)
                    {
                        ActiveSanPlayers.Remove(player);
                    }
                }

                player.MediaEnded += (_, __) => Done();
                player.MediaFailed += (_, __) => Done();
                player.Volume = Math.Max(0.0, Math.Min(1.0, volumePercent / 100.0));
                player.Open(new Uri(resolved));
                player.Play();

                var safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
                safety.Tick += (_, __) => { safety.Stop(); Done(); };
                safety.Start();
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger)?.Warn(ex, "[OfficialSan] Failed to play the notification sound.");
            }
        }

        private JObject BuildUpstreamElementsPayload(string providerKey, RarityTier tier, bool capstone)
        {
            try
            {
                var persisted = _settings?.Persisted;
                var toast = NotificationStyleResolver.Resolve(persisted, providerKey)?.Toast;
                if (toast == null)
                {
                    return null;
                }

                var color = RarityAppearanceHelper.GetBaseColor(tier, persisted);
                var colorText = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                return new JObject
                {
                    ["showHeader"] = toast.ShowHeader,
                    ["showName"] = toast.ShowName,
                    ["showDescription"] = toast.ShowDescription,
                    ["showIcon"] = toast.ShowIcon,
                    ["showRarityPercent"] = toast.ShowRarityPercent,
                    ["headerScale"] = toast.HeaderFontSize.HasValue ? toast.HeaderFontSize.Value / 11.0 : 1.0,
                    ["titleScale"] = toast.TitleFontSize.HasValue ? toast.TitleFontSize.Value / 16.0 : 1.0,
                    ["bodyScale"] = toast.BodyFontSize.HasValue ? toast.BodyFontSize.Value / 11.0 : 1.0,
                    ["fontFamily"] = toast.FontFamily ?? string.Empty,
                    ["rarityColor"] = colorText,
                    ["rarityColoredName"] = toast.RarityColoredName,
                    ["glow"] = toast.ShowRarityGlow || toast.NotificationBorderGlow
                };
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger)?.Warn(ex, "[OfficialSan] Could not read the upstream element settings.");
                return null;
            }
        }

        /// <summary>
        /// Sends a test notification through the official SAN template for one profile (and,
        /// optionally, its second slot).
        /// </summary>
        public bool ShowOfficialSanTest(
            LocalSettings settings,
            string profileKey,
            bool useAlt,
            string gameName,
            string achievementName,
            string description,
            string iconPath,
            Game game)
        {
            var key = (profileKey ?? "common").Trim().ToLowerInvariant();
            var context = new AchievementUnlockNotificationItem(achievementName, iconPath, description, 25, null, null)
            {
                IsCapstone = key == "capstone",
                IsCompletion = key == "completion",
                RarityTier = key == "ultrarare" ? "UltraRare" : key == "rare" ? "Rare" : key == "uncommon" ? "Uncommon" : "Common",
                GlobalPercent = key == "ultrarare" ? 1.8 : key == "rare" ? 9.5 : key == "uncommon" ? 27.0 : key == "common" ? 64.0 : 3.0
            };
            RememberOfficialSanContext(gameName, context);
            return TryShowOfficialSanNotification(
                gameName,
                achievementName,
                iconPath,
                "Local",
                settings,
                game,
                description,
                null,
                null,
                key,
                useAlt);
        }

        /// <summary>
        /// The only unlock notification renderer: the official SAN template. It is deliberately
        /// independent of the plugin's notification switches, the per-provider toast flags and every
        /// legacy Santodan overlay setting.
        /// </summary>
        private void ShowOfficialSanUnlocks(
            string gameName,
            IReadOnlyList<AchievementUnlockNotificationItem> items,
            string providerKey,
            Game game,
            LocalSettings settings)
        {
            if (settings == null || !settings.UseOfficialSanTemplate || items == null)
            {
                return;
            }

            var safeGameName = string.IsNullOrWhiteSpace(gameName) ? "Current Game" : gameName.Trim();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var name = item?.Name?.Trim();
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                {
                    continue;
                }

                var current = item;
                RememberOfficialSanContext(safeGameName, current);
                RunOnUiThread(() =>
                {
                    try
                    {
                        TryShowOfficialSanNotification(
                            safeGameName,
                            name,
                            current.IconPath,
                            providerKey,
                            settings,
                            game,
                            current.Description,
                            current.Rarity,
                            current.Trophy);
                    }
                    catch (Exception ex)
                    {
                        ForkLog.Wrap(_logger)?.Warn(ex, "[OfficialSan] Failed to show an unlock notification.");
                    }
                });
            }
        }

        private static string ToSanTier(RarityTier tier, bool capstone, double percent = double.NaN)
        {
            if (capstone)
            {
                return "psnplatinum";
            }

            switch (tier)
            {
                case RarityTier.UltraRare: return !double.IsNaN(percent) && percent <= 1.0 ? "diamond" : "sapphire";
                case RarityTier.Rare: return "gold";
                case RarityTier.Uncommon: return "silver";
                default: return "bronze";
            }
        }

        private static bool IsUsableImage(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                   File.Exists(path);
        }

        /// <summary>
        /// Writes Playnite Achievements' custom rarity / trophy images (Display settings) into the
        /// theme's per-tier icon slots, so the theme keeps its layout but shows the user's artwork.
        /// Slots without a custom image keep the theme's own icon.
        /// </summary>
        private void ApplyCustomRarityIconsToTheme(OfficialSanTheme theme, bool completion)
        {
            var overrides = _settings?.Persisted?.RarityIconOverrides;
            if (overrides == null)
            {
                return;
            }

            var customisation = theme.Customisation;
            var preset = theme.Preset;
            var icons = customisation["customicons"] as JObject;
            var presetIcons = icons?[preset] as JObject;
            if (presetIcons == null)
            {
                return;
            }

            // Tier order used by SAN presets: bronze, silver, gold, sapphire, PSN platinum, diamond.
            var slots = new string[6];
            var current = presetIcons["decoration"];
            if (current is JArray array)
            {
                for (var i = 0; i < slots.Length && i < array.Count; i++)
                {
                    slots[i] = array[i]?.Value<string>();
                }
            }
            else if (current != null && current.Type == JTokenType.String)
            {
                for (var i = 0; i < slots.Length; i++)
                {
                    slots[i] = current.Value<string>();
                }
            }

            var changed = false;
            void Set(int index, string path, string badgeKey)
            {
                if (!IsUsableImage(path))
                {
                    return;
                }

                slots[index] = path;
                changed = true;
                if (!string.IsNullOrEmpty(badgeKey))
                {
                    customisation[badgeKey] = path;
                }
            }

            Set(0, overrides.Common, "percentbadgeimgbronze");
            Set(1, overrides.Uncommon, "percentbadgeimgsilver");
            Set(2, overrides.Rare, "percentbadgeimggold");
            Set(3, overrides.UltraRare, "percentbadgeimgsapphire");
            var capstoneImage = IsUsableImage(overrides.CapstonePlatinum) ? overrides.CapstonePlatinum : overrides.TrophyPlatinum;
            Set(4, capstoneImage, "percentbadgeimgplatinum");
            if (completion && IsUsableImage(overrides.TrophyPlatinum))
            {
                presetIcons["plat"] = overrides.TrophyPlatinum;
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            // Slots left empty fall back to the nearest filled lower tier, as SAN does.
            for (var i = 0; i < slots.Length; i++)
            {
                if (string.IsNullOrEmpty(slots[i]))
                {
                    slots[i] = i > 0 ? slots[i - 1] : string.Empty;
                }
            }

            presetIcons["decoration"] = new JArray(slots.Cast<object>());
        }

        /// <summary>
        /// Game backgrounds are often multi-megabyte 4K images. Decoding one inside the notification
        /// page takes about a second, which is the delay between the unlock and the card appearing.
        /// A 1280 px copy (cached on disk) decodes in a few milliseconds and looks the same at
        /// notification size.
        /// </summary>
        private static string DownscaleForNotification(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return fullPath;
            }

            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists || info.Length < 300 * 1024)
                {
                    return fullPath;
                }

                var directory = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "san-img");
                Directory.CreateDirectory(directory);
                string key;
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    key = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(
                        fullPath + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks))).Replace("-", string.Empty);
                }

                var target = Path.Combine(directory, key + ".jpg");
                if (File.Exists(target))
                {
                    return target;
                }

                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 1280;
                bitmap.UriSource = new Uri(fullPath);
                bitmap.EndInit();
                bitmap.Freeze();
                var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 88 };
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                var temp = target + ".tmp";
                using (var stream = File.Create(temp))
                {
                    encoder.Save(stream);
                }

                File.Move(temp, target);
                return target;
            }
            catch
            {
                return fullPath;
            }
        }

        private string ResolveGameImagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                var full = _api?.Database?.GetFullFilePath(path);
                return !string.IsNullOrWhiteSpace(full) && File.Exists(full) ? full : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static double ParsePercent(string rarityText)
        {
            if (string.IsNullOrWhiteSpace(rarityText))
            {
                return double.NaN;
            }

            var match = Regex.Match(rarityText, "(\\d+(?:[.,]\\d+)?)\\s*%");
            if (!match.Success)
            {
                return double.NaN;
            }

            return double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : double.NaN;
        }

        private bool TryShowOfficialSanNotification(
            string gameName,
            string achievementName,
            string achievementIconPath,
            string providerKey,
            LocalSettings settings,
            Game game,
            string achievementDescription,
            string achievementRarity,
            string achievementTrophy,
            string forcedProfileKey = null,
            bool? forcedAlt = null)
        {
            if (settings == null || !settings.UseOfficialSanTemplate)
            {
                return false;
            }

            try
            {
                var runtimeRoot = OfficialSanThemeService.FindRuntimeRoot(GetPluginAssemblyDirectory());
                if (string.IsNullOrWhiteSpace(runtimeRoot))
                {
                    ForkLog.Wrap(_logger)?.Warn("[OfficialSan] The bundled SAN runtime was not found; using the regular renderer.");
                    return false;
                }

                var context = TakeOfficialSanContext(gameName, achievementName);
                var tier = RarityTier.Common;
                if (context != null && !string.IsNullOrWhiteSpace(context.RarityTier))
                {
                    RarityTierExtensions.TryParse(context.RarityTier, out tier);
                }

                var capstone = context?.IsCapstone == true;
                var completion = context?.IsCompletion == true;
                var profileKey = string.IsNullOrWhiteSpace(forcedProfileKey)
                    ? OfficialSanProfileKey(context, tier)
                    : forcedProfileKey;
                var slot = PickOfficialSanSlot(settings, profileKey, forcedAlt, out var usedAlt);
                if (slot == null)
                {
                    ForkLog.Wrap(_logger)?.Warn($"[OfficialSan] No .san theme is set for '{profileKey}' (or the Default profile); using the regular renderer.");
                    return false;
                }

                var themePath = slot.ThemePath;

                var theme = OfficialSanThemeService.Load(themePath, runtimeRoot, OfficialSanCacheRoot());
                if (settings.OfficialSanUseCustomRarityIcons)
                {
                    ApplyCustomRarityIconsToTheme(theme, completion);
                }

                var scale = theme.ThemeScale * (settings.OfficialSanScalePercent / 100.0);
                theme.Customisation["scale"] = scale * 100.0;

                var displaySeconds = slot.DurationSeconds > 0
                    ? slot.DurationSeconds
                    : theme.ThemeDisplayTimeSeconds;

                // SAN themes time their own animations from the theme's display time (the Xbox presets
                // hold, swap and close on it), so the chosen duration has to go into the theme itself.
                theme.Customisation["displaytime"] = displaySeconds;

                var position = slot.UseThemePosition
                    ? SanToPosition(OfficialSanThemeService.PositionName(theme.Customisation))
                    : slot.Position;

                var percent = context?.GlobalPercent ?? ParsePercent(achievementRarity);
                var hasPercent = !double.IsNaN(percent) && percent >= 0;
                var iconPath = IsUsableImage(achievementIconPath) ? achievementIconPath : string.Empty;
                var gameIcon = ResolveGameImagePath(context?.GameIconPath ?? game?.Icon);
                var header = DownscaleForNotification(ResolveGameImagePath(context?.GameCoverPath ?? game?.BackgroundImage ?? game?.CoverImage));

                bool? showPercent = null;
                if (settings.OfficialSanPercentMode == OfficialSanPercentMode.Always) showPercent = true;
                if (settings.OfficialSanPercentMode == OfficialSanPercentMode.Never) showPercent = false;

                var trophy = (achievementTrophy ?? string.Empty).Trim().ToLowerInvariant();
                // The rarity pill (under the achievement icon) is drawn by the host page. It is never
                // shown for the 100% completion unlock, and "Never show" hides every percentage.
                var pillShow = hasPercent && !completion && !(capstone && !completion) &&
                               settings.OfficialSanPercentMode != OfficialSanPercentMode.Never;
                var pillTier = ToSanTier(tier, false, hasPercent ? percent : double.NaN);
                var payload = new JObject
                {
                    ["paPill"] = new JObject
                    {
                        ["show"] = pillShow,
                        ["percent"] = hasPercent ? (JToken)new JValue(percent) : JValue.CreateNull(),
                        ["tier"] = pillTier
                    },
                    ["displayName"] = achievementName ?? string.Empty,
                    ["description"] = achievementDescription ?? string.Empty,
                    ["iconPath"] = iconPath,
                    ["gameIconPath"] = gameIcon,
                    ["headerPath"] = header,
                    ["rarityPct"] = hasPercent ? (JToken)new JValue(percent) : JValue.CreateNull(),
                    ["rarityTier"] = ToSanTier(tier, capstone && !completion, hasPercent ? percent : double.NaN),
                    ["trophyType"] = trophy,
                    ["isRare"] = capstone || completion || tier == RarityTier.Rare || tier == RarityTier.UltraRare,
                    ["isPlatinum"] = completion,
                    ["isPsnPlatinum"] = capstone && !completion,
                    ["hidden"] = settings.OfficialSanShowHiddenAsLocked && context?.IsHidden == true,
                    ["position"] = PositionToSan(position),
                    ["durationMs"] = (int)Math.Round(displaySeconds * 1000.0),
                    ["showRarityPercentage"] = showPercent.HasValue ? (JToken)new JValue(showPercent.Value) : JValue.CreateNull(),
                    ["sanTheme"] = new JObject
                    {
                        ["label"] = theme.Label,
                        ["cacheDir"] = theme.CacheDir,
                        ["preset"] = theme.Preset,
                        ["presetHtml"] = theme.PresetHtml,
                        ["presetDimensions"] = new JObject
                        {
                            ["width"] = theme.BaseWidth,
                            ["height"] = theme.BaseHeight,
                            ["offset"] = theme.Offset
                        },
                        ["runtime"] = new JObject
                        {
                            ["globalCssUrl"] = theme.GlobalCssUrl,
                            ["baseCssUrl"] = theme.BaseCssUrl,
                            ["baseAnimCssUrl"] = theme.BaseAnimCssUrl,
                            ["presetCssUrl"] = theme.PresetCssUrl
                        },
                        ["customisation"] = theme.Customisation
                    }
                };

                if (settings.OfficialSanApplyUpstreamElements)
                {
                    var upstream = BuildUpstreamElementsPayload(providerKey, tier, capstone || completion);
                    if (upstream != null)
                    {
                        payload["upstream"] = upstream;
                    }
                }

                var hostPath = Path.Combine(GetPluginAssemblyDirectory(), "Resources", "Tools", "OfficialSan", "san-host.html");
                if (!File.Exists(hostPath))
                {
                    ForkLog.Wrap(_logger)?.Warn("[OfficialSan] The SAN host page was not found; using the regular renderer.");
                    return false;
                }

                var windowWidth = Math.Max(260, Math.Ceiling((theme.BaseWidth + theme.Offset * 2) * scale));
                var windowHeight = Math.Max(90, Math.Ceiling((theme.BaseHeight + theme.Offset * 2) * scale));
                payload["windowDip"] = new JObject { ["width"] = windowWidth, ["height"] = windowHeight };
                var json = payload.ToString(Newtonsoft.Json.Formatting.None).Replace("</", "<\\/");
                var debug = settings.EnableOverlayDebugLogging;
                var followMonitor = settings.OfficialSanShowOnActiveGameMonitor;

                var soundFile = slot.SoundMode == OfficialSanSoundMode.Theme
                    ? OfficialSanThemeService.FindThemeSound(theme)
                    : slot.SoundMode == OfficialSanSoundMode.File ? slot.SoundPath : string.Empty;
                var soundVolume = slot.SoundVolume;

                // A warm host is a WebView2 window that already loaded the host page, so the unlock only
                // has to hand it the payload. Without one (first notification after start-up) a fresh
                // window is created, which takes noticeably longer.
                var warm = TakeWarmSanHost();
                var window = warm?.Window;
                var webView = warm?.View;
                var htmlPath = warm?.HtmlPath;
                if (warm == null)
                {
                    var html = File.ReadAllText(hostPath, Encoding.UTF8)
                        .Replace("<!--PA_PAYLOAD-->", "<script>window.__SAN_PAYLOAD__ = " + json + ";</script>");
                    htmlPath = WriteSanWebViewDocumentToTempFile(html);
                    webView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
                    window = CreateSanOverlayWindow(webView, windowWidth, windowHeight);
                }
                else
                {
                    window.Width = windowWidth;
                    window.Height = windowHeight;
                }

                AttachOverlayTopmostGuard(window, debug);
                var overlayState = RegisterOverlayWindow(window, position);
                if (overlayState != null)
                {
                    overlayState.Renderer = "OfficialSAN";
                    overlayState.SanInset = theme.Offset * scale;
                    overlayState.SanCardHeight = theme.BaseHeight * scale;
                    PositionOfficialSanWindow(overlayState, GetOverlayStackIndex(overlayState), followMonitor);
                }
                else
                {
                    PositionOverlayWindow(window, position, 0, followMonitor, debug);
                }

                window.Closed += (_, __) =>
                {
                    if (overlayState != null)
                    {
                        UnregisterOverlayWindow(overlayState);
                    }

                    try { webView.Dispose(); } catch { }
                    TryDeleteSanWebViewDocument(htmlPath);
                };

                // The sound and the display timer start when the page reports that it drew the
                // notification, so the sound is never heard before the card is on screen. SAN animates
                // itself out at the end of its display time; the window only has to outlive that, with
                // a little slack for the exit animation.
                var started = false;
                var showStopwatch = System.Diagnostics.Stopwatch.StartNew();
                DispatcherTimer readyFallback = null;
                void StartPlayback()
                {
                    if (started)
                    {
                        return;
                    }

                    started = true;
                    ForkLog.Wrap(_logger)?.Info($"[OfficialSan] '{achievementName}' on screen {showStopwatch.ElapsedMilliseconds} ms after the unlock reached the notifier ({(warm != null ? "warm" : "cold")}).");
                    try { readyFallback?.Stop(); } catch { }
                    var closeTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(Math.Max(1500, displaySeconds * 1000.0) + 1500)
                    };
                    closeTimer.Tick += (_, __) =>
                    {
                        closeTimer.Stop();
                        if (overlayState != null)
                        {
                            overlayState.IsClosing = true;
                        }

                        try { window.Close(); } catch { }
                    };
                    if (overlayState != null)
                    {
                        overlayState.CloseTimer = closeTimer;
                    }

                    closeTimer.Start();
                    PlayOfficialSanSound(soundFile, soundVolume);
                }

                // If the page never reports back (a broken theme), still play and still close.
                readyFallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                readyFallback.Tick += (_, __) => StartPlayback();
                readyFallback.Start();

                void Hook(Microsoft.Web.WebView2.Core.CoreWebView2 core)
                {
                    core.WebMessageReceived += (_, e) =>
                    {
                        string message = null;
                        try { message = e.TryGetWebMessageAsString(); } catch { }
                        if (message != null && message.StartsWith("san-ready", StringComparison.Ordinal))
                        {
                            ForkLog.Wrap(_logger)?.Debug($"[OfficialSan] Page timings ({message}); {showStopwatch.ElapsedMilliseconds} ms since hand-off.");
                            window.Dispatcher.BeginInvoke(new Action(StartPlayback));
                        }
                    };
                }

                if (warm != null)
                {
                    Hook(webView.CoreWebView2);
                    // The window has only just been moved from its parking spot to the screen. Give the
                    // browser a couple of frames to start painting there before the notification starts
                    // animating; otherwise the first frames are drawn half transparent / out of step.
                    var kick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
                    kick.Tick += (_, __) =>
                    {
                        kick.Stop();
                        try { webView.CoreWebView2.ExecuteScriptAsync("window.__sanCb(" + json + ");"); }
                        catch (Exception ex) { ForkLog.Wrap(_logger)?.Warn(ex, "[OfficialSan] Could not hand the unlock to the warm SAN host."); }
                    };
                    kick.Start();
                }
                else
                {
                    window.Loaded += async (_, __) =>
                    {
                        try
                        {
                            var environment = await GetSanWebView2EnvironmentAsync();
                            await webView.EnsureCoreWebView2Async(environment);
                            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                            Hook(webView.CoreWebView2);
                            webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                        }
                        catch (Exception ex)
                        {
                            ForkLog.Wrap(_logger)?.Warn(ex, "[OfficialSan] Failed to initialize the SAN WebView2 overlay.");
                            try { window.Close(); } catch { }
                        }
                    };

                    window.Show();
                }

                ForkLog.Wrap(_logger)?.Info($"[OfficialSan] Showing '{achievementName}' ({profileKey}{(usedAlt ? ", second theme" : string.Empty)}) with theme '{theme.Label}' (preset '{theme.Preset}', {(warm != null ? "warm host" : "cold start")}).");

                // Get the next warm host ready while this one is on screen.
                try
                {
                    Application.Current?.Dispatcher.BeginInvoke(
                        new Action(PrewarmOfficialSan),
                        DispatcherPriority.ApplicationIdle);
                }
                catch
                {
                }

                return true;
            }
            catch (Exception ex)
            {
                ForkLog.Wrap(_logger)?.Warn(ex, "[OfficialSan] Failed to render the official SAN notification; using the regular renderer.");
                return false;
            }
        }

        // ---- Warm WebView2 host + window helpers -----------------------------------------------

        private sealed class SanWarmHost
        {
            public Window Window;
            public WebView2 View;
            public string HtmlPath;
            public bool Ready;
        }

        private static SanWarmHost _sanWarmHost;
        private static bool _sanWarmBuilding;

        private static Window CreateSanOverlayWindow(WebView2 webView, double width, double height, bool parked = false)
        {
            var window = new Window
            {
                Content = webView,
                Width = width,
                Height = height,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                IsHitTestVisible = false,
                Focusable = false
            };

            if (parked)
            {
                // Windows decides a window's DPI from the monitor it is created on. A window parked far
                // off-screen gets 100%, so WebView2 renders at half size on a 200% display and the
                // notification ends up small and off-centre. Park it in the real work area instead, at a
                // size too small to matter.
                var work = SystemParameters.WorkArea;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = work.Right - width;
                window.Top = work.Top;
            }

            return window;
        }

        private SanWarmHost TakeWarmSanHost()
        {
            var host = _sanWarmHost;
            if (host == null || !host.Ready)
            {
                return null;
            }

            _sanWarmHost = null;
            return host;
        }

        /// <summary>
        /// Loads the SAN host page into a hidden WebView2 window ahead of time (and again after every
        /// notification), so an unlock appears without waiting for the browser to start and the page to
        /// load. Safe to call repeatedly; does nothing when the official SAN template is off.
        /// </summary>
        public void PrewarmOfficialSan()
        {
            try
            {
                if (_sanWarmHost != null || _sanWarmBuilding)
                {
                    return;
                }

                var settings = Providers.ProviderRegistry.Settings<LocalSettings>();
                if (settings == null || !settings.UseOfficialSanTemplate)
                {
                    return;
                }

                var hostPath = Path.Combine(GetPluginAssemblyDirectory(), "Resources", "Tools", "OfficialSan", "san-host.html");
                if (!File.Exists(hostPath))
                {
                    return;
                }

                _sanWarmBuilding = true;
                var webView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
                var window = CreateSanOverlayWindow(webView, 6, 6, parked: true);
                var host = new SanWarmHost { Window = window, View = webView };

                window.Loaded += async (_, __) =>
                {
                    try
                    {
                        var environment = await GetSanWebView2EnvironmentAsync();
                        await webView.EnsureCoreWebView2Async(environment);
                        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                        webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                        var html = File.ReadAllText(hostPath, Encoding.UTF8).Replace("<!--PA_PAYLOAD-->", string.Empty);
                        host.HtmlPath = WriteSanWebViewDocumentToTempFile(html);
                        webView.CoreWebView2.NavigationCompleted += (s, e) =>
                        {
                            host.Ready = e.IsSuccess;
                            _sanWarmBuilding = false;
                        };
                        webView.CoreWebView2.Navigate(new Uri(host.HtmlPath).AbsoluteUri);
                    }
                    catch (Exception ex)
                    {
                        ForkLog.Wrap(_logger)?.Warn(ex, "[OfficialSan] Could not warm up the SAN WebView2 host.");
                        _sanWarmBuilding = false;
                        if (ReferenceEquals(_sanWarmHost, host))
                        {
                            _sanWarmHost = null;
                        }

                        try { window.Close(); } catch { }
                    }
                };

                _sanWarmHost = host;
                window.Show();
            }
            catch (Exception ex)
            {
                _sanWarmBuilding = false;
                ForkLog.Wrap(_logger)?.Warn(ex, "[OfficialSan] Warm-up failed.");
            }
        }

        /// <summary>
        /// Places an official-SAN window flush against its screen corner, the way the Achievements app
        /// does: the theme already carries a transparent margin round its card, so no extra margin is
        /// added. Stacked cards are spaced by their visible edges (margins overlap).
        /// </summary>
        private void PositionOfficialSanWindow(OverlayWindowState state, int stackIndex, bool followActiveGameMonitor)
        {
            var window = state?.Window;
            if (window == null)
            {
                return;
            }

            var workArea = followActiveGameMonitor
                ? GetForegroundMonitorWorkArea(out _)
                : SystemParameters.WorkArea;
            var height = GetOverlayWindowHeight(window);
            var inset = Math.Max(0, state.SanInset);
            var card = state.SanCardHeight > 0 ? state.SanCardHeight : Math.Max(1, height - inset * 2);
            var visibleGap = Math.Max(1.0, Math.Round((inset * 2 + 8) * 0.15));
            var stackOffset = Math.Max(0, stackIndex) * (card + visibleGap);

            switch (state.Position)
            {
                case LocalUnlockOverlayPosition.TopLeft:
                    window.Left = workArea.Left;
                    window.Top = workArea.Top + stackOffset;
                    break;
                case LocalUnlockOverlayPosition.TopCenter:
                    window.Left = workArea.Left + ((workArea.Width - window.Width) / 2);
                    window.Top = workArea.Top + stackOffset;
                    break;
                case LocalUnlockOverlayPosition.BottomLeft:
                    window.Left = workArea.Left;
                    window.Top = workArea.Bottom - height - stackOffset;
                    break;
                case LocalUnlockOverlayPosition.BottomCenter:
                    window.Left = workArea.Left + ((workArea.Width - window.Width) / 2);
                    window.Top = workArea.Bottom - height - stackOffset;
                    break;
                case LocalUnlockOverlayPosition.BottomRight:
                    window.Left = workArea.Right - window.Width;
                    window.Top = workArea.Bottom - height - stackOffset;
                    break;
                default:
                    window.Left = workArea.Right - window.Width;
                    window.Top = workArea.Top + stackOffset;
                    break;
            }
        }
    }
}
