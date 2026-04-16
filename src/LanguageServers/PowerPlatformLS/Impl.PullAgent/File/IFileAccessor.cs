namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.PowerPlatformLS.Contracts.FileLayout;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using System.Text;

    /// <summary>
    /// Abstraction over file access.
    /// Note that .NET IFileProvider is a read-only abstraction, and this specifically needs writes.
    /// </summary>
    internal interface IFileAccessor
    {
        bool Exists(AgentFilePath path);

        void CreateHiddenDirectory(AgentFilePath path);

        /// <summary>
        /// Write contents to disk.
        /// Overwrite file. Create directory if needed. 
        /// </summary>
        /// <param name="path"></param>
        /// <returns>writeable stream.</returns>
        Stream OpenWrite(AgentFilePath path);

        /// <summary>
        /// Return a read-only stream for accessing the file. 
        /// </summary>
        /// <param name="path"></param>
        /// <returns>a read-only stream for accessing the file.
        /// Caller should dispose.</returns>
        /// <exception cref="FileNotFoundException">Thrown if directory or file does not exist.
        /// Use <see cref="Exists(AgentFilePath)"/> to detect.
        /// </exception>
        Stream OpenRead(AgentFilePath path);

        /// <summary>
        /// Delete the file.
        /// nop if file doesn't exist. 
        /// </summary>
        /// <param name="path"></param>
        void Delete(AgentFilePath path);

        /// <summary>
        /// Replace file content from sourcePath to targetPath.
        /// </summary>
        /// <param name="sourcePath">Path of source file.</param>
        /// <param name="targetPath">Path of target file.</param>
        void Replace(AgentFilePath sourcePath, AgentFilePath targetPath);
    }

    internal static class FileAccessorExtensions
    {
        public static async Task WriteAsync(this IFileAccessor writer, AgentFilePath path, string contents, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();

            using var stream = writer.OpenWrite(path);
            using TextWriter sw = new StreamWriter(stream, Encoding.UTF8);
            await sw.WriteAsync(contents.AsMemory(), cancel);
        }

        public static async Task WriteAsync(this IFileAccessor writer, AgentFilePath path, byte[] bytes, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();

            using var stream = writer.OpenWrite(path);

            await stream.WriteAsync(bytes, cancel);
        }

        public static async Task<string> ReadStringAsync(this IFileAccessor writer, AgentFilePath path, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();

            using var stream = writer.OpenRead(path);
            using var sr = new StreamReader(stream);

            string str = await sr.ReadToEndAsync(cancel);
            return str;
        }
    }
}