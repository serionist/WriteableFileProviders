using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json.Linq;

namespace WriteableFileProviders
{
    public interface IWriteableFileProvider:IFileProvider
    {
        /// <summary>Locate a file at the given path.</summary>
        /// <param name="subpath">Relative path that identifies the file.</param>
        /// <returns>The file information. Caller must check Exists property.</returns>
        Task<IFileInfo> GetFileInfoAsync(string subpath);
        
        /// <summary>Enumerate a directory at the given path, if any.</summary>
        /// <param name="subpath">Relative path that identifies the directory.</param>
        /// <returns>Returns the contents of the directory.</returns>
        Task<IDirectoryContents> GetDirectoryContentsAsync(string subpath);

        /// <summary>
        /// Creates a <see cref="T:Microsoft.Extensions.Primitives.IChangeToken" /> for the specified <paramref name="filter" />.
        /// </summary>
        /// <param name="filter">Filter string used to determine what files or folders to monitor. Example: **/*.cs, *.*, subFolder/**/*.cshtml.</param>
        /// <returns>An <see cref="T:Microsoft.Extensions.Primitives.IChangeToken" /> that is notified when a file matching <paramref name="filter" /> is added, modified or deleted.</returns>
        Task<IChangeToken> WatchAsync(string filter);
        /// <summary>
        /// Reads the contents of a specified file
        /// </summary>
        /// <param name="subPath"></param>
        /// <returns></returns>
        Task<Stream> ReadFileAsync(string subpath);
        /// <summary>
        /// Reads the contents of a specified file. This a read-only stream
        /// </summary>
        /// <param name="subPath"></param>
        /// <returns></returns>
        Stream ReadFile(string subpath);
        /// <summary>
        /// Updates a file with the specified contents
        /// </summary>
        /// <param name="subpath"></param>
        /// <param name="contents"></param>
        /// <returns></returns>
        Task UpdateFileAsync(string subpath, Stream contents);
        /// <summary>
        /// Updates a file with the specified contents
        /// </summary>
        /// <param name="subpath"></param>
        /// <param name="contents"></param>
        /// <returns></returns>
        void UpdateFile(string subpath, Stream contents);
        /// <summary>
        /// Deletes a file or directory. The delete is recursive for directories
        /// </summary>
        /// <param name="subpath"></param>
        /// <returns></returns>
        bool Delete(string subpath);
        /// <summary>
        /// Deletes a file or directory. The delete is recursive for directories
        /// </summary>
        /// <param name="subpath"></param>
        /// <returns></returns>
        Task<bool> DeleteAsync(string subpath);


        /// <summary>
        /// Return file contents as a local stream that can be written to. This will ensure the file is placed on the local filesystem (at least temporarily). Caller should dispose stream when complete, only then will the file be committed.
        /// </summary>
        /// <param name="subpath">The path to the file</param>
        /// <returns>The file stream</returns>
        /// <exception cref="System.IO.FileNotFoundException"></exception>
        Task<LocalFileStream> CreateLocalFileStreamAsync(string subpath);
        /// <summary>
        /// Return file contents as a local stream that can be written to. This will ensure the file is placed on the local filesystem (at least temporarily). Caller should dispose stream when complete, only then will the file be committed.
        /// </summary>
        /// <param name="subpath">The path to the file</param>
        /// <returns>The file stream</returns>
        /// <exception cref="System.IO.FileNotFoundException"></exception>
        LocalFileStream CreateLocalFileStream(string subpath);
        /// <summary>
        /// The callback called when a local file stream is closed. Used to commit the contents of a local stream
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        Task OnLocalFileStreamDisposingAsync(LocalFileStream stream);
    }
}
