using Emanager.Core;
using Xunit;

namespace Emanager.Core.Tests;

public sealed class CoreFoundationTests
{
    [Fact]
    public void CoreAssembly_is_independent_of_Avalonia()
    {
        Assert.Equal("Emanager.Core", CoreInfo.Name);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(),
            assembly => assembly.GetName().Name is "Avalonia" or "Avalonia.Base");
    }
}
