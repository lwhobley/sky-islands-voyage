using System;
using System.Numerics;
using NUnit.Framework;
using SkyIslands.Backend;
using SkyIslands.UI;

namespace SkyIslands.Tests
{
    public class UiTextTests
    {
        [TestCase("999", "999")]
        [TestCase("1000", "1K")]
        [TestCase("1234", "1.23K")]
        [TestCase("12345", "12.3K")]
        [TestCase("999999", "999K")]
        [TestCase("1000000", "1M")]
        [TestCase("142892018831855232798", "142Qi")]
        public void ShortNumbersTruncateNeverRoundUp(string value, string expected) =>
            Assert.AreEqual(expected, UiText.Short(BigInteger.Parse(value)));

        [Test]
        public void CountdownFormats()
        {
            Assert.AreEqual("4:05", UiText.Countdown(TimeSpan.FromSeconds(245)));
            Assert.AreEqual("1:02:03", UiText.Countdown(new TimeSpan(1, 2, 3)));
        }

        [Test]
        public void ErrorsMapToFriendlyText()
        {
            Assert.AreEqual("Not enough crystals yet.", UiText.Error(new SupabaseException(400, "P0001", "insufficient_crystals")));
            Assert.AreEqual("You're offline. Your rolls are saved; try again soon.",
                            UiText.Error(new SupabaseException(0, "network_error", "Cannot resolve destination host")));
        }
    }
}
