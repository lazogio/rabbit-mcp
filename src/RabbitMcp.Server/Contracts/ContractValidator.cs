// Copyright (c) 2026 lazogio
// SPDX-License-Identifier: MIT

using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace RabbitMcp.Server.Contracts;

/// <summary>
/// Runs DataAnnotations validation over a contract instance, including nested
/// objects and collections. Error paths use JSON (camelCase) names, e.g.
/// <c>lines[0].quantity: The field Quantity must be between 1 and 1000.</c>
/// </summary>
public static class ContractValidator
{
    public static IReadOnlyList<string> Validate(object? instance)
    {
        var errors = new List<string>();
        if (instance is null)
        {
            errors.Add("message: is required.");
            return errors;
        }

        // Attributes such as [Range(typeof(decimal), "0.01", "1000")] parse their limits with the current
        // culture, which throws on comma-decimal locales (ka-GE, de-DE). Contract DLLs are external, so
        // validate under the invariant culture instead of relying on ParseLimitsInInvariantCulture.
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            ValidateObject(instance, path: "", errors, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        return errors;
    }

    private static void ValidateObject(object obj, string path, List<string> errors, HashSet<object> visited)
    {
        if (!visited.Add(obj))
            return;

        var results = new List<ValidationResult>();
        Validator.TryValidateObject(obj, new ValidationContext(obj), results, validateAllProperties: true);

        foreach (var result in results)
        {
            var members = result.MemberNames.Any() ? result.MemberNames : new[] { "" };
            foreach (var member in members)
                errors.Add($"{Join(path, JsonName(member))}: {result.ErrorMessage}");
        }

        foreach (var prop in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0 || IsSimple(prop.PropertyType))
                continue;

            var value = prop.GetValue(obj);
            var propPath = Join(path, JsonName(prop.Name));

            switch (value)
            {
                case null:
                    continue;
                case IEnumerable items when value is not string:
                    var i = 0;
                    foreach (var item in items)
                    {
                        if (item is not null && !IsSimple(item.GetType()))
                            ValidateObject(item, $"{propPath}[{i}]", errors, visited);
                        i++;
                    }
                    break;
                default:
                    ValidateObject(value, propPath, errors, visited);
                    break;
            }
        }
    }

    private static bool IsSimple(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(Guid)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(DateOnly)
            || type == typeof(TimeOnly)
            || type == typeof(TimeSpan)
            || type == typeof(Uri)
            || type == typeof(JsonElement);
    }

    private static string JsonName(string member) => JsonNamingPolicy.CamelCase.ConvertName(member);

    private static string Join(string path, string member) =>
        path.Length == 0 ? (member.Length == 0 ? "message" : member)
        : member.Length == 0 ? path
        : $"{path}.{member}";
}
