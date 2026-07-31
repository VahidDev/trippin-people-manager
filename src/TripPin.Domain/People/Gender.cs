namespace TripPin.Domain.People;

/// <summary>
/// Mirrors the service's PersonGender enumeration.
/// </summary>
/// <remarks>
/// Deliberately non-nullable. The service declares the property nullable, but
/// writing null returns 204 and silently coerces the value to Male (ordinal
/// zero), so "no gender set" is not actually expressible. <see cref="Unknown"/>
/// is the honest third state, and the mapper never emits null for it.
/// <para>
/// The ordinals are load-bearing rather than incidental: they are the values
/// the service assigns, and Male being zero is exactly why a null write
/// silently becomes Male.
/// </para>
/// </remarks>
public enum Gender
{
    Male = 0,
    Female = 1,
    Unknown = 2,
}
