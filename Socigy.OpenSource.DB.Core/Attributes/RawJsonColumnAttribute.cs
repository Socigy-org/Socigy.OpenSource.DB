using System;

namespace Socigy.OpenSource.DB.Attributes
{
    /// <summary>
    /// Maps a <see cref="string"/> property to a <c>jsonb</c> column.
    /// The string value is stored and retrieved as raw JSON text — no serialization occurs.
    /// Use <see cref="JsonColumnAttribute"/> when you want the column to hold a typed object.
    /// </summary>
    /// <example><code>
    /// [RawJsonColumn]
    /// public string? Metadata { get; set; }
    /// </code></example>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class RawJsonColumnAttribute : Attribute { }
}
