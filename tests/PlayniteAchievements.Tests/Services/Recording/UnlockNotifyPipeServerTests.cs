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

        // Behaves like the Achievements app: write the line, then wait for the server to close.
        private static void Send(string pipeName, string text)
        {
            using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                client.Connect(5000);
                var bytes = Encoding.UTF8.GetBytes(text);
                client.Write(bytes, 0, bytes.Length);
                var waitForClose = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        client.Read(new byte[1], 0, 1);
                    }
                    catch (IOException)
                    {
                    }
                });
                waitForClose.Wait(3000);
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
                try
                {
                    // The server stops reading once the cap is passed, so a sender this large is
                    // cut off mid-write, as a real client would be.
                    Send(name, new string('x', UnlockNotifyMessage.MaxMessageBytes * 3));
                }
                catch (IOException)
                {
                }

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
        public void ManyAnnouncementsBackToBack_AreAllDelivered()
        {
            var name = UniqueName();
            var received = new BlockingCollection<string>();
            var logs = new ConcurrentQueue<string>();
            using (var server = new UnlockNotifyPipeServer(name, line => received.Add(line), logs.Enqueue))
            {
                server.Start();
                for (var i = 0; i < 12; i++)
                {
                    Send(name, "burst " + i + "\n");
                }

                var seen = new System.Collections.Generic.HashSet<string>();
                for (var i = 0; i < 12; i++)
                {
                    if (!received.TryTake(out var line, 5000))
                    {
                        Assert.Fail("only " + seen.Count + " of 12 arrived; accepted=" + server.AcceptedConnections +
                                    "; delivered=" + server.DeliveredLines + "; seen=[" + string.Join(",", seen) +
                                    "]; logs=[" + string.Join(" | ", logs) + "]");
                    }

                    seen.Add(line);
                }

                Assert.AreEqual(12, seen.Count);
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
