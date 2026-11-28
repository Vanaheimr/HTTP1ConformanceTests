/*
 * Copyright (c) 2010-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of Vanaheimr Hermod <https://www.github.com/Vanaheimr/Hermod>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Security.Cryptography;

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

#endregion

namespace org.GraphDefined.Vanaheimr.Hermod.HTTP1.Demo
{

    /// <summary>
    /// The keys /signatures/verify and /signatures/signed use: the example keys
    /// of RFC 9421, Appendix B.1, under the key identifiers the RFC gives them.
    ///
    /// **They are published, and that is the point.** A peer that is not this
    /// repository's code signs with the same keys, out of the same appendix, so
    /// neither side has to hand the other anything. Nothing they sign means
    /// anything, and nothing here may ever take a key of its own from this file.
    ///
    /// test-key-rsa signs only on the peer's side. Its primes have 1088 and 960
    /// bits, a valid RSA key that the BCL cannot import, so the demo verifies
    /// with its public key and never signs with it.
    /// </summary>
    public static class DemoSignatureKeys
    {

        #region Data

        // B.1.1, test-key-rsa
        private const String RSAPublicKeyPEM = """
            -----BEGIN RSA PUBLIC KEY-----
            MIIBCgKCAQEAhAKYdtoeoy8zcAcR874L8cnZxKzAGwd7v36APp7Pv6Q2jdsPBRrw
            WEBnez6d0UDKDwGbc6nxfEXAy5mbhgajzrw3MOEt8uA5txSKobBpKDeBLOsdJKFq
            MGmXCQvEG7YemcxDTRPxAleIAgYYRjTSd/QBwVW9OwNFhekro3RtlinV0a75jfZg
            kne/YiktSvLG34lw2zqXBDTC5NHROUqGTlML4PlNZS5Ri2U4aCNx2rUPRcKIlE0P
            uKxI4T+HIaFpv8+rdV6eUgOrB2xeI1dSFFn/nnv5OoZJEIB+VmuKn3DCUcCZSFlQ
            PSXSfBDiUGhwOw76WuSSsf1D4b/vLoJ10wIDAQAB
            -----END RSA PUBLIC KEY-----
            """;

        // B.1.2, test-key-rsa-pss, as JWK: the PEM's PKCS #8 names RSASSA-PSS
        // as its algorithm, which the BCL does not import.
        private const String RSAPSS_n   = "r4tmm3r20Wd_PbqvP1s2-QEtvpuRaV8Yq40gjUR8y2Rjxa6dpG2GXHbPfvMs8ct-Lh1GH45x28Rw3Ry53mm-oAXjyQ86OnDkZ5N8lYbggD4O3w6M6pAvLkhk95AndTrifbIFPNU8PPMO7OyrFAHqgDsznjPFmTOtCEcN2Z1FpWgchwuYLPL-Wokqltd11nqqzi-bJ9cvSKADYdUAAN5WUtzdpiy6LbTgSxP7ociU4Tn0g5I6aDZJ7A8Lzo0KSyZYoA485mqcO0GVAdVw9lq4aOT9v6d-nb4bnNkQVklLQ3fVAvJm-xdDOp9LCNCN48V2pnDOkFV6-U9nV5oyc6XI2w";
        private const String RSAPSS_e   = "AQAB";
        private const String RSAPSS_d   = "lAfIqfpCYomVShfAKnwf2lD9I0wKjkHsCtZCif4kAlwQqqW6N-tIL3bdOR-VWf0Q1ZBIDtpO91UrG7pansyrPERbNrRJlPiYEyPTHkCT1nD-l2isuiyGLNBNnFoKfBgA4KAbPJZQatFIV9Cn34JSHnpN5-2ehreGBYHtkwHFtlmzeF3yu5bqRcqOhx8lkYmBzDAEUFyyXjknU5-WjAT9DzuG0MpOTkcU1EnjnIjyVBZLUB5Lxm8puyq8hH8B_E5LNC-1oc8j-tDy98UvRTTiYvZvs87cGCFxg0LijNhg7CE3g9piNqB6DzMgA9MHSOwcElVtfKdYfo4H3OHZXsSmEQ";
        private const String RSAPSS_p   = "5V-6ISI5yEaCFXm-fk1EM2xwAWekePVCAyvr9QbTlFOCZwt9WwjUjhtKRusi5Uq-IYZ_tq2WRE4As4b_FHEMtp2AER43IcvmXPqKFBoUktVDS7dThIHrsnRi1U7dHqVdwiMEMe5jxKNgnsKLpnq-4NyhoS6OeWu1SFozG9J9xQk";
        private const String RSAPSS_q   = "w-wIde17W5Y0Cphp3ZZ0uM8OUq1AkrV2IKauqYHaDxAT32EM4ci2MMER2nIUEo4g_42lW0zYouFFqONwv0-HyOsgPpdSqKRC5WLgn0VXabjaNcy6KhNPXeJ0AgtqdiDwPeJ2_L_eKwNWQ43RfdQBUquAwSd7SEmmQ8sViqB628M";
        private const String RSAPSS_dp  = "otDolkxtJ7Sk8gmRJqZCGx6GAvlGznWJfibXPv6xgUAl-G83dD84YgcNGnoeMxRzEekfDtT5LVMRPF4_AoucsqPqHDyOdfb-dlGBYfOBVxj6w-xF5HE0lV_4J-HrI63Od9fTSn4lY5d1JjyCVJIcnBEAyiD6EUZbUBh23vDzRcE";
        private const String RSAPSS_dq  = "iZE1S6CpqmBoQDxOsXGQmaeBdhoCqkDSJhEDuS_dLhBq88FQa0UkcE1QvOK3J2Q21VnfDqGBx7SH1hOFOj-cpz45kNluB832ztxDvnHQ9AIA7h_HY_3VD6YPMNRVN4bfSYS3abdLR0Z7jsmInGJ9X0_fA0E2tkZIgXeas5EFU0M";
        private const String RSAPSS_qi  = "jRAqfYi_tKCjhP9eM0N2XaRlNeoYCTx06GlSLD8d0zc4ZZuEePY10LMGWI6Y_JC0CvvvQYhNa9sAj4hFjIVLsWeTplVVUezGO1ofLW4kYWVpnMpHgAY1pRM4kyzo1p3MKYY8DE1BA4KqhSOfhdGs6Ov3Dfj0migZeE7Fu7yc7Fc";

        // B.1.3, test-key-ecc-p256
        private const String ECCP256PrivateKeyPEM = """
            -----BEGIN EC PRIVATE KEY-----
            MHcCAQEEIFKbhfNZfpDsW43+0+JjUr9K+bTeuxopu653+hBaXGA7oAoGCCqGSM49
            AwEHoUQDQgAEqIVYZVLCrPZHGHjP17CTW0/+D9Lfw0EkjqF7xB4FivAxzic30tMM
            4GF+hR6Dxh71Z50VGGdldkkDXZCnTNnoXQ==
            -----END EC PRIVATE KEY-----
            """;

        // B.1.4, test-key-ed25519, as JWK: d is the private key, x the public one.
        private const String Ed25519_d  = "n4Ni-HpISpVObnQMW0wOhCKROaIKqKtW_2ZYb2p9KcU";

        // B.1.5, test-shared-secret
        private const String SharedSecretBase64 = "uzvJfB4u3N0Jy4T7NZ75MDVcr8zSTInedJtkgcu46YW4XByzNJjxBdtjUkdJPBtbmHhIDi6pcl8jsasjlTMtDQ==";

        #endregion

        #region Properties

        /// <summary>
        /// The keys by key identifier, each able to verify. All but test-key-rsa
        /// can sign as well.
        /// </summary>
        public static IReadOnlyDictionary<String, HTTPSignatureKey> ByKeyId { get; } = Load();

        #endregion


        #region (private static) Load()

        private static IReadOnlyDictionary<String, HTTPSignatureKey> Load()
        {

            var rsa = RSA.Create();
            rsa.ImportFromPem(RSAPublicKeyPEM);

            var modulus = FromBase64Url(RSAPSS_n);
            var half    = modulus.Length / 2;
            var rsaPSS  = RSA.Create();
            rsaPSS.ImportParameters(new RSAParameters {
                Modulus   = modulus,
                Exponent  = FromBase64Url(RSAPSS_e),
                D         = Pad(FromBase64Url(RSAPSS_d),  modulus.Length),
                P         = Pad(FromBase64Url(RSAPSS_p),  half),
                Q         = Pad(FromBase64Url(RSAPSS_q),  half),
                DP        = Pad(FromBase64Url(RSAPSS_dp), half),
                DQ        = Pad(FromBase64Url(RSAPSS_dq), half),
                InverseQ  = Pad(FromBase64Url(RSAPSS_qi), half)
            });

            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(ECCP256PrivateKeyPEM);

            return new Dictionary<String, HTTPSignatureKey> {
                       ["test-key-rsa"]        = HTTPSignatureKey.RSAPKCS1v15(rsa,                                     "test-key-rsa"),
                       ["test-key-rsa-pss"]    = HTTPSignatureKey.RSAPSS     (rsaPSS,                                  "test-key-rsa-pss"),
                       ["test-key-ecc-p256"]   = HTTPSignatureKey.ECDSA      (ecdsa,                                   "test-key-ecc-p256"),
                       ["test-key-ed25519"]    = HTTPSignatureKey.Ed25519    (FromBase64Url(Ed25519_d),                "test-key-ed25519"),
                       ["test-shared-secret"]  = HTTPSignatureKey.HMACSHA256 (Convert.FromBase64String(SharedSecretBase64), "test-shared-secret")
                   };

        }

        #endregion

        #region (private static) FromBase64Url(Text)

        private static Byte[] FromBase64Url(String Text)
        {

            var base64 = Text.Replace('-', '+').Replace('_', '/');

            return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));

        }

        #endregion

        #region (private static) Pad(Bytes, Length)

        // A JWK drops leading zero octets; RSAParameters wants them back.
        private static Byte[] Pad(Byte[] Bytes, Int32 Length)

            => Bytes.Length >= Length
                   ? Bytes
                   : [.. new Byte[Length - Bytes.Length], .. Bytes];

        #endregion

    }

}
