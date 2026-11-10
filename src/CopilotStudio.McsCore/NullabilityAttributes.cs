// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Polyfills for System.Diagnostics.CodeAnalysis nullability annotations that
// are public on net5+ but internal in netstandard2.0's BCL. Same-namespace
// internal copies in this assembly take precedence over the BCL's internal
// copies (which are invisible across assembly boundaries).

#if NETSTANDARD2_0
namespace System.Diagnostics.CodeAnalysis;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property, Inherited = false)]
internal sealed class NotNullWhenAttribute : Attribute
{
    public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;
    public bool ReturnValue { get; }
}

[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue, AllowMultiple = true, Inherited = false)]
internal sealed class NotNullIfNotNullAttribute : Attribute
{
    public NotNullIfNotNullAttribute(string parameterName) => ParameterName = parameterName;
    public string ParameterName { get; }
}
#endif
