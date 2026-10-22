using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class AsyncImageAnimationTests
    {
        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        public void RetainedFramesPauseAndResumeWithoutCopyingBitmaps(bool attachDuringSlide, bool unloadAndReload)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                Window window = null;
                try
                {
                    var image = new Image();
                    window = new Window { Content = image, Width = 16, Height = 16,
                        Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
                    window.Show();
                    var first = Frame(0x10);
                    var second = Frame(0x90);
                    var animation = new ObjectAnimationUsingKeyFrames
                    { Duration = TimeSpan.FromSeconds(1), RepeatBehavior = RepeatBehavior.Forever };
                    animation.KeyFrames.Add(new DiscreteObjectKeyFrame(first, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                    animation.KeyFrames.Add(new DiscreteObjectKeyFrame(second, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500))));
                    animation.Freeze();

                    if (attachDuringSlide) AsyncImage.PauseGifAnimationsUnder(window);
                    typeof(AsyncImage).GetMethod("ApplyAnimatedFrames", BindingFlags.Static | BindingFlags.NonPublic)
                        .Invoke(null, new object[] { image, "frames.webp", first, animation });
                    Pump(100);
                    if (!attachDuringSlide) AsyncImage.PauseGifAnimationsUnder(window);
                    Pump(70);
                    Assert.AreSame(first, image.Source);
                    Pump(600);
                    Assert.AreSame(first, image.Source, "A frame swap would invalidate the slide's cached card.");
                    if (unloadAndReload)
                    {
                        typeof(AsyncImage).GetMethod("OnUnloaded", BindingFlags.Static | BindingFlags.NonPublic)
                            .Invoke(null, new object[] { image, new RoutedEventArgs() });
                        typeof(AsyncImage).GetMethod("ApplyAnimatedFrames", BindingFlags.Static | BindingFlags.NonPublic)
                            .Invoke(null, new object[] { image, "frames.webp", first, animation });
                    }
                    else AsyncImage.ResumeGifAnimationsUnder(window);
                    Pump(600);
                    Assert.AreSame(second, image.Source, "Resume must reuse the retained frame, without cloning it.");
                    typeof(AsyncImage).GetMethod("StopAnimation", BindingFlags.Static | BindingFlags.NonPublic)
                        .Invoke(null, new object[] { image });
                    AsyncImage.ResumeGifAnimationsUnder(window);
                    Assert.IsFalse(image.HasAnimatedProperties);
                }
                catch (Exception exception) { failure = exception; }
                finally { window?.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
            if (failure != null) throw new AssertFailedException(failure.ToString());
        }

        private static BitmapSource Frame(byte value)
        {
            var frame = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { value, value, value, 255 }, 4);
            frame.Freeze();
            return frame;
        }

        private static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
