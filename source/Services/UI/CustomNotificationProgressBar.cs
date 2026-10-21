using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Services.UI
{
    internal static class CustomNotificationProgressBar
    {
        internal static IEnumerable<NotificationProgressBarDefinition> Definitions(LocalSettings settings)
        {
            if (settings == null) return Enumerable.Empty<NotificationProgressBarDefinition>();
            if (settings.OverlayCustomProgressBars.Count > 0) return settings.OverlayCustomProgressBars.Where(bar => bar.Enabled);
            if (!settings.OverlayCustomShowProgressBar) return Enumerable.Empty<NotificationProgressBarDefinition>();
            return new[] { new NotificationProgressBarDefinition
            {
                Id = "legacy-achievement", Width = settings.OverlayCustomProgressBarWidth, Height = settings.OverlayCustomProgressBarHeight,
                CornerRadius = settings.OverlayCustomProgressBarCornerRadius, Spacing = settings.OverlayCustomProgressBarSpacing,
                FillColor = settings.OverlayCustomProgressBarFillColor, TrackColor = settings.OverlayCustomProgressBarTrackColor,
                ShowLabel = settings.OverlayCustomProgressBarShowLabel, LabelColor = settings.OverlayCustomProgressBarLabelColor,
                LabelFontSize = settings.OverlayCustomProgressBarLabelFontSize,
                LeftInset = Math.Max(0, settings.OverlayCustomProgressBarOffsetX)
            } };
        }

        internal static LocalSettings ForDefinition(LocalSettings settings, NotificationProgressBarDefinition bar)
        {
            NotificationBarCounter counter = null;
            if (bar.Source == NotificationBarSource.Achievement && settings.AchievementProgressCurrent.HasValue && settings.AchievementProgressMaximum > 0)
                counter = new NotificationBarCounter { Current = settings.AchievementProgressCurrent.Value, Maximum = settings.AchievementProgressMaximum.Value };
            else if (settings.NotificationBarCounters != null) settings.NotificationBarCounters.TryGetValue(bar.Source, out counter);
            if (counter == null || counter.Maximum <= 0) return null;
            return new LocalSettings
            {
                OverlayCustomShowProgressBar = true, AchievementProgressCurrent = counter.Current, AchievementProgressMaximum = counter.Maximum,
                OverlayCustomProgressBarWidth = bar.Width, OverlayCustomProgressBarHeight = bar.Height, OverlayCustomProgressBarCornerRadius = bar.CornerRadius,
                OverlayCustomProgressBarSpacing = 0, OverlayCustomProgressBarFillColor = bar.FillColor, OverlayCustomProgressBarTrackColor = bar.TrackColor,
                OverlayCustomProgressBarShowLabel = bar.ShowLabel, OverlayCustomProgressBarLabelColor = bar.LabelColor,
                OverlayCustomProgressBarLabelFontSize = bar.LabelFontSize
            };
        }

        internal static IEnumerable<List<NotificationProgressBarDefinition>> Rows(LocalSettings settings, NotificationBarPlacement placement)
        {
            var definitions=(settings?.OverlayCustomProgressBars?.Count>0?settings.OverlayCustomProgressBars.AsEnumerable():Definitions(settings)).ToList();
            return NotificationBarLayout.Groups(definitions)
                .Where(row=>NotificationBarLayout.Root(row,row[0]).Placement==placement)
                .Select(row=>row.Where(bar=>bar.Enabled && ForDefinition(settings,bar)!=null).ToList()).Where(row=>row.Count>0);
        }

        private static List<Color> GradientColors(string value) => (value ?? "").Split(new[] { ',', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(text => { try { return (Color?)ColorConverter.ConvertFromString(text); } catch { return null; } }).Where(color => color.HasValue).Select(color => color.Value).Take(32).ToList();
        private static Brush BarBrush(NotificationProgressBarDefinition bar, bool fill)
        {
            var solid = ColorOrDefault(fill ? bar.FillColor : bar.TrackColor, fill ? Color.FromRgb(167,224,255) : Color.FromArgb(48,255,255,255));
            var colors = GradientColors(fill ? bar.FillGradientColors : bar.TrackGradientColors);
            if ((fill ? bar.FillGradientEnabled : bar.TrackGradientEnabled) && colors.Count == 1) return new SolidColorBrush(colors[0]);
            if (!(fill ? bar.FillGradientEnabled : bar.TrackGradientEnabled) || colors.Count < 2) return new SolidColorBrush(solid);
            var angle = bar.GradientAngle * Math.PI / 180;
            var brush = new LinearGradientBrush { StartPoint = new Point(.5 - Math.Cos(angle)/2, .5 - Math.Sin(angle)/2), EndPoint = new Point(.5 + Math.Cos(angle)/2, .5 + Math.Sin(angle)/2) };
            for (var i=0; i<colors.Count; i++) brush.GradientStops.Add(new GradientStop(colors[i], i/(double)(colors.Count-1)));
            return brush;
        }
        private static string BarBackground(NotificationProgressBarDefinition bar, bool fill)
        {
            var colors = GradientColors(fill ? bar.FillGradientColors : bar.TrackGradientColors);
            if ((fill ? bar.FillGradientEnabled : bar.TrackGradientEnabled) && colors.Count == 1) return CssColor(colors[0].ToString(), Colors.White);
            if ((fill ? bar.FillGradientEnabled : bar.TrackGradientEnabled) && colors.Count > 1)
                return "linear-gradient(" + N(bar.GradientAngle + 90) + "deg," + string.Join(",", colors.Select(color => CssColor(color.ToString(), Colors.White))) + ")";
            return CssColor(fill ? bar.FillColor : bar.TrackColor, fill ? Color.FromRgb(167,224,255) : Color.FromArgb(48,255,255,255));
        }
        private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        private static bool SameFont(NotificationBarFont first,NotificationBarFont second) => Newtonsoft.Json.JsonConvert.SerializeObject(first)==Newtonsoft.Json.JsonConvert.SerializeObject(second);
        private static double BarHeight(NotificationProgressBarDefinition bar)
        {
            var caption=bar.CaptionPosition==NotificationBarTextPosition.Inside && !string.IsNullOrWhiteSpace(bar.Title);
            var counter=bar.TextPosition==NotificationBarTextPosition.Inside && bar.ShowLabel;
            var textHeight=caption && counter && bar.CaptionAlignment==bar.CounterAlignment && !SameFont(bar.CaptionFont,bar.CounterFont)
                ? (bar.CaptionFont.Size+bar.CounterFont.Size)*1.4+6 : Math.Max(caption?bar.CaptionFont.Size*1.4+6:0,counter?bar.CounterFont.Size*1.4+6:0);
            return Math.Max(bar.Height,textHeight);
        }
        private sealed class BarText
        {
            public string Text;
            public NotificationBarTextAlignment Alignment;
            public NotificationBarFont Font;
        }
        private static List<BarText> TextAt(NotificationProgressBarDefinition bar, LocalSettings snapshot, NotificationBarTextPosition position)
        {
            var parts=new List<BarText>();
            if (bar.CaptionPosition==position && !string.IsNullOrWhiteSpace(bar.Title)) parts.Add(new BarText { Text=bar.Title, Alignment=bar.CaptionAlignment, Font=bar.CaptionFont });
            if (bar.ShowLabel && bar.TextPosition==position)
            {
                var count=CustomNotificationProgressTokens.Expand("<progress>",snapshot.AchievementProgressCurrent,snapshot.AchievementProgressMaximum);
                if (parts.Count>0 && parts[0].Alignment==bar.CounterAlignment && SameFont(bar.CaptionFont,bar.CounterFont)) parts[0].Text += " " + count;
                else parts.Add(new BarText { Text=count, Alignment=bar.CounterAlignment, Font=bar.CounterFont });
            }
            return parts;
        }
        private static FrameworkElement CreateStyledText(BarText text,NotificationBarTextPosition position,LocalOverlayTextFormattingMode formattingMode)
        {
            var font=text.Font;
            TextBlock MakeLabel(string color)
            {
                var label=new TextBlock { Text=text.Text, FontSize=font.Size, Foreground=new SolidColorBrush(ColorOrDefault(color,Colors.White)), FontWeight=font.Bold?FontWeights.Bold:FontWeights.Normal, FontStyle=font.Italic?FontStyles.Italic:FontStyles.Normal,
                    TextAlignment=text.Alignment==NotificationBarTextAlignment.Center?TextAlignment.Center:text.Alignment==NotificationBarTextAlignment.Right?TextAlignment.Right:TextAlignment.Left,
                    VerticalAlignment=VerticalAlignment.Center,TextWrapping=position==NotificationBarTextPosition.Inside?TextWrapping.NoWrap:TextWrapping.Wrap,TextTrimming=TextTrimming.CharacterEllipsis,IsHitTestVisible=false,UseLayoutRounding=true,SnapsToDevicePixels=true };
                TextOptions.SetTextFormattingMode(label,formattingMode==LocalOverlayTextFormattingMode.Ideal?TextFormattingMode.Ideal:TextFormattingMode.Display);
                if(!string.IsNullOrWhiteSpace(font.Family) && !string.Equals(font.Family,"Default",StringComparison.OrdinalIgnoreCase)) { try { label.FontFamily=new FontFamily(font.Family); } catch { } }
                var decorations=new TextDecorationCollection();
                if(font.Underline)foreach(var item in TextDecorations.Underline)decorations.Add(item);
                if(font.Strikethrough)foreach(var item in TextDecorations.Strikethrough)decorations.Add(item);
                label.TextDecorations=decorations;
                return label;
            }
            var foreground=MakeLabel(font.Color);foreground.Tag="foreground";
            if(!font.OutlineEnabled && !font.ShadowEnabled)return foreground;
            var layers=new Grid { IsHitTestVisible=false,UseLayoutRounding=true,SnapsToDevicePixels=true };
            if(font.ShadowEnabled)
            {
                var shadow=MakeLabel(font.ShadowColor);shadow.Tag="shadow";
                shadow.Effect=new System.Windows.Media.Effects.BlurEffect { Radius=font.ShadowSize,RenderingBias=System.Windows.Media.Effects.RenderingBias.Quality };
                shadow.RenderTransform=new TranslateTransform(0,font.ShadowSize/2);layers.Children.Add(shadow);
            }
            if(font.OutlineEnabled && font.OutlineSize>0)
            {
                for(var i=0;i<8;i++)
                {
                    var angle=i*Math.PI/4;var outline=MakeLabel(font.OutlineColor);outline.Tag="outline";
                    outline.RenderTransform=new TranslateTransform(Math.Cos(angle)*font.OutlineSize,Math.Sin(angle)*font.OutlineSize);
                    layers.Children.Add(outline);
                }
            }
            layers.Children.Add(foreground);
            return layers;
        }
        private static string FontCss(NotificationBarFont font)
        {
            var family=string.IsNullOrWhiteSpace(font.Family)||string.Equals(font.Family,"Default",StringComparison.OrdinalIgnoreCase)?"inherit":"'"+new string(font.Family.Where(c=>!char.IsControl(c)).ToArray()).Replace("\\","\\\\").Replace("'","\\27 ")+"'";
            return ";font-family:"+family+";font-size:"+N(font.Size)+"px;color:"+CssColor(font.Color,Colors.White)+";font-weight:"+(font.Bold?"bold":"normal")+";font-style:"+(font.Italic?"italic":"normal")+
                ";text-decoration:"+(font.Underline||font.Strikethrough?(font.Underline?"underline ":"")+(font.Strikethrough?"line-through":""):"none")+
                ";paint-order:stroke fill;-webkit-text-stroke:"+(font.OutlineEnabled?N(font.OutlineSize*2)+"px "+CssColor(font.OutlineColor,Colors.Black):"0")+
                ";text-shadow:"+(font.ShadowEnabled?"0 "+N(font.ShadowSize/2)+"px "+N(font.ShadowSize)+"px "+CssColor(font.ShadowColor,Colors.Black):"none");
        }
        internal static StackPanel CreateElementsWpf(LocalSettings settings, NotificationBarPlacement placement)
        {
            var panel = new StackPanel();
            foreach (var definitions in Rows(settings, placement))
            {
                var row = new Grid { UseLayoutRounding=true,SnapsToDevicePixels=true };
                Grid.SetIsSharedSizeScope(row, true);
                foreach (var bar in definitions)
                {
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var snapshot = ForDefinition(settings, bar);
                    var element = new Grid { Tag = "progressBar-" + bar.Id, Margin = new Thickness(bar.LeftInset + (row.Children.Count > 0 ? 6 : 0),bar.Spacing,bar.RightInset,0), VerticalAlignment = VerticalAlignment.Center };
                    if (bar.Width > 0) { element.Width = bar.Width; element.HorizontalAlignment = HorizontalAlignment.Left; row.SizeChanged += (_,__) => element.MaxWidth = Math.Max(0, row.ActualWidth / definitions.Count - element.Margin.Left - element.Margin.Right); }
                    element.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto, SharedSizeGroup="above" });
                    element.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto, SharedSizeGroup="track" });
                    element.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto, SharedSizeGroup="below" });
                    element.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
                    element.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
                    element.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
                    var track = new Border { Height=BarHeight(bar), CornerRadius=new CornerRadius(Math.Min(bar.CornerRadius,BarHeight(bar)/2)), Background=BarBrush(bar,false), ClipToBounds=true };
                    var fill = new Border { Background=BarBrush(bar,true), CornerRadius=track.CornerRadius, HorizontalAlignment=HorizontalAlignment.Left, Width=0 };
                    track.Child=fill;
                    track.SizeChanged += (_,__) => fill.Width=track.ActualWidth*Fraction(snapshot);
                    Grid.SetRow(track,1); Grid.SetColumn(track,1); element.Children.Add(track);
                    foreach (NotificationBarTextPosition position in Enum.GetValues(typeof(NotificationBarTextPosition)))
                    {
                        var texts=TextAt(bar,snapshot,position); if (texts.Count==0) continue;
                        var labels=texts.Select(text=>CreateStyledText(text,position,settings.OverlayCustomTextFormattingMode)).ToList();
                        FrameworkElement label=labels[0];
                        if (texts.Count>1)
                        {
                            if(texts[0].Alignment==texts[1].Alignment)
                            {
                                var group=new StackPanel();foreach(var child in labels)group.Children.Add(child);label=group;
                            }
                            else
                            {
                                var group=new Grid { IsHitTestVisible=false };
                                for (var i=0;i<3;i++) group.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
                                for (var i=0;i<texts.Count;i++) { Grid.SetColumn(labels[i],(int)texts[i].Alignment); group.Children.Add(labels[i]); }
                                label=group;
                            }
                        }
                        if(position==NotificationBarTextPosition.Above || position==NotificationBarTextPosition.Below) { Grid.SetRow(label,position==NotificationBarTextPosition.Above?0:2); Grid.SetColumnSpan(label,3); label.Margin=new Thickness(0,position==NotificationBarTextPosition.Below?3:0,0,position==NotificationBarTextPosition.Above?3:0); }
                        else { Grid.SetRow(label,1); Grid.SetColumn(label,position==NotificationBarTextPosition.Left?0:position==NotificationBarTextPosition.Right?2:1); label.Margin=new Thickness(position==NotificationBarTextPosition.Right?8:position==NotificationBarTextPosition.Inside?6:0,0,position==NotificationBarTextPosition.Left?8:position==NotificationBarTextPosition.Inside?6:0,0); }
                        element.Children.Add(label);
                    }
                    Grid.SetColumn(element,row.Children.Count); row.Children.Add(element);
                }
                panel.Children.Add(row);
            }
            return panel;
        }
        internal static string CreateElementsHtml(LocalSettings settings, NotificationBarPlacement placement)
        {
            var html=new StringBuilder();
            foreach(var row in Rows(settings,placement))
            {
                html.Append("<div class=\"san-progress-row\" style=\"display:grid;grid-template-columns:repeat(").Append(row.Count).Append(",minmax(0,1fr));column-gap:6px;align-items:stretch;min-width:0\">");
                var column = 0;
                foreach(var bar in row)
                {
                    column++;
                    var snapshot=ForDefinition(settings,bar);
                    html.Append("<div class=\"san-progress-cell\" style=\"display:grid;grid-template-rows:subgrid;grid-row:1/span 3;grid-column:").Append(column).Append(";min-width:0\"><div class=\"san-progress-element\" data-progress-id=\"").Append(WebUtility.HtmlEncode(bar.Id)).Append("\" style=\"display:grid;grid-template-columns:auto minmax(0,1fr) auto;grid-template-rows:subgrid;grid-row:1/span 3;align-items:center;box-sizing:border-box;max-width:100%;width:").Append(bar.Width>0?N(bar.Width)+"px":"100%")
                        .Append(";padding:").Append(N(bar.Spacing)).Append("px ").Append(N(bar.RightInset)).Append("px 0 ").Append(N(bar.LeftInset)).Append("px\">");
                    html.Append("<div role=\"progressbar\" aria-valuemin=\"0\" aria-valuenow=\"").Append(N(Math.Max(0,Math.Min(snapshot.AchievementProgressCurrent.Value,snapshot.AchievementProgressMaximum.Value)))).Append("\" aria-valuemax=\"").Append(snapshot.AchievementProgressMaximum.Value).Append("\" style=\"grid-area:2/2;height:").Append(N(BarHeight(bar))).Append("px;border-radius:").Append(N(Math.Min(bar.CornerRadius,BarHeight(bar)/2))).Append("px;overflow:hidden;background:").Append(BarBackground(bar,false)).Append("\"><div style=\"height:100%;width:").Append(N(Fraction(snapshot)*100)).Append("%;border-radius:inherit;background:").Append(BarBackground(bar,true)).Append("\"></div></div>");
                    foreach(NotificationBarTextPosition position in Enum.GetValues(typeof(NotificationBarTextPosition)))
                    {
                        var texts=TextAt(bar,snapshot,position); if(texts.Count==0)continue;
                        var area=position==NotificationBarTextPosition.Above?"1/1/2/4":position==NotificationBarTextPosition.Below?"3/1/4/4":position==NotificationBarTextPosition.Left?"2/1":position==NotificationBarTextPosition.Right?"2/3":"2/2";
                        if(texts.Count>1) html.Append("<div style=\"display:grid;grid-template-columns:").Append(texts[0].Alignment==texts[1].Alignment?"minmax(0,1fr)":"repeat(3,minmax(0,1fr))").Append(";min-width:0;z-index:1;grid-area:").Append(area).Append("\">");
                        foreach(var text in texts)
                        {
                            html.Append("<span style=\"z-index:1;min-width:0;overflow-wrap:anywhere;").Append(texts.Count>1?"grid-column:"+(texts[0].Alignment==texts[1].Alignment?1:(int)text.Alignment+1):"grid-area:"+area).Append(position==NotificationBarTextPosition.Inside?";white-space:nowrap;overflow:hidden;text-overflow:ellipsis":"").Append(";text-align:").Append(text.Alignment.ToString().ToLowerInvariant()).Append(";padding:").Append(position==NotificationBarTextPosition.Inside?"0 6px":position==NotificationBarTextPosition.Left?"0 8px 0 0":position==NotificationBarTextPosition.Right?"0 0 0 8px":"3px 0").Append(WebUtility.HtmlEncode(FontCss(text.Font))).Append("\">").Append(WebUtility.HtmlEncode(text.Text)).Append("</span>");
                        }
                        if(texts.Count>1) html.Append("</div>");
                    }
                    html.Append("</div></div>");
                }
                html.Append("</div>");
            }
            return html.ToString();
        }

        internal static bool IsVisible(LocalSettings settings) =>
            settings?.OverlayCustomShowProgressBar == true &&
            settings.AchievementProgressCurrent.HasValue && settings.AchievementProgressMaximum > 0;

        internal static double Fraction(LocalSettings settings) => !IsVisible(settings) ? 0 :
            Math.Min(1, Math.Max(0, settings.AchievementProgressCurrent.Value / (double)settings.AchievementProgressMaximum.Value));

        private static Color ColorOrDefault(string value, Color fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(value); }
            catch { return fallback; }
        }

        private static string CssColor(string value, Color fallback)
        {
            var color = ColorOrDefault(value, fallback);
            return string.Format(CultureInfo.InvariantCulture, "rgba({0},{1},{2},{3:0.###})", color.R, color.G, color.B, color.A / 255d);
        }

        internal static FrameworkElement CreateWpf(LocalSettings settings)
        {
            if (!IsVisible(settings)) return null;
            var row = new Grid
            {
                Margin = new Thickness(0, settings.OverlayCustomProgressBarSpacing, 0, 0),
                HorizontalAlignment = settings.OverlayCustomProgressBarWidth > 0 ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
                RenderTransform = new TranslateTransform(settings.OverlayCustomProgressBarOffsetX, settings.OverlayCustomProgressBarOffsetY)
            };
            if (settings.OverlayCustomProgressBarWidth > 0) row.Width = settings.OverlayCustomProgressBarWidth;
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var radius = Math.Min(settings.OverlayCustomProgressBarCornerRadius, settings.OverlayCustomProgressBarHeight / 2);
            var track = new Border
            {
                Height = settings.OverlayCustomProgressBarHeight,
                CornerRadius = new CornerRadius(radius),
                Background = new SolidColorBrush(ColorOrDefault(settings.OverlayCustomProgressBarTrackColor, Color.FromArgb(48, 255, 255, 255))),
                VerticalAlignment = VerticalAlignment.Center,
                ClipToBounds = true
            };
            var fill = new Border
            {
                CornerRadius = new CornerRadius(radius),
                Background = new SolidColorBrush(ColorOrDefault(settings.OverlayCustomProgressBarFillColor, Color.FromRgb(167, 224, 255))),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0
            };
            // Size the fill instead of scaling it, preserving the chosen corner radius.
            track.SizeChanged += (_, __) => fill.Width = track.ActualWidth * Fraction(settings);
            track.Child = fill;
            row.Children.Add(track);
            if (settings.OverlayCustomProgressBarShowLabel)
            {
                var label = new TextBlock
                {
                    Text = CustomNotificationProgressTokens.Expand("<progress>", settings.AchievementProgressCurrent, settings.AchievementProgressMaximum),
                    Foreground = new SolidColorBrush(ColorOrDefault(settings.OverlayCustomProgressBarLabelColor, Colors.White)),
                    FontSize = settings.OverlayCustomProgressBarLabelFontSize,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(label, 1);
                row.Children.Add(label);
            }
            return row;
        }

        internal static string CreateHtml(LocalSettings settings)
        {
            if (!IsVisible(settings)) return string.Empty;
            string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
            var width = settings.OverlayCustomProgressBarWidth > 0 ? N(settings.OverlayCustomProgressBarWidth) + "px" : "100%";
            var radius = N(Math.Min(settings.OverlayCustomProgressBarCornerRadius, settings.OverlayCustomProgressBarHeight / 2));
            var label = settings.OverlayCustomProgressBarShowLabel
                ? "<span style=\"white-space:nowrap;color:" + CssColor(settings.OverlayCustomProgressBarLabelColor, Colors.White) +
                    ";font-size:" + N(settings.OverlayCustomProgressBarLabelFontSize) + "px\">" +
                    CustomNotificationProgressTokens.Expand("<progress>", settings.AchievementProgressCurrent, settings.AchievementProgressMaximum) + "</span>"
                : string.Empty;
            return "<div class=\"san-achievement-progress\" style=\"display:flex;align-items:center;gap:8px;flex-shrink:0;box-sizing:border-box;max-width:100%;width:" + width +
                ";margin-top:" + N(settings.OverlayCustomProgressBarSpacing) + "px;transform:translate(" + N(settings.OverlayCustomProgressBarOffsetX) + "px," + N(settings.OverlayCustomProgressBarOffsetY) + "px)\">" +
                "<div role=\"progressbar\" aria-valuemin=\"0\" aria-valuenow=\"" + N(Math.Max(0, Math.Min(settings.AchievementProgressCurrent.Value, settings.AchievementProgressMaximum.Value))) +
                "\" aria-valuemax=\"" + settings.AchievementProgressMaximum.Value.ToString(CultureInfo.InvariantCulture) + "\" style=\"flex:1;min-width:0;height:" + N(settings.OverlayCustomProgressBarHeight) +
                "px;border-radius:" + radius + "px;overflow:hidden;background:" + CssColor(settings.OverlayCustomProgressBarTrackColor, Color.FromArgb(48, 255, 255, 255)) + "\">" +
                "<div style=\"height:100%;width:" + N(Fraction(settings) * 100) + "%;border-radius:inherit;background:" + CssColor(settings.OverlayCustomProgressBarFillColor, Color.FromRgb(167, 224, 255)) + "\"></div></div>" + label + "</div>";
        }
    }
}
