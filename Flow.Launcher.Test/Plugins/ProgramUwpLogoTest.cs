using NUnit.Framework;
using NUnit.Framework.Legacy;
using Flow.Launcher.Plugin.Program.Programs;

namespace Flow.Launcher.Test.Plugins
{
    [TestFixture]
    public class ProgramUwpLogoTest
    {
        [Test]
        public void PrefersUnplatedNonContrastLogoAtDesiredSize()
        {
            var logos = new[]
            {
                @"C:\App\Assets\AppList.targetsize-64_contrast-black.png",
                @"C:\App\Assets\AppList.targetsize-64.png",
                @"C:\App\Assets\AppList.targetsize-64_altform-lightunplated.png",
                @"C:\App\Assets\AppList.targetsize-64_altform-unplated.png",
                @"C:\App\Assets\AppList.targetsize-256_altform-unplated.png",
                @"C:\App\Assets\AppList.targetsize-16_altform-unplated.png",
            };

            ClassicAssert.AreEqual(@"C:\App\Assets\AppList.targetsize-64_altform-unplated.png",
                UWPApp.SelectLogoByQualifiers(logos, "AppList", 64));
        }

        [Test]
        public void PrefersPlatedLogoOverUpscalingTinyUnplatedOne()
        {
            var logos = new[]
            {
                @"C:\App\Assets\AppList.targetsize-16_altform-unplated.png",
                @"C:\App\Assets\AppList.targetsize-64.png",
            };

            ClassicAssert.AreEqual(@"C:\App\Assets\AppList.targetsize-64.png",
                UWPApp.SelectLogoByQualifiers(logos, "AppList", 64));
        }

        [Test]
        public void UsesScaleQualifiersWithBaseSizeFromName()
        {
            var logos = new[]
            {
                @"C:\App\Assets\Square44x44Logo.scale-100.png",
                @"C:\App\Assets\Square44x44Logo.scale-150.png",
                @"C:\App\Assets\Square44x44Logo.scale-400.png",
            };

            // 44 * 150% = 66px is the closest at or above 64px
            ClassicAssert.AreEqual(@"C:\App\Assets\Square44x44Logo.scale-150.png",
                UWPApp.SelectLogoByQualifiers(logos, "Square44x44Logo", 64));
        }

        [Test]
        public void ReturnsNullWhenNoFileNamesCarrySizes()
        {
            var logos = new[] { @"C:\App\Assets\StoreLogo.png" };

            ClassicAssert.IsNull(UWPApp.SelectLogoByQualifiers(logos, "StoreLogo", 64));
        }
    }
}
