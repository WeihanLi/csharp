using System.Reflection;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace k8s
{
    /// <summary>
    /// This is a utility class that helps you load objects from YAML files.
    /// </summary>
    public static class KubernetesYaml
    {
        private static readonly object DeserializerLockObject = new object();
        private static readonly object SerializerLockObject = new object();

        private static DeserializerBuilder CommonDeserializerBuilder =>
            new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithTypeConverter(new IntOrStringYamlConverter())
                .WithTypeConverter(new ByteArrayStringYamlConverter())
                .WithTypeConverter(new ResourceQuantityYamlConverter())
                .WithAttemptingUnquotedStringTypeDeserialization()
                .WithOverridesFromJsonPropertyAttributes();

        private static readonly IDeserializer StrictDeserializer =
            CommonDeserializerBuilder
            .WithDuplicateKeyChecking()
            .Build();
        private static readonly IDeserializer Deserializer =
            CommonDeserializerBuilder
            .IgnoreUnmatchedProperties()
            .Build();
        private static IDeserializer GetDeserializer(bool strict) => strict ? StrictDeserializer : Deserializer;

        private static readonly IValueSerializer Serializer =
            new SerializerBuilder()
                .DisableAliases()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithTypeConverter(new IntOrStringYamlConverter())
                .WithTypeConverter(new ByteArrayStringYamlConverter())
                .WithTypeConverter(new ResourceQuantityYamlConverter())
                .WithEventEmitter(e => new StringQuotingEmitter(e))
                .WithEventEmitter(e => new FloatEmitter(e))
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
                .WithOverridesFromJsonPropertyAttributes()
                .BuildValueSerializer();

        private static readonly IDictionary<string, Type> ModelTypeMap = typeof(KubernetesEntityAttribute).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(KubernetesEntityAttribute), true).Any())
            .ToDictionary(
                t =>
                {
                    var attr = (KubernetesEntityAttribute)t.GetCustomAttribute(
                        typeof(KubernetesEntityAttribute), true);
                    var groupPrefix = string.IsNullOrEmpty(attr.Group) ? "" : $"{attr.Group}/";
                    return $"{groupPrefix}{attr.ApiVersion}/{attr.Kind}";
                },
                t => t);

        private class ByteArrayStringYamlConverter : IYamlTypeConverter
        {
            public bool Accepts(Type type)
            {
                return type == typeof(byte[]);
            }

            public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
            {
                if (parser?.Current is Scalar scalar)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(scalar.Value))
                        {
                            return null;
                        }

                        return Encoding.UTF8.GetBytes(scalar.Value);
                    }
                    finally
                    {
                        parser.MoveNext();
                    }
                }

                throw new InvalidOperationException(parser.Current?.ToString());
            }

            public void WriteYaml(IEmitter emitter, object value, Type type, ObjectSerializer serializer)
            {
                var obj = (byte[])value;
                emitter?.Emit(new Scalar(Encoding.UTF8.GetString(obj)));
            }
        }

        /// <summary>
        /// Load a collection of objects from a stream asynchronously
        ///
        /// caller is responsible for closing the stream
        /// </summary>
        /// <param name="stream">
        /// The stream to load the objects from.
        /// </param>
        /// <param name="typeMap">
        /// A map from apiVersion/kind to Type. For example "v1/Pod" -> typeof(V1Pod). If null, a default mapping will
        /// be used.
        /// </param>
        /// <param name="strict">true if a strict deserializer should be used (throwing exception on unknown properties), false otherwise</param>
        /// <returns>collection of objects</returns>
        public static async Task<List<object>> LoadAllFromStreamAsync(Stream stream, IDictionary<string, Type> typeMap = null, bool strict = false)
        {
            var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync().ConfigureAwait(false);
            return LoadAllFromString(content, typeMap);
        }


        /// <summary>
        /// Load a collection of objects from a file asynchronously
        /// </summary>
        /// <param name="fileName">The name of the file to load from.</param>
        /// <param name="typeMap">
        /// A map from apiVersion/kind to Type. For example "v1/Pod" -> typeof(V1Pod). If null, a default mapping will
        /// be used.
        /// </param>
        /// <param name="strict">true if a strict deserializer should be used (throwing exception on unknown properties), false otherwise</param>
        /// <returns>collection of objects</returns>
        public static async Task<List<object>> LoadAllFromFileAsync(string fileName, IDictionary<string, Type> typeMap = null, bool strict = false)
        {
            using (var fileStream = File.OpenRead(fileName))
            {
                return await LoadAllFromStreamAsync(fileStream, typeMap).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Load a collection of objects from a string
        /// </summary>
        /// <param name="content">
        /// The string to load the objects from.
        /// </param>
        /// <param name="typeMap">
        /// A map from apiVersion/kind to Type. For example "v1/Pod" -> typeof(V1Pod). If null, a default mapping will
        /// be used.
        /// </param>
        /// <param name="strict">true if a strict deserializer should be used (throwing exception on unknown properties), false otherwise</param>
        /// <returns>collection of objects</returns>
        public static List<object> LoadAllFromString(string content, IDictionary<string, Type> typeMap = null, bool strict = false)
        {
            var mergedTypeMap = new Dictionary<string, Type>(ModelTypeMap);
            // merge in KVPs from typeMap, overriding any in ModelTypeMap
            typeMap?.ToList().ForEach(x => mergedTypeMap[x.Key] = x.Value);

            var types = new List<Type>();
            var parser = new MergingParser(new Parser(new StringReader(content)));
            parser.Consume<StreamStart>();
            while (parser.Accept<DocumentStart>(out _))
            {
                lock (DeserializerLockObject)
                {
                    var dict = GetDeserializer(strict).Deserialize<Dictionary<object, object>>(parser);
                    types.Add(mergedTypeMap[dict["apiVersion"] + "/" + dict["kind"]]);
                }
            }

            parser = new MergingParser(new Parser(new StringReader(content)));
            parser.Consume<StreamStart>();
            var ix = 0;
            var results = new List<object>();
            while (parser.Accept<DocumentStart>(out _))
            {
                var objType = types[ix++];
                lock (DeserializerLockObject)
                {
                    var obj = GetDeserializer(strict).Deserialize(parser, objType);
                    results.Add(obj);
                }
            }

            return results;
        }

        public static async Task<T> LoadFromStreamAsync<T>(Stream stream, bool strict = false)
        {
            var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync().ConfigureAwait(false);
            return Deserialize<T>(content, strict);
        }

        public static async Task<T> LoadFromFileAsync<T>(string file, bool strict = false)
        {
            using (var fs = File.OpenRead(file))
            {
                return await LoadFromStreamAsync<T>(fs, strict).ConfigureAwait(false);
            }
        }

        [Obsolete("use Deserialize")]
        public static T LoadFromString<T>(string content, bool strict = false)
        {
            return Deserialize<T>(content, strict);
        }

        [Obsolete("use Serialize")]
        public static string SaveToString<T>(T value)
        {
            return Serialize(value);
        }

        public static TValue Deserialize<TValue>(string yaml, bool strict = false)
        {
            using var reader = new StringReader(yaml);
            lock (DeserializerLockObject)
            {
                return GetDeserializer(strict).Deserialize<TValue>(new MergingParser(new Parser(reader)));
            }
        }

        public static TValue Deserialize<TValue>(Stream yaml, bool strict = false)
        {
            using var reader = new StreamReader(yaml);
            lock (DeserializerLockObject)
            {
                return GetDeserializer(strict).Deserialize<TValue>(new MergingParser(new Parser(reader)));
            }
        }

        public static string SerializeAll(IEnumerable<object> values)
        {
            if (values == null)
            {
                return "";
            }

            var stringBuilder = new StringBuilder();
            var writer = new StringWriter(stringBuilder);
            var emitter = new Emitter(writer);

            emitter.Emit(new StreamStart());

            foreach (var value in values)
            {
                if (value != null)
                {
                    emitter.Emit(new DocumentStart());
                    lock (SerializerLockObject)
                    {
                        Serializer.SerializeValue(emitter, value, value.GetType());
                    }

                    emitter.Emit(new DocumentEnd(true));
                }
            }

            return stringBuilder.ToString();
        }

        public static string Serialize(object value)
        {
            if (value == null)
            {
                return "";
            }

            var stringBuilder = new StringBuilder();
            var writer = new StringWriter(stringBuilder);
            var emitter = new Emitter(writer);

            emitter.Emit(new StreamStart());
            emitter.Emit(new DocumentStart());
            lock (SerializerLockObject)
            {
                Serializer.SerializeValue(emitter, value, value.GetType());
            }

            return stringBuilder.ToString();
        }

        private static TBuilder WithOverridesFromJsonPropertyAttributes<TBuilder>(this TBuilder builder)
            where TBuilder : BuilderSkeleton<TBuilder>
        {
            // Use VersionInfo from the model namespace as that should be stable.
            // If this is not generated in the future we will get an obvious compiler error.
            var targetNamespace = typeof(VersionInfo).Namespace;

            // Get all the concrete model types from the code generated namespace.
            var types = typeof(KubernetesEntityAttribute).Assembly
                .ExportedTypes
                .Where(type => type.Namespace == targetNamespace &&
                               !type.IsInterface &&
                               !type.IsAbstract);

            // Map any JsonPropertyAttribute instances to YamlMemberAttribute instances.
            foreach (var type in types)
            {
                foreach (var property in type.GetProperties())
                {
                    var jsonAttribute = property.GetCustomAttribute<JsonPropertyNameAttribute>();
                    if (jsonAttribute == null)
                    {
                        continue;
                    }

                    var yamlAttribute = new YamlMemberAttribute { Alias = jsonAttribute.Name, ApplyNamingConventions = false };
                    builder.WithAttributeOverride(type, property.Name, yamlAttribute);
                }
            }

            return builder;
        }
    }
}
