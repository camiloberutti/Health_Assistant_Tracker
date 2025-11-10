using System;
using Microsoft.Extensions.Options;

namespace GarminTempApi.Tests.TestUtilities;

internal sealed class StaticOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
{
    private readonly TOptions _value;

    public StaticOptionsMonitor(TOptions value)
    {
        _value = value;
    }

    public TOptions CurrentValue => _value;

    public TOptions Get(string? name) => _value;

    public IDisposable OnChange(Action<TOptions, string> listener) => NullDisposable.Instance;

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose()
        {
        }
    }
}
