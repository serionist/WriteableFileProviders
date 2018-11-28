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
        public async Task<IFileInfo> GetFileInfoAsync(string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return await getFileInfo(normalizedPath);
        }
        public IFileInfo GetFileInfo(string subpath)
        {
            return RunTaskSerial(GetFileInfoAsync(subpath));
        }
        public async Task<IDirectoryContents> GetDirectoryContentsAsync(string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return await getDirectoryContents(normalizedPath);
        }

        public IDirectoryContents GetDirectoryContents(string subpath)
        {
            return RunTaskSerial(GetDirectoryContentsAsync(subpath));
        }

        public IChangeToken Watch(string filter)
        {
            return RunTaskSerial(WatchAsync(filter));
        }
        public async Task<IChangeToken> WatchAsync(string filter)
        {
            return await watchAsync(filter);
        }

        public async Task<Stream> ReadFileAsync(string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return await readFileAsync(normalizedPath);
        }

        public Stream ReadFile(string subpath)
        {
            return RunTaskSerial(ReadFileAsync(subpath));
        }

        public async Task UpdateFileAsync(string subpath, Stream contents)
        {
            var normalizedPath = normalizePath(subpath);
            await writeFileAsync(contents, normalizedPath);
        }
        public void UpdateFile(string subpath, Stream contents)
        {
            RunTaskSerial(UpdateFileAsync(subpath, contents));
        }
        public bool Delete(string subpath)
        {
            return RunTaskSerial(DeleteAsync(subpath));
        }

        public async Task<bool> DeleteAsync(string subpath)
        {
            return await deleteAsync(subpath);
        }

        public LocalFileStream CreateLocalFileStream(string subpath)
        {
            return RunTaskSerial(CreateLocalFileStreamAsync(subpath));
        }
        public async Task<LocalFileStream> CreateLocalFileStreamAsync(string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return await createLocalFileStreamAsync(subpath);
        }

    

        protected abstract Task<IFileInfo> getFileInfo(params string[] pathSegments);

        protected abstract Task<IDirectoryContents> getDirectoryContents(params string[] pathSegments);

        protected abstract Task<IChangeToken> watchAsync(string filter);

        protected abstract Task<Stream> readFileAsync(params string[] pathSegments);
        protected abstract Task writeFileAsync(Stream contents, params string[] pathSegments);
        protected abstract Task<bool> deleteAsync(params string[] pathSegments);

        protected virtual async Task<LocalFileStream> createLocalFileStreamAsync(params string[] pathSegments)
        {
            var tempFileName = Path.GetTempFileName();
            var tempStream = new FileStream(tempFileName, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            try
            {
                using (var fs = await readFileAsync(pathSegments))
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
            await writeFileAsync(stream, stream.TargetFilePathSegments);
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
