using Xunit.Abstractions;
using Xunit.Sdk;

namespace x86cc.Ripple.Sample.E2ETests;

/// <summary>Orders tests within a class by an explicit priority so seeding runs before the taxation scenarios.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestPriorityAttribute(int priority) : Attribute
{
    public int Priority { get; } = priority;
}

public sealed class PriorityOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase
        => testCases.OrderBy(Priority);

    private static int Priority<TTestCase>(TTestCase testCase) where TTestCase : ITestCase
    {
        var attr = testCase.TestMethod.Method
            .GetCustomAttributes(typeof(TestPriorityAttribute).AssemblyQualifiedName!)
            .FirstOrDefault();
        return attr?.GetNamedArgument<int>(nameof(TestPriorityAttribute.Priority)) ?? 0;
    }
}
