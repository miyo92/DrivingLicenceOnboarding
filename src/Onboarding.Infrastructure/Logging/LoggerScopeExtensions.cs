using System.Collections;
using Microsoft.Extensions.Logging;

namespace Onboarding.Infrastructure.Logging;

public static class LoggerScopeExtensions
{
    /// <summary>
    /// Begins a scope whose properties appear as structured fields in JSON logs, with a readable scope message
    /// (a plain dictionary scope renders its type name).
    /// </summary>
    public static IDisposable? BeginPropertyScope(this ILogger logger, params (string Key, object? Value)[] properties) =>
        logger.BeginScope(new PropertyScope(properties));

    private sealed class PropertyScope((string Key, object? Value)[] properties) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public int Count => properties.Length;

        public KeyValuePair<string, object?> this[int index] => new(properties[index].Key, properties[index].Value);

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (var i = 0; i < properties.Length; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => string.Join(", ", properties.Select(p => $"{p.Key}:{p.Value}"));
    }
}
