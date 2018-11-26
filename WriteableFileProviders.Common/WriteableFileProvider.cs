using System;
using System.Collections.Generic;
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

   
        public static IWriteableFileProvider FromJson(JObject obj, params Assembly[] searchInAssemblies)
        {
            var typeString = obj["Type"]?.Value<string>();
            if (string.IsNullOrEmpty(typeString))
                throw new ArgumentException("FileProvider cannot be parsed. 'Type' is not provided");
            var providerType =  searchInAssemblies.SelectMany(e=>e.GetTypes())
                .FirstOrDefault(e => e.IsSubclassOf(typeof(WriteableFileProvider<>)) && !e.IsAbstract && e.IsClass &&
                    e.Name.Equals(typeString, StringComparison.InvariantCultureIgnoreCase));
            if (providerType == null)
                throw new ArgumentException($"FileProvider cannot be parsed. Type: '{typeString}' is not recognized");
            Type baseType = providerType;
            while (baseType != null && (baseType.IsGenericType|| baseType.GetGenericTypeDefinition() != typeof(WriteableFileProvider<>)))
                baseType = baseType?.BaseType;
            var optionsType = baseType?.GetGenericArguments()?.FirstOrDefault();
            if (optionsType == null)
                throw new ArgumentException($"No options type can be found for provider");
            var options = obj.ToObject(optionsType);
            var constructor = providerType.GetConstructors().FirstOrDefault(e =>
                e.GetParameters().Length == 1 && e.GetParameters().First().ParameterType == optionsType);
            if (constructor == null)
                throw new ArgumentException($"No Constructor can be found on FileProvider '{typeString}' that accepts a single options object");
             return (IWriteableFileProvider) Activator.CreateInstance(providerType, options);
        }
        
      
        
        public JObject ToJson()
        {
            var ret = JObject.FromObject(Options);
            ret["Type"] = this.GetType().Name;
            return ret;
        }
        public async Task<IFileInfo> GetFileInfoAsync(string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return await getFileInfo(normalizedPath.path, normalizedPath.name);
        }
        public IFileInfo GetFileInfo(string subpath)
        {
            return RunTaskSerial(GetFileInfoAsync(subpath));
        }
        public async Task<IDirectoryContents> GetDirectoryContentsAsync(string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return await getDirectoryContents(normalizedPath.path, normalizedPath.name);
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
            var normalizedPath = normalizePath(filter);
            return await watchAsync(normalizedPath.path);
        }

        public async Task<Stream> ReadFileAsync(string subpath)
        {
            var normalizedPath = normalizePath(subpath);
            return await readFileAsync(normalizedPath.path);
        }

        public Stream ReadFile(string subpath)
        {
            return RunTaskSerial(ReadFileAsync(subpath));
        }

        public async Task UpdateFileAsync(string subpath, Stream contents)
        {
            var normalizedPath = normalizePath(subpath);
            await writeFileAsync(normalizedPath.path, contents);
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

        public abstract Task OnLocalFileStreamDisposingAsync(LocalFileStream stream);

        protected abstract Task<IFileInfo> getFileInfo(string normalizedPath, string name);

        protected abstract Task<IDirectoryContents> getDirectoryContents(string normalizedPath, string name);

        protected abstract Task<IChangeToken> watchAsync(string normalizedPath);

        protected abstract Task<Stream> readFileAsync(string normalizedPath);
        protected abstract Task writeFileAsync(string normalizedPath, Stream contents);
        protected abstract Task<bool> deleteAsync(string normalizedPath);
        protected abstract Task<LocalFileStream> createLocalFileStreamAsync(string normalizedPath);
        protected (string path, string name) normalizePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return (p, p);
            var ret = p.Replace("\\", "/");
            if (ret.StartsWith("/")) ret = ret.Substring(1);
            var name = ret;
            if (name.EndsWith("/")) name = name.Substring(0, name.Length - 1);
            if (name.Contains("/")) name = name.Substring(name.LastIndexOf("/") + 1);
            return (ret, name);

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
