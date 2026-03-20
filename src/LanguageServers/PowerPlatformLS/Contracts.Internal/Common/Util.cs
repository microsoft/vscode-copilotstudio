namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;

    public static class Util
    {
        private static readonly ThreadLocal<Random> Random = new ThreadLocal<Random>(() => new Random());

        public static string GenerateRandomString(int length)
        {
            Random rng = Random.Value ?? throw new InvalidOperationException("Error building thread-safe Random Number Generator");
            const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            char[] stringChars = new char[length];
            for (int idx = 0; idx < length; ++idx)
            {
                stringChars[idx] = Alphabet[rng.Next(Alphabet.Length)];
            }

            return new string(stringChars);
        }
    }
}