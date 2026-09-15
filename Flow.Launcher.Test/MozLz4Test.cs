using System;
using System.IO;
using System.Linq;
using System.Text;
using Flow.Launcher.Infrastructure;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Flow.Launcher.Test
{
    [TestFixture]
    public class MozLz4Test
    {
        // Vectors produced with python-lz4: b"mozLz40\0" + int32 size + lz4.block.compress(data, store_size=False)
        private const string ShortFile = "6d6f7a4c7a343000050000005068656c6c6f";

        private const string OverlappingFile =
            "6d6f7a4c7a343000660600003f6162630300ffff443f78797a0300f8fff1000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f606162636465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9fa0a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3b4b5b6b7b8b9babbbcbdbebfc0c1c2c3c4c5c6c7c8c9cacbcccdcecfd0d1d2d3d4d5d6d7d8d9dadbdcdddedfe0e1e2e3e4e5e6e7e8e9eaebecedeeeff0f1f2f3f4f5f6f7f8f9fafbfcfdfeff0001ffe950fbfcfdfeff";

        private const string JsonFile =
            "6d6f7a4c7a343000a00f0000f2287b22737061636573223a5b7b2275756964223a227b39626134333237617d222c227468656d65223a7b226772616469656e74436f6c6f723000ff1863223a5b3132352c3138312c3133365d2c2269735072696d617279223a747275657d5d7d7d5d7d6400ffffffffffffffffffffffffffffff33505d7d7d5d7d";

        [Test]
        public void DecompressesLiteralOnlyBlock()
        {
            ClassicAssert.AreEqual("hello", Encoding.ASCII.GetString(MozLz4.Decompress(Convert.FromHexString(ShortFile))));
        }

        [Test]
        public void DecompressesOverlappingMatches()
        {
            var expected = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abc", 200)))
                .Concat(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("xyz", 90))))
                .Concat(Enumerable.Range(0, 256 * 3).Select(i => (byte)(i % 256)))
                .ToArray();

            CollectionAssert.AreEqual(expected, MozLz4.Decompress(Convert.FromHexString(OverlappingFile)));
        }

        [Test]
        public void DecompressesLongMatchLengths()
        {
            const string record = "{\"spaces\":[{\"uuid\":\"{9ba4327a}\",\"theme\":{\"gradientColors\":[{\"c\":[125,181,136],\"isPrimary\":true}]}}]}";
            var expected = string.Concat(Enumerable.Repeat(record, 40));

            ClassicAssert.AreEqual(expected, Encoding.ASCII.GetString(MozLz4.Decompress(Convert.FromHexString(JsonFile))));
        }

        [Test]
        public void RejectsFilesWithoutMagic()
        {
            Assert.Throws<InvalidDataException>(() => MozLz4.Decompress(Encoding.ASCII.GetBytes("not a mozlz4 file")));
        }

        [Test]
        public void RejectsTruncatedBlocks()
        {
            var truncated = Convert.FromHexString(JsonFile)[..40];
            Assert.Throws<InvalidDataException>(() => MozLz4.Decompress(truncated));
        }
    }
}
