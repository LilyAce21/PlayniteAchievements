using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Recording;

namespace PlayniteAchievements.Services.Tests.Recording
{
    /// <summary>Round trips through a real named pipe, the same way the Achievements app connects.</summary>
    [TestClass]
    public class UnlockNotifyPipeServerTests
    {
        private static string UniqueName() => "PlayniteAchievementsTest." + Guid.NewGuid().ToString("N");

        private static void Send(string pipeName, string text)
        {
            using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
            {
                client.Connect(5000);
                var bytes = Encoding.UTF8.GetBytes(text);
                client.Write(bytes, 0, bytes.Length);
                client.Flush();
            }
        }

        [TestMethod]
        public void DeliversAMessageSentOverTheRealPipe()
        {
            var name = UniqueName();
            var received = new BlockingCollection<string>();
            using (var server = new UnlockNotifyPipeServer(name, line => received.Add(line)))
            {
                server.Start();
                Send(name, "{\"v\":1,\"type\":\"unlock\"}\n");

                Assert.IsTrue(received.TryTake(out var line, 5000));
                Assert.AreEqual("{\"v\":1,\"type\":\"unlock\"}", line);
            }
        }

        [TestMethod]
        public void KeepsServingAfterEachConnection()
        {
            var name = UniqueName();
            var received = new BlockingCollection<string>();
            using (var server = new UnlockNotifyPipeServer(name, line => received.Add(line)))
            {
                server.Start();
                for (var i = 0; i < 3; i++)
                {
                    Send(name, "message " + i + "\n");
                    Assert.IsTrue(received.TryTake(out var line, 5000), "message " + i + " was not delivered");
                    Assert.AreEqual("message " + i, line);
                }
            }
        }

        [TestMethod]
        public void SplitsSeveralLinesSentInOneConnection()
        {
            var name = UniqueName();
            var received = new BlockingCollection<string>();
            using (var server = new UnlockNotifyPipeServer(name, line => received.Add(line)))
            {
                server.Start();
                Send(name, "first\nsecond\n");

                Assert.IsTrue(received.TryTake(out var one, 5000));
                Assert.IsTrue(received.TryTake(out var two, 5000));
                Assert.AreEqual("first", one);
                Assert.AreEqual("second", two);
            }
        }

        [TestMethod]
        public void ADroppedOversizedMessageDoesNotStopTheServer()
        {
            var name = UniqueName();
            var received = new BlockingCollection<string>();
            using (var server = new UnlockNotifyPipeServer(name, line => received.Add(line)))
            {
                server.Start();
                Send(name, new string('x', UnlockNotifyMessage.MaxMessageBytes * 3));
                Send(name, "still alive\n");

                Assert.IsTrue(received.TryTake(out var line, 5000));
                Assert.AreEqual("still alive", line);
            }
        }

        [TestMethod]
        public void AThrowingHandlerDoesNotStopTheServer()
        {
            var name = UniqueName();
            var received = new BlockingCollection<string>();
            var calls = 0;
            using (var server = new UnlockNotifyPipeServer(name, line =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new InvalidOperationException("boom");
                }

                received.Add(line);
            }))
            {
                server.Start();
                Send(name, "first\n");
                Send(name, "second\n");

                Assert.IsTrue(received.TryTake(out var line, 5000));
                Assert.AreEqual("second", line);
            }
        }

        [TestMethod]
        public void DisposeStopsTheServerPromptly()
        {
            var server = new UnlockNotifyPipeServer(UniqueName(), _ => { });
            server.Start();
            var started = DateTime.UtcNow;
            server.Dispose();
            Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        }
    }
}
