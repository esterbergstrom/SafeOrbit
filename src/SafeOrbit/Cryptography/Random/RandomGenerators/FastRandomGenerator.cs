﻿
/*
MIT License

Copyright (c) 2016 Erkin Ekici - undergroundwires@safeorb.it

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using SafeOrbit.Cryptography.Random.RandomGenerators.Crypto;
using SafeOrbit.Cryptography.Random.RandomGenerators.Crypto.Digests;
using SafeOrbit.Cryptography.Random.RandomGenerators.Crypto.Prng;
using SafeOrbit.Helpers;

namespace SafeOrbit.Cryptography.Random.RandomGenerators
{
    /// <summary>
    ///     <p>
    ///         <see cref="FastRandomGenerator" /> returns cryptographically strong random data.  It uses a crypto prng to
    ///         generate more bytes than actually available in hardware entropy, so it's about 1,000 times faster than
    ///         <see cref="SafeRandomGenerator" />.
    ///     </p>
    ///     <p>
    ///         For general purposes, <see cref="FastRandomGenerator" /> is recommended because of its performance
    ///         characteristics, but for extremely strong keys and other things that don't require a large number of bytes
    ///         quickly,  <see cref="SafeRandomGenerator" /> is recommended instead.
    ///     </p>
    /// </summary>
    /// <example>
    /// <code>
    ///  using SafeOrbit.Cryptography.Random;
    ///  
    ///  static void Main(string[] args)
    ///  {
    ///      StartEarly.StartFillingEntropyPools();  // Start gathering entropy as early as possible
    ///  
    ///      var randomBytes = new byte[32];
    /// 
    ///      // Performance is highly variable.  On my system, it generated 2.00MB(minimum)/3.04MB(avg)/3.91MB(max) per second
    ///      // default FastRandom() constructor uses the SafeRandom() default constructor, which uses:
    ///      //     SystemRNGCryptoServiceProvider/SHA256, 
    ///      //     ThreadedSeedGeneratorRNG/SHA256/RipeMD256Digest,
    ///      FastRandomGenerator.StaticInstance.GetBytes(randomBytes);
    ///  }
    ///  </code>
    /// </example>
    internal sealed class FastRandomGenerator : RandomNumberGenerator
    {

        private const int ReseedLocked = 1;
        private const int ReseedUnlocked = 0;

        private const int MaxBytesPerSeedSoft = 64*1024; // See "BouncyCastle DigestRandomGenerator Analysis" comment
        private const int MaxStateCounterHard = 1024*1024; // See "BouncyCastle DigestRandomGenerator Analysis" comment
        public static FastRandomGenerator StaticInstance => StaticInstanceLazy.Value;

        private static readonly Lazy<FastRandomGenerator> StaticInstanceLazy = new Lazy<FastRandomGenerator>(
            () => new FastRandomGenerator(SafeRandomGenerator.StaticInstance));

        private readonly int _digestSize;
        private readonly DigestRandomGenerator _myPrng;
        private readonly SafeRandomGenerator _safeRandomGenerator;
        private readonly bool _safeRandomGeneratorIsMineExclusively;
        private readonly object _stateCounterLockObj = new object();
        private int _isDisposed = IntCondition.False;
        private int _reseedLockInt = ReseedUnlocked;
        private int _stateCounter = MaxStateCounterHard; // Guarantee to seed immediately on first call to GetBytes

        /// <summary>
        ///     Number of SafeRandomGenerator bytes to use when reseeding PRNG.
        /// </summary>
        public int SeedSize;

        /* BouncyCastle DigestRandomGenerator Analysis
         * BouncyCastle DigestRandomGenerator maintains two separate but related internal states, represented by the following:
         *     byte[] seed
         *     long   seedCounter
         *     byte[] state
         *     long   stateCounter
         * The size of seed and state are both equal to the size of the digest.  I am going to refer to the digest size, in bits,
         * as "M".  The counters are obviously 64 bits each.
         * 
         * In order to generate repeated output, there would need to be a collision of stateCounter, state, and seed.  We expect a seed
         * collision every 2^(M/2) times that we cycle seed.  We expect a state collision every 2^(M/2) times that we GenerateState,
         * and stateCounter will repeat itself every 2^64 times that we call GenerateState.  This means we can never have a repeated
         * stateCounter&state&seed in less than 2^64 calls to GenerateState, and very likely, it would be much much larger than that.
         * 
         * GenerateState is called at least once for every call to NextBytes, and it's called more times, if the number of bytes reqested
         * >= digest size in bytes.  We can easily measure the number of calls to GenerateState, by counting 1+(bytes.Length/digest.Size),
         * and we want to ensure this number is always below 2^64, which is UInt64.MaxValue
         * 
         * bytes.Length is an Int32.  We can easily guarantee we'll never repeat an internal state, if we use a UInt64 to tally the
         * number of calls to GenerateState, and require new seed material before UInt64.MaxValue - Int32.MaxValue.  This is a huge number.
         * 
         * To put this in perspective, supposing a 128 bit digest, and supposing the user on average requests 8 bytes per call to NextBytes.
         * Then there is guaranteed to be no repeat state before 147 quintillion bytes (147 billion billion).  So let's just tone this 
         * down a bit, and choose thresholds that are way more conservative.
         * 
         * Completely unrelated to analysis of DigestRandomGenerator, some other prng's (fortuna) recommend new seed material in 2^20
         * iterations, due to limitations they have, which we don't have.  So let's just ensure we end up choosing thresholds that are down
         * on-par with that level, even though completely unnecessary for us, it will feel conservative and safe.
         * 
         * Let's use a plain old int to tally the number of calls to GenerateState.  We need to ensure we never overflow this counter, so
         * let's assume all digests are at least 4 bytes, and let's require new seed material every int.MaxValue/2.  This is basically 
         * 1 billion calls to NextBytes, so a few GB of random data or so.  Extremely safe and conservative.
         * 
         * But let's squish it down even more than that.  FastRandomGenerator performs approx 1,000 times faster than SafeRandomGenerator.  So to 
         * maximize the sweet spot between strong security and good performance, let's only stretch the entropy 1,000,000 times at hard 
         * maximum, and 64,000 times softly suggested.  Typically, for example with Sha256, this means we'll generate up to 2MB before 
         * requesting reseed, and up to 32MB before requiring reseed.
         * 
         * Now we're super duper conservative, being zillions of times more conservative than necessary, maximally conservative to the point 
         * where we do not take an appreciable performance degradation.
         */

        public FastRandomGenerator()
        {
            _safeRandomGenerator = new SafeRandomGenerator();
            _safeRandomGeneratorIsMineExclusively = true;
            IDigest digest = new Sha512Digest();
            _myPrng = new DigestRandomGenerator(digest);
            _digestSize = digest.GetDigestSize();
            SeedSize = _digestSize;
            Reseed();
        }

        public FastRandomGenerator(IDigest digest)
        {
            _safeRandomGenerator = new SafeRandomGenerator();
            _safeRandomGeneratorIsMineExclusively = true;
            _myPrng = new DigestRandomGenerator(digest);
            _digestSize = digest.GetDigestSize();
            SeedSize = _digestSize;
            Reseed();
        }

        public FastRandomGenerator(List<IEntropyHasher> entropyHashers, IDigest digest)
        {
            _safeRandomGenerator = new SafeRandomGenerator(entropyHashers);
            _safeRandomGeneratorIsMineExclusively = true;
            _myPrng = new DigestRandomGenerator(digest);
            _digestSize = digest.GetDigestSize();
            SeedSize = _digestSize;
            Reseed();
        }

        public FastRandomGenerator(SafeRandomGenerator safeRandomGenerator)
        {
            _safeRandomGenerator = safeRandomGenerator;
            _safeRandomGeneratorIsMineExclusively = false;
            IDigest digest = new Sha512Digest();
            _myPrng = new DigestRandomGenerator(digest);
            _digestSize = digest.GetDigestSize();
            SeedSize = _digestSize;
            Reseed();
        }

        public FastRandomGenerator(SafeRandomGenerator safeRandomGenerator, IDigest digest)
        {
            _safeRandomGenerator = safeRandomGenerator;
            _safeRandomGeneratorIsMineExclusively = false;
            _myPrng = new DigestRandomGenerator(digest);
            _digestSize = digest.GetDigestSize();
            SeedSize = _digestSize;
            Reseed();
        }

        public override void GetBytes(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length == 0) return;
            lock (_stateCounterLockObj)
            {
                var newStateCounter = _stateCounter + 1 + data.Length/_digestSize;
                if (newStateCounter > MaxStateCounterHard)
                    Reseed(); // Guarantees to reset stateCounter = 0
                else if (newStateCounter > MaxBytesPerSeedSoft)
                    if (Interlocked.Exchange(ref _reseedLockInt, ReseedLocked) == ReseedUnlocked)
                        // If more than one thread race here, let the first one through, and others exit
                        ThreadPool.QueueUserWorkItem(ReseedCallback);
                // Repeat the addition, instead of using newStateCounter, because the above Reseed() might have changed stateCounter
                _stateCounter += 1 + data.Length/_digestSize;
                _myPrng.NextBytes(data);
                // Internally, DigestRandomGenerator locks all operations, so reseeding cannot occur in the middle of NextBytes()
            }
        }

#if NETFRAMEWORK
        public override void GetNonZeroBytes(byte[] data)
        {
            // Apparently, the reason for GetNonZeroBytes to exist, is sometimes people generate null-terminated salt strings.
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length == 0) return;
            var pos = 0;
            while (true)
            {
                var tempData = new byte[(int) (1.05*(data.Length - pos))];
                // Request 5% more data than needed, to reduce the probability of repeating loop
                GetBytes(tempData);
                for (var i = 0; i < tempData.Length; i++)
                    if (tempData[i] != 0)
                    {
                        data[pos] = tempData[i];
                        pos++;
                        if (pos == data.Length)
                        {
                            Array.Clear(tempData, 0, tempData.Length);
                            return;
                        }
                    }
            }
        }
#endif

        private void ReseedCallback(object state)
        {
            Reseed();
        }

        private void Reseed()
        {
            // If we were already disposed, it's nice to skip any attempt at GetBytes, etc.
            if (_isDisposed == IntCondition.True)
                return;
            // Even though we just checked to see if we're disposed, somebody could call Dispose() while I'm in the middle
            // of the following code block.
            try
            {
                var newSeed = new byte[SeedSize];
                _safeRandomGenerator.GetBytes(newSeed);
                lock (_stateCounterLockObj)
                {
                    _myPrng.AddSeedMaterial(newSeed);
                    _stateCounter = 0;
                    _reseedLockInt = ReseedUnlocked;
                }
            }
            catch
            {
                // If we threw any kind of exception after we were disposed, then just swallow it and go away quietly
                if (_isDisposed == IntCondition.False)
                    throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _isDisposed, IntCondition.True) == IntCondition.True)
                return;
            if (_safeRandomGeneratorIsMineExclusively)
                _safeRandomGenerator.Dispose();
            base.Dispose(disposing);
        }

        ~FastRandomGenerator()
        {
            Dispose(false);
        }
    }
}