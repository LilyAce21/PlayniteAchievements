using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace PlayniteAchievements.Providers.Local
{
    public enum NotificationBarSource { Achievement, GameAchievements, GamePoints, GameCollectionScore, GamePrestigeScore, CollectionLevelExperience, PrestigeLevelExperience }
    public enum NotificationBarTextPosition { Above, Inside, Below, Left, Right }
    public enum NotificationBarTextAlignment { Left, Center, Right }
    public enum NotificationBarPlacement { BeforeContent, AfterContent }
    public sealed class NotificationBarSourceOption
    {
        public NotificationBarSource Value { get; set; }
        public string Name { get; set; }
    }
    public static class NotificationBarSources
    {
        public static IReadOnlyList<NotificationBarSourceOption> All { get; } = new[]
        {
            new NotificationBarSourceOption { Value = NotificationBarSource.Achievement, Name = "Individual achievement" },
            new NotificationBarSourceOption { Value = NotificationBarSource.GameAchievements, Name = "Game achievements completed" },
            new NotificationBarSourceOption { Value = NotificationBarSource.GamePoints, Name = "Game achievement points" },
            new NotificationBarSourceOption { Value = NotificationBarSource.GameCollectionScore, Name = "Game collection score" },
            new NotificationBarSourceOption { Value = NotificationBarSource.GamePrestigeScore, Name = "Game prestige score" },
            new NotificationBarSourceOption { Value = NotificationBarSource.CollectionLevelExperience, Name = "Collection level experience" },
            new NotificationBarSourceOption { Value = NotificationBarSource.PrestigeLevelExperience, Name = "Prestige level experience" }
        };
        public static Array TextPositions => Enum.GetValues(typeof(NotificationBarTextPosition));
        public static Array TextAlignments => Enum.GetValues(typeof(NotificationBarTextAlignment));
        public static IReadOnlyList<NotificationBarPlacement> Placements { get; } = new[] { NotificationBarPlacement.BeforeContent, NotificationBarPlacement.AfterContent };
    }
    public sealed class NotificationProgressBarDefinition : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        private string _Id = Guid.NewGuid().ToString("N");
        public string Id { get => _Id; set => Set(ref _Id, value ?? string.Empty); }
        private bool _Enabled = true;
        public bool Enabled { get => _Enabled; set => Set(ref _Enabled, value); }
        private string _Title = "";
        public string Title { get => _Title; set => Set(ref _Title, value ?? string.Empty); }
        private NotificationBarSource _Source = NotificationBarSource.Achievement;
        public NotificationBarSource Source { get => _Source; set => Set(ref _Source, value); }
        private NotificationBarPlacement _Placement = NotificationBarPlacement.AfterContent;
        public NotificationBarPlacement Placement { get => _Placement; set => Set(ref _Placement, value); }
        private double _Width = 0;
        public double Width { get => _Width; set => Set(ref _Width, Math.Max(0, Math.Min(1200, double.IsNaN(value) || double.IsInfinity(value) ? 0 : value))); }
        private double _Height = 6;
        public double Height { get => _Height; set => Set(ref _Height, Math.Max(1, Math.Min(64, double.IsNaN(value) || double.IsInfinity(value) ? 6 : value))); }
        private double _CornerRadius = 3;
        public double CornerRadius { get => _CornerRadius; set => Set(ref _CornerRadius, Math.Max(0, Math.Min(64, double.IsNaN(value) || double.IsInfinity(value) ? 3 : value))); }
        private double _Spacing = 6;
        public double Spacing { get => _Spacing; set => Set(ref _Spacing, Math.Max(0, Math.Min(100, double.IsNaN(value) || double.IsInfinity(value) ? 6 : value))); }
        private string _FillColor = "#A7E0FF";
        public string FillColor { get => _FillColor; set => Set(ref _FillColor, value ?? string.Empty); }
        private string _TrackColor = "#30FFFFFF";
        public string TrackColor { get => _TrackColor; set => Set(ref _TrackColor, value ?? string.Empty); }
        private bool _ShowLabel = true;
        public bool ShowLabel { get => _ShowLabel; set => Set(ref _ShowLabel, value); }
        private string _LabelColor = "#FFFFFF";
        public string LabelColor { get => _LabelColor; set => Set(ref _LabelColor, value ?? string.Empty); }
        private double _LabelFontSize = 11;
        public double LabelFontSize { get => _LabelFontSize; set => Set(ref _LabelFontSize, Math.Max(6, Math.Min(48, double.IsNaN(value) || double.IsInfinity(value) ? 11 : value))); }
        private double _LeftInset = 0;
        public double LeftInset { get => _LeftInset; set => Set(ref _LeftInset, Math.Max(0, Math.Min(600, double.IsNaN(value) || double.IsInfinity(value) ? 0 : value))); }
        private double _RightInset = 0;
        public double RightInset { get => _RightInset; set => Set(ref _RightInset, Math.Max(0, Math.Min(600, double.IsNaN(value) || double.IsInfinity(value) ? 0 : value))); }
        private string _RowAnchorId="";
        public string RowAnchorId { get => _RowAnchorId; set => Set(ref _RowAnchorId,value ?? string.Empty); }
        private bool _IsExpanded=true;
        public bool IsExpanded { get => _IsExpanded; set => Set(ref _IsExpanded,value); }
        [JsonIgnore] internal IList<NotificationProgressBarDefinition> RowPeers { get; set; }
        [JsonIgnore] public string EditorTitle => "Bar " + ((RowPeers?.IndexOf(this) ?? 0)+1) + " ? " + (string.IsNullOrWhiteSpace(Title)?NotificationBarSources.All.FirstOrDefault(option=>option.Value==Source)?.Name ?? Source.ToString():Title);
        [JsonIgnore] public IReadOnlyList<NotificationBarRowOption> RowTargets => new[] { new NotificationBarRowOption { Id="",Name="Separate row" } }
            .Concat((RowPeers ?? new List<NotificationProgressBarDefinition>()).Where(bar=>bar!=this && !NotificationBarLayout.DependsOn(RowPeers,bar,this)).Select(bar=>new NotificationBarRowOption { Id=bar.Id,Name=bar.EditorTitle })).ToList();
        internal void RefreshRowEditor() { Notify(nameof(RowTargets));Notify(nameof(EditorTitle)); }
        private bool _SameRowAsPrevious = false;
        public bool SameRowAsPrevious { get => _SameRowAsPrevious; set => Set(ref _SameRowAsPrevious, value); }
        private NotificationBarTextPosition _TextPosition = NotificationBarTextPosition.Right;
        public NotificationBarTextPosition TextPosition { get => _TextPosition; set => Set(ref _TextPosition, value); }
        private NotificationBarTextPosition _CaptionPosition = NotificationBarTextPosition.Above;
        public NotificationBarTextPosition CaptionPosition { get => _CaptionPosition; set => Set(ref _CaptionPosition, value); }
        private NotificationBarTextAlignment _TextAlignment = NotificationBarTextAlignment.Left;
        public NotificationBarTextAlignment TextAlignment
        {
            get => _TextAlignment;
            set { Set(ref _TextAlignment, value); if (!_CounterAlignment.HasValue) Notify(nameof(CounterAlignment)); if (!_CaptionAlignment.HasValue) Notify(nameof(CaptionAlignment)); }
        }
        private NotificationBarTextAlignment? _CounterAlignment;
        public NotificationBarTextAlignment CounterAlignment { get => _CounterAlignment ?? _TextAlignment; set => Set(ref _CounterAlignment, (NotificationBarTextAlignment?)value); }
        private NotificationBarTextAlignment? _CaptionAlignment;
        public NotificationBarTextAlignment CaptionAlignment { get => _CaptionAlignment ?? _TextAlignment; set => Set(ref _CaptionAlignment, (NotificationBarTextAlignment?)value); }
        private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        private NotificationBarFont _captionFont;
        public NotificationBarFont CaptionFont
        {
            get { if (_captionFont==null) { _captionFont=new NotificationBarFont { Size=LabelFontSize, Color=LabelColor }; _captionFont.PropertyChanged+=CaptionFontChanged; } return _captionFont; }
            set { if (_captionFont!=null) _captionFont.PropertyChanged-=CaptionFontChanged; _captionFont=value ?? new NotificationBarFont(); _captionFont.PropertyChanged+=CaptionFontChanged; Notify(nameof(CaptionFont)); }
        }
        private void CaptionFontChanged(object sender, PropertyChangedEventArgs args) => Notify(nameof(CaptionFont));
        private NotificationBarFont _counterFont;
        public NotificationBarFont CounterFont
        {
            get { if (_counterFont==null) { _counterFont=new NotificationBarFont { Size=LabelFontSize, Color=LabelColor }; _counterFont.PropertyChanged+=CounterFontChanged; } return _counterFont; }
            set { if (_counterFont!=null) _counterFont.PropertyChanged-=CounterFontChanged; _counterFont=value ?? new NotificationBarFont(); _counterFont.PropertyChanged+=CounterFontChanged; Notify(nameof(CounterFont)); }
        }
        private void CounterFontChanged(object sender, PropertyChangedEventArgs args) => Notify(nameof(CounterFont));
        private bool _FillGradientEnabled = false;
        public bool FillGradientEnabled { get => _FillGradientEnabled; set => Set(ref _FillGradientEnabled, value); }
        private bool _TrackGradientEnabled = false;
        public bool TrackGradientEnabled { get => _TrackGradientEnabled; set => Set(ref _TrackGradientEnabled, value); }
        private string _FillGradientColors = "#A7E0FF, #6C63FF";
        public string FillGradientColors { get => _FillGradientColors; set { Set(ref _FillGradientColors, value ?? string.Empty); Notify(nameof(FillGradientStops)); Notify(nameof(CanAddFillGradientColor)); } }
        [JsonIgnore] public IReadOnlyList<NotificationGradientColor> FillGradientStops => GetGradientColors(true).Select((color,index) => new NotificationGradientColor(this,true,index,color)).ToList();
        [JsonIgnore] public bool CanAddFillGradientColor => GetGradientColors(true).Count < 32;
        private string _TrackGradientColors = "#303030, #606060";
        public string TrackGradientColors { get => _TrackGradientColors; set { Set(ref _TrackGradientColors, value ?? string.Empty); Notify(nameof(TrackGradientStops)); Notify(nameof(CanAddTrackGradientColor)); } }
        [JsonIgnore] public IReadOnlyList<NotificationGradientColor> TrackGradientStops => GetGradientColors(false).Select((color,index) => new NotificationGradientColor(this,false,index,color)).ToList();
        [JsonIgnore] public bool CanAddTrackGradientColor => GetGradientColors(false).Count < 32;
        private double _GradientAngle = 0;
        public double GradientAngle { get => _GradientAngle; set => Set(ref _GradientAngle, double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Max(0, Math.Min(360, value))); }
        public List<string> GetGradientColors(bool fill) => (fill ? FillGradientColors : TrackGradientColors)
            .Split(new[] { ',', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        public void SetGradientColor(bool fill, int index, string color)
        {
            var colors=GetGradientColors(fill);
            if (index < 0) { if (colors.Count >= 32) return; colors.Add(color); }
            else if (index < colors.Count) { if (color == null) colors.RemoveAt(index); else colors[index]=color; }
            else return;
            if (fill) FillGradientColors=string.Join(", ",colors); else TrackGradientColors=string.Join(", ",colors);
        }
        public NotificationProgressBarDefinition Clone() => JsonConvert.DeserializeObject<NotificationProgressBarDefinition>(JsonConvert.SerializeObject(this));
    }
    public sealed class NotificationBarRowOption
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }
    internal static class NotificationBarLayout
    {
        internal static bool DependsOn(IList<NotificationProgressBarDefinition> bars,NotificationProgressBarDefinition bar,NotificationProgressBarDefinition target)
        {
            var visited=new HashSet<NotificationProgressBarDefinition>();
            while(bar!=null && visited.Add(bar)) { if(bar==target)return true;bar=bars?.FirstOrDefault(item=>!string.IsNullOrEmpty(bar.RowAnchorId) && item.Id==bar.RowAnchorId); }
            return false;
        }
        internal static NotificationProgressBarDefinition Root(IList<NotificationProgressBarDefinition> bars,NotificationProgressBarDefinition bar)
        {
            var path=new List<NotificationProgressBarDefinition>();
            while(!path.Contains(bar))
            {
                path.Add(bar);
                var parent=bars.FirstOrDefault(item=>!string.IsNullOrEmpty(bar.RowAnchorId) && item.Id==bar.RowAnchorId);
                if(parent==null)return bar;
                bar=parent;
            }
            return path.Skip(path.IndexOf(bar)).OrderBy(item=>bars.IndexOf(item)).First();
        }
        internal static void Migrate(IList<NotificationProgressBarDefinition> bars)
        {
            for(var i=0;i<bars.Count;i++)
            {
                var bar=bars[i];
                if(!bar.SameRowAsPrevious)continue;
                if(string.IsNullOrEmpty(bar.RowAnchorId))bar.RowAnchorId=bars.Take(i).LastOrDefault(item=>item.Placement==bar.Placement)?.Id ?? bars.Take(i).LastOrDefault()?.Id ?? "";
                bar.SameRowAsPrevious=false;
            }
        }
        internal static IEnumerable<List<NotificationProgressBarDefinition>> Groups(IList<NotificationProgressBarDefinition> bars)
        {
            // Resolve old flags on copies without mutating the notification's saved settings.
            var legacy=bars.Any(bar=>bar.SameRowAsPrevious);
            if(legacy) { bars=bars.Select(bar=>bar.Clone()).ToList();Migrate(bars); }
            return bars.GroupBy(bar=>Root(bars,bar)).Select(group=>group.ToList());
        }
        internal static void ReparentRemovedBar(IList<NotificationProgressBarDefinition> bars,NotificationProgressBarDefinition removed)
        {
            var root=Root(bars,removed);
            var replacement=bars.FirstOrDefault(bar=>bar!=removed && Root(bars,bar)==root);
            if(replacement==null)return;
            if(root!=removed)replacement=root;
            if(replacement.RowAnchorId==removed.Id)replacement.RowAnchorId="";
            foreach(var bar in bars.Where(bar=>bar!=removed && bar!=replacement && bar.RowAnchorId==removed.Id).ToList())bar.RowAnchorId=replacement.Id;
        }
    }
    public sealed class NotificationBarFont : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Set<T>(ref T field,T value,[CallerMemberName] string name=null) { if(EqualityComparer<T>.Default.Equals(field,value))return;field=value;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name)); }
        private string _Family="Default";
        public string Family { get => _Family; set => Set(ref _Family,value ?? string.Empty); }
        private double _Size=11;
        public double Size { get => _Size; set => Set(ref _Size,double.IsNaN(value) || double.IsInfinity(value) ? 11 : Math.Max(6,Math.Min(48,value))); }
        private string _Color="#FFFFFF";
        public string Color { get => _Color; set => Set(ref _Color,value ?? string.Empty); }
        private bool _Bold=false;
        public bool Bold { get => _Bold; set => Set(ref _Bold,value); }
        private bool _Italic=false;
        public bool Italic { get => _Italic; set => Set(ref _Italic,value); }
        private bool _Underline=false;
        public bool Underline { get => _Underline; set => Set(ref _Underline,value); }
        private bool _Strikethrough=false;
        public bool Strikethrough { get => _Strikethrough; set => Set(ref _Strikethrough,value); }
        private bool _OutlineEnabled=false;
        public bool OutlineEnabled { get => _OutlineEnabled; set => Set(ref _OutlineEnabled,value); }
        private string _OutlineColor="#000000";
        public string OutlineColor { get => _OutlineColor; set => Set(ref _OutlineColor,value ?? string.Empty); }
        private double _OutlineSize=0.6;
        public double OutlineSize { get => _OutlineSize; set => Set(ref _OutlineSize,double.IsNaN(value) || double.IsInfinity(value) ? 0.6 : Math.Max(0,Math.Min(20,value))); }
        private bool _ShadowEnabled=false;
        public bool ShadowEnabled { get => _ShadowEnabled; set => Set(ref _ShadowEnabled,value); }
        private string _ShadowColor="#000000";
        public string ShadowColor { get => _ShadowColor; set => Set(ref _ShadowColor,value ?? string.Empty); }
        private double _ShadowSize=2;
        public double ShadowSize { get => _ShadowSize; set => Set(ref _ShadowSize,double.IsNaN(value) || double.IsInfinity(value) ? 2 : Math.Max(0,Math.Min(20,value))); }
    }
    public sealed class NotificationGradientColor
    {
        public NotificationGradientColor(NotificationProgressBarDefinition bar, bool fill, int index, string color) { Bar=bar; Fill=fill; Index=index; _Color=color; }
        public NotificationProgressBarDefinition Bar { get; }
        public bool Fill { get; }
        public int Index { get; }
        private string _Color;
        public string Color
        {
            get => _Color;
            set { try { System.Windows.Media.ColorConverter.ConvertFromString(value); } catch { return; } _Color=value; Bar.SetGradientColor(Fill,Index,value); }
        }
        public string SwatchColor
        {
            get { try { return ((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Color)).ToString(); } catch { return "#FFFFFF"; } }
        }
    }
    internal sealed class NotificationBarCounter
    {
        public int Current { get; set; }
        public int Maximum { get; set; }
    }
}
