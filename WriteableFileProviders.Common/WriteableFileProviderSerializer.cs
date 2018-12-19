using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;

namespace WriteableFileProviders
{
    public static  class WriteableFileProviderSerializer
    {
        public static IWriteableFileProvider FromConfiguration(IConfiguration configSection,
            params Assembly[] searchInAssemblies)
        {
            var typeString = configSection.GetValue<string>("Type");

            return fromExternalSource(typeString, searchInAssemblies, type =>
            {
                var ret = Activator.CreateInstance(type);
                foreach (var t in type.GetProperties().Where(e=>e.CanWrite))
                    if (configSection.GetValue(t.PropertyType, t.Name) != null)
                        t.SetValue(ret, configSection.GetValue(t.PropertyType, t.Name));
                return ret;
            });
        }
        public static IWriteableFileProvider FromJson(JObject obj, params Assembly[] searchInAssemblies)
        {
            var typeString = obj["Type"]?.Value<string>();
            return fromExternalSource(typeString, searchInAssemblies, obj.ToObject);
        }
        private static IWriteableFileProvider fromExternalSource(string typeString, Assembly[] searchInAssemblies, Func<Type, object> createArgumentObj)
        {
            if (string.IsNullOrEmpty(typeString))
                throw new ArgumentException("FileProvider cannot be parsed. 'Type' is not provided");
            var providerType = searchInAssemblies.SelectMany(e => e.GetTypes())
                .FirstOrDefault(e => e.GetInterfaces().Contains(typeof(IWriteableFileProvider)) && !e.IsAbstract && e.IsClass &&
                                     e.Name.Equals(typeString, StringComparison.InvariantCultureIgnoreCase));
            if (providerType == null)
                throw new ArgumentException($"FileProvider cannot be parsed. Type: '{typeString}' is not recognized");
            Type baseType = providerType;
            while (baseType != null &&
                   (!baseType.IsGenericType || baseType.GetGenericTypeDefinition() != typeof(WriteableFileProvider<>)))
                baseType = baseType?.BaseType;
            var optionsType = baseType?.GetGenericArguments()?.FirstOrDefault();
            if (optionsType == null)
                throw new ArgumentException($"No options type can be found for provider");
            var options = createArgumentObj(optionsType);
            var constructor = providerType.GetConstructors().FirstOrDefault(e =>
                e.GetParameters().Length == 1 && e.GetParameters().First().ParameterType == optionsType);
            if (constructor == null)
                throw new ArgumentException(
                    $"No Constructor can be found on FileProvider '{typeString}' that accepts a single options object");
            return (IWriteableFileProvider) Activator.CreateInstance(providerType, options);
        }

      

        public static JObject ToJson(IWriteableFileProvider provider)
        {
            return provider.ToJson();
        }
    }
}
