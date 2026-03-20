namespace Microsoft.PowerPlatformLS.Contracts.Internal.Common
{
    using Microsoft.Extensions.FileProviders;
    using System.Diagnostics;

    public static class FileInfoExtensions
    {
        /// <summary>
        /// Read all text from the given document path.
        /// </summary>
        public static string ReadAllText(this IFileInfo file)
        {
            Debug.Assert(file.Exists, "Check file.Exists first");
            using var fs = file.CreateReadStream();
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }

        /// <summary>
        /// Get the base64 encoded content of the file at the specified document path.
        /// </summary>
        /// <param name="file">File handle.</param>
        public static string ReadBase64(this IFileInfo file)
        {
            Debug.Assert(file.Exists, "Check file.Exists first");
            using var fs = file.CreateReadStream();
            byte[] bytes;
            using (var memoryStream = new MemoryStream())
            {
                fs.CopyTo(memoryStream);
                fs.Close();
                bytes = memoryStream.ToArray();
            }

            return Convert.ToBase64String(bytes);
        }
    }
}
