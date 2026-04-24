using System;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Maps a typed property to a <c>jsonb</c> column using an AOT-safe
    /// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> for serialization.
    /// The property value is serialized to JSON on write and deserialized on read.
    /// Use <see cref="RawJsonColumnAttribute"/> when the column holds a raw <see cref="string"/>.
    /// </summary>
    /// <example><code>
    /// [JsonSerializable(typeof(TypedMetadata))]
    /// internal partial class MyJsonContext : JsonSerializerContext { }
    ///
    /// [JsonColumn(typeof(MyJsonContext))]
    /// public TypedMetadata? Metadata { get; set; }
    /// </code></example>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class JsonColumnAttribute : Attribute
    {
        /// <summary>
        /// The <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> subclass
        /// used for AOT-safe serialization and deserialization of this column's value.
        /// </summary>
        public Type JsonContextType { get; }

        /// <param name="jsonContextType">
        /// A <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> subclass
        /// that includes the property's type as a registered serializable type.
        /// </param>
        public JsonColumnAttribute(Type jsonContextType)
        {
            JsonContextType = jsonContextType;
        }
    }
}
