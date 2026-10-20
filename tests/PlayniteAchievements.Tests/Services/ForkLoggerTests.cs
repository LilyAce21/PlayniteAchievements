using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class ForkLoggerTests
    {
        [DataTestMethod]
        [DataRow("Debug")]
        [DataRow("Trace")]
        [DataRow("Info")]
        [DataRow("Warn")]
        [DataRow("Error")]
        public void OnlyExplicitForkMessagesReceiveSuffixAndExceptionsArePreserved(string level)
        {
            var sink = new RecordingLogger();
            var original = new NamedLoggerWrapper(sink, "PlayniteAchievementsPlugin");
            var fork = ForkLogger.Wrap(original);
            Emit(original, level, null, "upstream");
            Assert.AreEqual("[PlayniteAchievementsPlugin] upstream", sink.Message);
            Emit(fork, level, null, "fork");
            Assert.AreEqual("[PlayniteAchievementsPlugin-Santodan] fork", sink.Message);
            Assert.AreEqual(level, sink.Level);
            var exception = new InvalidOperationException("example");
            Emit(fork, level, exception, "failed");
            Assert.AreEqual("[PlayniteAchievementsPlugin-Santodan] failed", sink.Message);
            Assert.AreSame(exception, sink.Exception);
            Assert.AreEqual(level, sink.Level);
            Emit(original, level, null, "still upstream");
            Assert.AreEqual("[PlayniteAchievementsPlugin] still upstream", sink.Message);
        }

        [TestMethod]
        public void ForkLoggerIsReusedAndDoesNotAppendSuffixTwice()
        {
            var original = new NamedLoggerWrapper(new RecordingLogger(), "Local");
            var fork = ForkLogger.Wrap(original);
            Assert.AreSame(fork, ForkLogger.Wrap(original));
            Assert.AreSame(fork, ForkLogger.Wrap(fork));
            Assert.IsNull(ForkLogger.Wrap(null));
        }

        [TestMethod]
        public void ExternalLoggerKeepsItsDestinationAndGetsComponentLabel()
        {
            var sink = new RecordingLogger();
            ForkLogger.Wrap(sink, @"C:\src\LocalProvider.cs").Info("example");
            Assert.AreEqual("[LocalProvider-Santodan] example", sink.Message);
        }

        private static void Emit(ILogger logger, string level, Exception error, string message)
        {
            switch (level)
            {
                case "Debug": if (error == null) logger.Debug(message); else logger.Debug(error, message); break;
                case "Trace": if (error == null) logger.Trace(message); else logger.Trace(error, message); break;
                case "Info": if (error == null) logger.Info(message); else logger.Info(error, message); break;
                case "Warn": if (error == null) logger.Warn(message); else logger.Warn(error, message); break;
                case "Error": if (error == null) logger.Error(message); else logger.Error(error, message); break;
            }
        }

        private sealed class RecordingLogger : ILogger
        {
            public string Message;
            public string Level;
            public Exception Exception;
            private void Record(string level, string message, Exception exception = null)
            { Level = level; Message = message; Exception = exception; }
            public void Debug(string message) => Record("Debug", message);
            public void Debug(Exception exception, string message) => Record("Debug", message, exception);
            public void Trace(string message) => Record("Trace", message);
            public void Trace(Exception exception, string message) => Record("Trace", message, exception);
            public void Info(string message) => Record("Info", message);
            public void Info(Exception exception, string message) => Record("Info", message, exception);
            public void Warn(string message) => Record("Warn", message);
            public void Warn(Exception exception, string message) => Record("Warn", message, exception);
            public void Error(string message) => Record("Error", message);
            public void Error(Exception exception, string message) => Record("Error", message, exception);
        }
    }
}
