using System;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace PowerTrader.Core.Util
{
    /// <summary>
    /// Ed25519 signing/keygen. .NET Framework 4.8 has no built-in Ed25519, so we use
    /// BouncyCastle. This mirrors PyNaCl's SigningKey (seed-based, 32-byte private seed)
    /// and the cryptography library's Ed25519 keypair generation used by the API-key wizard.
    /// </summary>
    public static class Ed25519Util
    {
        /// <summary>
        /// Sign <paramref name="message"/> (UTF-8) with a 32-byte private seed and return the
        /// base64-encoded 64-byte signature. Matches:
        ///   signed = SigningKey(seed).sign(message.encode("utf-8"))
        ///   base64.b64encode(signed.signature)
        /// </summary>
        public static string SignBase64(byte[] privateSeed32, string message)
        {
            if (privateSeed32 == null || privateSeed32.Length != 32)
                throw new ArgumentException("Ed25519 private seed must be exactly 32 bytes.");

            var priv = new Ed25519PrivateKeyParameters(privateSeed32, 0);
            var signer = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
            signer.Init(true, priv);
            byte[] data = Encoding.UTF8.GetBytes(message ?? string.Empty);
            signer.BlockUpdate(data, 0, data.Length);
            byte[] sig = signer.GenerateSignature();
            return Convert.ToBase64String(sig);
        }

        /// <summary>
        /// Generate a new Ed25519 keypair.
        /// Returns (privateSeedBase64, publicKeyBase64) exactly as Robinhood's onboarding expects:
        /// the private seed is what gets stored (r_secret.txt), the public key is pasted into Robinhood.
        /// </summary>
        public static (string PrivateSeedBase64, string PublicKeyBase64) GenerateKeypair()
        {
            var random = new SecureRandom();
            var gen = new Org.BouncyCastle.Crypto.Generators.Ed25519KeyPairGenerator();
            gen.Init(new Ed25519KeyGenerationParameters(random));
            var kp = gen.GenerateKeyPair();

            var priv = (Ed25519PrivateKeyParameters)kp.Private;
            var pub = (Ed25519PublicKeyParameters)kp.Public;

            byte[] seed = priv.GetEncoded();   // 32-byte seed
            byte[] pubBytes = pub.GetEncoded(); // 32-byte public key

            return (Convert.ToBase64String(seed), Convert.ToBase64String(pubBytes));
        }
    }
}
