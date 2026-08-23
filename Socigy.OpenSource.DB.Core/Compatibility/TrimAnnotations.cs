// Trim/AOT annotation attributes for the netstandard2.0 build.
//
// Core targets netstandard2.0, which predates System.Diagnostics.CodeAnalysis's trimming attributes.
// ILLink and ILC match these attributes by *full name*, not by assembly identity, so a library that
// defines its own internal copies gets exactly the same treatment as one compiled against the BCL.
// This is the standard polyfill pattern and is what lets Core suppress or annotate a reflection site
// in a way an application's `dotnet publish -p:PublishAot=true` actually honours.
//
// Under net5.0+ (the Socigy.OpenSource.DB.Core.AotAnalysis harness project, which compiles these same
// sources with the trim analyzers enabled) the BCL supplies the real types, so this file compiles to
// nothing and the analyzers bind to the framework definitions.

#if !NET5_0_OR_GREATER

using System;

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>Specifies the types of members that are dynamically accessed.</summary>
    [Flags]
    internal enum DynamicallyAccessedMemberTypes
    {
        None = 0,
        PublicParameterlessConstructor = 0x0001,
        PublicConstructors = 0x0002 | PublicParameterlessConstructor,
        NonPublicConstructors = 0x0004,
        PublicMethods = 0x0008,
        NonPublicMethods = 0x0010,
        PublicFields = 0x0020,
        NonPublicFields = 0x0040,
        PublicNestedTypes = 0x0080,
        NonPublicNestedTypes = 0x0100,
        PublicProperties = 0x0200,
        NonPublicProperties = 0x0400,
        PublicEvents = 0x0800,
        NonPublicEvents = 0x1000,
        Interfaces = 0x2000,
        All = ~None
    }

    /// <summary>
    /// Indicates that certain members on a dynamically accessed <see cref="Type"/> are accessed
    /// reflectively and must therefore be preserved by trimming.
    /// </summary>
    [AttributeUsage(
        AttributeTargets.Field | AttributeTargets.ReturnValue | AttributeTargets.GenericParameter |
        AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Method,
        Inherited = false)]
    internal sealed class DynamicallyAccessedMembersAttribute : Attribute
    {
        public DynamicallyAccessedMembersAttribute(DynamicallyAccessedMemberTypes memberTypes)
            => MemberTypes = memberTypes;

        public DynamicallyAccessedMemberTypes MemberTypes { get; }
    }

    /// <summary>
    /// Suppresses a trim or AOT analysis warning unconditionally — the annotated code has been
    /// reviewed and proven safe. Every use in this codebase carries a Justification explaining why.
    /// </summary>
    [AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)]
    internal sealed class UnconditionalSuppressMessageAttribute : Attribute
    {
        public UnconditionalSuppressMessageAttribute(string category, string checkId)
        {
            Category = category;
            CheckId = checkId;
        }

        public string Category { get; }
        public string CheckId { get; }
        public string? Scope { get; set; }
        public string? Target { get; set; }
        public string? MessageId { get; set; }
        public string? Justification { get; set; }
    }

    /// <summary>Indicates that the annotated member requires code that trimming cannot statically prove reachable.</summary>
    [AttributeUsage(
        AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Class,
        Inherited = false)]
    internal sealed class RequiresUnreferencedCodeAttribute : Attribute
    {
        public RequiresUnreferencedCodeAttribute(string message) => Message = message;

        public string Message { get; }
        public string? Url { get; set; }
    }

    /// <summary>Indicates that the annotated member requires runtime code generation, which NativeAOT cannot do.</summary>
    [AttributeUsage(
        AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Class,
        Inherited = false)]
    internal sealed class RequiresDynamicCodeAttribute : Attribute
    {
        public RequiresDynamicCodeAttribute(string message) => Message = message;

        public string Message { get; }
        public string? Url { get; set; }
    }
}

#endif
