using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json.Linq;

namespace WriteableFileProviders
{
    public abstract class WriteableFileProvider<T>:IWriteableFileProvider where T: FileProviderOptions, new()
    {
        protected WriteableFileProvider(T options)
        {
            Options = options;
        }
        public T Options { get; }
        
        public JObject ToJson()
        {
            var ret = JObject.FromObject(Options);
            ret["Type"] = this.GetType().Name;
            return ret;
        }

        private string[] getAbsoluteSegments(string[] pathSegments)
        {
            return (Options.RootDirectorySegments ?? new string[0]).Concat(pathSegments ?? new string[0]).ToArray();
        }
        protected abstract Task<IFileInfo> getFileInfoAsync(params string[] absolutePathSegments);
        public async Task<IFileInfo> GetFileInfoAsync(params string[] pathSegments)
        {
            return await getFileInfoAsync(getAbsoluteSegments(pathSegments));
        }
        public async Task<IFileInfo> GetFileInfoAsync(string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return await GetFileInfoAsync(normalizedPath);
        }
        public IFileInfo GetFileInfo(params string[] pathSegments)
        {
            return RunTaskSerial(GetFileInfoAsync(pathSegments));
        }
        public IFileInfo GetFileInfo(string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return GetFileInfo(normalizedPath);
        }


        protected abstract Task<IDirectoryContents> getDirectoryContentsAsync(params string[] absolutePathSegments);

        public async Task<IDirectoryContents> GetDirectoryContentsAsync(params string[] pathSegments)
        {
            return await getDirectoryContentsAsync(getAbsoluteSegments(pathSegments));
        }
        public async Task<IDirectoryContents> GetDirectoryContentsAsync(string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return await GetDirectoryContentsAsync(normalizedPath);
        }
        public IDirectoryContents GetDirectoryContents(params string[] pathSegments)
        {
            return RunTaskSerial(GetDirectoryContentsAsync(pathSegments));
        }
        public IDirectoryContents GetDirectoryContents(string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return GetDirectoryContents(normalizedPath);
        }


        protected abstract Task<IChangeToken> watchAsync(string filter);
        public IChangeToken Watch(string filter)
        {
            return RunTaskSerial(WatchAsync(filter));
        }
        public async Task<IChangeToken> WatchAsync(string filter)
        {
            return await watchAsync(filter);
        }


        protected abstract Task<Stream> readFileAsync(params string[] absolutePathSegments);

        public async Task<Stream> ReadFileAsync(params string[] pathSegments)
        {
            return await readFileAsync(getAbsoluteSegments(pathSegments));
        }
        public async Task<Stream> ReadFileAsync(string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return await ReadFileAsync(normalizedPath);
        }
        public Stream ReadFile(string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return ReadFile(normalizedPath);
        }
        public Stream ReadFile(params string[] pathSegments)
        {
            return RunTaskSerial(ReadFileAsync(pathSegments));
        }


        protected abstract Task updateFileAsync(Stream contents, params string[] absolutePathSegments);

        public async Task UpdateFileAsync(Stream contents, params string[] pathSegments)
        {
            await updateFileAsync(contents, getAbsoluteSegments(pathSegments));
        }
        public async Task UpdateFileAsync(string subpath, Stream contents)
        {
            var normalizedPath = normalizePath(subpath);
            await UpdateFileAsync(contents, normalizedPath);
        }
        public void UpdateFile(string subpath, Stream contents)
        {
            var normalizedPath = normalizePath(subpath);
            UpdateFile(contents, normalizedPath);
        }
        public void UpdateFile(Stream contents, params string[] pathSegments)
        {
            RunTaskSerial(UpdateFileAsync(contents, pathSegments));
        }


        protected abstract Task<bool> deleteAsync(params string[] absolutePathSegments);

        public async Task<bool> DeleteAsync(params string[] pathSegments)
        {
            return await deleteAsync(getAbsoluteSegments(pathSegments));
        }
        public bool Delete(string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return Delete(normalizedPath);
        }
        public async Task<bool> DeleteAsync(string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return await DeleteAsync(normalizedPath);
        }
        public bool Delete(params string[] pathSegments)
        {
            return RunTaskSerial(DeleteAsync(pathSegments));
        }



        public LocalFileStream CreateLocalFileStream(bool readOnly, string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return CreateLocalFileStream(readOnly, normalizedPath);
        }
        public async Task<LocalFileStream> CreateLocalFileStreamAsync(bool readOnly, string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return await CreateLocalFileStreamAsync(readOnly, normalizedPath);
        }
        public LocalFileStream CreateLocalFileStream(bool readOnly, params string[] pathSegments)
        {
            return RunTaskSerial(CreateLocalFileStreamAsync(readOnly, pathSegments));
        }
        public virtual async Task<LocalFileStream> CreateLocalFileStreamAsync(bool readOnly, params string[] pathSegments)
        {
            return await createLocalFileStreamAsync(readOnly, getAbsoluteSegments(pathSegments));

        }
        protected virtual async Task<LocalFileStream> createLocalFileStreamAsync(bool readOnly, params string[] absolutePathSegments)
        {
            var tempFileName = Path.GetTempFileName();
            var tempStream = new FileStream(tempFileName, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            try
            {
                using (var fs = await readFileAsync(absolutePathSegments))
                    fs.CopyTo(tempStream);
                tempStream.Position = 0;
                return new LocalFileStream(tempStream, this, readOnly, absolutePathSegments);
            }
            catch
            {
                tempStream.Dispose();
                throw;
            }

        }


        public virtual async Task OnLocalFileStreamDisposingAsync(LocalFileStream stream)
        {
            if (stream.ReadOnly) return;
            stream.Position = 0;
            await updateFileAsync(stream, stream.TargetFilePathSegments);
        }


        protected string[] normalizePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return new string[0];
            var ret = p.Replace("\\", "/");
            if (ret.StartsWith("/")) ret = ret.Substring(1);
            return ret.Split(new []{'/'}, StringSplitOptions.RemoveEmptyEntries);

        }

        protected T RunTaskSerial<T>(Task<T> t)
        {
            try
            {
                return t.Result;
            }
            catch (AggregateException e)
            {
                throw e.InnerException;
            }
        }
        protected void RunTaskSerial(Task t)
        {
            try
            {
                t.Wait();
            }
            catch (AggregateException e)
            {
                throw e.InnerException;
            }
        }

     
    }
}
