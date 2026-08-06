using Shouldly;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>Pure-logic tests for <see cref="RippleOptions"/> (no database).</summary>
public sealed class RippleOptionsTests
{
    [Fact]
    public void retention_for_prefers_the_per_type_override_then_the_default()
    {
        var o = new RippleOptions
        {
            DefaultRetention = TimeSpan.FromDays(30),
            RetentionByWaveType =
            {
                ["Special"] = TimeSpan.FromDays(365),
                ["Forever"] = null // explicit keep-forever override
            }
        };

        o.RetentionFor("Special").ShouldBe(TimeSpan.FromDays(365));
        o.RetentionFor("Forever").ShouldBeNull();
        o.RetentionFor("Unlisted").ShouldBe(TimeSpan.FromDays(30)); // falls back to the default
    }

    [Fact]
    public void retention_defaults_to_keep_forever()
        => new RippleOptions().RetentionFor("anything").ShouldBeNull();
}
