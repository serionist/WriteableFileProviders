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
    public abstract class WriteableFileProvider<T>:IWriteableFileProvider where T: class, new()
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
        public abstract Task<IFileInfo> GetFileInfoAsync(params string[] pathSegments);
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


        public abstract Task<IDirectoryContents> GetDirectoryContentsAsync(params string[] pathSegments);
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


        public abstract Task<Stream> ReadFileAsync(params string[] pathSegments);
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


        public abstract Task UpdateFileAsync(Stream contents, params string[] pathSegments);
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


        public abstract Task<bool> DeleteAsync(params string[] pathSegments);
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



        public LocalFileStream CreateLocalFileStream(string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return CreateLocalFileStream(normalizedPath);
        }
        public async Task<LocalFileStream> CreateLocalFileStreamAsync(string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return await CreateLocalFileStreamAsync(normalizedPath);
        }
        public LocalFileStream CreateLocalFileStream(params string[] pathSegments)
        {
            return RunTaskSerial(CreateLocalFileStreamAsync(pathSegments));
        }
        public virtual async Task<LocalFileStream> CreateLocalFileStreamAsync(params string[] pathSegments)
        {
            var tempFileName = Path.GetTempFileName();
            var tempStream = new FileStream(tempFileName, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            try
            {
                using (var fs = await ReadFileAsync(pathSegments))
                    fs.CopyTo(tempStream);
                tempStream.Position = 0;
                return new LocalFileStream(tempStream, this, false, pathSegments);
            }
            catch
            {
                tempStream.Dispose();
                throw;
            }

        }


        public virtual async Task OnLocalFileStreamDisposingAsync(LocalFileStream stream)
        {
            stream.Position = 0;
            await UpdateFileAsync(stream, stream.TargetFilePathSegments);
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
