using HapticAI.Config;
using NUnit.Framework;

namespace HapticAI.Tests
{
    /// <summary>
    /// EditMode tests for the settings the setup screen saves. The address parsing is worth pinning
    /// down because it is what stands between an operator and a typo they cannot see: the screen
    /// edits four numbers, and anything that does not round-trip through these two methods would
    /// show one address and use another.
    ///
    /// The round-trip test writes to PlayerPrefs (the Editor's own, in EditMode) and clears up after
    /// itself, so it cannot leave a machine configured behind the test run's back.
    /// </summary>
    public class StudySettingsTests
    {
        [TearDown]
        public void ClearSavedSettings() => StudySettings.ResetToDefaults();

        [Test]
        public void ParseIPv4_RoundTrips()
        {
            Assert.IsTrue(StudySettings.TryParseIPv4("192.168.1.42", out var octets));
            CollectionAssert.AreEqual(new[] { 192, 168, 1, 42 }, octets);
            Assert.AreEqual("192.168.1.42", StudySettings.FormatIPv4(octets));
        }

        [TestCase("")]
        [TestCase("laptop.local")]
        [TestCase("192.168.1")]
        [TestCase("192.168.1.256")]
        [TestCase("192.168.01.4")]
        [TestCase("192.168.1.-1")]
        public void ParseIPv4_RejectsAnythingNotADottedQuad(string host)
        {
            Assert.IsFalse(StudySettings.TryParseIPv4(host, out _));
        }

        [Test]
        public void ServiceUrl_IsEmptyWhenTheServerIsSwitchedOff()
        {
            var settings = new StudySettings { useServer = false, serverHost = "192.168.1.42" };
            Assert.AreEqual(string.Empty, settings.ServiceUrl);
        }

        [Test]
        public void ServiceUrl_IsEmptyWithNoHost()
        {
            var settings = new StudySettings { useServer = true, serverHost = "  " };
            Assert.AreEqual(string.Empty, settings.ServiceUrl);
        }

        [Test]
        public void ServiceUrl_CombinesHostAndPort()
        {
            var settings = new StudySettings
            {
                useServer = true, serverHost = " 192.168.1.42 ", serverPort = 9000,
            };
            Assert.AreEqual("http://192.168.1.42:9000", settings.ServiceUrl);
        }

        [Test]
        public void Save_ThenLoad_KeepsEverythingAndMarksConfigured()
        {
            var saved = new StudySettings
            {
                participantNumber = 7,
                condition = HapticsMode.Static,
                lockCondition = false,
                useServer = true,
                serverHost = "10.0.0.5",
                serverPort = 8123,
            };
            saved.Save();

            var loaded = StudySettings.Load();

            Assert.IsTrue(loaded.Configured, "a saved settings file is by definition configured");
            Assert.AreEqual(7, loaded.participantNumber);
            Assert.AreEqual("P07", loaded.ParticipantId);
            Assert.AreEqual(HapticsMode.Static, loaded.condition);
            Assert.IsFalse(loaded.lockCondition);
            Assert.AreEqual("http://10.0.0.5:8123", loaded.ServiceUrl);
        }

        [Test]
        public void Load_OnAFreshDeviceIsNotConfigured()
        {
            StudySettings.ResetToDefaults();

            var loaded = StudySettings.Load();

            Assert.IsFalse(loaded.Configured,
                "until the setup screen has been used, URL resolution must keep its old behaviour");
            Assert.AreEqual(string.Empty, loaded.ServiceUrl);
        }

        [Test]
        public void Save_ClampsOutOfRangeValues()
        {
            var settings = new StudySettings { participantNumber = 1000, serverPort = 0 };
            settings.Save();

            Assert.AreEqual(StudySettings.MaxParticipant, StudySettings.Load().participantNumber);
            Assert.AreEqual(1, StudySettings.Load().serverPort);
        }

        [Test]
        public void ResetToDefaults_ForgetsTheSavedAddress()
        {
            new StudySettings { serverHost = "192.168.1.42" }.Save();

            var defaults = StudySettings.ResetToDefaults();

            Assert.IsFalse(defaults.Configured);
            Assert.AreEqual(string.Empty, defaults.serverHost);
            Assert.AreEqual(StudySettings.DefaultPort, defaults.serverPort);
        }

        [Test]
        public void Copy_DoesNotShareStateWithTheOriginal()
        {
            var original = new StudySettings { participantNumber = 3 };
            var copy = original.Copy();

            copy.participantNumber = 9;

            Assert.AreEqual(3, original.participantNumber);
            Assert.AreEqual(9, copy.participantNumber);
        }

        [Test]
        public void FormatIPv4_ClampsRatherThanThrowing()
        {
            Assert.AreEqual("0.0.255.255", StudySettings.FormatIPv4(new[] { -5, 0, 255, 900 }));
            Assert.AreEqual(string.Empty, StudySettings.FormatIPv4(new[] { 1, 2, 3 }));
            Assert.AreEqual(string.Empty, StudySettings.FormatIPv4(null));
        }

        [Test]
        public void Current_ReflectsTheLastSave()
        {
            new StudySettings { participantNumber = 12 }.Save();
            Assert.AreEqual("P12", StudySettings.Current.ParticipantId);
        }
    }
}
