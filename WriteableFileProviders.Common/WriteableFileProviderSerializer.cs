using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;

namespace WriteableFileProviders
{
    public static  class WriteableFileProviderSerializer
    {
        public static IWriteableFileProvider FromJson(JObject obj, params Assembly[] searchInAssemblies)
        {
            var typeString = obj["Type"]?.Value<string>();
            if (string.IsNullOrEmpty(typeString))
                throw new ArgumentException("FileProvider cannot be parsed. 'Type' is not provided");
            var providerType = searchInAssemblies.SelectMany(e => e.GetTypes())
                .FirstOrDefault(e => e.IsSubclassOf(typeof(WriteableFileProvider<>)) && !e.IsAbstract && e.IsClass &&
                                     e.Name.Equals(typeString, StringComparison.InvariantCultureIgnoreCase));
            if (providerType == null)
                throw new ArgumentException($"FileProvider cannot be parsed. Type: '{typeString}' is not recognized");
            Type baseType = providerType;
            while (baseType != null && (baseType.IsGenericType || baseType.GetGenericTypeDefinition() != typeof(WriteableFileProvider<>)))
                baseType = baseType?.BaseType;
            var optionsType = baseType?.GetGenericArguments()?.FirstOrDefault();
            if (optionsType == null)
                throw new ArgumentException($"No options type can be found for provider");
            var options = obj.ToObject(optionsType);
            var constructor = providerType.GetConstructors().FirstOrDefault(e =>
                e.GetParameters().Length == 1 && e.GetParameters().First().ParameterType == optionsType);
            if (constructor == null)
                throw new ArgumentException($"No Constructor can be found on FileProvider '{typeString}' that accepts a single options object");
            return (IWriteableFileProvider)Activator.CreateInstance(providerType, options);
        }

        public static JObject ToJson(IWriteableFileProvider provider)
        {
            return provider.ToJson();
        }
    }
}
