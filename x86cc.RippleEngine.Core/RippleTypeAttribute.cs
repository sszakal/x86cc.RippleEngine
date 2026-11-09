using System.Collections.Concurrent;
using System.Reflection;

namespace x86cc.RippleEngine.Core;

/// <summary>
/// Pins the persisted name of a wave or ripple payload type — the string stamped as its <c>payload_type</c> and
/// composed into the <c>type_key</c> (<c>"{waveType}|{rippleType}"</c>) that selects its handler and schedule.
/// Without it the name is the CLR <see cref="MemberInfo.Name"/>, which makes the name an accident of the code:
/// renaming the class orphans every ripple already in the database (no handler matches the old key), and two
/// classes with the same name in different namespaces collide on one key.
/// </summary>
/// <example>
/// Renaming <c>RecalcCompany</c> to <c>CompanyRecalc</c> without stranding in-flight work:
/// <code>
/// [RippleType("RecalcCompany")]
/// public sealed class CompanyRecalc : IRippleTarget { … }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class RippleTypeAttribute(string name) : Attribute
{
    /// <summary>The persisted type name.</summary>
    public string Name { get; } = name;
}

/// <summary>
/// The one place a payload type's persisted name is decided — every generator (fan-out side) and the handler
/// registry (worker side) resolve it here, so the <c>type_key</c> written at fan-out is guaranteed to be the
/// one the worker looks its handler up by. <see cref="RippleTypeAttribute"/> if present, else the CLR name.
/// </summary>
public static class RippleTypeName
{
    // The composite type_key separator (RippleTypeKey.Separator in Storage, which Core can't reference). A name
    // containing it would make "{wave}|{ripple}" ambiguous, so it is rejected.
    private const char Separator = '|';

    private static readonly ConcurrentDictionary<Type, string> Cache = new();

    /// <summary>The persisted name of <typeparamref name="T"/>.</summary>
    public static string Of<T>() => Of(typeof(T));

    /// <summary>The persisted name of <paramref name="type"/>.</summary>
    /// <exception cref="InvalidOperationException">The <see cref="RippleTypeAttribute"/> name is blank or
    /// contains the <c>type_key</c> separator.</exception>
    public static string Of(Type type) => Cache.GetOrAdd(type, static t =>
    {
        if (t.GetCustomAttribute<RippleTypeAttribute>() is not { } attr)
        {
            return t.Name;
        }

        if (string.IsNullOrWhiteSpace(attr.Name) || attr.Name.Contains(Separator))
        {
            throw new InvalidOperationException(
                $"[RippleType(\"{attr.Name}\")] on {t.FullName} is invalid: the name must be non-blank and must not "
                + $"contain '{Separator}' (the type_key separator).");
        }

        return attr.Name;
    });
}
