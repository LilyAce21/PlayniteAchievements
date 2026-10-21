using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class CustomNotificationProgressBarTests
    {
        [TestMethod]
        public void SettingsPreviewSuppliesEveryBarSourceWithoutSavingSampleCounters()
        {
            var settings=new LocalSettings { OverlayCustomPreviewShowProgress=true };
            foreach(NotificationBarSource source in Enum.GetValues(typeof(NotificationBarSource)))settings.OverlayCustomProgressBars.Add(new NotificationProgressBarDefinition { Source=source });
            var before=JsonConvert.SerializeObject(settings);
            var copy=CustomNotificationProgressTokens.CreatePreviewSettingsCopy(settings);
            Assert.AreEqual(16,copy.AchievementProgressCurrent);Assert.AreEqual(100,copy.AchievementProgressMaximum);
            Assert.AreEqual(7,CustomNotificationProgressBar.Rows(copy,NotificationBarPlacement.AfterContent).Sum(row=>row.Count));
            var html=CustomNotificationProgressBar.CreateElementsHtml(copy,NotificationBarPlacement.AfterContent);
            foreach(var label in new[]{"16/100","27/100","120/500","180/600","240/800","40/100","65/100"})StringAssert.Contains(html,label);
            Assert.AreEqual(before,JsonConvert.SerializeObject(settings));Assert.IsNull(settings.AchievementProgressCurrent);Assert.IsNull(settings.NotificationBarCounters);
            Assert.IsFalse(JsonConvert.SerializeObject(copy).Contains("NotificationBarCounters"));
        }

        [TestMethod]
        public void SettingsPreviewHonorsHiddenIndividualCounterWithoutHidingGameBars()
        {
            var settings=new LocalSettings { OverlayCustomPreviewShowProgress=false };
            var individual=new NotificationProgressBarDefinition { Id="individual" };
            settings.OverlayCustomProgressBars.Add(individual);
            settings.OverlayCustomProgressBars.Add(new NotificationProgressBarDefinition { Id="game",RowAnchorId=individual.Id,Source=NotificationBarSource.GameAchievements });
            var copy=CustomNotificationProgressTokens.CreatePreviewSettingsCopy(settings);
            Assert.IsNull(copy.AchievementProgressCurrent);Assert.IsNull(copy.AchievementProgressMaximum);
            Assert.AreEqual("game",CustomNotificationProgressBar.Rows(copy,NotificationBarPlacement.AfterContent).Single().Single().Id);
            StringAssert.Contains(CustomNotificationProgressBar.CreateElementsHtml(copy,NotificationBarPlacement.AfterContent),"27/100");
            var live=CustomNotificationProgressTokens.CreateSettingsCopy(settings,null,null);
            Assert.AreEqual("",CustomNotificationProgressBar.CreateElementsHtml(live,NotificationBarPlacement.AfterContent));
        }

        [TestMethod]
        public void LinkedRowsFollowEitherBarsPlacementAcrossReorderingAndRemoval()
        {
            var settings=new LocalSettings();
            var first=new NotificationProgressBarDefinition { Id="first",Title="Progress" };
            var second=new NotificationProgressBarDefinition { Id="second",Source=NotificationBarSource.GameAchievements,RowAnchorId="first" };
            var third=new NotificationProgressBarDefinition { Id="third",Source=NotificationBarSource.GamePoints,RowAnchorId="second" };
            settings.OverlayCustomProgressBars.Add(first);settings.OverlayCustomProgressBars.Add(second);settings.OverlayCustomProgressBars.Add(third);
            Assert.IsFalse(first.RowTargets.Any(option=>option.Id==second.Id));
            first.Placement=NotificationBarPlacement.BeforeContent;
            Assert.AreEqual(first.Placement,second.Placement);Assert.AreEqual(first.Placement,third.Placement);
            second.Placement=NotificationBarPlacement.AfterContent;Assert.AreEqual(second.Placement,first.Placement);
            settings.OverlayCustomProgressBars.Move(2,0);first.Placement=NotificationBarPlacement.BeforeContent;
            Assert.AreEqual(first.Placement,second.Placement);Assert.AreEqual(first.Placement,third.Placement);
            var copy=CustomNotificationProgressTokens.CreateSettingsCopy(settings,16,100,counters:new Dictionary<NotificationBarSource,NotificationBarCounter> {
                [NotificationBarSource.GameAchievements]=new NotificationBarCounter { Current=27,Maximum=100 },[NotificationBarSource.GamePoints]=new NotificationBarCounter { Current=120,Maximum=500 }
            });
            Assert.AreEqual(3,CustomNotificationProgressBar.Rows(copy,NotificationBarPlacement.BeforeContent).Single().Count);
            Assert.AreEqual(0,CustomNotificationProgressBar.Rows(copy,NotificationBarPlacement.AfterContent).Count());
            NotificationBarLayout.ReparentRemovedBar(settings.OverlayCustomProgressBars,first);settings.OverlayCustomProgressBars.Remove(first);
            Assert.AreEqual(1,NotificationBarLayout.Groups(settings.OverlayCustomProgressBars).Count());
            Assert.IsFalse(settings.OverlayCustomProgressBars.Any(bar=>bar.RowAnchorId=="first"));
            second.IsExpanded=false;
            var restored=JsonConvert.DeserializeObject<LocalSettings>(JsonConvert.SerializeObject(settings));
            Assert.IsFalse(restored.OverlayCustomProgressBars.Single(bar=>bar.Id=="second").IsExpanded);
            Assert.IsFalse(JsonConvert.SerializeObject(settings).Contains("RowTargets"));
        }

        [TestMethod]
        public void ImportedRowCyclesResolveWithoutRecursionAndLegacyRowsMigrate()
        {
            var bars=new List<NotificationProgressBarDefinition> {
                new NotificationProgressBarDefinition { Id="a",RowAnchorId="b" },new NotificationProgressBarDefinition { Id="b",RowAnchorId="a" }
            };
            Assert.AreEqual("a",NotificationBarLayout.Root(bars,bars[1]).Id);
            Assert.AreEqual(1,NotificationBarLayout.Groups(bars).Count());
            var legacy=LocalSettings.CloneProgressBars(new[] { new NotificationProgressBarDefinition { Id="leader" },new NotificationProgressBarDefinition { SameRowAsPrevious=true } });
            Assert.AreEqual("leader",legacy[1].RowAnchorId);Assert.IsFalse(legacy[1].SameRowAsPrevious);
        }

        [TestMethod]
        public void EquivalentRgbAndArgbColorsKeepIdenticalForegroundPixels()
        {
            Exception failure=null;
            var thread=new Thread(()=>
            {
                try
                {
                    var settings=new LocalSettings();var bar=new NotificationProgressBarDefinition { TextPosition=NotificationBarTextPosition.Inside,CounterAlignment=NotificationBarTextAlignment.Center };
                    settings.OverlayCustomProgressBars.Add(bar);
                    byte[] Pixels(string color)
                    {
                        bar.CounterFont.Color=color;
                        var panel=CustomNotificationProgressBar.CreateElementsWpf(CustomNotificationProgressTokens.CreateSettingsCopy(settings,27,100),NotificationBarPlacement.AfterContent);
                        panel.Measure(new Size(300,double.PositiveInfinity));var height=(int)Math.Ceiling(panel.DesiredSize.Height);panel.Arrange(new Rect(0,0,300,height));panel.UpdateLayout();
                        panel.Dispatcher.Invoke(new Action(()=>{}),System.Windows.Threading.DispatcherPriority.Render);
                        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(300,height,96,96,PixelFormats.Pbgra32);bitmap.Render(panel);
                        var pixels=new byte[300*height*4];bitmap.CopyPixels(pixels,300*4,0);return pixels;
                    }
                    CollectionAssert.AreEqual(Pixels("#FFFFFF"),Pixels("#FFFFFFFF"));
                }
                catch(Exception ex){failure=ex;}
            });
            thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));
            if(failure!=null)throw failure;
        }

        [TestMethod]
        public void IndependentFontStylesRenderAndNotifyWithoutChangingTheOtherFont()
        {
            Exception failure=null;
            var thread=new Thread(()=>
            {
                try
                {
                    var bar=new NotificationProgressBarDefinition { Title="Cats", CaptionPosition=NotificationBarTextPosition.Inside, TextPosition=NotificationBarTextPosition.Inside, CaptionAlignment=NotificationBarTextAlignment.Left, CounterAlignment=NotificationBarTextAlignment.Center };
                    var settings=new LocalSettings();settings.OverlayCustomProgressBars.Add(bar);
                    var changes=0;settings.PropertyChanged+=(_,args)=>{if(args.PropertyName==nameof(settings.OverlayCustomProgressBars))changes++;};
                    bar.CaptionFont.Family="Consolas";bar.CaptionFont.Size=18;bar.CaptionFont.Color="#42D6BC";bar.CaptionFont.Bold=true;bar.CaptionFont.Italic=true;bar.CaptionFont.Underline=true;bar.CaptionFont.Strikethrough=true;
                    bar.CaptionFont.OutlineEnabled=true;bar.CaptionFont.OutlineSize=1;bar.CaptionFont.ShadowEnabled=true;bar.CaptionFont.ShadowSize=4;
                    Assert.IsTrue(changes>0);Assert.IsFalse(bar.CounterFont.Bold);Assert.AreEqual(11d,bar.CounterFont.Size);
                    var copy=CustomNotificationProgressTokens.CreateSettingsCopy(settings,16,100);
                    var panel=CustomNotificationProgressBar.CreateElementsWpf(copy,NotificationBarPlacement.AfterContent);
                    panel.Measure(new Size(600,double.PositiveInfinity));panel.Arrange(new Rect(0,0,600,panel.DesiredSize.Height));panel.UpdateLayout();
                    var element=(Grid)((Grid)panel.Children[0]).Children[0];var group=element.Children.OfType<Grid>().Single();
                    var wrapper=(Grid)group.Children[0];var caption=wrapper.Children.OfType<TextBlock>().Single(item=>Equals(item.Tag,"foreground"));var counter=(TextBlock)group.Children[1];
                    Assert.AreEqual("Consolas",caption.FontFamily.Source);Assert.AreEqual(18d,caption.FontSize);Assert.AreEqual(FontWeights.Bold,caption.FontWeight);Assert.AreEqual(FontStyles.Italic,caption.FontStyle);
                    Assert.AreEqual(2,caption.TextDecorations.Count);Assert.IsNull(caption.Effect);Assert.IsNull(wrapper.Effect);Assert.AreEqual(8,wrapper.Children.OfType<TextBlock>().Count(item=>Equals(item.Tag,"outline")));Assert.IsInstanceOfType(wrapper.Children.OfType<TextBlock>().Single(item=>Equals(item.Tag,"shadow")).Effect,typeof(System.Windows.Media.Effects.BlurEffect));Assert.AreEqual(FontWeights.Normal,counter.FontWeight);
                    Assert.IsTrue(((Border)element.Children[0]).ActualHeight>=Math.Floor(18*1.4+6));
                    var html=CustomNotificationProgressBar.CreateElementsHtml(copy,NotificationBarPlacement.AfterContent);
                    StringAssert.Contains(html,"font-weight:bold");StringAssert.Contains(html,"font-style:italic");StringAssert.Contains(html,"underline line-through");StringAssert.Contains(html,"-webkit-text-stroke:2px");StringAssert.Contains(html,"text-shadow:0 2px 4px");
                    var restored=JsonConvert.DeserializeObject<LocalSettings>(JsonConvert.SerializeObject(settings));
                    Assert.IsTrue(restored.OverlayCustomProgressBars[0].CaptionFont.Bold);Assert.IsFalse(restored.OverlayCustomProgressBars[0].CounterFont.Bold);
                    restored.OverlayCustomProgressBars[0].CaptionFont.Size=30;Assert.AreEqual(18d,bar.CaptionFont.Size);
                    var legacy=JsonConvert.DeserializeObject<NotificationProgressBarDefinition>("{\"LabelFontSize\":15,\"LabelColor\":\"#123456\"}");
                    Assert.AreEqual(15d,legacy.CaptionFont.Size);Assert.AreEqual("#123456",legacy.CounterFont.Color);
                }
                catch(Exception ex){failure=ex;}
            });
            thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));
            if(failure!=null)throw failure;
        }

        [TestMethod]
        public void MissingAchievementCounterExpandsTheRemainingBarAndRemovesEmptyRows()
        {
            Exception failure=null;
            var thread=new Thread(()=>
            {
                try
                {
                    var settings=new LocalSettings();
                    settings.OverlayCustomProgressBars.Add(new NotificationProgressBarDefinition { Source=NotificationBarSource.Achievement });
                    settings.OverlayCustomProgressBars.Add(new NotificationProgressBarDefinition { Source=NotificationBarSource.GameAchievements,SameRowAsPrevious=true });
                    var counters=new Dictionary<NotificationBarSource,NotificationBarCounter> { [NotificationBarSource.GameAchievements]=new NotificationBarCounter { Current=27,Maximum=100 } };
                    var available=CustomNotificationProgressTokens.CreateSettingsCopy(settings,16,100,counters:counters);
                    var missing=CustomNotificationProgressTokens.CreateSettingsCopy(settings,null,null,counters:counters);
                    var pair=CustomNotificationProgressBar.CreateElementsWpf(available,NotificationBarPlacement.AfterContent);
                    var single=CustomNotificationProgressBar.CreateElementsWpf(missing,NotificationBarPlacement.AfterContent);
                    foreach(var panel in new[]{pair,single}) { panel.Measure(new Size(600,double.PositiveInfinity));panel.Arrange(new Rect(0,0,600,panel.DesiredSize.Height));panel.UpdateLayout(); }
                    Assert.AreEqual(2,((Grid)pair.Children[0]).Children.Count);Assert.AreEqual(1,((Grid)single.Children[0]).Children.Count);
                    var remaining=(Grid)((Grid)single.Children[0]).Children[0];Assert.AreEqual(600d,remaining.ActualWidth,0.01);
                    StringAssert.Contains(CustomNotificationProgressBar.CreateElementsHtml(missing,NotificationBarPlacement.AfterContent),"repeat(1,minmax(0,1fr))");
                    settings.OverlayCustomProgressBars.RemoveAt(1);
                    var empty=CustomNotificationProgressBar.CreateElementsWpf(CustomNotificationProgressTokens.CreateSettingsCopy(settings,null,null),NotificationBarPlacement.AfterContent);
                    empty.Measure(new Size(600,double.PositiveInfinity));Assert.AreEqual(0,empty.Children.Count);Assert.AreEqual(0d,empty.DesiredSize.Height);
                    Assert.AreEqual("",CustomNotificationProgressBar.CreateElementsHtml(CustomNotificationProgressTokens.CreateSettingsCopy(settings,null,null),NotificationBarPlacement.AfterContent));
                }
                catch(Exception ex){failure=ex;}
            });
            thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));
            if(failure!=null)throw failure;
        }

        [TestMethod]
        public void GradientColorEditsRefreshStopsAndRoundTripWithoutSavingEditorObjects()
        {
            var bar=new NotificationProgressBarDefinition();
            var notifications=new List<string>();bar.PropertyChanged+=(_,args)=>notifications.Add(args.PropertyName);
            bar.SetGradientColor(true,-1,"#80FF3366");
            Assert.AreEqual(3,bar.FillGradientStops.Count);
            StringAssert.Contains(bar.FillGradientStops[2].SwatchColor,"80FF3366");
            Assert.IsTrue(notifications.Contains(nameof(bar.FillGradientStops)));
            bar.FillGradientStops[0].Color="#112233";
            Assert.AreEqual("#112233",bar.GetGradientColors(true)[0]);
            bar.SetGradientColor(true,1,null);Assert.AreEqual(2,bar.FillGradientStops.Count);
            Assert.AreEqual("#80FF3366",bar.FillGradientStops[1].Color);
            var json=JsonConvert.SerializeObject(bar);Assert.IsFalse(json.Contains("GradientStops"));Assert.IsFalse(json.Contains("CanAdd"));
            Assert.AreEqual(bar.FillGradientColors,JsonConvert.DeserializeObject<NotificationProgressBarDefinition>(json).FillGradientColors);
            while(bar.CanAddFillGradientColor)bar.SetGradientColor(true,-1,"#FFFFFF");
            bar.SetGradientColor(true,-1,"#000000");Assert.AreEqual(32,bar.FillGradientStops.Count);
        }

        [TestMethod]
        public void CaptionAndCounterAlignIndependentlyInsideTheSameBar()
        {
            Exception failure=null;
            var thread=new Thread(()=>
            {
                try
                {
                    var bar=new NotificationProgressBarDefinition { Title="Cats", CaptionPosition=NotificationBarTextPosition.Inside, TextPosition=NotificationBarTextPosition.Inside, CaptionAlignment=NotificationBarTextAlignment.Left, CounterAlignment=NotificationBarTextAlignment.Center };
                    var settings=new LocalSettings();settings.OverlayCustomProgressBars.Add(bar);
                    var copy=CustomNotificationProgressTokens.CreateSettingsCopy(settings,16,100);
                    var panel=CustomNotificationProgressBar.CreateElementsWpf(copy,NotificationBarPlacement.AfterContent);
                    panel.Measure(new Size(600,double.PositiveInfinity));panel.Arrange(new Rect(0,0,600,panel.DesiredSize.Height));panel.UpdateLayout();
                    var element=(Grid)((Grid)panel.Children[0]).Children[0];
                    var group=element.Children.OfType<Grid>().Single();
                    var caption=(TextBlock)group.Children[0];var counter=(TextBlock)group.Children[1];
                    Assert.AreEqual("Cats",caption.Text);Assert.AreEqual("16/100",counter.Text);
                    Assert.AreEqual(0,Grid.GetColumn(caption));Assert.AreEqual(1,Grid.GetColumn(counter));
                    Assert.AreEqual(TextAlignment.Left,caption.TextAlignment);Assert.AreEqual(TextAlignment.Center,counter.TextAlignment);
                    var track=(Border)element.Children[0];
                    Assert.AreEqual(track.TranslatePoint(new Point(track.ActualWidth/2,0),element).X,counter.TranslatePoint(new Point(counter.ActualWidth/2,0),element).X,0.01);
                    Assert.IsTrue(caption.TranslatePoint(new Point(caption.ActualWidth,0),element).X<=counter.TranslatePoint(new Point(),element).X);
                    var html=CustomNotificationProgressBar.CreateElementsHtml(copy,NotificationBarPlacement.AfterContent);
                    StringAssert.Contains(html,"text-align:left");StringAssert.Contains(html,"grid-column:2");StringAssert.Contains(html,"text-align:center");
                    var restored=JsonConvert.DeserializeObject<NotificationProgressBarDefinition>(JsonConvert.SerializeObject(bar));
                    Assert.AreEqual(NotificationBarTextAlignment.Left,restored.CaptionAlignment);Assert.AreEqual(NotificationBarTextAlignment.Center,restored.CounterAlignment);
                    var legacy=JsonConvert.DeserializeObject<NotificationProgressBarDefinition>("{\"TextAlignment\":2}");
                    Assert.AreEqual(NotificationBarTextAlignment.Right,legacy.CaptionAlignment);Assert.AreEqual(NotificationBarTextAlignment.Right,legacy.CounterAlignment);
                }
                catch(Exception ex){failure=ex;}
            });
            thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));
            if(failure!=null)throw failure;
        }

        [TestMethod]
        public void HidingARowLeaderPreservesTheFollowingBarsRow()
        {
            var settings=new LocalSettings();
            settings.OverlayCustomProgressBars.Add(new NotificationProgressBarDefinition());
            var leader=new NotificationProgressBarDefinition { Enabled=false, Source=NotificationBarSource.GamePoints };
            settings.OverlayCustomProgressBars.Add(leader);
            settings.OverlayCustomProgressBars.Add(new NotificationProgressBarDefinition { SameRowAsPrevious=true });
            var copy=CustomNotificationProgressTokens.CreateSettingsCopy(settings,16,100);
            Assert.AreEqual(2,CustomNotificationProgressBar.Rows(copy,NotificationBarPlacement.AfterContent).Count());
            leader.Enabled=true;
            copy=CustomNotificationProgressTokens.CreateSettingsCopy(settings,16,100);
            Assert.AreEqual(2,CustomNotificationProgressBar.Rows(copy,NotificationBarPlacement.AfterContent).Count());
        }

        [TestMethod]
        public void SharedRowsRenderCenteredInsideTextAndMultipleGradientStops()
        {
            Exception failure=null;
            var thread=new Thread(()=>
            {
                try
                {
                    var settings=new LocalSettings();
                    var first=new NotificationProgressBarDefinition { Id="first", Title="Cats", CaptionPosition=NotificationBarTextPosition.Inside, TextPosition=NotificationBarTextPosition.Inside, TextAlignment=NotificationBarTextAlignment.Center, FillGradientEnabled=true, FillGradientColors="#FF0000, #00FF00, #0000FF", TrackGradientEnabled=true, TrackGradientColors="#80000000, #80FFFFFF", GradientAngle=45 };
                    settings.OverlayCustomProgressBars.Add(first);
                    settings.OverlayCustomProgressBars.Add(new NotificationProgressBarDefinition { Id="second", SameRowAsPrevious=true });
                    settings.OverlayCustomProgressBars.Add(new NotificationProgressBarDefinition { Id="third", TextPosition=NotificationBarTextPosition.Below });
                    var copy=CustomNotificationProgressTokens.CreateSettingsCopy(settings,16,100);
                    var panel=CustomNotificationProgressBar.CreateElementsWpf(copy,NotificationBarPlacement.AfterContent);
                    panel.Measure(new Size(600,double.PositiveInfinity));
                    panel.Arrange(new Rect(0,0,600,panel.DesiredSize.Height)); panel.UpdateLayout();
                    Assert.AreEqual(2,panel.Children.Count);
                    var row=(Grid)panel.Children[0];Assert.AreEqual(2,row.Children.Count);
                    var element=(Grid)row.Children[0];var other=(Grid)row.Children[1];
                    var left=element.TranslatePoint(new Point(),panel);var right=other.TranslatePoint(new Point(),panel);
                    Assert.IsTrue(left.X+element.ActualWidth<=right.X+0.01);
                    Assert.AreEqual(left.Y+element.ActualHeight/2,right.Y+other.ActualHeight/2,0.01);
                    var track=(Border)element.Children[0];
                    Assert.AreEqual(2,((LinearGradientBrush)track.Background).GradientStops.Count);
                    Assert.AreEqual(3,((LinearGradientBrush)((Border)track.Child).Background).GradientStops.Count);
                    Assert.IsTrue(track.ActualHeight>=Math.Floor(first.LabelFontSize*1.4+6));
                    var label=element.Children.OfType<TextBlock>().Single();
                    Assert.AreEqual("Cats 16/100",label.Text);Assert.AreEqual(TextAlignment.Center,label.TextAlignment);
                    Assert.AreEqual(Grid.GetRow(track),Grid.GetRow(label));Assert.AreEqual(Grid.GetColumn(track),Grid.GetColumn(label));
                    var html=CustomNotificationProgressBar.CreateElementsHtml(copy,NotificationBarPlacement.AfterContent);
                    StringAssert.Contains(html,"linear-gradient(135deg,");StringAssert.Contains(html,"text-align:center");StringAssert.Contains(html,"Cats 16/100");
                    var restored=JsonConvert.DeserializeObject<LocalSettings>(JsonConvert.SerializeObject(settings));
                    Assert.IsFalse(restored.OverlayCustomProgressBars[1].SameRowAsPrevious);Assert.AreEqual(restored.OverlayCustomProgressBars[0].Id,restored.OverlayCustomProgressBars[1].RowAnchorId);
                    Assert.AreEqual(first.FillGradientColors,restored.OverlayCustomProgressBars[0].FillGradientColors);
                    Assert.AreEqual(NotificationBarTextPosition.Inside,restored.OverlayCustomProgressBars[0].TextPosition);
                }
                catch(Exception ex){failure=ex;}
            });
            thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));
            if(failure!=null)throw failure;
        }

        [TestMethod]
        public void InvalidGradientStopsUseSolidFallbackWithoutInjectingMarkup()
        {
            var settings=new LocalSettings();
            settings.OverlayCustomProgressBars.Add(new NotificationProgressBarDefinition { FillGradientEnabled=true, FillGradientColors="invalid, <script>alert(1)</script>", Title="<Cats>" });
            var copy=CustomNotificationProgressTokens.CreateSettingsCopy(settings,16,100);
            var html=CustomNotificationProgressBar.CreateElementsHtml(copy,NotificationBarPlacement.AfterContent);
            Assert.IsFalse(html.Contains("<script>"));Assert.IsFalse(html.Contains("linear-gradient("));StringAssert.Contains(html,"&lt;Cats&gt;");
        }

        [TestMethod]
        public void MultipleElementsSurviveSlotNormalizationAndApplyAsIndependentCopies()
        {
            var slot=new LocalCustomOverlayStyleSlot { Name="Multiple bars" };
            slot.ProgressBars.Add(new NotificationProgressBarDefinition { Source=NotificationBarSource.GameAchievements, Placement=NotificationBarPlacement.BeforeContent, Height=8 });
            slot.ProgressBars.Add(new NotificationProgressBarDefinition { Source=NotificationBarSource.Achievement, Title="Achievement" });
            var settings=new LocalSettings { CustomOverlayStyleSlots=new List<LocalCustomOverlayStyleSlot> { slot } };
            var restored=JsonConvert.DeserializeObject<LocalSettings>(JsonConvert.SerializeObject(settings));
            var saved=restored.CustomOverlayStyleSlots.Find(item=>item.Name=="Multiple bars");
            Assert.AreEqual(2,saved.ProgressBars.Count);
            LocalSettings.ApplyCustomStyleSlot(restored,saved);
            Assert.AreEqual(2,restored.OverlayCustomProgressBars.Count);
            Assert.AreEqual(NotificationBarSource.GameAchievements,restored.OverlayCustomProgressBars[0].Source);
            restored.OverlayCustomProgressBars[0].Height=20;
            Assert.AreEqual(8d,saved.ProgressBars[0].Height);
        }

        [TestMethod]
        public void IndependentSourcesHideOnlyMissingCountersAndRespectPlacement()
        {
            var settings = new LocalSettings();
            settings.OverlayCustomProgressBars.Add(new NotificationProgressBarDefinition { Id="achievement", Source=NotificationBarSource.Achievement });
            settings.OverlayCustomProgressBars.Add(new NotificationProgressBarDefinition { Id="game", Source=NotificationBarSource.GameAchievements, Placement=NotificationBarPlacement.BeforeContent, Title="Game completion" });
            settings.OverlayCustomProgressBars.Add(new NotificationProgressBarDefinition { Id="disabled", Enabled=false });
            var copy = CustomNotificationProgressTokens.CreateSettingsCopy(settings, null, null, counters: new Dictionary<NotificationBarSource, NotificationBarCounter> {
                [NotificationBarSource.GameAchievements] = new NotificationBarCounter { Current=27, Maximum=100 }
            });
            Assert.AreEqual("", CustomNotificationProgressBar.CreateElementsHtml(copy, NotificationBarPlacement.AfterContent));
            var html = CustomNotificationProgressBar.CreateElementsHtml(copy, NotificationBarPlacement.BeforeContent);
            StringAssert.Contains(html, "Game completion");
            StringAssert.Contains(html, ">27/100</span>");
            Assert.IsFalse(html.Contains("disabled"));
            copy.AchievementProgressCurrent=16; copy.AchievementProgressMaximum=100;
            StringAssert.Contains(CustomNotificationProgressBar.CreateElementsHtml(copy, NotificationBarPlacement.AfterContent), ">16/100</span>");
        }

        [TestMethod]
        public void ElementEditsNotifyPreviewAndRemovedElementsStopNotifying()
        {
            var settings=new LocalSettings();
            var bar=new NotificationProgressBarDefinition();
            settings.OverlayCustomProgressBars.Add(bar);
            var changes=0;
            settings.PropertyChanged += (_,args) => { if(args.PropertyName==nameof(LocalSettings.OverlayCustomProgressBars)) changes++; };
            bar.Height=12; Assert.AreEqual(1,changes);
            var saved=JsonConvert.DeserializeObject<LocalSettings>(JsonConvert.SerializeObject(settings));
            Assert.AreEqual(12d,saved.OverlayCustomProgressBars[0].Height);
            settings.OverlayCustomProgressBars.Clear(); changes=0;
            bar.Height=14; Assert.AreEqual(0,changes);
            saved.OverlayCustomProgressBars[0].Height=18; Assert.AreEqual(0,changes);
        }

        [TestMethod]
        public void VisibilityRequiresEnabledBarAndAValidCounter()
        {
            var settings = new LocalSettings();
            Assert.AreEqual("", CustomNotificationProgressBar.CreateHtml(CustomNotificationProgressTokens.CreateSettingsCopy(settings, 16, 100)));
            settings.OverlayCustomShowProgressBar = true;
            foreach (var current in new int?[] { 0, 16, 100 })
            {
                var copy = CustomNotificationProgressTokens.CreateSettingsCopy(settings, current, 100);
                Assert.IsTrue(CustomNotificationProgressBar.IsVisible(copy));
            }
            Assert.AreEqual("", CustomNotificationProgressBar.CreateHtml(CustomNotificationProgressTokens.CreateSettingsCopy(settings, null, null)));
            Assert.AreEqual("", CustomNotificationProgressBar.CreateHtml(CustomNotificationProgressTokens.CreateSettingsCopy(settings, 16, 0)));
        }

        [TestMethod]
        public void HtmlUsesTheCounterAndConfiguredAppearance()
        {
            var copy = CustomNotificationProgressTokens.CreateSettingsCopy(new LocalSettings
            {
                OverlayCustomShowProgressBar = true,
                OverlayCustomProgressBarWidth = 250,
                OverlayCustomProgressBarHeight = 8,
                OverlayCustomProgressBarShowLabel = true,
                OverlayCustomProgressBarFillColor = "#FF0000",
                OverlayCustomProgressBarTrackColor = "#80000000",
                OverlayCustomProgressBarOffsetX = 12,
                OverlayCustomProgressBarOffsetY = -3
            }, 16, 100);
            var html = CustomNotificationProgressBar.CreateHtml(copy);
            StringAssert.Contains(html, "width:16%");
            StringAssert.Contains(html, "width:250px");
            StringAssert.Contains(html, "height:8px");
            StringAssert.Contains(html, "rgba(255,0,0,1)");
            StringAssert.Contains(html, "rgba(0,0,0,0.502)");
            StringAssert.Contains(html, "translate(12px,-3px)");
            StringAssert.Contains(html, ">16/100</span>");
            StringAssert.Contains(html, "aria-valuemax=\"100\"");
            copy.AchievementProgressCurrent = 150;
            Assert.AreEqual(1d, CustomNotificationProgressBar.Fraction(copy));
            copy.AchievementProgressCurrent = -1;
            Assert.AreEqual(0d, CustomNotificationProgressBar.Fraction(copy));
            copy.OverlayCustomProgressBarFillColor = "red;\" onclick=\"alert(1)";
            Assert.IsFalse(CustomNotificationProgressBar.CreateHtml(copy).Contains("onclick"));
        }

        [TestMethod]
        public void WpfBarHasCorrectFillAndCollapsesEntirelyWithoutProgress()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var settings = new LocalSettings { OverlayCustomShowProgressBar = true, OverlayCustomProgressBarShowLabel = true };
                    var copy = CustomNotificationProgressTokens.CreateSettingsCopy(settings, 16, 100);
                    var row = (Grid)CustomNotificationProgressBar.CreateWpf(copy);
                    row.Measure(new Size(320, double.PositiveInfinity));
                    row.Arrange(new Rect(0, 0, 320, row.DesiredSize.Height));
                    row.UpdateLayout();
                    row.Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.Render);
                    var track = (Border)row.Children[0];
                    Assert.IsTrue(track.ActualWidth > 0);
                    Assert.AreEqual(track.ActualWidth * 0.16, ((Border)track.Child).Width, 0.00001);
                    Assert.AreEqual("16/100", ((TextBlock)row.Children[1]).Text);
                    Assert.IsNull(CustomNotificationProgressBar.CreateWpf(CustomNotificationProgressTokens.CreateSettingsCopy(settings, null, null)));
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "WPF progress bar verification timed out.");
            if (failure != null) throw failure;
        }

        [TestMethod]
        public void StyleSlotsRoundTripAndApplyBarSettingsWithoutSavingRuntimeProgress()
        {
            var settings = new LocalSettings
            {
                CustomOverlayStyleSlots = new List<LocalCustomOverlayStyleSlot>
                {
                    new LocalCustomOverlayStyleSlot { Name = "Progress bar test", ShowProgressBar = true, ProgressBarHeight = 9, ProgressBarWidth = 240, ProgressBarFillColor = "#123456", ProgressBarShowLabel = true, ProgressBarOffsetY = 7 }
                }
            };
            var restored = JsonConvert.DeserializeObject<LocalSettings>(JsonConvert.SerializeObject(settings));
            var slot = restored.CustomOverlayStyleSlots.Find(item => item.Name == "Progress bar test");
            Assert.IsNotNull(slot);
            LocalSettings.ApplyCustomStyleSlot(restored, slot);
            Assert.IsTrue(restored.OverlayCustomShowProgressBar);
            Assert.AreEqual(9d, restored.OverlayCustomProgressBarHeight);
            Assert.AreEqual(240d, restored.OverlayCustomProgressBarWidth);
            Assert.AreEqual("#123456", restored.OverlayCustomProgressBarFillColor);
            Assert.AreEqual(7d, restored.OverlayCustomProgressBarOffsetY);
            Assert.IsTrue(restored.OverlayCustomProgressBarShowLabel);
            var copy = CustomNotificationProgressTokens.CreateSettingsCopy(restored, 16, 100);
            Assert.IsFalse(JsonConvert.SerializeObject(copy).Contains("AchievementProgressCurrent"));
            restored.OverlayCustomProgressBarHeight = double.NaN;
            Assert.AreEqual(6d, restored.OverlayCustomProgressBarHeight);
        }
    }
}
